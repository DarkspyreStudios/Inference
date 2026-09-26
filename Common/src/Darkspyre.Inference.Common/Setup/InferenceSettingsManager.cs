namespace Darkspyre.Inference.Common.Setup;

/// <summary>
/// The service that acts on and saves a provider's settings object. Every call takes the settings
/// object with its current, possibly unsaved, values. A provider manager also has a typed
/// <c>OpenAsync</c> that returns its settings for a provider type and an optional saved entry.
/// </summary>
public interface IInferenceSettingsManager
{
    /// <summary>Runs one enabled action from <see cref="InferenceSettings.Actions"/> or the current interaction.</summary>
    Task<SetupActionResult> RunAsync(InferenceSettings settings, string actionId,
        IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Lists choices for one choice field from the settings object's current values.</summary>
    Task<SetupChoicePage> QueryChoicesAsync(InferenceSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken = default);

    /// <summary>Validates the settings and calls the host's typed commit target once.</summary>
    Task<SetupCommitResult> SaveAsync(InferenceSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implemented by a manager whose settings follow a running engine. <see cref="StatusChanged"/> reports
/// that the engine changed; the editor then calls <see cref="RefreshStatusAsync"/> and renders the result.
/// </summary>
public interface IInferenceSettingsStatusSource
{
    event EventHandler? StatusChanged;

    Task RefreshStatusAsync(InferenceSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Shared action, choice and save behavior for one provider's settings manager.</summary>
public abstract class InferenceSettingsManager<TSettings, TChanges> : IInferenceSettingsManager
    where TSettings : InferenceSettings<TChanges>
{
    private readonly ISetupCommitTarget<TChanges> _target;

    protected InferenceSettingsManager(ISetupCommitTarget<TChanges> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
    }

    public async Task<SetupActionResult> RunAsync(InferenceSettings settings, string actionId,
        IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        TSettings typed = Typed(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        if (typed.FindAction(actionId) is not { IsEnabled: true } action)
        {
            return new(SetupActionState.Unavailable);
        }

        try
        {
            // A Tensor teardown reads a cancelled token as urgency, not as "do nothing".
            if (action.Purpose is not (SetupActionPurpose.Unload or SetupActionPurpose.Stop))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await RunCoreAsync(typed, action, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(SetupActionState.Cancelled);
        }
        catch (Exception)
        {
            // Protocol exceptions can contain credentials or raw responses. Never project them.
            return new(SetupActionState.Failed,
                [new("operation-failed", "The operation failed. Check configuration and provider availability.")]);
        }
    }

    public async Task<SetupChoicePage> QueryChoicesAsync(InferenceSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken = default)
    {
        TSettings typed = Typed(settings);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.PageSize, 200);
        if (typed.FindField(fieldId) is not SetupChoiceField { Description: { IsEnabled: true, IsVisible: true } })
        {
            return new([], State: SetupChoiceState.Unavailable);
        }

        if (typed.GetLocalChoices(fieldId, query) is { } local)
        {
            return local;
        }

        try
        {
            return await QueryProviderChoicesAsync(typed, fieldId, query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new([], State: SetupChoiceState.Cancelled);
        }
        catch (Exception)
        {
            return new([], State: SetupChoiceState.Failed,
                Issues: [new("choices-unavailable", "The choices could not be retrieved.", fieldId)]);
        }
    }

    public async Task<SetupCommitResult> SaveAsync(InferenceSettings settings, CancellationToken cancellationToken = default)
    {
        TSettings typed = Typed(settings);
        if (typed.IsSaved)
        {
            return new(SetupCommitState.Invalid, Issues: [new("settings-saved", "These settings were already saved; open the entry again.")]);
        }

        if (cancellationToken.IsCancellationRequested) { return new(SetupCommitState.Cancelled); }
        SetupValidation validation = typed.Validate();
        if (!validation.IsValid) { return new(SetupCommitState.Invalid, Issues: validation.Issues); }
        if (!typed.CanSave)
        {
            return new(SetupCommitState.Invalid, Issues: [new("save-not-ready", "Fill every required field and complete any sign-in before saving.")]);
        }

        TChanges changes = typed.CreateChanges();
        try
        {
            SetupCommitResult result = await _target.CommitAsync(changes, cancellationToken).ConfigureAwait(false);
            if (result.State is SetupCommitState.Committed or SetupCommitState.Uncertain)
            {
                // One settings object commits once; a later edit opens the entry at its new saved version.
                typed.IsSaved = true;
                typed.SaveOutcomeUncertain = result.State == SetupCommitState.Uncertain;
                if (result.State == SetupCommitState.Committed)
                {
                    typed.Status = typed.Status with { Configured = SetupObservation.Known(true) };
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            typed.IsSaved = true;
            typed.SaveOutcomeUncertain = true;
            return new(SetupCommitState.Uncertain);
        }
        catch (Exception)
        {
            // Host exceptions may contain secrets and do not prove that no write occurred.
            typed.IsSaved = true;
            typed.SaveOutcomeUncertain = true;
            return new(SetupCommitState.Uncertain, Issues: [new("commit-outcome-unknown", "Read the saved configuration before retrying.")]);
        }
    }

    /// <summary>Runs one enabled action against the settings' current values.</summary>
    protected abstract Task<SetupActionResult> RunCoreAsync(TSettings settings, SetupActionDescriptor action,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Lists choices that come from the provider. Local enumerations never reach this method.</summary>
    protected virtual Task<SetupChoicePage> QueryProviderChoicesAsync(TSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new SetupChoicePage([], State: SetupChoiceState.Unavailable));

    private static TSettings Typed(InferenceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings as TSettings
            ?? throw new ArgumentException($"These settings are not {typeof(TSettings).Name}.", nameof(settings));
    }
}
