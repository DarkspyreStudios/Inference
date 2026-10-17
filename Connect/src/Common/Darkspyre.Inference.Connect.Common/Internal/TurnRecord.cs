using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// One turn's entries in the shape the Host's native transcript uses: the input as one user message, each model
/// step as one assistant message with its calls, followed by one user message with their results in call order.
/// Entry ids are <c>{turnId}-{n}</c>, counted from 0 within the turn.
/// </summary>
internal sealed class TurnRecord(string turnId, bool recordsCalls)
{
    private int _next = 1;
    private Step? _step;

    public string InputId { get; } = $"{turnId}-0";

    /// <summary>The turn recorded a model step.</summary>
    public bool HasSteps { get; private set; }

    /// <summary>The next arrival starts a new step: the current one is in a compacted history.</summary>
    public void Close() => _step = null;

    /// <summary>The id of a steer's user message. The model's next output starts a new step after it.</summary>
    public string Steer()
    {
        _step = null;
        return NextId();
    }

    /// <summary>The entries an event changed.</summary>
    public IReadOnlyList<LlmAgentContextEntry> Apply(LlmAgentEvent item)
    {
        switch (item)
        {
            case LlmAgentMessageCompletedEvent { Text.Length: > 0 } message:
                {
                    Step step = Open();
                    step.Text.Add(message.Text);
                    return step.Entries(turnId);
                }
            case LlmAgentToolEvent { Status: "inProgress" } call when recordsCalls:
                {
                    Step step = Open();
                    if (step.Calls.Any(existing => existing.Id == call.ItemId)) return [];
                    step.ResultsId ??= NextId();
                    step.Calls.Add(new LlmToolCall(call.ItemId, call.Tool, call.Arguments.GetRawText()));
                    return step.Entries(turnId);
                }
            case LlmAgentToolEvent ended when recordsCalls && _step is { } current
                && current.Calls.Any(existing => existing.Id == ended.ItemId):
                current.Results[ended.ItemId] = ended.Output;
                current.HasResult = true;
                return current.Entries(turnId);
            default:
                return [];
        }
    }

    /// <summary>The current step; an arrival after one of its results starts the next.</summary>
    private Step Open()
    {
        if (_step is null || _step.HasResult) _step = new Step(NextId());
        HasSteps = true;
        return _step;
    }

    private string NextId() => $"{turnId}-{_next++}";

    private sealed class Step(string assistantId)
    {
        public List<string> Text { get; } = [];
        public List<LlmToolCall> Calls { get; } = [];
        public Dictionary<string, LlmToolOutput?> Results { get; } = new(StringComparer.Ordinal);
        public string? ResultsId { get; set; }
        public bool HasResult { get; set; }

        public IReadOnlyList<LlmAgentContextEntry> Entries(string turnId)
        {
            var assistant = new LlmAgentContextEntry(assistantId, turnId,
                new LlmChatMessage("assistant", string.Join('\n', Text)) { ToolCalls = [.. Calls] });
            if (ResultsId is null) return [assistant];
            return [assistant, new LlmAgentContextEntry(ResultsId, turnId, new LlmChatMessage("user", string.Empty)
            {
                ToolResults = [.. Calls.Select(call => new LlmToolResult(call.Id,
                    Results.TryGetValue(call.Id, out LlmToolOutput? output) && output is not null ? output
                        : LlmToolOutput.FromText("No result was delivered for this call.", isError: true)))]
            })];
        }
    }
}
