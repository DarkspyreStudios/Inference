using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Tensor.Common;

public sealed record TensorSetupConfiguration(string ImplementationId, TensorModelKey Model, TensorBackendKind Backend);
/// <summary>The host persists the selected immutable manifest with the configuration and restores its registration on restart.</summary>
public sealed record TensorSetupChanges(SetupConfigurationIdentity Identity, TensorSetupConfiguration Configuration,
    bool RequiresUnload, TensorModelPackDefinition Definition);

/// <summary>Shared explicit pack/lifecycle setup; the host owns the supplied manager and live instance.</summary>
public sealed class TensorSetupSession : InferenceSetupSession<TensorSetupChanges>
{
    private readonly SetupConfigurationIdentity _identity;
    private readonly IReadOnlyList<TensorModelPackDefinition> _models;
    private readonly ITensorModelPackManager _packs;
    private readonly ITensorModelInstance? _instance;
    private readonly TensorSetupConfiguration? _original;
    private readonly ITensorBackendProbe? _backendProbe;
    private readonly ITensorModelResolver? _resolver;
    private TensorBackendAvailability? _backendAvailability;
    private TensorModelPackDefinition? _resolved;
    private TensorModelEvaluation? _evaluation;
    private string _repository = string.Empty;
    private string _sourceRevision = string.Empty;
    private string _variant = string.Empty;
    private TensorModelKey? _model;
    private TensorBackendKind _backend;

