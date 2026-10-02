using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.Video;

namespace Darkspyre.Inference.Connect.Resources.Tests;

public sealed class ResourcesJsonTests
{
    private const string Scope = "{\"Owner\":\"endpoint\",\"Identity\":\"account\"}";
    private const string Descriptor = "{\"Reference\":{\"WorkspaceId\":\"w\",\"Id\":\"r\"},\"SessionId\":\"s\",\"FileName\":\"out.json\",\"ContentType\":\"application/json\",\"Bytes\":0}";
    private const string Batch = "{\"Id\":\"b\",\"Name\":\"name\",\"Scope\":" + Scope + ",\"Counts\":{\"Requests\":null,\"Pending\":0,\"Success\":1,\"Error\":0,\"Cancelled\":null}}";
    private const string ChatRequest = "{\"ModelId\":\"manual-model\",\"Resources\":null,\"Messages\":[{\"Role\":\"user\",\"Content\":\"text\",\"Attachments\":[{\"type\":\"binary\",\"Kind\":2,\"FileName\":\"reference.txt\",\"Data\":\"AQI=\",\"MediaType\":\"text/plain\"},{\"type\":\"workspace-resource\",\"Resource\":{\"WorkspaceId\":\"w\",\"Id\":\"r\"}}]}],\"HostedTools\":[{\"type\":\"xai-x-search\",\"AllowedHandles\":[\"actual\"]},{\"type\":\"remote-mcp\",\"ServerUrl\":\"https://example.invalid/mcp\",\"ServerLabel\":\"server\",\"Authorization\":null}],\"ProviderOptions\":{\"opaque\":{\"value\":1}}}";
    private const string ChatResult = "{\"Text\":\"answer\",\"Status\":0,\"Diagnostics\":{\"HttpStatusCode\":200,\"RequestId\":null,\"RawFinalResponseJson\":null}}";
    private const string FunctionChatRequest = "{\"ModelId\":\"m\",\"Tools\":[{\"type\":\"function\",\"Name\":\"tool\",\"InputSchema\":{\"type\":\"object\"},\"Strict\":false}],\"Messages\":[{\"Role\":\"assistant\",\"Content\":\"\",\"ToolCalls\":[{\"type\":\"function\",\"Id\":\"call\",\"Name\":\"tool\",\"ArgumentsJson\":\"{}\"}],\"ContinuationState\":{\"FormatVersion\":2,\"ImplementationId\":\"owner\",\"ModelId\":\"m\",\"ScopeId\":\"scope\",\"Payload\":\"opaque\"}},{\"Role\":\"tool\",\"Content\":\"\",\"ToolResults\":[{\"type\":\"function\",\"ToolCallId\":\"call\",\"Output\":{\"Parts\":[{\"type\":\"text\",\"Text\":\"text\"},{\"type\":\"binary\",\"Kind\":1,\"MediaType\":\"text/plain\",\"Data\":\"AQI=\"},{\"type\":\"workspace-resource\",\"Resource\":{\"WorkspaceId\":\"w\",\"Id\":\"r\"}}],\"StructuredContent\":{\"actual\":1}}}]}]}";
    private const string ObservedChatResult = "{\"Text\":\"answer\",\"Status\":0,\"Usage\":{\"InputTokens\":null,\"OutputTokens\":0,\"TotalTokens\":null,\"ProviderReportedCostUsd\":0.001,\"HostedToolCalls\":{\"WebSearch\":0},\"HostedToolCallCount\":0,\"HostedSourcesUsed\":null},\"Citations\":[{\"Url\":\"https://example.invalid/source\",\"StartIndex\":null,\"EndIndex\":null}],\"Diagnostics\":{\"HttpStatusCode\":200,\"RequestId\":null,\"RawFinalResponseJson\":null}}";
    private const string ImageRequest = "{\"ModelId\":\"image-model\",\"Prompt\":\"prompt\",\"Count\":2,\"References\":[{\"type\":\"binary\",\"ContentType\":null,\"Data\":\"AQI=\"},{\"type\":\"resource\",\"Resource\":{\"WorkspaceId\":\"w\",\"Id\":\"r\"}}]}";
    private const string Workflow = "{\"Id\":\"w\",\"Label\":\"workflow\",\"Transport\":0,\"TransportConfiguration\":{\"opaque\":1}}";
    private const string ImageResult = "{\"Images\":[{\"ContentType\":null,\"Data\":\"AQI=\",\"Resource\":" + Descriptor + "}],\"Provenance\":{\"ImplementationId\":\"actual\",\"ModelId\":\"image-model\"}}";
    private const string VideoGeneration = "{\"ModelId\":\"video-model\",\"FirstFrame\":{\"type\":\"binary\",\"ContentType\":null,\"Data\":\"AQI=\"},\"ProviderOptions\":{\"type\":\"xai-video\",\"Storage\":{\"FileName\":\"output\",\"PublicUrl\":{\"type\":\"flag\",\"Enabled\":false}},\"User\":\"opaque\"}}";
    private const string VideoEdit = "{\"ModelId\":\"video-model\",\"Prompt\":\"edit\",\"SourceVideo\":{\"type\":\"file\",\"File\":{\"FileId\":\"native\"}}}";
    private const string VideoExtension = "{\"ModelId\":\"video-model\",\"SourceVideo\":{\"type\":\"resource\",\"Resource\":{\"WorkspaceId\":\"w\",\"Id\":\"r\"}}}";
    private const string VideoSnapshot = "{\"Job\":{\"ProviderJobId\":\"native-job\",\"Scope\":" + Scope + "},\"State\":1,\"Output\":{\"Scope\":" + Scope + ",\"ProviderState\":{\"actual\":1},\"Duration\":\"00:00:00\"}}";
    private const string Materialization = "{\"Resource\":" + Descriptor + ",\"Duration\":\"00:00:00\",\"Provenance\":null}";
    private const string Failure = "{\"Code\":10,\"ProviderCode\":null,\"Message\":\"sanitized\"}";

