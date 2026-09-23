using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Tensor.Common;

public sealed record TensorSetupConfiguration(string ImplementationId, TensorModelKey Model, TensorBackendKind Backend);
public sealed record TensorSetupChanges(SetupConfigurationIdentity Identity, TensorSetupConfiguration Configuration, bool RequiresUnload);

/// <summary>Shared explicit pack/lifecycle setup; the host owns the supplied manager and live instance.</summary>
public sealed class TensorSetupSession : InferenceSetupSession<TensorSetupChanges>
{
    private readonly SetupConfigurationIdentity _identity;
    private readonly IReadOnlyList<TensorModelPackDefinition> _models;
    private readonly ITensorModelPackManager _packs;
    private readonly ITensorModelInstance? _instance;
    private readonly TensorSetupConfiguration? _original;
    private TensorModelKey? _model;
    private TensorBackendKind _backend;

    public TensorSetupSession(InferenceSetupDefinition definition, SetupConfigurationIdentity identity,
        ISetupCommitTarget<TensorSetupChanges> target, IReadOnlyList<TensorModelPackDefinition> supportedModels,
        ITensorModelPackManager packs, TensorSetupConfiguration? existing = null, ITensorModelInstance? instance = null)
        : base(definition, target)
    {
        ArgumentNullException.ThrowIfNull(supportedModels);
        ArgumentNullException.ThrowIfNull(packs);
        identity.Validate();
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || (existing is not null && existing.ImplementationId != definition.Implementation.Id))
        {
            throw new ArgumentException("An edit requires the matching saved Tensor configuration.", nameof(existing));
        }
        _identity = identity;
        _models = supportedModels.ToArray();
        _packs = packs;
        _instance = instance;
        _original = existing;
        _model = existing?.Model;
        _backend = existing?.Backend ?? TensorBackendKind.Auto;
        Status = new()
        {
            Configured = SetupObservation.Known(existing is not null),
            Installed = SetupObservation.Unknown,
            Loaded = SetupObservation.Unknown
        };
    }

    protected override IReadOnlyList<SetupStep> CreateSteps(long revision) =>
    [new("model", "Model and backend", "Select a supported pack. Installation and loading are separate explicit actions.",
    [new("model", "Selection",
    [
        new SetupChoiceField(new("model", "Model pack") { IsRequired = true }, _model?.ToString() ?? string.Empty,
            string.Empty, value => SelectModel(revision, value), false,
            (query, token) => QueryChoicesAsync(query, _ => Task.FromResult(Page(query,
                _models.Select(model => new SetupChoice(model.Key.ToString(), model.Key.ModelId, model.Key.Revision)))), token), revision),
        new SetupChoiceField(new("backend", "Execution backend") { IsRequired = true,
            HelpText = "Supported backend names are not a hardware availability guarantee. Load confirms actual runtime availability." },
            _backend.ToString(), TensorBackendKind.Auto.ToString(), value => SelectBackend(revision, value), false,
            (query, token) => QueryChoicesAsync(query, _ => Task.FromResult(Page(query,
                Backends().Select(backend => new SetupChoice(backend.ToString(), backend.ToString())))), token), revision)
    ])])];

    private SetupEditResult SelectModel(long revision, string value)
    {
        TensorModelPackDefinition? selected = _models.FirstOrDefault(model => model.Key.ToString() == value);
        return selected is null ? new(SetupEditState.Invalid, [new("model-unsupported", "Select a supported pack.", "model")])
            : Edit(revision, () => _model = selected.Key);
    }

    private SetupEditResult SelectBackend(long revision, string value) =>
        Enum.TryParse(value, out TensorBackendKind backend) && Backends().Contains(backend)
            ? Edit(revision, () => _backend = backend)
            : new(SetupEditState.Invalid, [new("backend-unsupported", "Select a supported backend.", "backend")]);

    private IEnumerable<TensorBackendKind> Backends() => new[] { TensorBackendKind.Auto }.Concat(
        _models.FirstOrDefault(model => model.Key == _model)?.SupportedBackends ?? new HashSet<TensorBackendKind>()).Distinct();

    private static SetupChoicePage Page(SetupChoiceQuery query, IEnumerable<SetupChoice> choices)
    {
        // A revision-scoped offset is sufficient for this immutable local list; source-host paging is separate.
        if (!int.TryParse(query.Continuation ?? "0", out int offset) || offset < 0)
        {
            return new(query.ExpectedRevision, [], State: SetupChoiceState.Failed,
                Issues: [new("cursor-invalid", "The choice cursor is invalid.")]);
        }
        SetupChoice[] matches = choices.Where(choice => choice.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || choice.Value.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new(query.ExpectedRevision, matches.Skip(offset).Take(query.PageSize).ToArray(),
            offset + query.PageSize < matches.Length ? (offset + query.PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    protected override IReadOnlyList<ISetupAction> CreateActions(long revision)
    {
        bool selected = ValidateDraft().IsValid;
        bool live = _instance is not null && !_instance.Observation.IsStopped;
        bool matchingBackend = _instance?.RequestedBackend == _backend;
        return
        [
            BindAction(revision, new("status", "Check installation and readiness", SetupActionPurpose.CheckReadiness,
                SetupSideEffect.LocalVerification, selected), CheckAsync),
            BindAction(revision, new("install", "Install selected pack", SetupActionPurpose.Install,
                SetupSideEffect.Network | SetupSideEffect.Download, selected), InstallAsync),
            BindAction(revision, new("verify", "Verify installed weights", SetupActionPurpose.Verify,
                SetupSideEffect.LocalVerification, selected), VerifyAsync),
            BindAction(revision, new("load", "Load selected model", SetupActionPurpose.Load,
                SetupSideEffect.ModelLoad, selected && live && matchingBackend && _instance!.Observation.Model is null,
                !live ? "The host must supply a live instance." : !matchingBackend ? "The host must supply an instance configured for this backend." : null),
                async (_, token) => { await _instance!.LoadAsync(_model!, token).ConfigureAwait(false); return await CheckAsync(null, token).ConfigureAwait(false); }),
            BindAction(revision, new("unload", "Unload model", SetupActionPurpose.Unload, SetupSideEffect.ModelUnload, live),
                async (_, token) => { await _instance!.UnloadAsync(token).ConfigureAwait(false); RefreshLiveStatus(); return SetupActionResult.Completed; }),
            BindAction(revision, new("stop", "Stop instance permanently", SetupActionPurpose.Stop, SetupSideEffect.TerminalStop | SetupSideEffect.ModelUnload, live),
                async (_, token) => { await _instance!.StopAsync(token).ConfigureAwait(false); RefreshLiveStatus(); return SetupActionResult.Completed; })
        ];
    }

    private async Task<SetupActionResult> CheckAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        TensorModelPackStatus status = await _packs.GetStatusAsync(_model!, token).ConfigureAwait(false);
        Publish(() => Status = Status with { Installed = SetupObservation.Known(status.State == TensorModelPackState.Installed) });
        RefreshLiveStatus();
        return SetupActionResult.Completed;
    }

    private async Task<SetupActionResult> InstallAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        Publish(() => Status = Status with { Installed = SetupObservation.Unknown, Verified = SetupObservation.Unknown });
        await _packs.InstallAsync(_model!, new InstallProgress(progress), token).ConfigureAwait(false);
        return await VerifyAsync(progress, token).ConfigureAwait(false);
    }

    private async Task<SetupActionResult> VerifyAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        Publish(() => Status = Status with { Verified = SetupObservation.Unknown });
        TensorModelPackStatus status = await _packs.VerifyAsync(_model!, token).ConfigureAwait(false);
        bool valid = status.State == TensorModelPackState.Installed;
        Publish(() => Status = Status with
        {
            Installed = SetupObservation.Known(valid),
            Verified = SetupObservation.Known(valid)
        });
        RefreshLiveStatus();
        return valid ? SetupActionResult.Completed : new(SetupActionState.Failed,
            [new("pack-unverified", "The selected pack is missing or failed integrity verification.", "model")], IsRetryable: true);
    }

    private void RefreshLiveStatus()
    {
        Publish(() => { });
    }

    protected override SetupStatus GetStatus()
    {
        TensorModelObservation? observed = _instance?.Observation;
        return Status with
        {
            Loaded = observed is null ? SetupObservation.Unknown : new(true, observed.Model == _model && _model is not null, observed.ObservedAt),
            Ready = observed is null ? SetupObservation.Unknown : new(true,
                observed.Model == _model && _model is not null && !observed.IsStopped && _instance!.RequestedBackend == _backend,
                observed.ObservedAt)
        };
    }

    protected override void OnDraftEdited()
    {
        base.OnDraftEdited();
        Status = Status with { Installed = SetupObservation.Unknown, Verified = SetupObservation.Unknown, Loaded = SetupObservation.Unknown, Ready = SetupObservation.Unknown };
    }

    protected override SetupValidation ValidateDraft() => _model is null || !_models.Any(model => model.Key == _model)
        ? new([new("model-required", "Select a supported model pack.", "model")])
        : !Backends().Contains(_backend) ? new([new("backend-unsupported", "The pack does not support this backend.", "backend")])
        : SetupValidation.Valid;

    protected override TensorSetupChanges CreateChanges() => new(_identity,
        new(Definition.Implementation.Id, _model!, _backend),
        _original is not null && (_original.Model != _model || _original.Backend != _backend));

    protected override void ClearSensitiveState() { }

    private sealed class InstallProgress(IProgress<SetupProgress>? progress) : IProgress<TensorModelInstallProgress>
    {
        public void Report(TensorModelInstallProgress value) => progress?.Report(
            new(value.Stage.ToString(), value.ArtifactRole, value.OverallBytesTransferred, value.OverallTotalBytes));
    }
}
