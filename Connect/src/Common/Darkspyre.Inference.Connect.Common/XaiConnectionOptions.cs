namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The xAI API region that serves a connection's requests.</summary>
public enum XaiRegion
{
    /// <summary>The global endpoint, api.x.ai.</summary>
    Global,
    /// <summary>The United States endpoint, us.api.x.ai.</summary>
    UnitedStates
}

/// <summary>The xAI service tier that requests are billed and scheduled under.</summary>
public enum XaiServiceTier
{
    /// <summary>The standard tier.</summary>
    Default,
    /// <summary>The priority tier, sent as service_tier "priority".</summary>
    Priority
}

/// <summary>Non-secret xAI routing and billing choices. Authentication remains in LlmCredential.</summary>
public sealed record XaiConnectionOptions
{
    /// <summary>The region whose endpoint serves requests.</summary>
    public XaiRegion Region { get; init; }
    /// <summary>The service tier sent with requests.</summary>
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
