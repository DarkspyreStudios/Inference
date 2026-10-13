using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Private adapter evidence consumed by the Connect facade, never forwarded to its caller.</summary>
internal sealed record AccountUsageObservedEvent(LlmAccountUsageSnapshot Snapshot) : LlmStreamEvent;
