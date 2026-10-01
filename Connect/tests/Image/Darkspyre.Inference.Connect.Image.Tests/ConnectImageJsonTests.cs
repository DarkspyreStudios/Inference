using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed class ConnectImageJsonTests
{
    [Theory]
    [InlineData("job-missing")]
    [InlineData("download-missing")]
    [InlineData("download-null")]
    [InlineData("size-missing")]
    public void RequiredImageFactsDoNotSynthesizeMissingOrNullValues(string kind)
    {
        Assert.Throws<JsonException>(() =>
        {
            switch (kind)
            {
                case "job-missing": JsonSerializer.Deserialize("{}", ConnectImageJson.ImageTransportJobSnapshot); break;
                case "download-missing": JsonSerializer.Deserialize("{}", ConnectImageJson.ImageTransportDownload); break;
                case "download-null": JsonSerializer.Deserialize("{\"Data\":null}", ConnectImageJson.ImageTransportDownload); break;
                default: JsonSerializer.Deserialize("{}", InferenceJson.ImageSize); break;
            }
        });
        Assert.Empty(JsonSerializer.Deserialize("{\"Data\":\"\"}", ConnectImageJson.ImageTransportDownload)!.Data.ToArray());
        Assert.Null(JsonSerializer.Deserialize("{\"Data\":\"\",\"ContentType\":null}", ConnectImageJson.ImageTransportDownload)!.ContentType);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("receipt-scope")]
    [InlineData("output-scope")]
    public void RequiredImageFactsRefuseNullWithMutation(string kind)
    {
        var receipt = new ImagePreparedReference(new() { Id = Guid.NewGuid() }, new("owner", "identity"), JsonElement.Parse("{}"));
        var output = new ImageTransportOutput(null, receipt.Scope, receipt.ProviderState);
        Assert.Throws<ArgumentNullException>(() =>
        {
            if (kind == "metadata") _ = receipt with { Metadata = null! };
            else if (kind == "receipt-scope") _ = receipt with { Scope = null! };
            else _ = output with { Scope = null! };
        });
    }

    [Theory]
    [InlineData("metadata", false)]
    [InlineData("metadata", true)]
    [InlineData("receipt-scope", false)]
    [InlineData("receipt-scope", true)]
    [InlineData("output-scope", false)]
    [InlineData("output-scope", true)]
    public void RequiredReceiptFactsRefuseMissingAndExplicitNullOwnedJson(string kind, bool missing)
    {
        string metadata = missing ? "" : "\"Metadata\":null,";
        string scope = missing ? "" : "\"Scope\":null,";
        JsonTypeInfo root = kind == "output-scope" ? ConnectImageJson.ImageTransportOutput : ConnectImageJson.ImagePreparedReference;
        string json = kind == "metadata"
            ? "{" + metadata + "\"Scope\":{\"Owner\":\"owner\",\"Identity\":\"identity\"},\"ProviderState\":{}}"
            : "{" + scope + (kind == "receipt-scope" ? "\"Metadata\":{\"Id\":\"7ec55b60-b1f5-4e71-b2de-aa8accfd2ef0\"}," : "") + "\"ProviderState\":{}}";
        if (missing) Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, root));
        else Assert.Throws<ArgumentNullException>(() => JsonSerializer.Deserialize(json, root));
    }

    [Theory]
    [InlineData("ImageConnection", "{\"Transport\":0,\"Endpoint\":\"http://provider.invalid\",\"Authorization\":null}")]
    [InlineData("ImageTransport", "0")]
    [InlineData("ImageTransportCapabilities", "{\"SupportsProgress\":true,\"SupportsReferenceImages\":true,\"SupportsCancellation\":true}")]
    [InlineData("ImageTransportDescriptor", "{\"Transport\":0,\"DisplayName\":\"Comfy\",\"Description\":\"test\",\"Capabilities\":{}}")]
    [InlineData("ImageWorkflow", "{\"Id\":\"workflow\",\"Label\":\"Workflow\",\"Transport\":0,\"TransportConfiguration\":{\"opaque\":[1,null]}}")]
    [InlineData("ImageReference", "{\"Id\":\"7ec55b60-b1f5-4e71-b2de-aa8accfd2ef0\",\"FileName\":null,\"ContentType\":null}")]
    [InlineData("ImageWorkflowOrigin", "1")]
    [InlineData("ImageWorkflowSelection", "{\"Origin\":1,\"Id\":\"same\"}")]
    [InlineData("ImageSetupConfiguration", "{\"Transport\":0,\"Endpoint\":\"http://provider.invalid\",\"RequestTimeout\":\"00:10:00\",\"Workflow\":{\"Origin\":0,\"Id\":\"one\"}}")]
    [InlineData("ImageSetupHeaderChanges", "{\"Set\":{},\"Remove\":[]}")]
    [InlineData("ImageSetupChanges", "{\"Identity\":{\"Intent\":0,\"Id\":\"one\",\"ExpectedVersion\":null},\"Configuration\":{\"Transport\":0,\"Endpoint\":\"http://provider.invalid\",\"RequestTimeout\":\"00:10:00\",\"Workflow\":{\"Origin\":0,\"Id\":\"one\"}},\"Headers\":{\"Set\":{},\"Remove\":[]}}")]
    [InlineData("ImageTransportPreparationRequest", "{\"Workflow\":{\"Id\":\"one\",\"Label\":\"One\",\"Transport\":0,\"TransportConfiguration\":{}},\"PositivePrompt\":\"draw\",\"CacheKey\":\"one\",\"References\":[]}")]
    [InlineData("ImagePreparedReference", "{\"Metadata\":{\"Id\":\"7ec55b60-b1f5-4e71-b2de-aa8accfd2ef0\"},\"Scope\":{\"Owner\":\"endpoint\",\"Identity\":\"owner\"},\"ProviderState\":{\"opaque\":[1,null]},\"Resource\":{\"WorkspaceId\":\"workspace\",\"Id\":\"source\"}}")]
    [InlineData("PreparedImageTransportRequest", "{\"ProviderRequest\":\"{}\"}")]
    [InlineData("ImageTransportOutput", "{\"FileName\":null,\"Scope\":{\"Owner\":\"endpoint\",\"Identity\":\"owner\"},\"ProviderState\":{\"opaque\":true},\"ContentType\":null,\"File\":{\"FileId\":\"native\"},\"Resource\":{\"Reference\":{\"WorkspaceId\":\"workspace\",\"Id\":\"output\"},\"SessionId\":\"session\",\"FileName\":null,\"ContentType\":null,\"Bytes\":2}}")]
    [InlineData("ImageTransportDownload", "{\"ContentType\":null,\"Data\":\"AQI=\"}")]
    [InlineData("ImageTransportJobState", "2")]
    [InlineData("ImageTransportJobSnapshot", "{\"State\":3,\"Outputs\":[],\"Error\":{\"Code\":0,\"ProviderCode\":null,\"Message\":\"failed\",\"Resource\":{\"Phase\":4,\"Reference\":{\"WorkspaceId\":\"workspace\",\"Id\":\"output\"},\"CommitOutcome\":3}},\"Diagnostics\":{\"safe\":true}}")]
    [InlineData("ImageTransportProgressKind", "1")]
    [InlineData("ImageTransportProgress", "{\"Kind\":1,\"Value\":1,\"Maximum\":3}")]
    [InlineData("ImageProviderHealth", "{\"IsHealthy\":true,\"Message\":\"ready\",\"Diagnostics\":\"{}\"}")]
    public void EveryExportedRootUsesActualForeignOwnedMetadataAndCollects(string root, string json)
    {
        JsonTypeInfo metadata = (JsonTypeInfo)typeof(ConnectImageJson).GetProperty(root)!.GetValue(null)!;
        object value = JsonSerializer.Deserialize(json, metadata)!;
        Assert.NotNull(JsonSerializer.Deserialize(JsonSerializer.Serialize(value, metadata), metadata));
        WeakReference generation = ExerciseForeign(root, json);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(generation.IsAlive);
    }

    [Fact]
    public void RuntimeAuthorityRefusesInOwnedAndExternalGeneratedGraphsWithoutCallbacks()
    {
        var authority = new Authority();
        var connection = new ImageConnection { Transport = ImageTransport.ComfyUi, Endpoint = "http://provider.invalid", Authorization = authority };
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(connection, ConnectImageJson.ImageConnection));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize(
            "{\"Transport\":0,\"Endpoint\":\"http://provider.invalid\",\"Authorization\":{}}", ConnectImageJson.ImageConnection));
        var options = new JsonSerializerOptions(ConnectImageJson.ImageConnection.Options) { TypeInfoResolver = null };
        var external = new ImageConsumerJsonContext(options);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ImageRuntimeEnvelope(authority), external.ImageRuntimeEnvelope));
        Assert.Null(JsonSerializer.Deserialize("{\"Authority\":null}", external.ImageRuntimeEnvelope)!.Authority);
        Assert.Equal(0, authority.Calls);
    }

    [Fact]
    public void DownloadReusesExactCommonPropertyPolicyWithoutChangingOtherBinaryMemory()
    {
        Assert.Same(InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter,
            ConnectImageJson.ImageTransportDownload.Properties.Single(property => property.Name == "Data").CustomConverter);
        var options = new JsonSerializerOptions(ConnectImageJson.ImageTransportDownload.Options)
        { TypeInfoResolver = JsonTypeInfoResolver.Combine(ConnectImageJson.ImageTransportDownload.Options.TypeInfoResolver!, ImageConsumerJsonContext.Default) };
        options.MakeReadOnly();
        JsonTypeInfo<ImageDownloadEnvelope> metadata = options.GetTypeInfo<ImageDownloadEnvelope>();
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Download\":{\"Data\":null},\"Other\":null}", metadata));
        ImageDownloadEnvelope empty = JsonSerializer.Deserialize("{\"Download\":{\"Data\":\"\",\"ContentType\":null},\"Other\":null}", metadata)!;
        Assert.True(empty.Download.Data.IsEmpty);
        Assert.True(empty.Other.IsEmpty);
        Assert.Null(empty.Download.ContentType);
    }

    [Fact]
    public void NestedImageSourceUsesTargetedCommonRequiredDataPolicy()
    {
        var options = new JsonSerializerOptions(ConnectImageJson.ImageConnection.Options)
        { TypeInfoResolver = JsonTypeInfoResolver.Combine(ConnectImageJson.ImageConnection.Options.TypeInfoResolver!, ImageConsumerJsonContext.Default) };
        options.MakeReadOnly();
        JsonTypeInfo<ImageSourceEnvelope> metadata = options.GetTypeInfo<ImageSourceEnvelope>();
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Request\":{\"ModelId\":\"one\",\"Prompt\":\"draw\",\"References\":[{\"type\":\"binary\",\"ContentType\":null,\"Data\":null}]}}", metadata));
        ImageSourceEnvelope empty = JsonSerializer.Deserialize("{\"Request\":{\"ModelId\":\"one\",\"Prompt\":\"draw\",\"References\":[{\"type\":\"binary\",\"ContentType\":null,\"Data\":\"\"}]}}", metadata)!;
        Assert.True(Assert.IsType<BinaryMediaSource>(Assert.Single(empty.Request.References)).Data.IsEmpty);
        Assert.Same(InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter,
            options.GetTypeInfo<BinaryMediaSource>().Properties.Single(property => property.Name == "Data").CustomConverter);
    }

    [Fact]
    public void ReceiptOutputAndDownloadOwnTheirSnapshotsIncludingWithUpdates()
    {
        using var state = JsonDocument.Parse("{\"opaque\":[1,null]}");
        var receipt = new ImagePreparedReference(new() { Id = Guid.NewGuid() }, new("owner", "identity"), state.RootElement);
        receipt = receipt with { ProviderState = state.RootElement };
        var output = new ImageTransportOutput(null, receipt.Scope, state.RootElement);
        output = output with { ProviderState = state.RootElement };
        var outputs = new List<ImageTransportOutput> { output };
        var snapshot = new ImageTransportJobSnapshot(ImageTransportJobState.Completed, outputs, Diagnostics: state.RootElement);
        snapshot = snapshot with { Outputs = outputs, Diagnostics = state.RootElement };
        byte[] bytes = [1, 2];
        var download = new ImageTransportDownload(null, bytes) with { Data = bytes };
        bytes[0] = 9;
        outputs.Clear();
        state.Dispose();
        Assert.Equal(1, receipt.ProviderState.GetProperty("opaque")[0].GetInt32());
        Assert.Equal(1, output.ProviderState.GetProperty("opaque")[0].GetInt32());
        Assert.Single(snapshot.Outputs!);
        Assert.Equal(1, snapshot.Diagnostics!.Value.GetProperty("opaque")[0].GetInt32());
        Assert.Equal((byte)1, download.Data.Span[0]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(string root, string json)
    {
        string path = typeof(ConnectImageJson).Assembly.Location;
        var generation = new ImageGeneration(Path.GetDirectoryName(path)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly image = generation.LoadFromAssemblyPath(path);
            Assert.NotSame(typeof(ConnectImageJson).Assembly, image);
            Assert.Equal(typeof(ConnectImageJson).Assembly.ManifestModule.ModuleVersionId, image.ManifestModule.ModuleVersionId);
            Type facade = image.GetType(typeof(ConnectImageJson).FullName!, true)!;
            JsonTypeInfo metadata = (JsonTypeInfo)facade.GetProperty(root)!.GetValue(null)!;
            Assert.Same(image, metadata.Type.Assembly);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Options.TypeInfoResolver!.GetType().Assembly));
            object value = JsonSerializer.Deserialize(json, metadata)!;
            object second = JsonSerializer.Deserialize(JsonSerializer.Serialize(value, metadata), metadata)!;
            if (root is "ImagePreparedReference" or "ImageTransportOutput")
            {
                object scope = second.GetType().GetProperty("Scope")!.GetValue(second)!;
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(scope.GetType().Assembly));
                Assert.Equal("Darkspyre.Inference.Common", scope.GetType().Assembly.GetName().Name);
            }
            if (root == "ImageTransportDownload")
            {
                JsonConverter converter = metadata.Properties.Single(property => property.Name == "Data").CustomConverter!;
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(converter.GetType().Assembly));
                Assert.Equal("Darkspyre.Inference.Common", converter.GetType().Assembly.GetName().Name);
                Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Data\":null}", metadata));
                JsonTypeInfo binary = metadata.Options.GetTypeInfo(image.GetType("Darkspyre.Inference.Common.Media.BinaryMediaSource", false)
                    ?? generation.Assemblies.Single(item => item.GetName().Name == "Darkspyre.Inference.Common").GetType(typeof(BinaryMediaSource).FullName!, true)!);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(binary.Type.Assembly));
                Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ContentType\":null,\"Data\":null}", binary));
            }
        }
        finally { generation.Unload(); }
        return weak;
    }

    private sealed class ImageGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class Authority : IProviderAuthorization
    {
        public int Calls { get; private set; }
        public ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Runtime authority must not be invoked by serialization."); }
    }
}

internal sealed record ImageRuntimeEnvelope(IProviderAuthorization? Authority);
internal sealed record ImageDownloadEnvelope(ImageTransportDownload Download, ReadOnlyMemory<byte> Other);
internal sealed record ImageSourceEnvelope(ImageGenerationRequest Request);
[JsonSerializable(typeof(ImageRuntimeEnvelope))]
[JsonSerializable(typeof(ImageDownloadEnvelope))]
[JsonSerializable(typeof(ImageSourceEnvelope))]
internal sealed partial class ImageConsumerJsonContext : JsonSerializerContext;
