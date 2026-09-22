namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The host interaction required to complete one provider setup step.</summary>
public enum LlmSetupAction
{
    EnterApiKey,
    AuthorizeDeviceCode,
    AuthorizeBrowserCallback,
    VerifyExternalCli,
    ConfigureAwsProfile,
    SelectModel,
    TestConnection
}

/// <summary>One input described by a provider-authored setup step.</summary>
public sealed record LlmSetupInput
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string HelpText { get; init; } = string.Empty;

    public bool IsRequired { get; init; }

    public bool IsSecret { get; init; }
}

/// <summary>One ordered, provider-authored step in a guided connection workflow.</summary>
public sealed record LlmSetupStep
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Instructions { get; init; }

    public required LlmSetupAction Action { get; init; }

    public IReadOnlyList<LlmSetupInput> Inputs { get; init; } = [];

    public string CompletionRequirement { get; init; } = string.Empty;

    public string Troubleshooting { get; init; } = string.Empty;

    public string WindowsInstructions { get; init; } = string.Empty;

    public string MacOsInstructions { get; init; } = string.Empty;

    public string LinuxInstructions { get; init; } = string.Empty;

    public string PlatformInstructions => OperatingSystem.IsWindows()
        ? WindowsInstructions
        : OperatingSystem.IsMacOS()
            ? MacOsInstructions
            : OperatingSystem.IsLinux()
                ? LinuxInstructions
                : string.Empty;

    public bool HasPlatformInstructions => !string.IsNullOrWhiteSpace(PlatformInstructions);

    public bool HasCompletionRequirement => !string.IsNullOrWhiteSpace(CompletionRequirement);

    public bool HasTroubleshooting => !string.IsNullOrWhiteSpace(Troubleshooting);
}

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

    public required IReadOnlyList<LlmSetupStep> SetupSteps { get; init; }
}
