using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Common.Tests;

public sealed class HostedMetadataTests
{
    public static TheoryData<string, string> Roots => new()
    {
        { "LlmHostedToolPhase", "2" },
        { "LlmImageGenerationAction", "1" },
        { "LlmHostedTool", "{\"type\":\"code-execution\"}" },
        { "LlmWebSearchTool", "{\"AllowedDomains\":[],\"ExcludedDomains\":null,\"IncludeImageUnderstanding\":true,\"IncludeImageSearch\":false}" },
        { "LlmCodeExecutionTool", "{}" },
        { "LlmImageGenerationTool", "{\"Action\":1}" },
        { "LlmDocumentSearchTool", "{\"CollectionIds\":[\"collection\"],\"MaxResults\":1}" },
        { "LlmRemoteMcpTool", "{\"ServerUrl\":\"https://example.test/mcp\",\"ServerLabel\":\"tools\",\"ServerDescription\":null,\"AllowedTools\":[],\"Authorization\":null,\"Headers\":{\"x-test\":\"exact\"},\"DeferLoading\":true}" },
        { "LlmHostedToolOutput", "{\"type\":\"search\",\"Sources\":[],\"Resources\":null}" },
        { "LlmHostedSearchSource", "{\"Url\":\"https://example.test/source\",\"Title\":null}" },
        { "LlmHostedSearchOutput", "{\"Sources\":null,\"Resources\":null}" },
        { "LlmHostedCodeOutput", "{\"Code\":null,\"Logs\":[\"first\",\"second\"],\"ImageUrls\":[],\"Resources\":null}" },
        { "LlmHostedDocumentMatch", "{\"FileId\":\"file\",\"FileName\":null,\"Score\":0,\"Text\":null}" },
        { "LlmHostedDocumentSearchOutput", "{\"Results\":[],\"Resources\":null}" },
        { "LlmHostedMcpOutput", "{\"Arguments\":{},\"Output\":null,\"Error\":{\"message\":\"actual error\"},\"Resources\":null}" },
        { "LlmHostedImageOutput", "{\"Data\":\"AAEC\",\"MediaType\":\"image/png\",\"Resources\":null}" },
        { "LlmHostedToolCall", "{\"CallId\":\"call\",\"Kind\":0,\"Name\":\"search\",\"Status\":\"future-status\",\"Input\":null,\"Output\":null}" },
        { "LlmHostedToolCallStartedEvent", "{\"CallId\":\"call\",\"Kind\":0,\"Name\":\"search\"}" },
        { "LlmHostedToolCallProgressEvent", "{\"CallId\":\"call\",\"Phase\":0}" },
        { "LlmHostedToolCallCompletedEvent", "{\"Call\":{\"CallId\":\"call\",\"Kind\":0,\"Name\":\"search\",\"Status\":\"done\",\"Input\":null,\"Output\":null}}" },
        { "LlmCitation", "{\"Url\":\"https://example.test/source\",\"Title\":null,\"StartIndex\":0,\"EndIndex\":null}" },
        { "LlmCitationEvent", "{\"Citation\":{\"Url\":\"https://example.test/source\",\"Title\":null,\"StartIndex\":null,\"EndIndex\":0}}" }
    };

    [Theory]
    [MemberData(nameof(Roots))]
    public void ExactCommonHostedMetadataGetterClosesItsGraph(string name, string json)
    {
        PropertyInfo? getter = typeof(InferenceJson).GetProperty(name, BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(getter);
        var metadata = Assert.IsAssignableFrom<JsonTypeInfo>(getter.GetValue(null));
        Assert.Equal("Darkspyre.Inference.Common.LLM." + name, metadata.Type.FullName);
        Assert.Same(typeof(InferenceJson).Assembly, metadata.Options.TypeInfoResolver!.GetType().Assembly);
        object value = JsonSerializer.Deserialize(json, metadata)!;
        using JsonDocument expected = JsonDocument.Parse(json);
        using JsonDocument actual = JsonDocument.Parse(JsonSerializer.Serialize(value, metadata));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
    }

    [Fact]
    public void RequestAndCapabilitiesExposeHostedFieldsAndResultsExposeCitations()
    {
        Assert.NotNull(typeof(LlmInferenceRequest).GetProperty("HostedTools"));
        Assert.NotNull(typeof(LlmInferenceRequest).GetProperty("MaxHostedTurns"));
        Assert.NotNull(typeof(LlmCapabilities).GetProperty("HostedTools"));
        Assert.NotNull(typeof(LlmInferenceResult).GetProperty("Citations"));
    }
}
