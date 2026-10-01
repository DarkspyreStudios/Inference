using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Connect.Image.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed class ComfyUiJsonTests
{
    private const string OutputState = "{\"FileName\":\"result.png\",\"Subfolder\":\"\",\"Type\":\"output\"}";

    [Fact]
    public void PersistedOutputStateRetainsDefaultNamesNullsAndCaseSensitiveReading()
    {
        var output = new ComfyOutputImage("<&>", "", "output");
        Assert.Equal("{\"FileName\":\"\\u003C\\u0026\\u003E\",\"Subfolder\":\"\",\"Type\":\"output\"}",
            JsonSerializer.Serialize(output, ComfyOutputJsonContext.Default.ComfyOutputImage));
        Assert.Equal("{\"FileName\":null,\"Subfolder\":null,\"Type\":null}", JsonSerializer.Serialize(
            JsonSerializer.Deserialize("{}", ComfyOutputJsonContext.Default.ComfyOutputImage), ComfyOutputJsonContext.Default.ComfyOutputImage));
        Assert.Equal(output, JsonSerializer.Deserialize(JsonSerializer.Serialize(output, ComfyOutputJsonContext.Default.ComfyOutputImage),
            ComfyOutputJsonContext.Default.ComfyOutputImage));
        Assert.Null(JsonSerializer.Deserialize("{\"filename\":\"wrong-case\",\"future\":true}", ComfyOutputJsonContext.Default.ComfyOutputImage)!.FileName);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("not-json", ComfyOutputJsonContext.Default.ComfyOutputImage));
        Assert.Null(ComfyOutputJsonContext.Default.GetTypeInfo(typeof(ComfyPromptRequest)));
    }

    [Fact]
    public void ProtocolMetadataPreservesPromptNullsDeleteAndDiagnosticWireBytes()
    {
        Assert.Equal("{\"prompt\":{\"node\":{\"text\":\"\\u003C\\u0026\\u003E\",\"future\":null}},\"client_id\":\"client\"}",
            JsonSerializer.Serialize(new ComfyPromptRequest(JsonNode.Parse("{\"node\":{\"text\":\"<&>\",\"future\":null}}")!.AsObject(), "client"),
                ComfyProtocolJsonContext.Default.ComfyPromptRequest));
        Assert.Equal("{\"delete\":[\"job\"]}", JsonSerializer.Serialize(new ComfyDeleteRequest(["job"]), ComfyProtocolJsonContext.Default.ComfyDeleteRequest));
        Assert.Equal("{\"workflowId\":\"workflow\",\"workflowVersion\":\"1\",\"requiredNodeTypes\":[\"node\"],\"missingNodeTypes\":[]}",
            JsonSerializer.Serialize(new ComfyWorkflowDiagnostics("workflow", "1", ["node"], []), ComfyProtocolJsonContext.Default.ComfyWorkflowDiagnostics));
        Assert.Equal("{\"source\":\"history\",\"status\":\"completed\"}",
            JsonSerializer.Serialize(new ComfyHistoryDiagnostics("history", "completed"), ComfyProtocolJsonContext.Default.ComfyHistoryDiagnostics));
        Assert.Equal("{\"source\":\"queue\",\"running\":false,\"pending\":true}",
            JsonSerializer.Serialize(new ComfyQueueDiagnostics("queue", false, true), ComfyProtocolJsonContext.Default.ComfyQueueDiagnostics));
        Assert.Equal("{\"status\":\"OK\",\"response\":\"\\u003C\\u0026\\u003E\"}",
            JsonSerializer.Serialize(new ComfyResponseDiagnostics("OK", "<&>"), ComfyProtocolJsonContext.Default.ComfyResponseDiagnostics));
        Assert.Null(ComfyProtocolJsonContext.Default.GetTypeInfo(typeof(ComfyOutputImage)));
    }

    [Theory]
    [InlineData("ComfyOutputImage", OutputState)]
    [InlineData("ComfyPromptRequest", "{\"prompt\":{\"future\":null},\"client_id\":\"client\"}")]
    [InlineData("ComfyDeleteRequest", "{\"delete\":[\"job\"]}")]
    [InlineData("ComfyWorkflowDiagnostics", "{\"workflowId\":\"workflow\",\"workflowVersion\":\"1\",\"requiredNodeTypes\":[],\"missingNodeTypes\":[]}")]
    [InlineData("ComfyHistoryDiagnostics", "{\"source\":\"history\",\"status\":\"completed\"}")]
    [InlineData("ComfyQueueDiagnostics", "{\"source\":\"queue\",\"running\":false,\"pending\":true}")]
    [InlineData("ComfyResponseDiagnostics", "{\"status\":\"OK\",\"response\":\"text\"}")]
    [InlineData("ProviderPipeline", "")]
    public void ActualCollectibleComfyMetadataAndSettledHttpCallersRetire(string family, string json)
    {
        WeakReference generation = ExerciseGeneration(family, json);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "ComfyUI metadata or settled HTTP callers retain the collectible Image generation.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration(string family, string json)
    {
        string path = typeof(ComfyUiImageTransport).Assembly.Location;
        var generation = new ImageGeneration(Path.GetDirectoryName(path)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly image = generation.LoadFromAssemblyPath(path);
            if (family == "ProviderPipeline") ExerciseHttp(image);
            else
            {
                string contextName = family == "ComfyOutputImage" ? "ComfyOutputJsonContext" : "ComfyProtocolJsonContext";
                Type owner = image.GetType("Darkspyre.Inference.Connect.Image.Providers." + contextName, throwOnError: true)!;
                object context = owner.GetProperty("Default")!.GetValue(null)!;
                JsonTypeInfo metadata = (JsonTypeInfo)owner.GetProperty(family)!.GetValue(context)!;
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
                Assert.Same(context, metadata.Options.TypeInfoResolver);
                object value = JsonSerializer.Deserialize(json, metadata)!;
                Assert.NotNull(JsonSerializer.Deserialize(JsonSerializer.Serialize(value, metadata), metadata));
            }
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static void ExerciseHttp(Assembly image)
    {
        string? clientId = null;
        using var handler = new Handler(async request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/prompt":
                    using (JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()))
                    {
                        clientId = body.RootElement.GetProperty("client_id").GetString();
                        Assert.Equal(32, clientId!.Length);
                        Assert.Equal("{\"prompt\":{\"future\":null},\"client_id\":\"" + clientId + "\"}", await request.Content.ReadAsStringAsync());
                    }
                    return Response("{\"prompt_id\":\"job\"}");
                case "/history/job":
                    return Response("{\"job\":{\"outputs\":{\"10\":{\"images\":[{\"filename\":\"result.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}");
                case "/view":
                    Assert.Equal("?filename=result.png&subfolder=&type=output", request.RequestUri.Query);
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                case "/queue":
                    Assert.Equal("{\"delete\":[\"job\"]}", await request.Content!.ReadAsStringAsync());
                    return Response("{}");
                case "/interrupt": return Response("{}");
                default: throw new InvalidOperationException("Unexpected ComfyUI path.");
            }
        });
        Type transportType = image.GetType(typeof(ComfyUiImageTransport).FullName!, throwOnError: true)!;
        Type loggerType = typeof(NullLogger<>).MakeGenericType(transportType);
        object logger = loggerType.GetField("Instance")!.GetValue(null)!;
        object transport = Activator.CreateInstance(transportType, new ClientFactory(handler), logger)!;
        Type connectionType = image.GetType(typeof(ImageConnection).FullName!, throwOnError: true)!;
        object connection = Activator.CreateInstance(connectionType)!;
        connectionType.GetProperty("Endpoint")!.SetValue(connection, "https://provider.invalid");
        Type preparedType = image.GetType(typeof(PreparedImageTransportRequest).FullName!, throwOnError: true)!;
        object prepared = Activator.CreateInstance(preparedType, "{\"future\":null}")!;
        Assert.Equal("job", Result(transportType.GetMethod("SubmitAsync")!.Invoke(transport, [connection, prepared, CancellationToken.None])!));
        Type workflowType = image.GetType(typeof(ImageWorkflow).FullName!, throwOnError: true)!;
        object workflow = Activator.CreateInstance(workflowType)!;
        workflowType.GetProperty("Id")!.SetValue(workflow, "workflow");
        workflowType.GetProperty("Label")!.SetValue(workflow, "Workflow");
        workflowType.GetProperty("TransportConfiguration")!.SetValue(workflow, JsonElement.Parse("""
            {"outputNodeId":"10","filenamePrefixTemplate":"prefix","defaultParameters":{},"workflow":{}}
            """));
        object snapshot = Result(transportType.GetMethod("GetJobAsync")!.Invoke(transport, [connection, "job", workflow, CancellationToken.None])!)!;
        Assert.Same(image, snapshot.GetType().Assembly);
        object output = snapshot.GetType().GetProperty("Output")!.GetValue(snapshot)!;
        Assert.Equal(OutputState, output.GetType().GetProperty("ProviderState")!.GetValue(output));
        Assert.Equal("{\"source\":\"history\",\"status\":\"completed\"}", snapshot.GetType().GetProperty("Diagnostics")!.GetValue(snapshot));
        Assert.Equal(new byte[] { 1, 2, 3 }, Result(transportType.GetMethod("DownloadAsync")!.Invoke(transport, [connection, output, CancellationToken.None])!));
        Assert.Equal(true, Result(transportType.GetMethod("TryCancelAsync")!.Invoke(transport, [connection, "job", true, CancellationToken.None])!));
        Assert.Equal(5, handler.Calls);
    }

    private static object? Result(object operation)
    {
        ((Task)operation).GetAwaiter().GetResult();
        return operation.GetType().GetProperty("Result")!.GetValue(operation);
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request); }
    }

    private sealed class ImageGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