    public TensorSetupSession(InferenceSetupDefinition definition, SetupConfigurationIdentity identity,
        ISetupCommitTarget<TensorSetupChanges> target, IReadOnlyList<TensorModelPackDefinition> supportedModels,
        ITensorModelPackManager packs, TensorSetupConfiguration? existing = null, ITensorModelInstance? instance = null,
        ITensorBackendProbe? backendProbe = null, ITensorModelResolver? resolver = null)
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
        _models = new TensorModelCatalog(supportedModels).Definitions;
        _packs = packs;
        _instance = instance;
        _original = existing;
        _backendProbe = backendProbe;
        _resolver = resolver;
        _model = existing?.Model;
        _backend = existing?.Backend ?? TensorBackendKind.Auto;
        Status = new()
        {
            Configured = SetupObservation.Known(existing is not null),
            Installed = SetupObservation.Unknown,
            Loaded = SetupObservation.Unknown
        };
    }

    protected override IReadOnlyList<SetupStep> CreateSteps(long revision)
    {
        string repository = _repository, sourceRevision = _sourceRevision;
        TensorModelPackDefinition? selection = SelectedDefinition();
        var sections = new List<SetupSection>
        { new("model", "Selection", [
        new SetupChoiceField(new("model", "Model pack") { IsRequired = true }, _model?.ToString() ?? string.Empty,
            string.Empty, value => SelectModel(revision, value), false,
            (query, token) => QueryChoicesAsync(query, _ => Task.FromResult(Page(query,
                Choices().Select(model => new SetupChoice(model.Key.ToString(), model.Key.ModelId, model.Key.Revision)))), token), revision),
        new SetupChoiceField(new("backend", "Execution backend") { IsRequired = true,
            HelpText = "Supported backend names are not a hardware availability guarantee. Load confirms actual runtime availability." },
            _backend.ToString(), TensorBackendKind.Auto.ToString(), value => SelectBackend(revision, value), false,
            (query, token) => QueryChoicesAsync(query, _ => Task.FromResult(Page(query,
                Backends().Select(backend => new SetupChoice(backend.ToString(), backend.ToString())))), token), revision),
        Observation("backend-availability", "Backend availability",
            _backendAvailability?.Reason ?? "Unknown. Run Check backend prerequisites explicitly."),
        Observation("pack-details", "Selected pack", selection is null ? "No resolved pack selected."
            : $"{selection.Artifacts.Count} artifacts; {selection.Artifacts.Sum(artifact => (decimal)artifact.Length):0} bytes; license: {selection.License}. Installation does not support resume."),
        Observation("pack-artifacts", "Pack artifacts and provenance", selection is null ? "No resolved pack selected."
            : string.Join(Environment.NewLine, selection.Artifacts.Select(artifact => $"{artifact.Role}: {artifact.RelativePath}; {artifact.Length} bytes; "
                + (artifact.Provenance is { } provenance ? $"{provenance.Repository}@{provenance.Revision}; {provenance.License}" : "Source provenance not supplied by the host."))))
        ]) };
        if (_resolver is not null)
        {
            sections.Add(new("source", "Browse remote model metadata", [
                new SetupChoiceField(new("repository", "Repository"), repository, string.Empty,
                    value => EditSource(revision, "repository", value), true,
                    (query, token) => QueryChoicesAsync(query, async ct =>
                    {
                        var result = await _resolver.Source.SearchAsync(new(query.Search, query.Continuation, Math.Min(query.PageSize, 100)), ct).ConfigureAwait(false);
                        return SourcePage(query, result, model => new(model.Repository, model.Repository, model.License), "repository");
                    }, token), revision, SetupSideEffect.Network),
                new SetupChoiceField(new("source-revision", "Revision") { IsEnabled = repository.Length != 0,
                    HelpText = "A branch or tag is pinned to an immutable revision during resolution." }, sourceRevision, string.Empty,
                    value => EditSource(revision, "source-revision", value), true,
                    (query, token) => QueryChoicesAsync(query, async ct =>
                    {
                        var result = await _resolver.Source.ListRevisionsAsync(new(repository, query.Continuation, Math.Min(query.PageSize, 100)), ct).ConfigureAwait(false);
                        // Source paging remains authoritative; filtering must not invent a replacement cursor.
                        return SourcePage(query, result, item => new(item.Revision, item.Revision), "source-revision");
                    }, token), revision, SetupSideEffect.Network),
                new SetupChoiceField(new("variant", "Primary artifact / variant") { IsEnabled = repository.Length != 0 && sourceRevision.Length != 0,
                    HelpText = "A repository file is only a candidate. Resolve compatibility to identify the complete pack." }, _variant, string.Empty,
                    value => EditSource(revision, "variant", value), true,
                    (query, token) => QueryChoicesAsync(query, async ct =>
                    {
                        var result = await _resolver.Source.ListVariantsAsync(new(repository, sourceRevision), ct).ConfigureAwait(false);
                        return result.State == TensorSourceState.Available && result.Value is not null
                            ? Page(query, result.Value.Items.Select(item => new SetupChoice(item.Path, item.Path,
                                item.Length is long length ? $"{length} bytes" : "Size unknown")))
                            : SourcePage(query, result, item => new(item.Path, item.Path), "variant");
                    }, token), revision, SetupSideEffect.Network),
                Observation("compatibility", "Compatibility", _evaluation is null ? "Not evaluated."
                    : $"{_evaluation.State} ({_evaluation.SourceState}). " + string.Join(" ", _evaluation.Issues.Select(issue => issue.Message))),
                Observation("compatibility-evidence", "Compatibility evidence", _evaluation is null ? "Not evaluated."
                    : string.Join(Environment.NewLine, _evaluation.Evidence.Select(evidence => $"{evidence.ArtifactPath}: {evidence.Check} — {evidence.Detail}")))
            ]));
        }
        return [new("model", "Model and backend", "Select a supported pack. Browsing and saving never install or load weights.", sections)];
    }

    private static SetupTextField Observation(string id, string label, string value) =>
        new(new(id, label) { IsReadOnly = true }, value, string.Empty,
            _ => new(SetupEditState.Invalid, [new("read-only", "This field is an observation.", id)]));

    private static SetupChoicePage SourcePage<T>(SetupChoiceQuery query, TensorSourceResult<TensorSourcePage<T>> result,
        Func<T, SetupChoice> choice, string field) => result.State == TensorSourceState.Available && result.Value is not null
        ? new(query.ExpectedRevision, result.Value.Items.Select(choice).Where(item => item.Value.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).ToArray(), result.Value.Continuation)
        : new(query.ExpectedRevision, [], State: SetupChoiceState.Failed,
            Issues: (result.Issues ?? [new("source-" + result.State.ToString().ToLowerInvariant(), "The source could not provide choices.")])
                .Select(issue => new SetupIssue(issue.Code, issue.Message, field)).ToArray());

    private SetupEditResult EditSource(long revision, string field, string value)
    {
        if (value is null || value.Length > 1024 || value.Any(char.IsControl))
        { return new(SetupEditState.Invalid, [new("source-selection", "Enter a valid source identity.", field)]); }
        return Edit(revision, () =>
        {
            switch (field)
            {
                case "repository": _repository = value; _sourceRevision = _variant = string.Empty; break;
                case "source-revision": _sourceRevision = value; _variant = string.Empty; break;
                case "variant": _variant = value; break;
            }
            ClearResolution();
        });
    }

    private void ClearResolution() { _model = null; _resolved = null; _evaluation = null; }
    private TensorModelPackDefinition? SelectedDefinition() => Choices().FirstOrDefault(model => model.Key == _model);
    private IEnumerable<TensorModelPackDefinition> Choices() => _resolved is null ? _models
        : _models.Where(model => model.Key != _resolved.Key).Append(_resolved);

    private SetupEditResult SelectModel(long revision, string value)
    {
        TensorModelPackDefinition? selected = Choices().FirstOrDefault(model => model.Key.ToString() == value);
        return selected is null ? new(SetupEditState.Invalid, [new("model-unsupported", "Select a supported pack.", "model")])
            : Edit(revision, () =>
            {
                _model = selected.Key;
                if (selected.Key != _resolved?.Key)
                { _repository = _sourceRevision = _variant = string.Empty; _evaluation = null; _resolved = null; }
            });
    }

    private SetupEditResult SelectBackend(long revision, string value) =>
        Enum.TryParse(value, out TensorBackendKind backend) && Backends().Contains(backend)
            ? Edit(revision, () => { _backend = backend; if (_repository.Length != 0) { ClearResolution(); } })
            : new(SetupEditState.Invalid, [new("backend-unsupported", "Select a supported backend.", "backend")]);

    private IEnumerable<TensorBackendKind> Backends() => new[] { TensorBackendKind.Auto }.Concat(
        SelectedDefinition()?.SupportedBackends ?? _models.SelectMany(model => model.SupportedBackends)).Distinct();

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
            BindAction(revision, new("backend-check", "Check backend prerequisites", SetupActionPurpose.CheckPrerequisites,
                SetupSideEffect.LocalVerification, _backendProbe is not null,
                _backendProbe is null ? "This composition does not provide a native backend probe." : null), CheckBackendAsync),
            BindAction(revision, new("resolve", "Resolve compatible model pack", SetupActionPurpose.DiscoverModels,
                SetupSideEffect.Network, _resolver is not null && _repository.Length != 0 && _sourceRevision.Length != 0 && _variant.Length != 0,
                _resolver is null ? "This composition does not provide an engine model resolver." : "Select a repository, revision and variant. Resolve reads bounded metadata, never full weights."), ResolveAsync),
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

    private async Task<SetupActionResult> ResolveAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        Publish(() =>
        {
            ClearResolution();
            Status = Status with { Installed = SetupObservation.Unknown, Verified = SetupObservation.Unknown };
        });
        progress?.Report(new("Resolving", "Inspecting bounded model metadata and required companions."));
        TensorModelResolution result = await _resolver!.ResolveAsync(new(new(_repository, _sourceRevision, _variant), _backend), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        TensorModelPackDefinition? definition = result.Evaluation.State == TensorCompatibilityState.Supported && result.Definition is not null
            ? new TensorModelCatalog([result.Definition]).GetRequired(result.Definition.Key) : null;
        Publish(() => { _evaluation = result.Evaluation; _resolved = definition; _model = definition?.Key; });
        return definition is not null ? SetupActionResult.Completed : new(SetupActionState.Failed,
            result.Evaluation.Issues.Select(issue => new SetupIssue(issue.Code, issue.Message, "variant")).DefaultIfEmpty(
                new("model-unresolved", "Compatibility and the complete installable pack have not been established.", "variant")).ToArray(), IsRetryable: true);
    }

    private async Task<SetupActionResult> CheckBackendAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        TensorBackendAvailability observation = await _backendProbe!.CheckAsync(_backend, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Publish(() => _backendAvailability = observation);
        return observation.IsAvailable == true ? SetupActionResult.Completed : new(SetupActionState.Failed,
            [new(observation.IsAvailable == false ? "backend-unavailable" : "backend-unverified", observation.Reason, "backend")], IsRetryable: true);
    }

    private async Task<SetupActionResult> CheckAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        if (!_packs.Definitions.Any(definition => definition.Key == _model))
        {
            Publish(() => Status = Status with { Installed = SetupObservation.Known(false), Verified = SetupObservation.Known(false) });
            return SetupActionResult.Completed;
        }
        TensorModelPackStatus status = await _packs.GetStatusAsync(_model!, token).ConfigureAwait(false);
        Publish(() => Status = Status with { Installed = SetupObservation.Known(status.State == TensorModelPackState.Installed) });
        RefreshLiveStatus();
        return SetupActionResult.Completed;
    }

    private async Task<SetupActionResult> InstallAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        Publish(() => Status = Status with { Installed = SetupObservation.Unknown, Verified = SetupObservation.Unknown });
        _packs.Register(SelectedDefinition()!);
        await _packs.InstallAsync(_model!, new InstallProgress(progress), token).ConfigureAwait(false);
        return await VerifyAsync(progress, token).ConfigureAwait(false);
    }

    private async Task<SetupActionResult> VerifyAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        if (!_packs.Definitions.Any(definition => definition.Key == _model))
        { return new(SetupActionState.Failed, [new("pack-not-installed", "Install the resolved pack explicitly before verifying it.", "model")]); }
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
        _backendAvailability = null;
        Status = Status with { Installed = SetupObservation.Unknown, Verified = SetupObservation.Unknown, Loaded = SetupObservation.Unknown, Ready = SetupObservation.Unknown };
    }

    protected override SetupValidation ValidateDraft() => SelectedDefinition() is null
        ? new([new("model-required", "Select a supported model pack.", "model")])
        : !Backends().Contains(_backend) ? new([new("backend-unsupported", "The pack does not support this backend.", "backend")])
        : SetupValidation.Valid;

    protected override TensorSetupChanges CreateChanges() => new(_identity,
        new(Definition.Implementation.Id, _model!, _backend),
        _original is not null && (_original.Model != _model || _original.Backend != _backend), SelectedDefinition()!);

    protected override void ClearSensitiveState() { }

    private sealed class InstallProgress(IProgress<SetupProgress>? progress) : IProgress<TensorModelInstallProgress>
    {
        public void Report(TensorModelInstallProgress value) => progress?.Report(
            new(value.Stage.ToString(), value.ArtifactRole, value.OverallBytesTransferred, value.OverallTotalBytes));
    }
}
