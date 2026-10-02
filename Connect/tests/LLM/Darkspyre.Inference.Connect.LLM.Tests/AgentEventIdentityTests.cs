using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AgentEventIdentityTests
{
    [Fact]
    public void TurnEventsShareOneGuardedIdentityThroughBaseAndConcreteViews()
    {
        var initial = new LlmAgentTextDeltaEvent("thread", "turn", "item", "observed");
        LlmAgentEvent typedAsBase = initial;
        Assert.Equal(initial.TurnId, typedAsBase.TurnId);
        var changed = initial with { TurnId = "revised" };
        Assert.Equal("revised", ((LlmAgentEvent)changed).TurnId);
        Assert.Equal("turn", initial.TurnId);
        LlmAgentEvent changedThroughBase = typedAsBase with { TurnId = "base-revised" };
        Assert.Equal("base-revised", Assert.IsType<LlmAgentTextDeltaEvent>(changedThroughBase).TurnId);
        Assert.Throws<ArgumentNullException>(() => initial with { TurnId = null });
        Assert.Throws<ArgumentNullException>(() => typedAsBase with { TurnId = null });
        Assert.Throws<ArgumentException>(() => typedAsBase with { TurnId = "" });
        Assert.Throws<ArgumentNullException>(() => new LlmAgentTurnStartedEvent("thread", null!));
        Assert.Throws<ArgumentNullException>(() => new LlmAgentUsageEvent("thread", null!, "request", null, null));
        (string thread, string? turn) = changedThroughBase;
        Assert.Equal("thread", thread); Assert.Equal("base-revised", turn);
    }

    [Fact]
    public void ConcreteTurnMetadataRejectsMissingNullAndEmptyIdentity()
    {
        const string valid = "{\"ThreadId\":\"thread\",\"TurnId\":\"turn\",\"RequestId\":\"request\",\"CallId\":\"call\",\"Kind\":0,\"Name\":\"search\"}";
        var value = JsonSerializer.Deserialize(valid, ConnectLlmJson.LlmAgentHostedToolCallStartedEvent)!;
        Assert.Equal("turn", ((LlmAgentEvent)value).TurnId);
        Assert.Equal("turn", value.TurnId);
        Assert.Contains("\"TurnId\":\"turn\"", JsonSerializer.Serialize(value, ConnectLlmJson.LlmAgentHostedToolCallStartedEvent), StringComparison.Ordinal);
        Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(valid.Replace("\"TurnId\":\"turn\",", "", StringComparison.Ordinal), ConnectLlmJson.LlmAgentHostedToolCallStartedEvent)));
        Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(valid.Replace("\"TurnId\":\"turn\"", "\"TurnId\":null", StringComparison.Ordinal), ConnectLlmJson.LlmAgentHostedToolCallStartedEvent)));
        Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(valid.Replace("\"TurnId\":\"turn\"", "\"TurnId\":\"\"", StringComparison.Ordinal), ConnectLlmJson.LlmAgentHostedToolCallStartedEvent)));
        Assert.Throws<ArgumentNullException>(() => value with { TurnId = null });
        LlmAgentEvent asBase = value;
        Assert.Throws<ArgumentNullException>(() => asBase with { TurnId = null });
    }

    [Fact]
    public void SelectionHintPreservesActualSessionOnlyIdentityAndObservedRevision()
    {
        var hint = new LlmAgentResourceSelectionChangedEvent("thread", null, 0);
        Assert.Null(hint.TurnId); Assert.Null(((LlmAgentEvent)hint).TurnId);
        string json = JsonSerializer.Serialize(hint, ConnectLlmJson.LlmAgentResourceSelectionChangedEvent);
        Assert.Contains("\"TurnId\":null", json, StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize(json, ConnectLlmJson.LlmAgentResourceSelectionChangedEvent)!;
        Assert.Equal(hint, restored);
        LlmAgentEvent concrete = hint;
        concrete = concrete with { TurnId = "actual-turn" };
        Assert.Equal("actual-turn", Assert.IsType<LlmAgentResourceSelectionChangedEvent>(concrete).TurnId);
        concrete = concrete with { TurnId = null };
        Assert.Null(Assert.IsType<LlmAgentResourceSelectionChangedEvent>(concrete).TurnId);
        Assert.Throws<ArgumentException>(() => hint with { TurnId = "" });
        Assert.Throws<ArgumentNullException>(() => hint with { ThreadId = null! });
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ThreadId\":\"thread\",\"TurnId\":null}", ConnectLlmJson.LlmAgentResourceSelectionChangedEvent));
        Assert.Null(new LlmAgentResourceOutputEvent("thread", "resource", "cursor").TurnId);
    }
}
