using Darkspyre.Inference.Common;
namespace Darkspyre.Inference.Common.Setup;

/// <summary>Describes how an inference implementation takes part in setup: its description, ordering and whether it can be created and edited.</summary>
/// <param name="Implementation">The implementation this definition sets up.</param>
/// <param name="Description">A description of the implementation for the setup screen.</param>
/// <param name="DisplayOrder">The sort position among setup options. Lower values come first.</param>
/// <param name="IsGuidedSetupOption">Whether the implementation is offered in guided setup.</param>
/// <param name="SupportsCreate">Whether a new configuration can be created for the implementation.</param>
/// <param name="SupportsEdit">Whether an existing configuration can be edited.</param>
public sealed record InferenceSetupDefinition(
    InferenceImplementationDescriptor Implementation,
    string Description,
    int DisplayOrder = 0,
    bool IsGuidedSetupOption = true,
    InferenceSupport SupportsCreate = InferenceSupport.Supported,
    InferenceSupport SupportsEdit = InferenceSupport.Supported)
{
    /// <summary>Prerequisites the user must have in place before setup, as display text.</summary>
    public IReadOnlyList<string> Prerequisites { get; init; } = [];

    /// <summary>The provider's or model family's logo, or null when it has none of its own.</summary>
    public SetupIcon? Icon { get; init; }

    /// <summary>The add-connection group the implementation is listed under.</summary>
    public SetupProviderGroup Group { get; init; } = SetupProviderGroup.More;
}

/// <summary>How serious a setup issue is.</summary>
public enum SetupIssueSeverity
{
    /// <summary>The issue does not block the configuration.</summary>
    Warning,
    /// <summary>The issue makes the configuration invalid.</summary>
    Error
}

/// <summary>A problem found in a setup configuration or action.</summary>
/// <param name="Code">A stable code that identifies the kind of issue.</param>
/// <param name="Message">The message shown to the user.</param>
/// <param name="FieldId">The field the issue concerns, or null when it concerns no single field.</param>
/// <param name="StepId">The step the issue concerns, or null when it concerns no single step.</param>
/// <param name="Severity">How serious the issue is. Only errors make a configuration invalid.</param>
public sealed record SetupIssue(
    string Code, string Message, string? FieldId = null, string? StepId = null,
    SetupIssueSeverity Severity = SetupIssueSeverity.Error);

/// <summary>The result of validating a setup configuration.</summary>
/// <param name="Issues">The issues found.</param>
public sealed record SetupValidation(IReadOnlyList<SetupIssue> Issues)
{
    /// <summary>A validation result with no issues.</summary>
    public static SetupValidation Valid { get; } = new([]);
    /// <summary>True when no issue has error severity.</summary>
    public bool IsValid => Issues.All(issue => issue.Severity != SetupIssueSeverity.Error);
}

/// <summary>Whether a setup edit was applied.</summary>
public enum SetupEditState
{
    /// <summary>The edit was applied.</summary>
    Applied,
    /// <summary>The edit was rejected as invalid.</summary>
    Invalid
}

/// <summary>The result of applying an edit to a setup draft.</summary>
/// <param name="State">Whether the edit was applied.</param>
/// <param name="Issues">The issues that blocked or accompany the edit.</param>
public sealed record SetupEditResult(SetupEditState State, IReadOnlyList<SetupIssue> Issues)
{
    /// <summary>An applied edit result with no issues.</summary>
    public static SetupEditResult Applied { get; } = new(SetupEditState.Applied, []);
}

/// <summary>The outcome of committing a setup configuration.</summary>
public enum SetupCommitState
{
    /// <summary>The changes were saved.</summary>
    Committed,
    /// <summary>The saved state did not match the expected version, so nothing was saved.</summary>
    Conflict,
    /// <summary>The changes were invalid, so nothing was saved.</summary>
    Invalid,
    /// <summary>The commit was cancelled before anything was saved.</summary>
    Cancelled,
    /// <summary>The commit failed.</summary>
    Failed,
    /// <summary>It is not known whether the changes were saved.</summary>
    Uncertain
}

