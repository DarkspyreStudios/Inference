using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.Resources.Tests;

public sealed class ClosedResourceEnumJsonTests
{
    private static JsonTypeInfo Metadata => ConnectResourcesJson.LlmBatchFailure;
    private static LlmBatchFailure Envelope(InferenceResourceOperationFailure failure)
    {
        var error = new InferenceResourceFailure(InferenceFailureCode.ExecutionFailed, null, "safe", failure);
        return new LlmBatchFailure("id", error);
    }

    [Theory]
    [InlineData("Phase")]
    [InlineData("CommitOutcome")]
    [InlineData("SelectionOutcome")]
    public void ActualNestedFailureReadRefusesUndefined(string property)
    {
        string json = JsonSerializer.Serialize(Envelope(new(InferenceResourceOperationPhase.OpenRead, null, InferenceResourceCommitOutcome.NotAttempted)), Metadata);
        JsonNode root = JsonNode.Parse(json)!;
        root["Error"]!["Resource"]![property] = 99;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(root.ToJsonString(), Metadata));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ActualNestedFailureWriteRefusesUndefined(int field)
    {
        var failure = new InferenceResourceOperationFailure(field == 0 ? (InferenceResourceOperationPhase)99 : InferenceResourceOperationPhase.Read,
            null, field == 1 ? (InferenceResourceCommitOutcome)99 : InferenceResourceCommitOutcome.NotAttempted)
        { SelectionOutcome = field == 2 ? (InferenceResourceCommitOutcome)99 : InferenceResourceCommitOutcome.Unknown };
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Envelope(failure), Metadata));
    }

    [Fact]
    public void DefinedFailureFactsAndActualCommonConverterOwnershipRemainExact()
    {
        var failure = new InferenceResourceOperationFailure(InferenceResourceOperationPhase.Commit, null, InferenceResourceCommitOutcome.Unknown, 0)
        { SelectionOutcome = InferenceResourceCommitOutcome.NotAttempted };
        string json = JsonSerializer.Serialize(Envelope(failure), Metadata);
        object restored = JsonSerializer.Deserialize(json, Metadata)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(JsonSerializer.Serialize(restored, Metadata))));
        foreach (Type type in new[] { typeof(InferenceResourceOperationPhase), typeof(InferenceResourceCommitOutcome) })
            Assert.Same(typeof(InferenceJson).Assembly, Metadata.Options.GetConverter(type).GetType().Assembly);
    }
}
