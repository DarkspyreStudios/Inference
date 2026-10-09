using Darkspyre.Inference.Common;
namespace Darkspyre.Inference.Common.Setup;

public sealed record InferenceSetupDefinition(
    InferenceImplementationDescriptor Implementation,
    string Description,
    int DisplayOrder = 0,
    bool IsGuidedSetupOption = true,
    InferenceSupport SupportsCreate = InferenceSupport.Supported,
    InferenceSupport SupportsEdit = InferenceSupport.Supported)
{
    public IReadOnlyList<string> Prerequisites { get; init; } = [];
}

public enum SetupIssueSeverity { Warning, Error }

public sealed record SetupIssue(
    string Code, string Message, string? FieldId = null, string? StepId = null,
    SetupIssueSeverity Severity = SetupIssueSeverity.Error);

public sealed record SetupValidation(IReadOnlyList<SetupIssue> Issues)
{
    public static SetupValidation Valid { get; } = new([]);
    public bool IsValid => Issues.All(issue => issue.Severity != SetupIssueSeverity.Error);
}

public enum SetupEditState { Applied, Invalid }

public sealed record SetupEditResult(SetupEditState State, IReadOnlyList<SetupIssue> Issues)
{
    public static SetupEditResult Applied { get; } = new(SetupEditState.Applied, []);
}

public enum SetupCommitState { Committed, Conflict, Invalid, Cancelled, Failed, Uncertain }

public sealed record SetupCommitResult(
    SetupCommitState State, string? Version = null, IReadOnlyList<SetupIssue>? Issues = null);

public enum SetupChangeIntent { Create, Edit }

/// <summary>The host compares this version over configuration and protected credentials atomically.</summary>
public sealed record SetupConfigurationIdentity(SetupChangeIntent Intent, string Id, string? ExpectedVersion)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        if (!Enum.IsDefined(Intent)
            || (Intent == SetupChangeIntent.Create && ExpectedVersion is not null)
            || (Intent == SetupChangeIntent.Edit && string.IsNullOrWhiteSpace(ExpectedVersion)))
        {
            throw new ArgumentException("Create requires absence; edit requires the saved version.");
        }
    }
}

public interface ISetupCommitTarget<in TChanges>
{
    /// <summary>
    /// Publishes configuration and secrets atomically. Once invoked, report the actual outcome;
    /// cancellation is not proof that nothing was committed. Never silently upsert an edit.
    /// </summary>
    Task<SetupCommitResult> CommitAsync(TChanges changes, CancellationToken cancellationToken = default);
}

public sealed record SetupObservation(
    bool IsApplicable, bool? Value = null, DateTimeOffset? ObservedAt = null,
    bool IsStale = false, string? Reason = null)
{
    public static SetupObservation Unknown { get; } = new(true);
    public static SetupObservation NotApplicable { get; } = new(false);
    public static SetupObservation Known(bool value, string? reason = null) =>
        new(true, value, DateTimeOffset.UtcNow, Reason: reason);
}

public sealed record SetupStatus
{
    public SetupObservation Configured { get; init; } = SetupObservation.Unknown;
    public SetupObservation Authenticated { get; init; } = SetupObservation.NotApplicable;
    public SetupObservation Installed { get; init; } = SetupObservation.NotApplicable;
    public SetupObservation Verified { get; init; } = SetupObservation.Unknown;
    public SetupObservation Loaded { get; init; } = SetupObservation.NotApplicable;
    public SetupObservation Ready { get; init; } = SetupObservation.Unknown;
}

[Flags]
public enum SetupSideEffect
{
    None = 0,
    Network = 1,
    PotentialBilling = 2,
    BrowserInteraction = 4,
    ExternalCli = 8,
    Download = 16,
    LocalVerification = 32,
    ModelLoad = 64,
    ModelUnload = 128,
    TerminalStop = 256
}

public enum SetupActionPurpose
{
    Authenticate, ContinueAuthentication, CancelAuthentication, CheckPrerequisites,
    DiscoverModels, Test, CheckReadiness, Install, Verify, Load, Unload, Stop
}

public sealed record SetupActionDescriptor(
    string Id, string DisplayName, SetupActionPurpose Purpose, SetupSideEffect SideEffects,
    bool IsEnabled = true, string? UnavailableReason = null);

public sealed record SetupProgress(string Stage, string? Message = null, long? Completed = null, long? Total = null);

public enum SetupActionState { Completed, NeedsInteraction, Failed, Cancelled, Unavailable }

public abstract record SetupInteraction;

/// <summary>A device sign-in in progress. Its Continue and Cancel actions run through the settings manager.</summary>
public sealed record SetupDeviceAuthorization(
    Uri VerificationUri, string UserCode, DateTimeOffset? ExpiresAt,
    SetupActionDescriptor Continue, SetupActionDescriptor Cancel) : SetupInteraction;

/// <summary>A browser sign-in in progress. The callback field is local; Continue and Cancel run through the settings manager.</summary>
public sealed record SetupBrowserAuthorization(
    Uri AuthorizationUri, SetupTextField Callback, SetupActionDescriptor Continue,
    SetupActionDescriptor Cancel, DateTimeOffset? ExpiresAt = null) : SetupInteraction;

public sealed record SetupExternalPrerequisite(string Instructions, SetupActionDescriptor Recheck) : SetupInteraction;

public sealed record SetupActionResult(
    SetupActionState State, IReadOnlyList<SetupIssue>? Issues = null,
    SetupInteraction? Interaction = null, bool IsRetryable = false)
{
    public static SetupActionResult Completed { get; } = new(SetupActionState.Completed);
}

public sealed record SetupSection(string Id, string Title, IReadOnlyList<SetupField> Fields, bool IsAdvanced = false);

public sealed record SetupStep(string Id, string Title, string Instructions, IReadOnlyList<SetupSection> Sections)
{
    public SetupStep() : this(string.Empty, string.Empty, string.Empty, []) { }

    public string CompletionRequirement { get; init; } = string.Empty;
    public string Troubleshooting { get; init; } = string.Empty;
    public string WindowsInstructions { get; init; } = string.Empty;
    public string MacOsInstructions { get; init; } = string.Empty;
    public string LinuxInstructions { get; init; } = string.Empty;
    public string PlatformInstructions => OperatingSystem.IsWindows() ? WindowsInstructions
        : OperatingSystem.IsMacOS() ? MacOsInstructions : OperatingSystem.IsLinux() ? LinuxInstructions : string.Empty;
    public bool HasPlatformInstructions => !string.IsNullOrWhiteSpace(PlatformInstructions);
    public bool HasCompletionRequirement => !string.IsNullOrWhiteSpace(CompletionRequirement);
    public bool HasTroubleshooting => !string.IsNullOrWhiteSpace(Troubleshooting);
}
