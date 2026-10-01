using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class GeminiEstimateJsonTests
{
    private const string EmptyProjection = """
        {"ModelId":"model","Instructions":null,"Messages":[],"Temperature":null,"MaxOutputTokens":null,"TopP":null,"TopK":null,"Seed":null,"PresencePenalty":null,"FrequencyPenalty":null,"StopSequences":[],"ReasoningLevel":null,"StructuredOutput":null,"Tools":[],"ToolChoice":null,"AllowParallelToolCalls":null,"ConversationKey":null,"PromptCache":null,"ProviderOptions":{},"UnboundContinuationState":0}
        """;

    [Fact]
    public void OwnedProjectionRetainsExactDefaultNamesOrderNullsAndNumericEnums()
    {
        var request = new LlmChatRequest { ModelId = "model" };
        Assert.Equal(EmptyProjection, JsonSerializer.Serialize(request, ConnectLlmJson.ChatRequest));
        Assert.Equal(EmptyProjection, JsonSerializer.Serialize(request));
        string omit = JsonSerializer.Serialize(request with { UnboundContinuationState = LlmUnboundContinuationState.Omit }, ConnectLlmJson.ChatRequest);
        Assert.Equal(EmptyProjection.Replace("\"UnboundContinuationState\":0", "\"UnboundContinuationState\":1", StringComparison.Ordinal), omit);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ActualEstimatorsRetainDefaultProjectionBytesAndCounts(bool vertex, bool tools)
    {
        LlmChatRequest request = Request(vertex, tools);
        string original = JsonSerializer.Serialize(request);
        string owned = JsonSerializer.Serialize(request, ConnectLlmJson.ChatRequest);
        Assert.Equal(original, owned);
        Assert.Contains("\\u0022\\\\\\n\\u003C\\u0026\\u003E\\u00E9", owned, StringComparison.Ordinal);
        Assert.Contains("\"ReasoningLevel\":3", owned, StringComparison.Ordinal);
        Assert.Contains("\"Temperature\":0", owned, StringComparison.Ordinal);
        Assert.Contains("\"ConversationKey\":\"\"", owned, StringComparison.Ordinal);
        using var http = new HttpClient(new NoHttp());
        using var authentication = new VertexAuthentication();
        ILlmContextEstimator estimator = vertex ? new VertexLlmClient(http, authentication) : new GeminiApiLlmClient(http);
        var estimate = estimator.EstimateInputTokens(Connection(vertex), request);
        Assert.Equal(new ContextEstimate(LocalTokenCount.Count(original)), estimate);
        Assert.Contains("not a provider admission check", estimator.EstimateDiagnostic!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "state")]
    [InlineData(false, "attachment")]
    [InlineData(false, "binary")]
    [InlineData(false, "resource")]
    [InlineData(true, "state")]
    [InlineData(true, "attachment")]
    [InlineData(true, "binary")]
    [InlineData(true, "resource")]
    public void OpaqueAndMediaOccupancyRemainsUnknown(bool vertex, string content)
    {
        LlmChatRequest request = Request(vertex, false);
        var message = new LlmChatMessage("user", "text");
        message = content switch
        {
            "state" => message with
            {
                ContinuationState = new()
                { ImplementationId = "fixture", ModelId = request.ModelId, ScopeId = "scope", Payload = "opaque" }
            },
            "attachment" => message with { Attachments = [new() { Kind = LlmBinaryKind.Image, FileName = "image.png", Data = new byte[] { 1 }, MediaType = "image/png" }] },
            "binary" => message with { ToolResults = [new("call", new() { Parts = [new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", new byte[] { 1 })] })] },
            _ => message with { ToolResults = [new("call", new() { Parts = [new LlmToolOutputResource("urn:fixture")] })] }
        };
        using var http = new HttpClient(new NoHttp());
        using var authentication = new VertexAuthentication();
        ILlmContextEstimator estimator = vertex ? new VertexLlmClient(http, authentication) : new GeminiApiLlmClient(http);
        Assert.Null(estimator.EstimateInputTokens(Connection(vertex), request with { Messages = [message] }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedControlsStillFailWithoutHttp(bool vertex)
    {
        using var http = new HttpClient(new NoHttp());
        using var authentication = new VertexAuthentication();
        ILlmContextEstimator estimator = vertex ? new VertexLlmClient(http, authentication) : new GeminiApiLlmClient(http);
        Assert.Throws<NotSupportedException>(() => estimator.EstimateInputTokens(
            Connection(vertex) with { CapabilityOverrides = new() { SupportsTemperature = false } }, Request(vertex, false)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ActualForeignEstimatorsReleaseTheirOwnedRequestGeneration(bool vertex, bool tools)
    {
        WeakReference generation = ExerciseGeneration(vertex, tools);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The settled local estimator retains its Connect/Vertex/Common generation.");
    }

    private static LlmConnection Connection(bool vertex) => new()
    {
        Version = "1",
        Transport = vertex ? LlmTransport.GoogleVertexAi : LlmTransport.GoogleGeminiApi,
        Endpoint = vertex ? VertexEndpoints.BaseEndpoint : GeminiApiLlmClient.DefaultEndpoint,
        Vertex = vertex ? new() { ProjectId = "project", Location = "global" } : null
    };

    private static LlmChatRequest Request(bool vertex, bool tools) => new()
    {
        ModelId = vertex ? "google/gemini-3-flash-preview" : "gemini-3-flash-preview",
        Instructions = null,
        Messages = [new("user", "\"\\\n<&>\u00e9"), new("tool", "")
        {
            ToolResults = tools ? [new("call", new()
            {
                Parts = [new LlmToolOutputText("result<&>")],
                StructuredContent = JsonElement.Parse("{\"raw\":\"<&>\",\"zero\":0}")
            })] : []
        }],
        Temperature = 0,
        MaxOutputTokens = 1,
        ReasoningLevel = LlmReasoningLevel.Medium,
        ConversationKey = string.Empty,
        PromptCache = new() { Scope = LlmPromptCacheScope.Prefix, Retention = LlmPromptCacheRetention.Extended },
        Tools = tools ? [new() { Name = "lookup", Description = null, InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }] : [],
        ToolChoice = tools ? new() { Mode = LlmToolChoiceMode.Auto } : null,
        ProviderOptions = new Dictionary<string, JsonElement> { ["opaque"] = JsonElement.Parse("{\"b\":false,\"a\":null}") }
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration(bool vertex, bool tools)
    {
        string directory = Path.GetDirectoryName(typeof(VertexLlmClient).Assembly.Location)!;
        var generation = new EstimateGeneration(directory);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assembly vertexAssembly = generation.LoadFromAssemblyPath(typeof(VertexLlmClient).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(connect));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(vertexAssembly));
            Type owner = connect.GetType("Darkspyre.Inference.Connect.LLM.ConnectLlmJson", true)!;
            var requestMetadata = (JsonTypeInfo)owner.GetProperty("ChatRequest")!.GetValue(null)!;
            var connectionMetadata = (JsonTypeInfo)owner.GetProperty("Connection")!.GetValue(null)!;
            Assert.Same(connect, requestMetadata.Type.Assembly);
            LlmChatRequest local = Request(vertex, tools);
            string projection = JsonSerializer.Serialize(local, ConnectLlmJson.ChatRequest);
            long expectedTokens = LocalTokenCount.Count(projection);
            object request = JsonSerializer.Deserialize(projection, requestMetadata)!;
            object connection = JsonSerializer.Deserialize(JsonSerializer.Serialize(Connection(vertex), ConnectLlmJson.Connection), connectionMetadata)!;
            Assert.Same(connect, request.GetType().Assembly);
            object hint = request.GetType().GetProperty("PromptCache")!.GetValue(request)!;
            Assert.Same(common, hint.GetType().Assembly);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(hint.GetType().Assembly));
            if (tools)
            {
                object tool = Assert.Single(((IEnumerable)request.GetType().GetProperty("Tools")!.GetValue(request)!).Cast<object>());
                Assert.Same(common, tool.GetType().Assembly);
                object message = ((IEnumerable)request.GetType().GetProperty("Messages")!.GetValue(request)!).Cast<object>().Last();
                object result = Assert.Single(((IEnumerable)message.GetType().GetProperty("ToolResults")!.GetValue(message)!).Cast<object>());
                object output = result.GetType().GetProperty("Output")!.GetValue(result)!;
                object part = Assert.Single(((IEnumerable)output.GetType().GetProperty("Parts")!.GetValue(output)!).Cast<object>());
                Assert.Same(common, result.GetType().Assembly);
                Assert.Same(common, output.GetType().Assembly);
                Assert.Same(common, part.GetType().Assembly);
            }
            using var http = new HttpClient(new NoHttp());
            using var authentication = (IDisposable)Activator.CreateInstance(vertexAssembly.GetType("Darkspyre.Inference.Connect.LLM.Vertex.VertexAuthentication", true)!)!;
            Type clientType = vertex ? vertexAssembly.GetType("Darkspyre.Inference.Connect.LLM.Vertex.VertexLlmClient", true)!
                : connect.GetType("Darkspyre.Inference.Connect.LLM.Providers.GeminiApiLlmClient", true)!;
            object client = Activator.CreateInstance(clientType, vertex ? [http, authentication] : [http])!;
            Assert.Same(vertex ? vertexAssembly : connect, client.GetType().Assembly);
            object estimate = clientType.GetMethod("EstimateInputTokens")!.Invoke(client, [connection, request])!;
            Assert.Same(connect, estimate.GetType().Assembly);
            Assert.Equal(expectedTokens, (long)estimate.GetType().GetProperty("Tokens")!.GetValue(estimate)!);
            Assert.Equal(false, estimate.GetType().GetProperty("IncludesProviderReport")!.GetValue(estimate));
        }
        finally
        {
            generation.Unload();
        }
        return weak;
    }

    private sealed class EstimateGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Local estimates must not perform HTTP calls.");
    }
}
