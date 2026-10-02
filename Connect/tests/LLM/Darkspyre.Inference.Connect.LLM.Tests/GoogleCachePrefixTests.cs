using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GoogleCachePrefixTests
{
    private const string Model = "gemini-3-flash-preview";
    private static GeminiModelProfile Profile => GeminiModelProfiles.Get(Model, null);
    private const string PrefixWire = """{"contents":[{"role":"user","parts":[{"text":"question"},{"inlineData":{"mimeType":"application/pdf","data":"QUI="}}]}],"systemInstruction":{"parts":[{"text":"instructions"},{"text":"system history"}]},"tools":[{"functionDeclarations":[{"name":"run","parametersJsonSchema":{"type":"object"}}]}],"toolConfig":{"functionCallingConfig":{"mode":"ANY","allowedFunctionNames":["run"]}}}""";

    [Theory]
    [InlineData(LlmTransport.GoogleGeminiApi)]
    [InlineData(LlmTransport.GoogleVertexAi)]
    public void CachePrefixHasCanonicalContentAndToolsWithoutGenerationSettings(LlmTransport transport)
    {
        var connection = Connection(transport);
        var request = CacheRequest();
        JsonObject prefix = GeminiRequest.BuildCachePrefix(connection, request, NoOutput, Profile);
        Assert.False(prefix.ContainsKey("generationConfig"));
        Assert.Equal(PrefixWire, prefix.ToJsonString());
        Assert.False(prefix.ContainsKey("cachedContent"));
        Assert.False(prefix.ContainsKey("model"));
    }

    [Theory]
    [InlineData(LlmTransport.GoogleGeminiApi)]
    [InlineData(LlmTransport.GoogleVertexAi)]
    public void EmptyCachePrefixDoesNotInventGenerationOrOptionalContent(LlmTransport transport)
    {
        JsonObject prefix = GeminiRequest.BuildCachePrefix(Connection(transport), new()
        { ModelId = Model, Correlation = new("op", "name") }, NoOutput, Profile);
        Assert.Equal("{\"contents\":[]}", prefix.ToJsonString());
    }

    [Theory]
    [InlineData(LlmTransport.GoogleGeminiApi)]
    [InlineData(LlmTransport.GoogleVertexAi)]
    public void ExistingGenerationBodyRetainsExactPropertyOrderAndBytes(LlmTransport transport)
    {
        LlmContextCacheCreate cache = CacheRequest();
        var request = new LlmChatRequest
        {
            ModelId = Model,
            Instructions = cache.Instructions,
            Messages = cache.Messages,
            Tools = cache.Tools,
            ToolChoice = cache.ToolChoice,
            MaxOutputTokens = 321,
            Temperature = 0.5,
            TopP = 0.75,
            TopK = 7,
            Seed = 0,
            PresencePenalty = 0,
            FrequencyPenalty = -0.5,
            StopSequences = ["end"]
        };
        const string generation = """{"candidateCount":1,"maxOutputTokens":321,"temperature":0.5,"topP":0.75,"topK":7,"seed":0,"presencePenalty":0,"frequencyPenalty":-0.5,"stopSequences":["end"]}""";
        string expected = PrefixWire.Replace("],\"systemInstruction\"", "],\"generationConfig\":" + generation + ",\"systemInstruction\"", StringComparison.Ordinal);
        Assert.Equal(expected, GeminiRequest.Build(Connection(transport), request, null, NoOutput, Profile, reasoningText: false).ToJsonString());
    }

    [Theory]
    [InlineData(LlmToolChoiceMode.Auto, "AUTO")]
    [InlineData(LlmToolChoiceMode.None, "NONE")]
    [InlineData(LlmToolChoiceMode.Required, "ANY")]
    [InlineData(LlmToolChoiceMode.Specific, "ANY")]
    public void ToolChoiceUsesTheSameCanonicalConfiguration(LlmToolChoiceMode mode, string wireMode)
    {
        var cache = CacheRequest() with { ToolChoice = new() { Mode = mode, Name = "run" } };
        var connection = Connection(LlmTransport.GoogleGeminiApi);
        JsonObject prefix = GeminiRequest.BuildCachePrefix(connection, cache, NoOutput, Profile);
        JsonObject generation = GeminiRequest.Build(connection, new()
        { ModelId = Model, Messages = cache.Messages, Tools = cache.Tools, ToolChoice = cache.ToolChoice }, null, NoOutput, Profile, false);
        Assert.True(JsonNode.DeepEquals(prefix["toolConfig"], generation["toolConfig"]));
        Assert.Equal(wireMode, prefix["toolConfig"]!["functionCallingConfig"]!["mode"]!.GetValue<string>());
        Assert.Equal(mode == LlmToolChoiceMode.Specific,
            prefix["toolConfig"]!["functionCallingConfig"]!.AsObject().ContainsKey("allowedFunctionNames"));
    }

    [Fact]
    public void CachePrefixOmitsRawGenerationOptionsReasoningAndStreamControls()
    {
        var connection = Connection(LlmTransport.GoogleGeminiApi) with
        { ProviderOptions = new Dictionary<string, JsonElement> { ["testGenerationControl"] = JsonElement.Parse("0.25") } };
        var cache = CacheRequest() with { ToolChoice = null, Instructions = " " };
        JsonObject prefix = GeminiRequest.BuildCachePrefix(connection, cache, NoOutput, Profile);
        Assert.Equal("{\"parts\":[{\"text\":\"system history\"}]}", prefix["systemInstruction"]!.ToJsonString());
        Assert.False(prefix.ContainsKey("generationConfig"));
        Assert.False(prefix.ContainsKey("toolConfig"));
        JsonObject generation = GeminiRequest.Build(connection, new()
        { ModelId = Model, Tools = cache.Tools, ReasoningLevel = LlmReasoningLevel.Low }, null, NoOutput, Profile, true, true);
        Assert.Equal(0.25, generation["generationConfig"]!["testGenerationControl"]!.GetValue<double>());
        Assert.NotNull(generation["generationConfig"]!["thinkingConfig"]);
        Assert.True(generation["toolConfig"]!["functionCallingConfig"]!["streamFunctionCallArguments"]!.GetValue<bool>());
    }

    [Fact]
    public void CachePrefixUsesTheExistingFrozenPrefixWithoutAnotherSnapshotOwner()
    {
        byte[] bytes = [65, 66];
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        List<LlmMessageAttachment> attachments = [new LlmAttachment
        { Kind = LlmBinaryKind.Document, FileName = "document.pdf", MediaType = "application/pdf", Data = bytes }];
        List<LlmChatMessage> messages = [new("user", "question") { Attachments = attachments }];
        List<LlmClientToolDefinition> tools = [new LlmToolDefinition { Name = "run", Description = "description", InputSchema = schema.RootElement }];
        var frozen = GoogleCacheData.Snapshot(CacheRequest() with
        { Messages = messages, Tools = tools, TimeToLive = TimeSpan.FromSeconds(1) }, DateTimeOffset.UtcNow);
        bytes[0] = 0;
        attachments.Clear();
        messages.Clear();
        tools.Clear();
        schema.Dispose();
        JsonObject prefix = GeminiRequest.BuildCachePrefix(Connection(LlmTransport.GoogleGeminiApi), frozen, NoOutput, Profile);
        Assert.Equal("QUI=", prefix["contents"]![0]!["parts"]![1]!["inlineData"]!["data"]!.GetValue<string>());
        Assert.Equal("object", prefix["tools"]![0]!["functionDeclarations"]![0]!["parametersJsonSchema"]!["type"]!.GetValue<string>());
        Assert.Equal("description", prefix["tools"]![0]!["functionDeclarations"]![0]!["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(LlmTransport.GoogleGeminiApi)]
    [InlineData(LlmTransport.GoogleVertexAi)]
    public void SignedReplayAndMultimodalToolResultsShareGenerationCodec(LlmTransport transport)
    {
        var connection = Connection(transport) with
        { ResolvedContinuationScope = "resolved", ContinuationCodec = new GeminiContinuationCodec("owner", "route") };
        JsonArray retained = JsonNode.Parse("""[{"functionCall":{"id":"native","name":"run","args":{}},"connectCallId":"visible","thoughtSignature":"signed"}]""")!.AsArray();
        var assistant = new LlmChatMessage("assistant", "")
        {
            ToolCalls = [new LlmToolCall("visible", "run", "{}")],
            ContinuationState = ProviderContinuationStateCodec.Encode(connection, Model, retained)
        };
        var cache = CacheRequest() with
        {
            Messages = [assistant, new("tool", "") { ToolResults = [new LlmToolResult("visible", new()
            { Parts = [new LlmToolOutputText("result"), new LlmToolOutputBinary(LlmBinaryKind.Document, "application/pdf", new byte[] { 65, 66 }, "r.pdf")] })] }]
        };
        static ProjectedToolOutput Project(LlmToolOutput output) =>
            ToolOutputProjector.Project(output, RichContentRoutes.GeminiFunctionResponse, RichContentRoutes.GeminiFunctionResponse);
        JsonObject prefix = GeminiRequest.BuildCachePrefix(connection, cache, Project, Profile);
        JsonObject generation = GeminiRequest.Build(connection, new()
        { ModelId = Model, Messages = cache.Messages, Tools = cache.Tools, ToolChoice = cache.ToolChoice }, null, Project, Profile, false);
        Assert.True(JsonNode.DeepEquals(prefix["contents"], generation["contents"]));
        Assert.Equal("signed", prefix["contents"]![0]!["parts"]![0]!["thoughtSignature"]!.GetValue<string>());
        JsonNode response = prefix["contents"]![1]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("native", response["id"]!.GetValue<string>());
        Assert.Equal("result", response["response"]!["output"]!.GetValue<string>());
        Assert.Equal("QUI=", response["parts"]![0]!["inlineData"]!["data"]!.GetValue<string>());
        Assert.Equal(response["parts"]![0]!["inlineData"]!["displayName"]!.GetValue<string>(),
            response["response"]!["attachments"]![0]!["$ref"]!.GetValue<string>());
        Assert.DoesNotContain("connectCallId", prefix.ToJsonString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => GeminiRequest.BuildCachePrefix(connection, cache with
        { Messages = [assistant] }, Project, Profile));
        Assert.Throws<ArgumentException>(() => GeminiRequest.BuildCachePrefix(connection, cache with
        { Messages = [assistant with { ContinuationState = null }, cache.Messages[1]] }, Project, Profile));
    }

    [Fact]
    public void UnsupportedShellAndAnthropicConfigurationAreNotSilentlyDropped()
    {
        var connection = Connection(LlmTransport.GoogleGeminiApi);
        Assert.Throws<NotSupportedException>(() => GeminiRequest.BuildCachePrefix(connection,
            CacheRequest() with { Tools = [new LlmShellTool([])] }, NoOutput, Profile));
        Assert.Throws<NotSupportedException>(() => GeminiRequest.BuildCachePrefix(connection with
        { ProviderOptions = new Dictionary<string, JsonElement> { [AnthropicTools.OptionName] = JsonElement.Parse("{}") } }, CacheRequest(), NoOutput, Profile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnmaterializedReferencesAreNotDroppedOrGivenInventedInlineBytes(bool workspace)
    {
        LlmMessageAttachment reference = workspace
            ? new LlmResourceAttachment(new InferenceResourceReference("workspace", "resource"))
            : new LlmFileReference(new ProviderFileReference("native"));
        Assert.Throws<NotSupportedException>(() => GeminiRequest.BuildCachePrefix(Connection(LlmTransport.GoogleGeminiApi),
            CacheRequest() with { Messages = [new("user", "question") { Attachments = [reference] }] }, NoOutput, Profile));
    }

    [Fact]
    public void ActualForeignPrefixBuilderAndOwnedCacheGraphCollectTogether()
    {
        WeakReference[] roots = ExerciseForeign();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(GoogleCachePrefixTests).Assembly.Location)!);
        List<WeakReference> roots = [new(generation)];
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(GoogleCachePrefixTests).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            foreach ((Assembly source, Assembly actual) in new[]
                { (typeof(GoogleCachePrefixTests).Assembly, fixture), (typeof(ConnectLlmJson).Assembly, connect), (typeof(InferenceJson).Assembly, common) })
            {
                Assert.NotSame(source, actual);
                Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
                roots.Add(new(actual));
            }
            Type caller = fixture.GetType(typeof(GoogleCachePrefixTests).FullName!, true)!;
            MethodInfo method = caller.GetMethod(nameof(ExercisePrefixInOwningGeneration))!;
            roots.Add(new(caller));
            roots.Add(new(method));
            roots.AddRange((WeakReference[])method.Invoke(null, null)!);
        }
        finally { generation.Unload(); }
        return roots.ToArray();
    }

    public static WeakReference[] ExercisePrefixInOwningGeneration()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(GoogleCachePrefixTests).Assembly)!;
        if (!generation.IsCollectible || !ReferenceEquals(generation, AssemblyLoadContext.GetLoadContext(typeof(GeminiRequest).Assembly))
            || !ReferenceEquals(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmAttachment).Assembly)))
            throw new InvalidOperationException("Builder, fixture and Common must execute in the actual private generation.");
        var request = CacheRequest();
        var metadata = ConnectLlmJson.LlmContextCacheCreate;
        var commonMetadata = InferenceJson.LlmMessageAttachment;
        if (!ReferenceEquals(metadata.Type.Assembly, typeof(GeminiRequest).Assembly)
            || !ReferenceEquals(commonMetadata.Type.Assembly, typeof(LlmAttachment).Assembly))
            throw new InvalidOperationException("Actual owned metadata must use the private assemblies.");
        string json = JsonSerializer.Serialize(request, metadata);
        var restored = JsonSerializer.Deserialize(json, metadata)!;
        if (!ReferenceEquals(restored.Messages[1].Attachments[0].GetType().Assembly, typeof(LlmAttachment).Assembly))
            throw new InvalidOperationException("Restored nested Common data must not use Default contracts.");
        JsonObject body = GeminiRequest.BuildCachePrefix(Connection(LlmTransport.GoogleGeminiApi), restored, NoOutput, Profile);
        if (body.ToJsonString() != PrefixWire) throw new InvalidOperationException("Actual foreign builder changed canonical prefix bytes.");
        return [new(typeof(GeminiRequest)), new(metadata), new(commonMetadata), new(request), new(restored), new(body)];
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name is
            "Darkspyre.Inference.Common" or "Darkspyre.Inference.Connect.LLM" or "Darkspyre.Inference.Connect.LLM.Tests"
            ? LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null;
    }

    private static LlmConnection Connection(LlmTransport transport) => new()
    { Transport = transport, Endpoint = "https://example.invalid", Version = "version" };

    private static LlmContextCacheCreate CacheRequest() => new()
    {
        ModelId = Model,
        Correlation = new("op", "name"),
        Instructions = "instructions",
        Messages =
        [
            new("system", "system history"),
            new("user", "question")
            { Attachments = [new LlmAttachment { Kind = LlmBinaryKind.Document, FileName = "document.pdf", MediaType = "application/pdf", Data = new byte[] { 65, 66 } }] }
        ],
        Tools = [new LlmToolDefinition { Name = "run", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
        ToolChoice = new() { Mode = LlmToolChoiceMode.Specific, Name = "run" }
    };

    private static ProjectedToolOutput NoOutput(LlmToolOutput _) => throw new InvalidOperationException("No tool output is present.");
}
