using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Common.Tests;

public sealed class HostedRequiredFactsTests
{
    public static IEnumerable<object[]> MissingCases()
    {
        foreach ((string root, string[] fields) in new (string, string[])[]
        {
            ("LlmHostedToolCall", ["CallId", "Kind", "Name", "Status"]),
            ("LlmHostedToolCallStartedEvent", ["CallId", "Kind", "Name"]),
            ("LlmHostedToolCallProgressEvent", ["CallId", "Phase"]),
            ("LlmHostedToolCallCompletedEvent", ["Call"]),
            ("LlmHostedImageOutput", ["Data", "MediaType"]),
            ("LlmHostedSearchSource", ["Url"]),
            ("LlmHostedDocumentMatch", ["FileId"]),
            ("LlmCitation", ["Url"]),
            ("LlmCitationEvent", ["Citation"]),
            ("LlmDocumentSearchTool", ["CollectionIds"]),
            ("LlmRemoteMcpTool", ["ServerUrl", "ServerLabel"])
        })
            foreach (string field in fields) yield return [root, field];
    }

    [Theory]
    [MemberData(nameof(MissingCases))]
    public void MissingRequiredFactsRefuseAtOwnedMetadata(string root, string field)
    {
        var data = JsonNode.Parse(HostedMetadataTests.Roots.Single(row => (string)row[0] == root)[1].ToString()!)!.AsObject();
        Assert.True(data.Remove(field));
        var metadata = (JsonTypeInfo)typeof(InferenceJson).GetProperty(root)!.GetValue(null)!;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(data.ToJsonString(), metadata));
    }

    public static IEnumerable<object[]> NullCases() => MissingCases().Where(row => (string)row[1] is not ("Kind" or "Phase" or "Data"));

    [Theory]
    [MemberData(nameof(NullCases))]
    public void NullRequiredReferencesRefuseAtOwnedMetadata(string root, string field)
    {
        var data = JsonNode.Parse(HostedMetadataTests.Roots.Single(row => (string)row[0] == root)[1].ToString()!)!.AsObject();
        data[field] = null;
        var metadata = (JsonTypeInfo)typeof(InferenceJson).GetProperty(root)!.GetValue(null)!;
        Assert.Throws<ArgumentNullException>(() => JsonSerializer.Deserialize(data.ToJsonString(), metadata));
    }

    [Fact]
    public void NullImageBytesNeverBecomeAnObservedEmptyImage()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Data\":null,\"MediaType\":\"image/png\"}", InferenceJson.LlmHostedImageOutput));
    }

    [Theory]
    [InlineData("LlmHostedToolPhase")]
    [InlineData("LlmImageGenerationAction")]
    public void NewClosedEnumMetadataRefusesUndefinedValues(string root)
    {
        var metadata = (JsonTypeInfo)typeof(InferenceJson).GetProperty(root)!.GetValue(null)!;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", metadata));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Enum.ToObject(metadata.Type, 99), metadata));
        Assert.Equal("0", JsonSerializer.Serialize(Enum.ToObject(metadata.Type, 0), metadata));
    }

    [Fact]
    public void OptionalRequestControlsKeepTheirDefaultAndNullPolicies()
    {
        Assert.Equal(LlmImageGenerationAction.Auto, JsonSerializer.Deserialize("{}", InferenceJson.LlmImageGenerationTool)!.Action);
        var source = JsonSerializer.Deserialize("{\"Url\":\"actual\"}", InferenceJson.LlmHostedSearchSource)!;
        Assert.Null(source.Title);
        var call = JsonSerializer.Deserialize("{\"CallId\":\"actual\",\"Kind\":0,\"Name\":\"name\",\"Status\":\"future-status\"}", InferenceJson.LlmHostedToolCall)!;
        Assert.Null(call.Input); Assert.Null(call.Output);
    }
}