/// <summary>The result of committing a setup configuration.</summary>
/// <param name="State">The commit outcome.</param>
/// <param name="Version">The saved version after a commit, or null when nothing was saved.</param>
/// <param name="Issues">The issues that explain a failed commit, or null when there are none.</param>
public sealed record SetupCommitResult(
    SetupCommitState State, string? Version = null, IReadOnlyList<SetupIssue>? Issues = null);

/// <summary>Whether a setup commit creates a new configuration or edits an existing one.</summary>
public enum SetupChangeIntent
{
    /// <summary>Create a configuration that does not exist yet.</summary>
    Create,
    /// <summary>Edit an existing configuration at its saved version.</summary>
    Edit
}

/// <summary>The host compares this version over configuration and protected credentials atomically.</summary>
/// <param name="Intent">Whether the change creates or edits a configuration.</param>
/// <param name="Id">The configuration id.</param>
/// <param name="ExpectedVersion">The saved version an edit expects; null for a create.</param>
public sealed record SetupConfigurationIdentity(SetupChangeIntent Intent, string Id, string? ExpectedVersion)
{
    /// <summary>Throws <see cref="ArgumentException"/> when the id is blank, or when a create carries an expected version or an edit lacks one.</summary>
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

/// <summary>A target that saves setup changes of type <typeparamref name="TChanges"/>.</summary>
public interface ISetupCommitTarget<in TChanges>
{
    /// <summary>
    /// Publishes configuration and secrets atomically. Once invoked, report the actual outcome;
    /// cancellation is not proof that nothing was committed. Never silently upsert an edit.
    /// </summary>
    Task<SetupCommitResult> CommitAsync(TChanges changes, CancellationToken cancellationToken = default);
}

/// <summary>One observed setup fact, such as whether an implementation is configured or ready.</summary>
/// <param name="IsApplicable">Whether the fact applies to the implementation at all.</param>
/// <param name="Value">The observed value, or null when it is unknown.</param>
/// <param name="ObservedAt">When the value was observed, or null when it was never observed.</param>
/// <param name="IsStale">Whether the observation is out of date.</param>
/// <param name="Reason">An explanation of the value for the user, or null.</param>
public sealed record SetupObservation(
    bool IsApplicable, bool? Value = null, DateTimeOffset? ObservedAt = null,
    bool IsStale = false, string? Reason = null)
{
    /// <summary>An applicable fact whose value is unknown.</summary>
    public static SetupObservation Unknown { get; } = new(true);
    /// <summary>A fact that does not apply to the implementation.</summary>
    public static SetupObservation NotApplicable { get; } = new(false);
    /// <summary>Creates an applicable observation with a value observed now.</summary>
    public static SetupObservation Known(bool value, string? reason = null) =>
        new(true, value, DateTimeOffset.UtcNow, Reason: reason);
}

/// <summary>The setup facts observed for one configuration.</summary>
public sealed record SetupStatus
{
    /// <summary>Whether the configuration is saved and complete.</summary>
    public SetupObservation Configured { get; init; } = SetupObservation.Unknown;
    /// <summary>Whether the configuration has signed in to its provider.</summary>
    public SetupObservation Authenticated { get; init; } = SetupObservation.NotApplicable;
    /// <summary>Whether the required software or model files are installed.</summary>
    public SetupObservation Installed { get; init; } = SetupObservation.NotApplicable;
    /// <summary>Whether a check has confirmed the configuration works.</summary>
    public SetupObservation Verified { get; init; } = SetupObservation.Unknown;
    /// <summary>Whether the model is loaded.</summary>
    public SetupObservation Loaded { get; init; } = SetupObservation.NotApplicable;
    /// <summary>Whether the configuration can serve requests.</summary>
    public SetupObservation Ready { get; init; } = SetupObservation.Unknown;
}

/// <summary>The side effects a setup action can have, shown to the user before it runs.</summary>
[Flags]
public enum SetupSideEffect
{
    /// <summary>No side effects.</summary>
    None = 0,
    /// <summary>Makes network requests.</summary>
    Network = 1,
    /// <summary>May incur charges with the provider.</summary>
    PotentialBilling = 2,
    /// <summary>Opens a browser for the user.</summary>
    BrowserInteraction = 4,
    /// <summary>Runs an external command-line tool.</summary>
    ExternalCli = 8,
    /// <summary>Downloads files.</summary>
    Download = 16,
    /// <summary>Runs a local check.</summary>
    LocalVerification = 32,
    /// <summary>Loads a model.</summary>
    ModelLoad = 64,
    /// <summary>Unloads a model.</summary>
    ModelUnload = 128,
    /// <summary>Stops a running process.</summary>
    TerminalStop = 256,
    /// <summary>Deletes files from local storage.</summary>
    DeleteFiles = 512
}

/// <summary>What a setup action does.</summary>
public enum SetupActionPurpose
{
    /// <summary>Starts sign-in.</summary>
    Authenticate,
    /// <summary>Continues a sign-in in progress.</summary>
    ContinueAuthentication,
    /// <summary>Cancels a sign-in in progress.</summary>
    CancelAuthentication,
    /// <summary>Checks the prerequisites.</summary>
    CheckPrerequisites,
    /// <summary>Lists the models the configuration can use.</summary>
    DiscoverModels,
    /// <summary>Tests the configuration.</summary>
    Test,
    /// <summary>Checks whether the configuration can serve requests.</summary>
    CheckReadiness,
    /// <summary>Installs required software or files.</summary>
    Install,
    /// <summary>Removes installed software or files.</summary>
    Remove,
    /// <summary>Verifies the installation.</summary>
    Verify,
    /// <summary>Loads the model.</summary>
    Load,
    /// <summary>Unloads the model.</summary>
    Unload,
    /// <summary>Stops the running process.</summary>
    Stop
}

/// <summary>Describes a setup action the user can run.</summary>
/// <param name="Id">The action identifier.</param>
/// <param name="DisplayName">The name shown to the user.</param>
/// <param name="Purpose">What the action does.</param>
/// <param name="SideEffects">The side effects the action can have.</param>
/// <param name="IsEnabled">Whether the action can run now.</param>
/// <param name="UnavailableReason">Why the action cannot run, or null when it can.</param>
public sealed record SetupActionDescriptor(
    string Id, string DisplayName, SetupActionPurpose Purpose, SetupSideEffect SideEffects,
    bool IsEnabled = true, string? UnavailableReason = null);

/// <summary>Progress reported by a running setup action.</summary>
/// <param name="Stage">The current stage, as display text.</param>
/// <param name="Message">Detail for the current stage, or null.</param>
/// <param name="Completed">The units of work done, or null when unknown.</param>
/// <param name="Total">The total units of work, or null when unknown.</param>
public sealed record SetupProgress(string Stage, string? Message = null, long? Completed = null, long? Total = null);

/// <summary>The outcome of a setup action.</summary>
public enum SetupActionState
{
    /// <summary>The action finished.</summary>
    Completed,
    /// <summary>The action waits for the user to take a step.</summary>
    NeedsInteraction,
    /// <summary>The action failed.</summary>
    Failed,
    /// <summary>The action was cancelled.</summary>
    Cancelled,
    /// <summary>The action cannot run.</summary>
    Unavailable
}

/// <summary>Base type for a step the user must take before a setup action can continue.</summary>
public abstract record SetupInteraction;

/// <summary>A device sign-in in progress. Its Continue and Cancel actions run through the settings manager.</summary>
/// <param name="VerificationUri">The address where the user enters the code.</param>
/// <param name="UserCode">The code the user enters at the verification address.</param>
/// <param name="ExpiresAt">When the code expires, or null when unknown.</param>
/// <param name="Continue">The action that checks whether the sign-in finished.</param>
/// <param name="Cancel">The action that abandons the sign-in.</param>
public sealed record SetupDeviceAuthorization(
    Uri VerificationUri, string UserCode, DateTimeOffset? ExpiresAt,
    SetupActionDescriptor Continue, SetupActionDescriptor Cancel) : SetupInteraction;

/// <summary>A browser sign-in in progress. The callback field is local; Continue and Cancel run through the settings manager.</summary>
/// <param name="AuthorizationUri">The address the user opens in a browser to sign in.</param>
/// <param name="Callback">The local field that receives the callback address after sign-in.</param>
/// <param name="Continue">The action that completes the sign-in.</param>
/// <param name="Cancel">The action that abandons the sign-in.</param>
/// <param name="ExpiresAt">When the sign-in expires, or null when unknown.</param>
public sealed record SetupBrowserAuthorization(
    Uri AuthorizationUri, SetupTextField Callback, SetupActionDescriptor Continue,
    SetupActionDescriptor Cancel, DateTimeOffset? ExpiresAt = null) : SetupInteraction;

/// <summary>A prerequisite the user must install or fix outside the app, with an action that checks it again.</summary>
/// <param name="Instructions">What the user must do.</param>
/// <param name="Recheck">The action that checks the prerequisite again.</param>
public sealed record SetupExternalPrerequisite(string Instructions, SetupActionDescriptor Recheck) : SetupInteraction;

/// <summary>The result of running a setup action.</summary>
/// <param name="State">The action outcome.</param>
/// <param name="Issues">The issues found, or null when there are none.</param>
/// <param name="Interaction">The step the user must take to continue, or null.</param>
/// <param name="IsRetryable">Whether running the action again can succeed.</param>
public sealed record SetupActionResult(
    SetupActionState State, IReadOnlyList<SetupIssue>? Issues = null,
    SetupInteraction? Interaction = null, bool IsRetryable = false)
{
    /// <summary>A completed result with no issues.</summary>
    public static SetupActionResult Completed { get; } = new(SetupActionState.Completed);

    /// <summary>One line shown after the action, such as the test reply.</summary>
    public string? Message { get; init; }

    /// <summary>How long the action took.</summary>
    public TimeSpan? Elapsed { get; init; }
}

/// <summary>A group of setup fields within a step.</summary>
/// <param name="Id">The section identifier.</param>
/// <param name="Title">The section title.</param>
/// <param name="Fields">The fields in display order.</param>
/// <param name="IsAdvanced">Whether the section holds advanced settings.</param>
public sealed record SetupSection(string Id, string Title, IReadOnlyList<SetupField> Fields, bool IsAdvanced = false);

/// <summary>One step of a setup flow, with its instructions and field sections.</summary>
/// <param name="Id">The step identifier.</param>
/// <param name="Title">The step title.</param>
/// <param name="Instructions">Instructions shown for the step.</param>
/// <param name="Sections">The field sections in display order.</param>
public sealed record SetupStep(string Id, string Title, string Instructions, IReadOnlyList<SetupSection> Sections)
{
    /// <summary>Creates an empty step.</summary>
    public SetupStep() : this(string.Empty, string.Empty, string.Empty, []) { }

