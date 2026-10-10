using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The final event of a Connect chat stream, carrying the complete result.</summary>
internal sealed record LlmChatCompletionEvent(LlmChatResult Result) : LlmStreamEvent;
