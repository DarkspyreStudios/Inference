namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Resolves explicit overrides and identifies values supplied by the host.</summary>
internal static class LlmCapabilityOverrideLookup
{
    public static LlmProviderCapabilities Apply(LlmProviderCapabilities defaults, LlmConnection connection,
        string? modelId, bool usageDeltasImplemented = false)
    {
        LlmProviderCapabilities capabilities = defaults.Apply(connection.CapabilityOverrides);
        if (modelId is not null && connection.ModelCapabilityOverrides.TryGetValue(modelId, out var modelOverrides))
        {
            capabilities = capabilities.Apply(modelOverrides);
        }
        if (capabilities.SupportsUsageDeltas && !defaults.SupportsUsageDeltas && !usageDeltasImplemented)
        {
            throw new NotSupportedException("This adapter does not implement running usage observations.");
        }
        if (capabilities.SupportsReasoningText && !defaults.SupportsReasoningText)
        {
            throw new NotSupportedException("This adapter does not implement public reasoning text.");
        }
        return capabilities;
    }

    /// <summary>True when the connection override or the model's override sets the selected value.</summary>
    public static bool Overrides(LlmConnection connection, string? modelId, Func<LlmCapabilityOverrides, object?> selector) =>
        (connection.CapabilityOverrides is { } overrides && selector(overrides) is not null)
        || (modelId is not null
            && connection.ModelCapabilityOverrides.TryGetValue(modelId, out LlmCapabilityOverrides? modelOverrides)
            && selector(modelOverrides) is not null);
}