    public static TheoryData<string, string> Shapes => new()
    {
        { "LlmResourcePageRequest", "{\"Limit\":1,\"PaginationToken\":\"opaque\"}" },
        { "LlmBatchCounts", "{\"Requests\":null,\"Pending\":0,\"Success\":9223372036854775807,\"Error\":null,\"Cancelled\":0}" },
        { "LlmBatch", Batch },
        { "LlmBatchOperation", "{\"type\":\"chat\",\"Request\":" + ChatRequest + "}" },
        { "LlmBatchOperation", "{\"type\":\"image\",\"Request\":" + ImageRequest + ",\"Workflow\":" + Workflow + "}" },
        { "LlmBatchOperation", "{\"type\":\"video-generation\",\"Request\":" + VideoGeneration + "}" },
        { "LlmBatchOperation", "{\"type\":\"video-edit\",\"Request\":" + VideoEdit + "}" },
        { "LlmBatchOperation", "{\"type\":\"video-extension\",\"Request\":" + VideoExtension + "}" },
        { "LlmBatchChatOperation", "{\"Request\":" + ChatRequest + "}" },
        { "LlmBatchChatOperation", "{\"Request\":" + FunctionChatRequest + "}" },
        { "LlmBatchChatOperation", "{\"Request\":{\"ModelId\":\"m\",\"Tools\":[{\"type\":\"function\",\"Name\":\"function\",\"InputSchema\":{\"type\":\"object\"}},{\"type\":\"shell\",\"Skills\":[{\"Name\":\"skill\",\"Description\":\"actual\",\"Path\":\"/skills/actual\"}]}],\"Messages\":[{\"Role\":\"assistant\",\"Content\":\"\",\"ToolCalls\":[{\"type\":\"function\",\"Id\":\"f\",\"Name\":\"function\",\"ArgumentsJson\":\"{}\"},{\"type\":\"shell\",\"Id\":\"sh\",\"Commands\":[\"first\",\"second\"],\"TimeoutMs\":-9223372036854775808,\"MaxOutputLength\":2147483647}]},{\"Role\":\"tool\",\"Content\":\"\",\"ToolResults\":[{\"type\":\"function\",\"ToolCallId\":\"f\",\"Output\":{\"Parts\":[{\"type\":\"text\",\"Text\":\"actual\"}]}},{\"type\":\"shell\",\"ToolCallId\":\"sh\",\"Output\":[{\"Stdout\":\"raw\",\"Stderr\":null,\"Outcome\":{\"type\":\"exit\",\"ExitCode\":-2147483648}},{\"Stdout\":null,\"Stderr\":\"error\",\"Outcome\":{\"type\":\"timeout\"}},{\"Stdout\":\"\",\"Stderr\":\"\",\"Outcome\":{\"type\":\"exit\",\"ExitCode\":0}}]}]}]}}" },
        { "LlmBatchImageOperation", "{\"Request\":" + ImageRequest + ",\"Workflow\":" + Workflow + "}" },
        { "LlmBatchVideoGenerationOperation", "{\"Request\":" + VideoGeneration + "}" },
        { "LlmBatchVideoGenerationOperation", "{\"Request\":{\"ModelId\":\"video-model\",\"Prompt\":\"actual\",\"Duration\":\"00:00:08\",\"AspectRatio\":\"16:9\",\"ResolutionHeight\":720,\"FirstFrame\":{\"type\":\"binary\",\"ContentType\":\"image/png\",\"Data\":\"AQI=\"},\"LastFrame\":{\"type\":\"url\",\"Url\":\"https://example.invalid/last\"},\"Keyframes\":[{\"Image\":{\"type\":\"resource\",\"Resource\":{\"WorkspaceId\":\"w\",\"Id\":\"r\"}},\"Timestamp\":\"00:00:00\"}],\"ReferenceImages\":[{\"type\":\"file\",\"File\":{\"FileId\":\"native\"}}],\"ReferenceAudio\":[{\"type\":\"voice\",\"VoiceId\":\"preset\"},{\"type\":\"audio\",\"Source\":{\"type\":\"binary\",\"ContentType\":\"audio/wav\",\"Data\":\"AwQ=\"}}],\"GenerateAudio\":false,\"ProviderOptions\":{\"type\":\"xai-video\",\"Storage\":{\"FileName\":\"output\",\"ExpiresAfter\":\"00:00:00\",\"PublicUrl\":{\"type\":\"configuration\",\"Options\":{\"ExpiresAfter\":\"01:00:00\"}}},\"Output\":{\"UploadUrl\":\"https://example.invalid/upload?signature=secret\"},\"User\":\"opaque\"}}}" },
        { "LlmBatchVideoEditOperation", "{\"Request\":" + VideoEdit + "}" },
        { "LlmBatchVideoExtensionOperation", "{\"Request\":" + VideoExtension + "}" },
        { "LlmBatchRequest", "{\"CustomId\":\"second\",\"Operation\":{\"type\":\"chat\",\"Request\":" + ChatRequest + "}}" },
        { "LlmBatchRequestMetadata", "{\"Id\":\"id\",\"Endpoint\":null,\"ModelId\":null,\"State\":null,\"CreateTime\":null,\"FinishTime\":null}" },
        { "LlmBatchPage", "{\"Items\":[" + Batch + "],\"PaginationToken\":null}" },
        { "LlmBatchRequestPage", "{\"Items\":[{\"Id\":\"id\",\"Endpoint\":\"/actual\",\"ModelId\":\"actual\",\"State\":\"observed\"}],\"PaginationToken\":\"\"}" },
        { "LlmBatchResult", "{\"type\":\"chat\",\"CustomId\":\"c\",\"Result\":" + ChatResult + "}" },
        { "LlmBatchResult", "{\"type\":\"image\",\"CustomId\":\"c\",\"Result\":" + ImageResult + "}" },
        { "LlmBatchResult", "{\"type\":\"video\",\"CustomId\":\"c\",\"Result\":" + VideoSnapshot + ",\"Materialization\":" + Materialization + "}" },
        { "LlmBatchResult", "{\"type\":\"failure\",\"CustomId\":\"c\",\"Error\":" + Failure + "}" },
        { "LlmBatchChatResult", "{\"CustomId\":\"c\",\"Result\":" + ChatResult + "}" },
        { "LlmBatchChatResult", "{\"CustomId\":\"c\",\"Result\":" + ObservedChatResult + "}" },
        { "LlmBatchChatResult", "{\"CustomId\":\"c\",\"Result\":{\"Text\":\"\",\"Status\":4,\"ToolCalls\":[{\"type\":\"function\",\"Id\":\"f\",\"Name\":\"function\",\"ArgumentsJson\":\"{}\"},{\"type\":\"shell\",\"Id\":\"sh\",\"Commands\":[],\"TimeoutMs\":0,\"MaxOutputLength\":null}],\"Diagnostics\":{\"HttpStatusCode\":200,\"RequestId\":null,\"RawFinalResponseJson\":null}}}" },
        { "LlmBatchImageResult", "{\"CustomId\":\"c\",\"Result\":" + ImageResult + "}" },
        { "LlmBatchVideoResult", "{\"CustomId\":\"c\",\"Result\":" + VideoSnapshot + ",\"Materialization\":" + Materialization + "}" },
        { "LlmBatchFailure", "{\"CustomId\":\"c\",\"Error\":" + Failure + "}" },
        { "LlmBatchResultPage", "{\"Items\":[{\"type\":\"failure\",\"CustomId\":\"second\",\"Error\":" + Failure + "},{\"type\":\"chat\",\"CustomId\":\"first\",\"Result\":" + ChatResult + "}],\"PaginationToken\":\"repeat-token\"}" },
        { "LlmDeferredCompletion", "{\"RequestId\":\"native\",\"Scope\":" + Scope + ",\"SubmittedAt\":null,\"ExpiresAt\":null}" },
        { "LlmDeferredResult", "{\"type\":\"pending\"}" },
        { "LlmDeferredResult", "{\"type\":\"completed\",\"Result\":" + ChatResult + "}" },
        { "LlmDeferredPending", "{}" },
        { "LlmDeferredCompleted", "{\"Result\":" + ChatResult + "}" },
        { "LlmMediaMaterializationOptions", "{\"MaximumInlineImageBytes\":1,\"MaximumInlineResultBytes\":9223372036854775807}" },
        { "MaterializedVideoResult", Materialization }
    };

