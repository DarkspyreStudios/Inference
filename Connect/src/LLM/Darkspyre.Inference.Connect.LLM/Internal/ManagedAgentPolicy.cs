using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class ManagedAgentPolicy
{
    public static LlmAgentCapabilities Capabilities { get; } = new()
    {
        ContextImport = LlmAgentContextImport.Structured,
        SupportsContextCapture = InferenceSupport.Supported,
        SupportsContextReplacement = InferenceSupport.Supported,
        SupportsContextStatus = InferenceSupport.Supported,
        SupportsCompactionHandoff = InferenceSupport.Supported,
        SupportsCompactNow = InferenceSupport.Unsupported,
        SupportsSteering = InferenceSupport.Supported,
        SupportsInterrupt = InferenceSupport.Supported,
        // The managed runtime sends tool output through Direct Codex Responses.
        RichContentSupported = RichContentRoutes.Responses
    };

    /// <summary>
    /// MaxModelIterations ≤ 0 becomes 32 and MaxConcurrentTools ≤ 0 becomes 4 (the record defaults);
    /// ReservedOutputTokens ≤ 0 and MaxContextTokens ≤ 0 become null; a MaxContextTokens above the model limit
    /// becomes the model limit.
    /// </summary>
    internal static LlmAgentExecutionOptions Normalize(LlmAgentExecutionOptions options, LlmModel? model)
    {
        LlmAgentContextPolicy policy = options.ContextPolicy;
        long? cap = policy.MaxContextTokens is > 0 ? policy.MaxContextTokens : null;
        if (cap is long requested && model?.MaxContextTokens is long hard && requested > hard) cap = hard;
        return options with
        {
            MaxModelIterations = options.MaxModelIterations > 0 ? options.MaxModelIterations : 32,
            MaxConcurrentTools = options.MaxConcurrentTools > 0 ? options.MaxConcurrentTools : 4,
            ContextPolicy = policy with
            {
                ReservedOutputTokens = policy.ReservedOutputTokens is > 0 ? policy.ReservedOutputTokens : null,
                MaxContextTokens = cap
            }
        };
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

    public static LlmAgentContextStatus Status(string modelId, long revision, ContextEstimate? used,
        LlmAgentContextPolicy policy, LlmModel? model, long? learnedLimit)
    {
        var accounting = Account(modelId, used, policy, model, learnedLimit);
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

    /// <summary>The request's accounting under the one limit rule, <see cref="CompactionLimits.For"/>.</summary>
    public static LlmRequestAccounting Account(string modelId, ContextEstimate? estimate, LlmAgentContextPolicy policy,
        LlmModel? model, long? learnedLimit)
    {
        long? used = estimate?.Tokens;
        CompactionLimits? limits = CompactionLimits.For(model?.MaxContextTokens, learnedLimit, model?.MaxOutputTokens, policy);
        return new()
        {
            ModelId = modelId,
            IsSupported = true,
            Diagnostic = used is null ? "Input occupancy is unknown for this projection." : null,
            ModelMaxContextTokens = model?.MaxContextTokens,
            ModelMaxOutputTokens = model?.MaxOutputTokens,
            InputBudgetTokens = limits?.UsableWindow,
            MaxContextTokens = limits?.Window,
            UsedContextTokens = used,
            ReservedOutputTokens = limits?.Reserve,
            AvailableInputTokens = limits is { } known && used is long occupied ? Math.Max(0, known.UsableWindow - occupied) : null,
            NeedsCompaction = limits is { } window && used is long count ? count >= window.TriggerTokens : null,
            Source = estimate is not { } measured ? LlmContextMeasurementSource.Unknown
                : measured.IncludesProviderReport ? LlmContextMeasurementSource.Mixed : LlmContextMeasurementSource.Estimated
        };
    }
}
