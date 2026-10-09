using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class ManagedAgentPolicy
{
    public static LlmAgentCapabilities Capabilities { get; } = new()
    {
        ContextOwnership = LlmAgentOwnership.Host,
        ContextImport = LlmAgentContextImport.Structured,
        SupportsContextCapture = InferenceSupport.Supported,
        SupportsContextReplacement = InferenceSupport.Supported,
        SupportsContextStatus = InferenceSupport.Supported,
        SupportsCompactionHandoff = InferenceSupport.Supported,
        CompactionOwnership = LlmAgentOwnership.Host,
        Steering = LlmAgentSteering.SafeBoundary,
        SupportsInterrupt = InferenceSupport.Supported,
        ToolExecutionOwnership = LlmAgentToolExecutionOwnership.Host,
        // The managed runtime sends tool output through Direct Codex Responses.
        RichContentSupported = RichContentRoutes.Responses
    };

    public static void Validate(LlmAgentExecutionOptions options, LlmModel? model)
    {
        var policy = options.ContextPolicy;
        if (options.MaxModelIterations <= 0 || options.MaxConcurrentTools <= 0
            || !Enum.IsDefined(options.ToolExecutionMode) || !double.IsFinite(policy.CompactionThresholdRatio)
            || policy.CompactionThresholdRatio <= 0 || policy.CompactionThresholdRatio > 1
            || policy.SafetyMarginTokens < 0 || policy.ReservedOutputTokens is <= 0
            || policy.MaxContextTokens is <= 0
            || (policy.MaxContextTokens is long cap && model?.MaxContextTokens is long hard && cap > hard))
        {
            throw new ArgumentException("Invalid managed execution policy or context cap above the provider limit.");
        }
    }

    /// <summary>
    /// Retains Codex's projected-input approximation and the Claude CLI's estimate despite omission metadata: neither sends
    /// an omitted attachment's bytes. Other routes require available bytes.
    /// </summary>
    public static ContextEstimate? Estimate(ILlmContextEstimator estimator, LlmConnection connection,
        LlmChatRequest request, bool hasOmittedAttachments)
    {
        ContextEstimate? used = estimator.EstimateInputTokens(connection, request);
        return hasOmittedAttachments && connection.Transport is not (LlmTransport.OpenAiCodex or LlmTransport.AnthropicClaudeCli)
            ? null : used;
    }

    public static LlmAgentContextStatus Status(
        string modelId, long revision, ContextEstimate? used, LlmAgentContextPolicy policy, LlmModel? model)
    {
        var accounting = Account(modelId, used, policy, model);
        return new()
        {
            ModelId = modelId,
            ContextRevision = revision,
            MaxContextTokens = accounting.MaxContextTokens,
            UsedContextTokens = accounting.UsedContextTokens,
            ReservedOutputTokens = accounting.ReservedOutputTokens,
            AvailableInputTokens = accounting.AvailableInputTokens,
            NeedsCompaction = accounting.NeedsCompaction,
            Source = accounting.Source
        };
    }

    public static LlmRequestAccounting Account(
        string modelId, ContextEstimate? estimate, LlmAgentContextPolicy policy, LlmModel? model)
    {
        long? used = estimate?.Tokens;
        Validate(new() { ContextPolicy = policy }, model);
        long? maximum = policy.MaxContextTokens ?? model?.MaxContextTokens;
        long? reserve = policy.ReservedOutputTokens ?? model?.MaxOutputTokens;
        long? budget = maximum is long limit && reserve is long output
            ? Math.Max(0, limit - Math.Min(limit, output) - Math.Min(limit, policy.SafetyMarginTokens)) : null;
        return new()
        {
            ModelId = modelId,
            IsSupported = true,
            Diagnostic = used is null ? "Input occupancy is unknown for this projection." : null,
            ModelMaxContextTokens = model?.MaxContextTokens,
            ModelMaxOutputTokens = model?.MaxOutputTokens,
            SafetyMarginTokens = policy.SafetyMarginTokens,
            InputBudgetTokens = budget,
            MaxContextTokens = maximum,
            UsedContextTokens = used,
            ReservedOutputTokens = reserve,
            AvailableInputTokens = budget is long available && used is long occupied ? Math.Max(0, available - occupied) : null,
            NeedsCompaction = budget is long input && used is long count
                ? count >= Math.Floor(input * policy.CompactionThresholdRatio) : null,
            Source = estimate is not { } known ? LlmContextMeasurementSource.Unknown
                : known.IncludesProviderReport ? LlmContextMeasurementSource.Mixed : LlmContextMeasurementSource.Estimated
        };
    }
}
