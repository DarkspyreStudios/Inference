namespace Darkspyre.Inference.Connect.LLM;

public enum XaiRegion
{
    Global,
    UnitedStates
}

public enum XaiServiceTier
{
    Default,
    Priority
}

/// <summary>Non-secret xAI routing and billing choices. Authentication remains in LlmCredential.</summary>
public sealed record XaiConnectionOptions
{
    public XaiRegion Region { get; init; }
    public XaiServiceTier ServiceTier { get; init; }

    /// <summary>Saved multi-agent count used only when a request supplies no provider controls.</summary>
    public XaiMultiAgentControls? MultiAgent { get; init; }

    /// <summary>The fixed API base URL for the selected region.</summary>
    public string GetEndpoint() => Region switch
    {
        XaiRegion.Global => "https://api.x.ai/v1",
        XaiRegion.UnitedStates => "https://us.api.x.ai/v1",
        _ => throw new ArgumentOutOfRangeException(nameof(Region), "Select an xAI region.")
    };
}
