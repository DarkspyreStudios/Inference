using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

public sealed record LlmAgentHostedToolCallStartedEvent(string ThreadId, string TurnId, string RequestId,
    string CallId, LlmHostedToolKind Kind, string Name) : LlmAgentEvent(ThreadId, TurnId);
public sealed record LlmAgentHostedToolCallProgressEvent(string ThreadId, string TurnId, string RequestId,
    string CallId, LlmHostedToolPhase Phase) : LlmAgentEvent(ThreadId, TurnId);
public sealed record LlmAgentHostedToolCallCompletedEvent(string ThreadId, string TurnId, string RequestId,
    LlmHostedToolCall Call) : LlmAgentEvent(ThreadId, TurnId);
public sealed record LlmAgentCitationEvent(string ThreadId, string TurnId, string RequestId,
    LlmCitation Citation) : LlmAgentEvent(ThreadId, TurnId);
public sealed record LlmAgentCompactedEvent(string ThreadId, string TurnId, string RequestId,
    long ContextRevision, int? DroppedMessageCount, LlmUsage? Usage) : LlmAgentEvent(ThreadId, TurnId);