    [Theory, MemberData(nameof(Shapes))]
    public void EveryGetterAndNestedVariantPreservesFacts(string getter, string json) => RoundTrip(Metadata(typeof(ConnectResourcesJson), getter), json);

    [Fact]
    public void ExactGetterInventoryContainsOnlyTheTwentyFiveDataRoots()
    {
        string[] actual = typeof(ConnectResourcesJson).GetProperties(BindingFlags.Static | BindingFlags.Public).Select(property => property.Name).Order().ToArray();
        string[] expected = Shapes.Select(row => (string)row[0]).Distinct().Order().ToArray();
        Assert.Equal(25, actual.Length); Assert.Equal(expected, actual);
        foreach (PropertyInfo property in typeof(ConnectResourcesJson).GetProperties(BindingFlags.Static | BindingFlags.Public))
            Assert.Equal(property.Name, ((JsonTypeInfo)property.GetValue(null)!).Type.Name);
    }

    public static TheoryData<string, string> InvalidShapes => new()
    {
        { "LlmBatch", "{}" }, { "LlmBatch", Batch.Replace("\"Counts\":{", "\"MissingCounts\":{", StringComparison.Ordinal) },
        { "LlmBatch", "{\"Id\":\"b\",\"Name\":\"n\",\"Scope\":null,\"Counts\":{}}" },
        { "LlmBatch", "{\"Id\":\"b\",\"Name\":null,\"Scope\":" + Scope + ",\"Counts\":{}}" },
        { "LlmBatch", "{\"Id\":\"b\",\"Name\":\"n\",\"Scope\":{\"Owner\":null,\"Identity\":\"i\"},\"Counts\":{}}" },
        { "LlmBatchCounts", "{\"Requests\":-1}" }, { "LlmBatchCounts", "{\"Requests\":9223372036854775808}" },
        { "LlmBatchOperation", "{\"type\":\"unknown\",\"Request\":" + ChatRequest + "}" },
        { "LlmBatchChatOperation", "{}" }, { "LlmBatchChatOperation", "{\"Request\":null}" },
        { "LlmBatchChatOperation", "{\"Request\":{\"ModelId\":\"m\",\"Tools\":[{\"type\":\"unknown\"}]}}" },
        { "LlmBatchChatOperation", "{\"Request\":{\"ModelId\":\"m\",\"Messages\":[{\"Role\":\"assistant\",\"Content\":\"\",\"ToolCalls\":[{\"type\":\"shell\",\"Id\":\"sh\"}]}]}}" },
        { "LlmBatchChatOperation", "{\"Request\":{\"ModelId\":\"m\",\"Messages\":[{\"Role\":\"tool\",\"Content\":\"\",\"ToolResults\":[{\"type\":\"shell\",\"ToolCallId\":\"sh\",\"Output\":[{\"Outcome\":{\"type\":\"unknown\"}}]}]}]}}" },
        { "LlmBatchImageOperation", "{\"Request\":" + ImageRequest + "}" },
        { "LlmBatchVideoGenerationOperation", "{}" }, { "LlmBatchVideoEditOperation", "{\"Request\":null}" },
        { "LlmBatchVideoExtensionOperation", "{}" },
        { "LlmBatchRequest", "{\"CustomId\":\"c\"}" }, { "LlmBatchRequest", "{\"CustomId\":null,\"Operation\":{\"type\":\"chat\",\"Request\":" + ChatRequest + "}}" },
        { "LlmBatchRequestMetadata", "{}" },
        { "LlmBatchPage", "{}" }, { "LlmBatchPage", "{\"Items\":null}" },
        { "LlmBatchRequestPage", "{\"Items\":[null]}" },
        { "LlmBatchResult", "{\"type\":\"unknown\",\"CustomId\":\"c\"}" },
        { "LlmBatchChatResult", "{\"Result\":" + ChatResult + "}" },
        { "LlmBatchImageResult", "{\"CustomId\":\"c\",\"Result\":null}" },
        { "LlmBatchVideoResult", "{\"CustomId\":\"c\",\"Result\":" + VideoSnapshot + "}" },
        { "LlmBatchFailure", "{\"CustomId\":\"c\",\"Error\":null}" },
        { "LlmBatchResultPage", "{\"Items\":null}" },
        { "LlmDeferredCompletion", "{\"RequestId\":\"r\"}" },
        { "LlmDeferredResult", "{\"type\":\"completed\"}" },
        { "LlmDeferredPending", "{\"Result\":" + ChatResult + "}" },
        { "LlmDeferredCompleted", "{\"Result\":null}" },
        { "LlmMediaMaterializationOptions", "{}" },
        { "LlmMediaMaterializationOptions", "{\"MaximumInlineImageBytes\":0,\"MaximumInlineResultBytes\":1}" },
        { "MaterializedVideoResult", "{}" }, { "MaterializedVideoResult", "{\"Resource\":null}" },
        { "LlmBatchImageOperation", "{\"Request\":{\"ModelId\":\"m\",\"Prompt\":\"p\",\"References\":[{\"type\":\"binary\",\"Data\":null}]},\"Workflow\":" + Workflow + "}" }
    };

