using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedRequiredFactsTests
{
    public static IEnumerable<object[]> MissingCases()
    {
        foreach ((string root, string[] fields) in new (string, string[])[]
        {
            ("LlmAgentHostedToolCallStartedEvent", ["ThreadId", "TurnId", "RequestId", "CallId", "Kind", "Name"]),
            ("LlmAgentHostedToolCallProgressEvent", ["ThreadId", "TurnId", "RequestId", "CallId", "Phase"]),
            ("LlmAgentHostedToolCallCompletedEvent", ["ThreadId", "TurnId", "RequestId", "Call"]),
            ("LlmAgentCitationEvent", ["ThreadId", "TurnId", "RequestId", "Citation"]),
            ("LlmAgentCompactedEvent", ["ThreadId", "TurnId", "RequestId", "ContextRevision"])
        })
            foreach (string field in fields) yield return [root, field];
    }

    [Theory]
    [MemberData(nameof(MissingCases))]
    public void MissingRequiredFactsRefuseAtOwnedMetadata(string root, string field)
    {
        var data = JsonNode.Parse(HostedContractTests.ForeignCases.Single(row => (string)row[0] == root)[1].ToString()!)!.AsObject();
        Assert.True(data.Remove(field));
        var metadata = (JsonTypeInfo)typeof(ConnectLlmJson).GetProperty(root)!.GetValue(null)!;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(data.ToJsonString(), metadata));
    }

    public static IEnumerable<object[]> NullCases() => MissingCases().Where(row => (string)row[1] is not ("Kind" or "Phase" or "ContextRevision"));

    [Theory]
    [MemberData(nameof(NullCases))]
    public void NullRequiredReferencesRefuseAtOwnedMetadata(string root, string field)
    {
        var data = JsonNode.Parse(HostedContractTests.ForeignCases.Single(row => (string)row[0] == root)[1].ToString()!)!.AsObject();
        data[field] = null;
        var metadata = (JsonTypeInfo)typeof(ConnectLlmJson).GetProperty(root)!.GetValue(null)!;
        Assert.Throws<ArgumentNullException>(() => JsonSerializer.Deserialize(data.ToJsonString(), metadata));
    }
}
