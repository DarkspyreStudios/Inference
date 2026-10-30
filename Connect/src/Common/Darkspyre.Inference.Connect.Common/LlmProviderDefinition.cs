using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Provider-authored metadata used by hosts to render a consistent connection wizard without
/// duplicating provider names, defaults, or setup instructions.
/// </summary>
public sealed record LlmProviderDefinition
{
    /// <summary>The provider identity.</summary>
    public required string Id { get; init; }

    /// <summary>The provider name shown to people.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Text that explains the provider in the connection wizard.</summary>
    public required string Description { get; init; }

    /// <summary>The transport a connection to this provider uses.</summary>
    public required LlmTransport Transport { get; init; }

    /// <summary>Default API base URL or executable name for this provider.</summary>
    public required string DefaultEndpoint { get; init; }

    /// <summary>The model a new connection starts with; empty for none.</summary>
    public string DefaultModelId { get; init; } = string.Empty;

    /// <summary>Controls whether a host should offer this provider in its primary guided setup.</summary>
    public bool IsGuidedSetupOption { get; init; } = true;

    /// <summary>The provider's position in provider lists; lower values come first.</summary>
    public int DisplayOrder { get; init; }

    /// <summary>The setup instructions the connection wizard shows, in order: the connection step, then the model step.</summary>
    public required IReadOnlyList<SetupStep> SetupSteps { get; init; }

    /// <summary>The provider's logo, or null when it has none of its own.</summary>
    public SetupIcon? Icon { get; init; }

    /// <summary>The add-connection group the provider is listed under.</summary>
    public SetupProviderGroup Group { get; init; } = SetupProviderGroup.More;

    /// <summary>
    /// The provider's own folder in the user's home folder, such as <c>.claude</c>, relative to it; null when the provider
    /// keeps none. A host lets sandboxed commands read it.
    /// </summary>
    public string? HomeFolder { get; init; }

    /// <summary>What the connection's endpoint holds. Default <see cref="LlmProviderEndpointKind.Url"/>.</summary>
    public LlmProviderEndpointKind EndpointKind { get; init; }

    /// <summary>How the settings' Check action confirms the connection. Default <see cref="LlmProviderAuthenticationCheck.ModelList"/>.</summary>
    public LlmProviderAuthenticationCheck AuthenticationCheck { get; init; }

    /// <summary>The sign-in action the settings offer, or null when the provider has none. When
    /// <see cref="AuthenticationCheck"/> is <see cref="LlmProviderAuthenticationCheck.ExternalTool"/> and the sign-in is
    /// required, the connection runs only on this sign-in's own token: the command-line tool's own login does not count
    /// for Save, for the Check, or for listing models.</summary>
    public LlmProviderSignIn? SignIn { get; init; }

    /// <summary>True when the settings show an optional API key field.</summary>
    public bool AcceptsApiKey { get; init; }

    /// <summary>The Service tiers checkbox the settings show, or null when they show none.</summary>
    public LlmProviderServiceTiers? ServiceTiers { get; init; }

    /// <summary>True when the settings show the Context lengths rows.</summary>
    public bool OffersContextLengths { get; init; }

    /// <summary>The provider as a text-generation setup definition for the inference catalog, with its icon and group.</summary>
    public InferenceSetupDefinition SetupDefinition => new(
        new(Id, DisplayName, InferenceImplementationKind.Connect, InferenceTask.TextGeneration),
        Description, DisplayOrder, IsGuidedSetupOption)
    { Icon = Icon, Group = Group };
}
