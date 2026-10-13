namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The neutral processing tier a turn asks for. A provider maps it to the nearest tier the model offers;
/// a provider without tiers ignores it.</summary>
public enum LlmServiceTier
{
    /// <summary>The tier set on the account or project.</summary>
    Auto,
    /// <summary>Slower and cheaper processing.</summary>
    Economy,
    /// <summary>The provider's standard processing.</summary>
    Standard,
    /// <summary>Faster processing at a higher price.</summary>
    Fast
}
