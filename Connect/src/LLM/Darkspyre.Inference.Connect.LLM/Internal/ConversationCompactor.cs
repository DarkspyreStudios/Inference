namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// One request's context limits: the effective window, the output reserve, the usable window (window minus reserve),
/// the compaction trigger and the compaction target, all in tokens.
/// </summary>
internal sealed record CompactionLimits(long Window, long Reserve, long UsableWindow, long TriggerTokens, long TargetTokens)
{
    /// <summary>Compaction starts when a request measures at or above this share of the usable window.</summary>
    private const double TriggerRatio = 0.85;

    // 0.80 keeps a compaction from landing at the trigger, where the next send would compact again; 0.05 leaves room
    // for the current turn and a summary in a 200K window.
    private const double MinimumTargetRatio = 0.05;
    private const double MaximumTargetRatio = 0.80;

    // The Host's default output reserve. Without a reserve no trigger exists.
    private const long DefaultReserve = 8000;

    /// <summary>
    /// The one limit rule for every provider. The window is the smallest known value of the listed window, the learned
    /// limit and the policy cap; null when none is known. The reserve is the policy's, else the model's output limit,
    /// else 8,000.
    /// </summary>
    internal static CompactionLimits? For(long? listedWindow, long? learnedLimit, long? modelMaxOutput,
        LlmAgentContextPolicy policy)
    {
        long? window = null;
        foreach (long? known in (ReadOnlySpan<long?>)[listedWindow, learnedLimit, policy.MaxContextTokens])
            if (known is long value && (window is null || value < window)) window = value;
        if (window is not long limit) return null;
        long reserve = policy.ReservedOutputTokens ?? modelMaxOutput ?? DefaultReserve;
        long usable = Math.Max(0, limit - reserve);
        double ratio = Math.Clamp(policy.CompactionTargetRatio, MinimumTargetRatio, MaximumTargetRatio);
        return new(limit, reserve, usable, (long)Math.Floor(TriggerRatio * usable), (long)Math.Floor(ratio * usable));
    }
}
