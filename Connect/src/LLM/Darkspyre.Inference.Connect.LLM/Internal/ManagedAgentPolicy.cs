namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class ManagedAgentPolicy
{
    public static LlmAgentCapabilities Capabilities { get; } = new()
    {
        ContextOwnership = LlmAgentContextOwnership.Host,
        SupportsContextCapture = true,
        SupportsContextReplacement = true,
        SupportsContextStatus = true,
        SupportsCompactionHandoff = true,
        CompactionOwnership = LlmAgentCompactionOwnership.Host,
        Steering = LlmAgentSteering.SafeBoundary,
        SupportsInterrupt = true,
        ToolExecutionOwnership = LlmAgentToolExecutionOwnership.Host
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

    public static LlmAgentContextStatus Status(
        string modelId, long revision, long? used, LlmAgentContextPolicy policy, LlmModel? model)
    {
        long? maximum = policy.MaxContextTokens ?? model?.MaxContextTokens;
        long? reserve = policy.ReservedOutputTokens ?? model?.MaxOutputTokens;
        long? budget = maximum is long limit && reserve is long output
            ? Math.Max(0, limit - Math.Min(limit, output) - Math.Min(limit, policy.SafetyMarginTokens)) : null;
        return new()
        {
            ModelId = modelId,
            ContextRevision = revision,
            MaxContextTokens = maximum,
            UsedContextTokens = used,
            ReservedOutputTokens = reserve,
            AvailableInputTokens = budget is long available && used is long occupied ? Math.Max(0, available - occupied) : null,
            NeedsCompaction = budget is long input && used is long count
                ? count >= Math.Floor(input * policy.CompactionThresholdRatio) : null,
            IsEstimated = used is not null,
            Source = used is null ? LlmContextMeasurementSource.Unknown : LlmContextMeasurementSource.Estimated
        };
    }
}