    /// <summary>What must be true before the step is complete, or empty when nothing is stated.</summary>
    public string CompletionRequirement { get; init; } = string.Empty;
    /// <summary>Help for when the step fails, or empty.</summary>
    public string Troubleshooting { get; init; } = string.Empty;
    /// <summary>Instructions for Windows, or empty.</summary>
    public string WindowsInstructions { get; init; } = string.Empty;
    /// <summary>Instructions for macOS, or empty.</summary>
    public string MacOsInstructions { get; init; } = string.Empty;
    /// <summary>Instructions for Linux, or empty.</summary>
    public string LinuxInstructions { get; init; } = string.Empty;
    /// <summary>The instructions for the operating system the app runs on, or empty.</summary>
    public string PlatformInstructions => OperatingSystem.IsWindows() ? WindowsInstructions
        : OperatingSystem.IsMacOS() ? MacOsInstructions : OperatingSystem.IsLinux() ? LinuxInstructions : string.Empty;
    /// <summary>True when the current operating system has instructions.</summary>
    public bool HasPlatformInstructions => !string.IsNullOrWhiteSpace(PlatformInstructions);
    /// <summary>True when a completion requirement is stated.</summary>
    public bool HasCompletionRequirement => !string.IsNullOrWhiteSpace(CompletionRequirement);
    /// <summary>True when troubleshooting help is present.</summary>
    public bool HasTroubleshooting => !string.IsNullOrWhiteSpace(Troubleshooting);

    /// <summary>
    /// Ids of the settings' actions shown on this step, in order. When no step of a settings object names any action,
    /// every action shows on every step.
    /// </summary>
    public IReadOnlyList<string> ActionIds { get; init; } = [];
}
