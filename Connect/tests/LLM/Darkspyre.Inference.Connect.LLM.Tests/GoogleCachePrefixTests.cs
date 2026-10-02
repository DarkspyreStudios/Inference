using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
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
