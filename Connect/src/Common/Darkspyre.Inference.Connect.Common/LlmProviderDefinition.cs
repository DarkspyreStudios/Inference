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

    /// <summary>The setup instructions the connection wizard shows, in order.</summary>
    public required IReadOnlyList<SetupStep> SetupSteps { get; init; }

    /// <summary>The provider as a text-generation setup definition for the inference catalog.</summary>
    public InferenceSetupDefinition SetupDefinition => new(
        new(Id, DisplayName, InferenceImplementationKind.Connect, InferenceTask.TextGeneration),
        Description, DisplayOrder, IsGuidedSetupOption);
}
