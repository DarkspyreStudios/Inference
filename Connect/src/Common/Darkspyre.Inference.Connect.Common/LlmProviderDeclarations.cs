namespace Darkspyre.Inference.Connect.LLM;

/// <summary>What a connection's endpoint value holds, which decides the endpoint field the settings show.</summary>
public enum LlmProviderEndpointKind
{
    /// <summary>An HTTP(S) base URL the person enters.</summary>
    Url = 0,
    /// <summary>The provider's own address, <see cref="LlmProviderDefinition.DefaultEndpoint"/>. The settings show no
    /// endpoint field and replace a saved endpoint with the default.</summary>
    Fixed = 1,
    /// <summary>The name or absolute path of the provider's command-line tool, under More options. A blank value uses
    /// <see cref="LlmProviderDefinition.DefaultEndpoint"/>.</summary>
    Executable = 2,
    /// <summary>The id of an engine the host runs in its own process, chosen from the host's engines.</summary>
    Engine = 3
}

/// <summary>How the settings' Check action confirms a connection.</summary>
public enum LlmProviderAuthenticationCheck
{
    /// <summary>Lists the connection's models.</summary>
    ModelList = 0,
    /// <summary>Asks the provider's command-line tool whether it is installed and signed in. The provider's client
    /// implements the external authentication probe.</summary>
    ExternalTool = 1
}

/// <summary>How a provider's sign-in action gets its credential.</summary>
public enum LlmProviderSignInFlow
{
    /// <summary>A device code the person enters at the provider's page, through <see cref="ILlmDeviceLogin"/>.</summary>
    DeviceCode = 0,
    /// <summary>A browser page whose authorization code the person pastes back, through <see cref="ILlmBrowserLogin"/>.</summary>
    BrowserCode = 1
}

/// <summary>The sign-in action a provider's settings offer on the connection step.</summary>
/// <param name="ActionId">The settings action id. It must differ from check, test, continue-authorization and cancel-authorization.</param>
/// <param name="DisplayName">The button text.</param>
/// <param name="Flow">How the action gets its credential.</param>
public sealed record LlmProviderSignIn(string ActionId, string DisplayName, LlmProviderSignInFlow Flow)
{
    /// <summary>True when the settings cannot be saved until the connection signs in or keeps its saved credential.</summary>
    public bool IsRequired { get; init; }
}

/// <summary>The Service tiers checkbox a provider's settings show, and where it starts checked.</summary>
/// <param name="DefaultOnHost">The endpoint host on which the checkbox defaults to checked, compared ignoring case.</param>
/// <param name="HelpText">The checkbox's help text, which names this provider.</param>
public sealed record LlmProviderServiceTiers(string DefaultOnHost, string HelpText)
{
    /// <summary>True when <paramref name="endpoint"/> is an absolute URI whose host is <see cref="DefaultOnHost"/>.</summary>
    /// <param name="endpoint">The connection's endpoint.</param>
    public bool IsDefaultOn(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
        && string.Equals(uri.Host, DefaultOnHost, StringComparison.OrdinalIgnoreCase);
}