    [Theory, MemberData(nameof(InvalidShapes))]
    public void MissingNullMalformedAndUnknownFactsRefuse(string getter, string json)
    {
        Exception error = Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize(json, Metadata(typeof(ConnectResourcesJson), getter)));
        Assert.True(error is JsonException or ArgumentException or NotSupportedException, error.GetType().Name);
    }

    [Fact]
    public void ActualAuthorityRemainsBorrowedButPortableJsonRefusesIt()
    {
        var resources = new ResourceAccess(); var authorization = new Authorization();
        var request = new LlmChatRequest
        {
            ModelId = "m",
            Resources = resources,
            HostedTools = [new LlmRemoteMcpTool("https://example.invalid/mcp", "mcp", Authorization: authorization)]
        };
        var operation = new LlmBatchChatOperation(request);
        Assert.Same(request, operation.Request); Assert.Same(resources, operation.Request.Resources);
        Assert.Same(authorization, ((LlmRemoteMcpTool)operation.Request.HostedTools[0]).Authorization);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(operation, ConnectResourcesJson.LlmBatchChatOperation));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(operation with { Request = request with { Resources = null } }, ConnectResourcesJson.LlmBatchChatOperation));
        Assert.Equal(0, resources.Calls); Assert.Equal(0, authorization.Calls);
        Assert.DoesNotContain("example.invalid", operation.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeClientsReadAndWriteRefuseWithoutEffects() => ExerciseRuntimeRefusal();

    [Fact]
    public void JoinedCallerAuthorityCannotBecomePortableThroughResourcesOptions() => ExerciseCallerAuthorityRefusal();

    internal static void ExerciseCallerAuthorityRefusal()
    {
        JsonTypeInfo<ResourcesRuntimeEnvelope> metadata = RuntimeMetadata();
        var caller = new CallerAuthority();
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { CallerServices = new() }, metadata));
        var services = new LlmAgentCallerServices
        {
            Permissions = caller,
            TextFileReader = caller,
            TextFileWriter = caller,
            Terminals = caller,
            ShellExecutor = caller
        };
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { CallerServices = services }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { Permissions = caller }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { TextFileReader = caller }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { TextFileWriter = caller }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { Terminals = caller }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { Terminal = caller }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { ShellExecutor = caller }, metadata));
        foreach (string field in new[] { "CallerServices", "Permissions", "TextFileReader", "TextFileWriter", "Terminals", "Terminal", "ShellExecutor" })
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"" + field + "\":{}}", metadata));
        Assert.NotNull(JsonSerializer.Deserialize("{\"CallerServices\":null,\"Permissions\":null,\"TextFileReader\":null,\"TextFileWriter\":null,\"Terminals\":null,\"Terminal\":null,\"ShellExecutor\":null}", metadata));
        Assert.Equal(0, caller.Calls);
    }

    [Fact]
    public void UnknownNestedProviderClrVariantsRefuseInsteadOfDroppingFields()
    {
        var request = new VideoGenerationRequest { ModelId = "m", ProviderOptions = new UnknownVideoOptions() };
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new LlmBatchVideoGenerationOperation(request), ConnectResourcesJson.LlmBatchVideoGenerationOperation));
        var chat = new LlmChatRequest { ModelId = "m", HostedTools = [new UnknownHostedTool()] };
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new LlmBatchChatOperation(chat), ConnectResourcesJson.LlmBatchChatOperation));
    }

    internal static void ExerciseRuntimeRefusal()
    {
        var resources = new ResourceAccess(); var authorization = new Authorization(); var batch = new BatchClient();
        var read = new ReadHandle(); var write = new WriteHandle();
        JsonTypeInfo<ResourcesRuntimeEnvelope> metadata = RuntimeMetadata();
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { Resources = resources }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { Authorization = authorization }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { Client = batch }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { Read = read }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourcesRuntimeEnvelope { Write = write }, metadata));
        foreach (string field in new[] { "Resources", "Read", "Write", "Authorization", "Client" })
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"" + field + "\":{}}", metadata));
        Assert.NotNull(JsonSerializer.Deserialize("{\"Resources\":null,\"Read\":null,\"Write\":null,\"Authorization\":null,\"Client\":null}", metadata));
        Assert.Equal(0, resources.Calls); Assert.Equal(0, authorization.Calls); Assert.Equal(0, batch.Calls);
        Assert.Equal(0, read.Calls); Assert.Equal(0, write.Calls);
    }

    public static JsonTypeInfo<ResourcesRuntimeEnvelope> RuntimeMetadata()
    {
        var options = new JsonSerializerOptions(ConnectResourcesJson.LlmBatch.Options) { TypeInfoResolver = ResourcesRuntimeContext.Default };
        options.MakeReadOnly(); return options.GetTypeInfo<ResourcesRuntimeEnvelope>();
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignWholeGraphRetires(string getter, string json)
    {
        WeakReference generation = ExerciseForeign(getter, json);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(generation.IsAlive);
    }

    [Theory]
    [InlineData("LlmBatch", "{}")]
    [InlineData("LlmBatchRequestPage", "{\"Items\":[null]}")]
    [InlineData("LlmBatchOperation", "{\"type\":\"unknown\"}")]
    [InlineData("LlmMediaMaterializationOptions", "{}")]
    public void ActualForeignMalformedFactsRefuseAndRetire(string getter, string json)
    {
        WeakReference generation = ExerciseForeign(getter, json, expectRefusal: true);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(generation.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(string getter, string json, bool expectRefusal = false)
    {
        var generation = new ForeignGeneration(Path.GetDirectoryName(typeof(ConnectResourcesJson).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly[] loaded = new[] { typeof(InferenceJson).Assembly, typeof(ConnectLlmJson).Assembly,
                typeof(ConnectImageJson).Assembly, typeof(ConnectVideoJson).Assembly,
                typeof(ConnectResourcesJson).Assembly, typeof(ResourcesJsonTests).Assembly }
                .Select(assembly => generation.LoadFromAssemblyPath(assembly.Location)).ToArray();
            Assembly[] actual = [typeof(InferenceJson).Assembly, typeof(ConnectLlmJson).Assembly,
                typeof(ConnectImageJson).Assembly, typeof(ConnectVideoJson).Assembly,
                typeof(ConnectResourcesJson).Assembly, typeof(ResourcesJsonTests).Assembly];
            for (int index = 0; index < loaded.Length; index++)
            {
                Assert.NotSame(actual[index], loaded[index]);
                Assert.Equal(actual[index].ManifestModule.ModuleVersionId, loaded[index].ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(loaded[index]));
            }
            JsonTypeInfo metadata = Metadata(loaded[4].GetType(typeof(ConnectResourcesJson).FullName!, true)!, getter);
            Assert.Same(loaded[4], metadata.Type.Assembly);
            Assert.Same(loaded[4], metadata.Options.TypeInfoResolver!.GetType().Assembly);
            object? value = null;
            if (expectRefusal)
            {
                Exception error = Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize(json, metadata));
                Assert.True(error is JsonException or ArgumentException or NotSupportedException, error.GetType().Name);
            }
            else value = RoundTrip(metadata, json);
            if (value is not null) AssertNestedProvenance(value, generation);
            Type binaryType = loaded[0].GetType(typeof(BinaryMediaSource).FullName!, true)!;
            Assert.Same(loaded[0], metadata.Options.GetTypeInfo(binaryType).Properties.Single(property => property.Name == "Data").CustomConverter!.GetType().Assembly);
            Type authorityType = loaded[0].GetType(typeof(IInferenceResourceAccess).FullName!, true)!;
            Assert.Same(loaded[0], metadata.Options.Converters.Single(converter => converter.CanConvert(authorityType)).GetType().Assembly);
            Type clientType = loaded[4].GetType(typeof(ILlmBatchClient).FullName!, true)!;
            Assert.Same(loaded[4], metadata.Options.Converters.Single(converter => converter.CanConvert(clientType)).GetType().Assembly);
            Type fixture = loaded[5].GetType(typeof(ResourcesJsonTests).FullName!, true)!;
            fixture.GetMethod(nameof(ExerciseRuntimeRefusal), BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            fixture.GetMethod(nameof(ExerciseCallerAuthorityRefusal), BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            foreach (Type type in new[] { typeof(LlmAgentCallerServices), typeof(ILlmAgentPermissionSelector),
                typeof(ILlmAgentTextFileReader), typeof(ILlmAgentTextFileWriter), typeof(ILlmAgentTerminalFactory),
                typeof(ILlmAgentTerminal), typeof(ILlmAgentShellExecutor) })
            {
                Type foreign = loaded[1].GetType(type.FullName!, true)!;
                Assert.Same(loaded[1], metadata.Options.Converters.Single(converter => converter.CanConvert(foreign)).GetType().Assembly);
            }
            var runtime = (JsonTypeInfo)fixture.GetMethod(nameof(RuntimeMetadata))!.Invoke(null, null)!;
            Assert.Same(loaded[5], runtime.Type.Assembly);
            foreach ((Type type, Type owner, string property) in new[]
            {
                (typeof(LlmChatRequest), typeof(ConnectLlmJson), "ChatRequest"),
                (typeof(ImageWorkflow), typeof(ConnectImageJson), "ImageWorkflow"),
                (typeof(VideoRequest), typeof(ConnectVideoJson), "VideoRequest")
            })
            {
                Assembly assembly = loaded.Single(item => item.GetName().Name == type.Assembly.GetName().Name);
                Assembly ownerAssembly = loaded.Single(item => item.GetName().Name == owner.Assembly.GetName().Name);
                JsonTypeInfo familyMetadata = Metadata(ownerAssembly.GetType(owner.FullName!, true)!, property);
                Assert.Same(assembly, familyMetadata.Type.Assembly);
                Assert.Same(ownerAssembly, familyMetadata.Options.TypeInfoResolver!.GetType().Assembly);
            }
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static void AssertNestedProvenance(object value, AssemblyLoadContext generation)
    {
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(value.GetType().Assembly));
        foreach (PropertyInfo property in value.GetType().GetProperties())
        {
            object? nested = property.GetValue(value);
            if (nested is System.Collections.IEnumerable sequence && nested is not string)
            {
                foreach (object? item in sequence)
                    if (item?.GetType().Namespace?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) == true) AssertNestedProvenance(item, generation);
            }
            else if (nested?.GetType().Namespace?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) == true)
                AssertNestedProvenance(nested, generation);
        }
    }

    private static JsonTypeInfo Metadata(Type owner, string getter) => (JsonTypeInfo)owner.GetProperty(getter)!.GetValue(null)!;
    private static object? RoundTrip(JsonTypeInfo metadata, string json)
    {
        object? value = JsonSerializer.Deserialize(json, metadata);
        string serialized = JsonSerializer.Serialize(value, metadata);
        using JsonDocument expected = JsonDocument.Parse(json); using JsonDocument actual = JsonDocument.Parse(serialized);
        Preserve(expected.RootElement, actual.RootElement);
        Assert.Equal(serialized, JsonSerializer.Serialize(JsonSerializer.Deserialize(serialized, metadata), metadata));
        return value;
    }
    private static void Preserve(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in expected.EnumerateObject())
            { Assert.True(actual.TryGetProperty(property.Name, out JsonElement child)); Preserve(property.Value, child); }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (int index = 0; index < expected.GetArrayLength(); index++) Preserve(expected[index], actual[index]); }
        else Assert.True(JsonElement.DeepEquals(expected, actual), expected.GetRawText());
    }

    private sealed class ForeignGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll"); return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
    private sealed class ResourceAccess : IInferenceResourceAccess
    {
        public int Calls { get; private set; }
        private InvalidOperationException Effect() { Calls++; return new InvalidOperationException("Authority must not execute during serialization."); }
        public string WorkspaceId => throw Effect();
        public string SessionId => throw Effect();
        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw Effect();
        public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw Effect();
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => throw Effect();
        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => throw Effect();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw Effect();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw Effect();
        public override string ToString() => throw Effect();
    }
    private sealed class Authorization : IProviderAuthorization
    {
        public int Calls { get; private set; }
        private InvalidOperationException Effect() { Calls++; return new InvalidOperationException(); }
        public ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default) => throw Effect();
        public override string ToString() => throw Effect();
    }
    private sealed record UnknownVideoOptions : VideoProviderOptions;
    private sealed record UnknownHostedTool : LlmHostedTool
    {
        public override LlmHostedToolKind Kind => LlmHostedToolKind.WebSearch;
    }
    private sealed class ReadHandle : IInferenceResourceReadHandle
    {
        public int Calls { get; private set; }
        private InvalidOperationException Effect() { Calls++; return new InvalidOperationException(); }
        public InferenceResourceDescriptor Resource => throw Effect();
        public Stream Content => throw Effect();
        public ValueTask DisposeAsync() => throw Effect();
        public override string ToString() => throw Effect();
    }
    private sealed class WriteHandle : IInferenceResourceWriteHandle
    {
        public int Calls { get; private set; }
        private InvalidOperationException Effect() { Calls++; return new InvalidOperationException(); }
        public InferenceResourceReference Reference => throw Effect();
        public Stream Content => throw Effect();
        public Task<InferenceResourceCommitResult> CommitAsync(long bytesWritten, InferenceResourceCommitOptions? options = null,
            CancellationToken cancellationToken = default) => throw Effect();
        public ValueTask DisposeAsync() => throw Effect();
        public override string ToString() => throw Effect();
    }
    private sealed class BatchClient : ILlmBatchClient
    {
        public int Calls { get; private set; }
        private InvalidOperationException Effect() { Calls++; return new InvalidOperationException(); }
        public Task<LlmBatch> CreateAsync(string connectionId, string name, CancellationToken cancellationToken = default) => throw Effect();
        public Task AddRequestsAsync(string connectionId, LlmBatch batch, IReadOnlyList<LlmBatchRequest> requests, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmBatch> GetAsync(string connectionId, LlmBatch batch, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmBatchPage> ListAsync(string connectionId, LlmResourcePageRequest page, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmBatchRequestPage> ListRequestsAsync(string connectionId, LlmBatch batch, LlmResourcePageRequest page, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmBatchResultPage> GetResultsAsync(string connectionId, LlmBatch batch, LlmResourcePageRequest page, LlmMediaMaterializationOptions materialization, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmBatch> CancelAsync(string connectionId, LlmBatch batch, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmDeferredCompletion> SubmitDeferredAsync(string connectionId, LlmChatRequest request, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmDeferredResult> GetDeferredAsync(string connectionId, LlmDeferredCompletion completion, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmBatch> CreateFromRequestsAsync(string connectionId, string name, IReadOnlyList<LlmBatchRequest> requests, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default) => throw Effect();
        public override string ToString() => throw Effect();
    }
    private sealed class CallerAuthority : ILlmAgentPermissionSelector, ILlmAgentTextFileReader, ILlmAgentTextFileWriter,
        ILlmAgentTerminalFactory, ILlmAgentTerminal, ILlmAgentShellExecutor
    {
        public int Calls { get; private set; }
        private InvalidOperationException Effect() { Calls++; return new InvalidOperationException(); }
        public Task<LlmAgentPermissionResult> RequestAsync(LlmAgentPermissionRequest request, CancellationToken cancellationToken = default) => throw Effect();
        public Task<string> ReadAsync(LlmAgentTextFileReadRequest request, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmAgentTextFileWriteResult> WriteAsync(LlmAgentTextFileWriteRequest request, CancellationToken cancellationToken = default) => throw Effect();
        public Task<ILlmAgentTerminal> CreateAsync(LlmAgentTerminalCreateRequest request, CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmAgentTerminalOutput> GetOutputAsync(CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmAgentTerminalExit> WaitForExitAsync(CancellationToken cancellationToken = default) => throw Effect();
        public Task KillAsync(CancellationToken cancellationToken = default) => throw Effect();
        public Task<LlmShellCallResult> ExecuteAsync(LlmAgentShellRequest request, CancellationToken cancellationToken = default) => throw Effect();
        public ValueTask DisposeAsync() => throw Effect();
        public override string ToString() => throw Effect();
    }
}

public sealed record ResourcesRuntimeEnvelope
{
    public IInferenceResourceAccess? Resources { get; init; }
    public IInferenceResourceReadHandle? Read { get; init; }
    public IInferenceResourceWriteHandle? Write { get; init; }
    public IProviderAuthorization? Authorization { get; init; }
    public ILlmBatchClient? Client { get; init; }
    public LlmAgentCallerServices? CallerServices { get; init; }
    public ILlmAgentPermissionSelector? Permissions { get; init; }
    public ILlmAgentTextFileReader? TextFileReader { get; init; }
    public ILlmAgentTextFileWriter? TextFileWriter { get; init; }
    public ILlmAgentTerminalFactory? Terminals { get; init; }
    public ILlmAgentTerminal? Terminal { get; init; }
    public ILlmAgentShellExecutor? ShellExecutor { get; init; }
}
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ResourcesRuntimeEnvelope))]
internal sealed partial class ResourcesRuntimeContext : JsonSerializerContext;
