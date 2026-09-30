namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Answers whether a host override supplies a capability value, so an adapter keeps it instead of its own.</summary>
internal static class LlmCapabilityOverrideLookup
{
    /// <summary>True when the connection override or the model's override sets the selected value.</summary>
    public static bool Overrides(LlmConnection connection, string? modelId, Func<LlmCapabilityOverrides, object?> selector) =>
        (connection.CapabilityOverrides is { } overrides && selector(overrides) is not null)
        || (modelId is not null
            && connection.ModelCapabilityOverrides.TryGetValue(modelId, out LlmCapabilityOverrides? modelOverrides)
            && selector(modelOverrides) is not null);
}
