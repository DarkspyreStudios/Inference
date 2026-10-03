using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal sealed record ProviderCompactionResult(
    LlmContinuationState ContinuationState,
    string RequestId,
    LlmUsage? Usage,
    int? DroppedMessageCount = null);
