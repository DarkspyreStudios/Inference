using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Provider-authored metadata used by hosts to render a consistent connection wizard without
/// duplicating provider names, defaults, or setup instructions.
/// </summary>
public sealed record LlmProviderDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    public required LlmTransport Transport { get; init; }

    /// <summary>Default API base URL or executable name for this provider.</summary>
    public required string DefaultEndpoint { get; init; }

    public string DefaultModelId { get; init; } = string.Empty;

    /// <summary>Controls whether a host should offer this provider in its primary guided setup.</summary>
    public bool IsGuidedSetupOption { get; init; } = true;

    public int DisplayOrder { get; init; }

    public required IReadOnlyList<SetupStep> SetupSteps { get; init; }

    public InferenceSetupDefinition SetupDefinition => new(
        new(Id, DisplayName, InferenceImplementationKind.Connect, InferenceTask.TextGeneration),
        Description, DisplayOrder, IsGuidedSetupOption);
}
