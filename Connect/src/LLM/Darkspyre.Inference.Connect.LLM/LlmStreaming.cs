namespace Darkspyre.Inference.Connect.LLM;

public abstract record LlmStreamEvent;

public sealed record LlmTextDeltaEvent(string Delta) : LlmStreamEvent;

/// <summary>Provider-exposed reasoning text only; never encrypted or private response-state bytes.</summary>
public sealed record LlmReasoningDeltaEvent(string Delta, bool IsSummary) : LlmStreamEvent;

public sealed record LlmRefusalDeltaEvent(string Delta) : LlmStreamEvent;

public sealed record LlmActivityEvent(string Activity) : LlmStreamEvent;

public sealed record LlmToolCallStartedEvent(string ToolCallId, string Name, int Index) : LlmStreamEvent;

public sealed record LlmToolCallArgumentsDeltaEvent(string ToolCallId, string Delta) : LlmStreamEvent;

public sealed record LlmToolCallCompletedEvent(LlmToolCall ToolCall) : LlmStreamEvent;

public sealed record LlmCompletionEvent(LlmChatResult Result) : LlmStreamEvent;
