using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using System.Runtime.InteropServices;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>The saved Tensor selection: the implementation, the model pack and the execution backend.</summary>
/// <param name="ImplementationId">The id of the Tensor implementation that owns this configuration.</param>
/// <param name="Model">The model pack key, as model id and revision.</param>
/// <param name="Backend">The requested execution backend. <see cref="TensorBackendKind.Auto"/> lets the engine choose.</param>
public sealed record TensorSetupConfiguration(string ImplementationId, TensorModelKey Model, TensorBackendKind Backend);
/// <summary>The host persists the selected immutable manifest with the configuration and restores its registration on restart.</summary>
/// <param name="Identity">The saved setup entry the changes apply to.</param>
/// <param name="Configuration">The new Tensor selection.</param>
/// <param name="RequiresUnload">True when the changes require unloading the resident model.</param>
/// <param name="Definition">The manifest of the selected model pack.</param>
public sealed record TensorSetupChanges(SetupConfigurationIdentity Identity, TensorSetupConfiguration Configuration,
    bool RequiresUnload, TensorModelPackDefinition Definition);

/// <summary>
/// Editable Tensor pack and backend settings. Plain data: edits change only this object. Pack actions,
/// source browsing and instance lifecycle run through <see cref="TensorSettingsManager"/>.
/// </summary>
public sealed class TensorSettings : InferenceSettings<TensorSetupChanges>
{
    private readonly TensorSetupConfiguration? _original;
    private readonly bool _hasBackendProbe;
    private readonly bool _hasInstance;
    private readonly TensorBackendKind? _instanceBackend;
    private readonly bool _hasResolver;
    private readonly bool _hasSource;
    private readonly IReadOnlyList<TensorArtifactRequirement> _requirements;
    private readonly string? _additionalArtifactRolePrefix;
    private string _repository = string.Empty;
    private string _sourceRevision = string.Empty;
    private string _variant = string.Empty;
    private string _locationKind;
    private string _localPath = string.Empty;
    private Uri? _sourceUrl;
    private string _license = string.Empty;
    private string _sha256 = string.Empty;
    private long? _length;
    private readonly Dictionary<string, string> _companions = new(StringComparer.Ordinal);
    private TensorModelKey? _model;
    private TensorBackendKind _backend;
    private readonly object _backendCheckGate = new();
    private long _backendCheckRevision;
    private TensorBackendAvailability? _backendAvailability;

    internal TensorSettings(InferenceSetupDefinition definition, SetupConfigurationIdentity identity,
        TensorModelListing listing, TensorSetupConfiguration? existing, bool hasBackendProbe,
        TensorBackendKind? instanceBackend, ITensorModelResolver? resolver)
        : base(definition, identity)
    {
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || (existing is not null && existing.ImplementationId != definition.Implementation.Id))
        {
            throw new ArgumentException("An edit requires the matching saved Tensor configuration.", nameof(existing));
        }
        Listing = listing;
        _original = existing;
        _hasBackendProbe = hasBackendProbe;
        _hasInstance = instanceBackend is not null;
        _instanceBackend = instanceBackend;
        _hasResolver = resolver is not null;
        _hasSource = resolver?.Source is not null;
        _requirements = resolver?.Requirements ?? [];
        _additionalArtifactRolePrefix = resolver?.AdditionalArtifactRolePrefix;
        _locationKind = _hasSource ? "repository" : "file";
        _model = existing?.Model;
        _backend = existing?.Backend ?? TensorBackendKind.Auto;
        Status = new()
        {
            Configured = SetupObservation.NotApplicable,
            Verified = SetupObservation.NotApplicable,
            Ready = SetupObservation.NotApplicable
        };
    }

    /// <summary>The host's model list as last read: offered models with install state, the saved model and the store folder.</summary>
    internal TensorModelListing Listing { get; set; }

    internal TensorModelKey? Model => _model;
    internal TensorBackendKind Backend => _backend;
    internal string Repository => _repository;
    internal string SourceRevision => _sourceRevision;
    internal TensorBackendAvailability? BackendAvailability
    {
        get { lock (_backendCheckGate) { return _backendAvailability; } }
    }

    internal (long Revision, TensorBackendProbeRequest Request) ReserveBackendCheck()
    {
        lock (_backendCheckGate)
        {
            return (++_backendCheckRevision, new(Guid.NewGuid(), Definition.Implementation.Id,
                _backend, RuntimeInformation.RuntimeIdentifier));
        }
    }

    internal bool PublishBackendCheck(long revision, TensorBackendAvailability observation, CancellationToken token)
    {
        lock (_backendCheckGate)
        {
            token.ThrowIfCancellationRequested();
            if (revision != _backendCheckRevision) return false;
            _backendAvailability = observation;
            return true;
        }
    }
    internal TensorModelPackDefinition? Resolved { get; private set; }
    internal TensorModelEvaluation? Evaluation { get; private set; }

    /// <summary>The engine observation last read from the host's instance.</summary>
    internal TensorModelObservation? InstanceObservation { get; set; }

    /// <inheritdoc />
    protected override IReadOnlyList<SetupStep> CreateSteps()
    {
        string repository = _repository, sourceRevision = _sourceRevision;
        TensorModelPackDefinition? selection = SelectedDefinition();
        var sections = new List<SetupSection>
        { new("model", "Selection", [
        new SetupChoiceField(new("model", "Model pack") { IsRequired = true }, _model?.ToString() ?? string.Empty,
            string.Empty, SelectModel, false, capabilities: new(InferenceSupport.Supported, InferenceSupport.Supported),
            showValue: SelectedState() == SetupInstallState.Installed),
        .. selection is null ? Array.Empty<SetupField>()
            : [new SetupInstallStatusField(new("install-status", "Install state") { IsReadOnly = true }, InstallStatus(selection))],
        new SetupChoiceField(new("backend", "Runs on") { IsRequired = true,
            HelpText = "Supported backend names are not a hardware availability guarantee. Load confirms actual runtime availability." },
            _backend.ToString(), TensorBackendKind.Auto.ToString(), SelectBackend, false),
        Observation("backend-availability", "Backend availability",
            BackendAvailability?.Reason ?? "Unknown. Run Check backend prerequisites explicitly.")
        ]),
        new("model-definition", "Model definition (what the pack contains, not whether it is downloaded)", [
        Observation("pack-details", "Contents", selection is null ? "No resolved pack selected."
            : $"{selection.Artifacts.Count} artifacts; {SetupByteSize.Format(selection.Artifacts.Sum(artifact => artifact.Length))}; license: {selection.License}. Installation does not support resume."),
        Observation("pack-artifacts", "Files and provenance", selection is null ? "No resolved pack selected."
            : string.Join(Environment.NewLine, selection.Artifacts.Select(artifact => $"{artifact.Role}: {artifact.RelativePath}; {SetupByteSize.Format(artifact.Length)}; "
                + (artifact.Provenance is { } provenance ? $"{(provenance.Repository is null ? provenance.Origin.ToString() : provenance.Repository + "@" + provenance.Revision)}; {provenance.License}" : "Source provenance not supplied by the host."))))
        ], IsAdvanced: true) };
        if (_hasResolver)
        {
            // Every way to bring in a model from outside the offered packs shares one advanced section.
            var elsewhere = new List<SetupField>
            {
                new SetupChoiceField(new("location-kind", "Location type"), _locationKind, _hasSource ? "repository" : "file",
                    value => EditSource("location-kind", value), false),
                new SetupTextField(new("local-path", "Absolute model path") { IsVisible = _locationKind is "file" or "folder",
                    HelpText = "Required for Inspect and Import. A folder is inspected only at its top level. Selecting a file does not grant discovery of adjacent files." },
                    _localPath, string.Empty, value => EditSource("local-path", value),
                    pathSelection: _locationKind == "folder" ? SetupPathSelectionKind.Folder : SetupPathSelectionKind.File),
                new SetupUriField(new("source-url", "Artifact URL") { IsVisible = _locationKind == "url",
                    HelpText = "Required for Inspect and Import. Use a durable HTTPS URL. The host supplies credentials separately." }, _sourceUrl, null,
                    value => EditDraft(() => { _sourceUrl = value; ClearResolution(); })),
                new SetupTextField(new("folder-primary", "Primary artifact filename") { IsVisible = _locationKind == "folder",
                    HelpText = "Optional when exactly one model matches this family's format; required for ambiguous folders or split GGUF." },
                    _variant, string.Empty, value => EditSource("variant", value)),
                new SetupTextField(new("source-license", "Artifact license") {
                    HelpText = "Required for Import. Declare the actual license for these artifacts; the library never invents a license." },
                    _license, string.Empty, value => EditSource("source-license", value)),
                new SetupTextField(new("source-sha256", "Expected primary SHA-256") { IsVisible = _locationKind != "repository",
                    HelpText = "Optional before Import. Import computes a content hash but does not authenticate its publisher." },
                    _sha256, string.Empty, value => EditSource("source-sha256", value)),
                new SetupIntegerField(new("source-length", "Expected primary length (bytes)") { IsVisible = _locationKind != "repository" },
                    _length, null, value => EditDraft(() => { _length = value; ClearResolution(); }), minimum: 0)
            };
            if (_requirements.Count > 1)
                elsewhere.AddRange(_requirements.Skip(1).Select(requirement => (SetupField)
                    new SetupTextField(new("companion-" + requirement.Role, requirement.Role + " location")
                    {
                        HelpText = "Optional absolute file path or HTTPS URL override. Folder filename: " + requirement.FileName
                            + (requirement.Default is null ? ". Otherwise provide this artifact explicitly." : ". Default: " + requirement.Default.Location)
                    }, _companions.GetValueOrDefault(requirement.Role, string.Empty), string.Empty,
                        value => EditSource("companion-" + requirement.Role, value), pathSelection: SetupPathSelectionKind.File)));
            if (_hasSource && _locationKind == "repository")
                elsewhere.AddRange([
                    new SetupChoiceField(new("repository", "Repository"), repository, string.Empty,
                        value => EditSource("repository", value), true, SetupSideEffect.Network, SetupChoiceCapabilities.Searchable),
                    new SetupChoiceField(new("source-revision", "Revision") { IsEnabled = repository.Length != 0,
                        HelpText = "A branch or tag is pinned to an immutable revision during resolution." }, sourceRevision, string.Empty,
                        value => EditSource("source-revision", value), true, SetupSideEffect.Network, SetupChoiceCapabilities.Searchable),
                    new SetupChoiceField(new("variant", "Primary artifact / variant") { IsEnabled = repository.Length != 0 && sourceRevision.Length != 0,
                        HelpText = "A repository file is only a candidate. Resolve compatibility to identify the complete pack." }, _variant, string.Empty,
                        value => EditSource("variant", value), true, SetupSideEffect.Network, SetupChoiceCapabilities.Searchable),
                ]);
            elsewhere.AddRange([
                Observation("compatibility", "Compatibility", Evaluation is null ? "Not evaluated."
                    : $"{Evaluation.State} ({Evaluation.SourceState}). " + string.Join(" ", Evaluation.Issues.Select(issue => issue.Message))),
                Observation("compatibility-evidence", "Compatibility evidence", Evaluation is null ? "Not evaluated."
                    : string.Join(Environment.NewLine, Evaluation.Evidence.Select(evidence => $"{evidence.ArtifactPath}: {evidence.Check} — {evidence.Detail}")))
            ]);
            if (_additionalArtifactRolePrefix is { } prefix)
                elsewhere.Add(new SetupRepeatableGroupField(new("additional-artifacts", "Additional shards")
                    { HelpText = "For split files, select every remaining shard explicitly, or select their containing folder." },
                    _companions.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(item => new SetupFieldRow(item.Key,
                        [new SetupTextField(new("companion-" + item.Key, "Shard path or URL"), item.Value, string.Empty,
                            value => EditSource("companion-" + item.Key, value), pathSelection: SetupPathSelectionKind.File)])).ToArray(),
                    () => _companions.Keys.Count(key => key.StartsWith(prefix, StringComparison.Ordinal)) >= 255
                        ? new(SetupEditState.Invalid, [new("artifact-limit", "At most 255 additional shards can be selected.")]) : EditDraft(() =>
                    {
                        int index = Enumerable.Range(2, 255).First(index => !_companions.ContainsKey(prefix + index));
                        _companions.Add(prefix + index, string.Empty); ClearResolution();
                    }),
                    id => EditDraft(() => { if (id.StartsWith(prefix, StringComparison.Ordinal)) _companions.Remove(id); ClearResolution(); })));
            sections.Add(new("elsewhere", "Use a model from somewhere else", elsewhere.AsReadOnly(), IsAdvanced: true));
        }
        return [new("model", "Model and backend", "Select a supported pack. Browsing and saving never install or load weights.", sections)];
    }

    // The selected model's install status card: the state of its listed entry and the actions that change it.
    private SetupInstallStatus InstallStatus(TensorModelPackDefinition selection)
    {
        TensorListedModel listed = SelectedListing() ?? new(selection, SetupInstallState.NotDownloaded, null, null);
        long size = selection.Artifacts.Sum(artifact => artifact.Length);
        bool valid = ValidateDraft().IsValid;
        SetupActionDescriptor[] actions = listed.State switch
        {
            SetupInstallState.NotDownloaded =>
            [
                new("download", $"Download ({SetupByteSize.Format(size)})", SetupActionPurpose.Install,
                    SetupSideEffect.Network | SetupSideEffect.Download, IsEnabled: valid)
            ],
            SetupInstallState.FilesMissing =>
            [
                new("download", "Download again", SetupActionPurpose.Install,
                    SetupSideEffect.Network | SetupSideEffect.Download, IsEnabled: valid),
                new("remove", "Delete download", SetupActionPurpose.Remove, SetupSideEffect.DeleteFiles, IsEnabled: true)
            ],
            SetupInstallState.NotSupported =>
            [
                new("remove", "Delete download", SetupActionPurpose.Remove, SetupSideEffect.DeleteFiles, IsEnabled: true)
            ],
            _ =>
            [
                new("remove", "Delete download", SetupActionPurpose.Remove, SetupSideEffect.DeleteFiles,
                    IsEnabled: InstanceObservation?.Model is null,
                    UnavailableReason: InstanceObservation?.Model is null ? null : "Unload the model first.")
            ]
        };
        return new(listed.State, selection.Key.ModelId, Listing.Store, selection.Artifacts.Count, size, selection.License,
            listed.MissingFile, listed.UnsupportedReason, actions);
    }

    // The selected model's listed entry, offered or saved; null when the selection is not in the listing.
    private TensorListedModel? SelectedListing() => _model is null ? null
        : Listing.Models.FirstOrDefault(model => model.Definition.Key == _model)
            ?? (Listing.Saved is { } saved && saved.Definition.Key == _model ? saved : null);

    // The selected model's install state; a selection outside the listing is NotDownloaded.
    private SetupInstallState SelectedState() => SelectedListing()?.State ?? SetupInstallState.NotDownloaded;

    // A model's choice chip state: Installed and NotSupported as listed; any other model is NotDownloaded.
    private SetupInstallState ChoiceState(TensorModelPackDefinition model) =>
        Listing.Models.FirstOrDefault(listed => listed.Definition.Key == model.Key)?.State switch
        {
            SetupInstallState.Installed => SetupInstallState.Installed,
            SetupInstallState.NotSupported => SetupInstallState.NotSupported,
            _ => SetupInstallState.NotDownloaded
        };

    private static SetupTextField Observation(string id, string label, string value) =>
        new(new(id, label) { IsReadOnly = true }, value, string.Empty,
            _ => new(SetupEditState.Invalid, [new("read-only", "This field is an observation.", id)]));

    /// <inheritdoc />
    protected override SetupChoicePage? GetLocalChoices(string fieldId, SetupChoiceQuery query) => fieldId switch
    {
        "model" => Page(query, Choices().Select(model => new SetupChoice(model.Key.ToString(), model.Key.ModelId, model.Key.Revision,
            new SetupChoiceInstall(ChoiceState(model), model.Artifacts.Sum(artifact => artifact.Length))))),
        "backend" => Page(query, Backends().Select(backend => new SetupChoice(backend.ToString(), BackendName(backend)))),
        "location-kind" => Page(query, new[] { new SetupChoice("file", "Local file"), new("folder", "Local folder"), new("url", "Artifact URL") }
            .Concat(_hasSource ? [new SetupChoice("repository", "Repository")] : [])),
        _ => null
    };

    /// <summary>The name a person sees for a backend in the "Runs on" choice.</summary>
    private static string BackendName(TensorBackendKind backend) => backend switch
    {
        TensorBackendKind.Auto => "Automatic",
        TensorBackendKind.Cpu or TensorBackendKind.GgmlCpu => "CPU",
        TensorBackendKind.GgmlMetal => "Apple GPU",
        TensorBackendKind.GgmlCuda or TensorBackendKind.Cuda => "NVIDIA GPU",
        TensorBackendKind.GgmlVulkan => "Vulkan GPU",
        TensorBackendKind.Mlx => "Apple MLX",
        TensorBackendKind.CoreMl => "Core ML",
        TensorBackendKind.WebGpu => "WebGPU",
        _ => backend.ToString()
    };

    private SetupEditResult EditSource(string field, string value)
    {
        if (value is null || value.Length > 4096 || value.Any(char.IsControl))
        { return new(SetupEditState.Invalid, [new("source-selection", "Enter a valid source identity.", field)]); }
        if (field == "location-kind" && value is not ("file" or "folder" or "url" or "repository")
            || field == "source-sha256" && value.Length != 0 && (value.Length != 64 || !value.All(Uri.IsHexDigit))
            || field == "local-path" && value.Length != 0 && !Path.IsPathFullyQualified(value)
            || field.StartsWith("companion-", StringComparison.Ordinal) && value.Length != 0 && ParseLocation(value) is null)
        { return new(SetupEditState.Invalid, [new("source-selection", "Enter a valid location or integrity value.", field)]); }
        return EditDraft(() =>
        {
            switch (field)
            {
                case "repository": _repository = value; _sourceRevision = _variant = string.Empty; break;
                case "source-revision": _sourceRevision = value; _variant = string.Empty; break;
                case "variant": _variant = value; break;
                case "location-kind": _locationKind = value; break;
                case "local-path": _localPath = value; break;
                case "source-license": _license = value; break;
                case "source-sha256": _sha256 = value; break;
                default:
                    if (field.StartsWith("companion-", StringComparison.Ordinal))
                    {
                        string role = field[10..];
                        if (value.Length == 0 && !(_additionalArtifactRolePrefix is { } prefix && role.StartsWith(prefix, StringComparison.Ordinal)))
                            _companions.Remove(role);
                        else _companions[role] = value;
                    }
                    break;
            }
            ClearResolution();
        });
    }

    internal void ClearResolution() { _model = null; Resolved = null; Evaluation = null; }

    /// <summary>Records an inspection or import outcome; a supported pack becomes the selection.</summary>
    internal void Resolve(TensorModelEvaluation evaluation, TensorModelPackDefinition? definition)
    {
        Evaluation = evaluation;
        Resolved = definition;
        _model = definition?.Key;
    }

    internal TensorModelPackDefinition? SelectedDefinition() => Choices().FirstOrDefault(model => model.Key == _model)
        ?? (Listing.Saved is { } saved && saved.Definition.Key == _model ? saved.Definition : null);

    private IEnumerable<TensorModelPackDefinition> Choices()
    {
        IEnumerable<TensorModelPackDefinition> offered = Listing.Models.Select(model => model.Definition);
        return Resolved is null || Listing.Models.Any(model => model.Definition.Key == Resolved.Key) ? offered : offered.Append(Resolved);
    }

    private SetupEditResult SelectModel(string value)
    {
        TensorModelPackDefinition? selected = Choices().FirstOrDefault(model => model.Key.ToString() == value);
        return selected is null ? new(SetupEditState.Invalid, [new("model-unsupported", "Select a supported pack.", "model")])
            : EditDraft(() =>
            {
                _model = selected.Key;
                if (selected.Key != Resolved?.Key)
                { _repository = _sourceRevision = _variant = _localPath = _license = _sha256 = string.Empty; _sourceUrl = null; _length = null; _companions.Clear(); Evaluation = null; Resolved = null; _locationKind = _hasSource ? "repository" : "file"; }
            });
    }

    private SetupEditResult SelectBackend(string value) =>
        Enum.TryParse(value, out TensorBackendKind backend) && Backends().Contains(backend)
            ? EditDraft(() => { _backend = backend; if (HasLocation) { ClearResolution(); } })
            : new(SetupEditState.Invalid, [new("backend-unsupported", "Select a supported backend.", "backend")]);

    private IEnumerable<TensorBackendKind> Backends() => new[] { TensorBackendKind.Auto }.Concat(
        SelectedDefinition()?.SupportedBackends ?? Listing.Models.SelectMany(model => model.Definition.SupportedBackends)).Distinct();

    /// <inheritdoc />
    protected override IReadOnlyList<SetupActionDescriptor> CreateActions()
    {
        bool selected = ValidateDraft().IsValid;
        bool live = _hasInstance && InstanceObservation?.State != TensorEngineState.Stopped;
        bool matchingBackend = _instanceBackend == _backend;
        return
        [
            new("backend-check", "Check backend prerequisites", SetupActionPurpose.CheckPrerequisites,
                SetupSideEffect.LocalVerification, _hasBackendProbe,
                !_hasBackendProbe ? "This composition does not provide a native backend probe." : null),
            new("resolve", "Inspect model location", SetupActionPurpose.DiscoverModels,
                SetupSideEffect.Network | SetupSideEffect.LocalVerification, _hasResolver && HasLocation,
                "Select a location. Inspection reads bounded metadata, never full weights; missing integrity requires explicit Import."),
            new("import", "Import model artifacts", SetupActionPurpose.Install,
                SetupSideEffect.Network | SetupSideEffect.Download | SetupSideEffect.LocalVerification, _hasResolver && HasLocation,
                "Copies complete artifacts, calculates hashes, validates compatibility and installs them into the pack directory. Does not load a model."),
            new("verify", "Check files", SetupActionPurpose.Verify,
                SetupSideEffect.LocalVerification, selected),
            new("load", "Load now", SetupActionPurpose.Load,
                SetupSideEffect.ModelLoad, selected && live && matchingBackend && InstanceObservation?.Model is null,
                !live ? "The host must supply a live instance." : !matchingBackend ? "The host must supply an instance configured for this backend." : null),
            new("unload", "Unload model", SetupActionPurpose.Unload, SetupSideEffect.ModelUnload, live),
            new("stop", "Stop instance permanently", SetupActionPurpose.Stop, SetupSideEffect.TerminalStop | SetupSideEffect.ModelUnload, live)
        ];
    }

    private bool HasLocation => _locationKind switch
    {
        "repository" => _hasSource && _repository.Length != 0 && _sourceRevision.Length != 0 && _variant.Length != 0,
        "url" => _sourceUrl is not null,
        _ => _localPath.Length != 0
    };

    private static TensorModelLocation? ParseLocation(string text) => Path.IsPathFullyQualified(text)
        ? new TensorModelLocation.LocalFile(text)
        : Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http"
            ? new TensorModelLocation.RemoteFile(uri) : null;

    internal TensorModelSelection CreateSelection(string? sourceId) => new(_locationKind switch
    {
        "repository" => new TensorModelLocation.RepositoryArtifact(sourceId!, new(_repository, _sourceRevision, _variant)),
        "folder" => new TensorModelLocation.LocalFolder(_localPath, _variant),
        "url" => new TensorModelLocation.RemoteFile(_sourceUrl!),
        _ => new TensorModelLocation.LocalFile(_localPath)
    }, _backend, string.IsNullOrWhiteSpace(_license) ? null : _license, _length, _sha256.Length == 0 ? null : _sha256,
        _companions.Where(item => item.Value.Length != 0).Select(item => new TensorArtifactLocation(item.Key, ParseLocation(item.Value)!)).ToArray());

    private SetupEditResult EditDraft(Action change)
    {
        lock (_backendCheckGate) { return Edit(change); }
    }

    /// <inheritdoc />
    protected override void OnDraftEdited()
    {
        base.OnDraftEdited();
        lock (_backendCheckGate)
        {
            ++_backendCheckRevision;
            _backendAvailability = null;
        }
    }

    /// <inheritdoc />
    protected override SetupValidation ValidateDraft() => SelectedDefinition() is null
        ? new([new("model-required", "Select a supported model pack.", "model")])
        : !Backends().Contains(_backend) ? new([new("backend-unsupported", "The pack does not support this backend.", "backend")])
        : SetupValidation.Valid;

    /// <inheritdoc />
    protected override bool IsDraftDirty => _model != _original?.Model
        || _backend != (_original?.Backend ?? TensorBackendKind.Auto)
        || _repository.Length != 0 || _sourceRevision.Length != 0 || _variant.Length != 0
        || _localPath.Length != 0 || _sourceUrl is not null || _license.Length != 0 || _sha256.Length != 0 || _length is not null
        || _companions.Count != 0 || _locationKind != (_hasSource ? "repository" : "file");

    /// <inheritdoc />
    protected override TensorSetupChanges CreateChanges() => new(Identity,
        new(Definition.Implementation.Id, _model!, _backend),
        _original is not null && (_original.Model != _model || _original.Backend != _backend), SelectedDefinition()!);
}

/// <summary>
/// Opens, acts on and saves one Tensor family's settings over the host's pack manager and optional
/// live instance. Import, Download and Remove write the pack directory at once; Load, Unload and Stop act on
/// the running instance at once. <see cref="StatusChanged"/> relays the host's <see cref="ITensorModelList.Changed"/>
/// and the instance's <see cref="ITensorModelInstance.EngineStatusChanged"/> while the editor listens.
/// </summary>
public sealed class TensorSettingsManager : InferenceSettingsManager<TensorSettings, TensorSetupChanges>, IInferenceSettingsStatusSource
{
    private readonly InferenceSetupDefinition _definition;
    private readonly ITensorModelList _models;
    private readonly ITensorModelPackManager _packs;
    private readonly ITensorModelInstance? _instance;
    private readonly ITensorBackendProbe? _backendProbe;
    private readonly ITensorModelResolver? _resolver;
    private readonly Lock _listeners = new();
    private EventHandler? _statusChanged;

    /// <summary>Creates a manager over the host's live model list, the host's pack manager and the optional instance, backend probe and model resolver.</summary>
    public TensorSettingsManager(InferenceSetupDefinition definition, ISetupCommitTarget<TensorSetupChanges> target,
        ITensorModelList models, ITensorModelPackManager packs,
        ITensorModelInstance? instance = null, ITensorBackendProbe? backendProbe = null, ITensorModelResolver? resolver = null)
        : base(target)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(packs);
        _definition = definition;
        _models = models;
        _packs = packs;
        _instance = instance;
        _backendProbe = backendProbe;
        _resolver = resolver;
    }

    /// <summary>Raised when the host's instance reports a lifecycle transition or the host's model list changes.</summary>
    public event EventHandler? StatusChanged
    {
        add
        {
            lock (_listeners)
            {
                if (_statusChanged is null)
                {
                    _models.Changed += OnModelsChanged;
                    if (_instance is not null) { _instance.EngineStatusChanged += OnEngineStatusChanged; }
                }
                _statusChanged += value;
            }
        }
        remove
        {
            lock (_listeners)
            {
                _statusChanged -= value;
                if (_statusChanged is null)
                {
                    _models.Changed -= OnModelsChanged;
                    if (_instance is not null) { _instance.EngineStatusChanged -= OnEngineStatusChanged; }
                }
            }
        }
    }

    /// <summary>Returns the family's settings, blank or from the saved configuration, with the instance's current state.</summary>
    public async Task<TensorSettings> OpenAsync(SetupConfigurationIdentity identity, TensorSetupConfiguration? existing = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TensorModelListing listing = await _models.ReadAsync(cancellationToken).ConfigureAwait(false);
        var settings = new TensorSettings(_definition, identity, listing, existing, _backendProbe is not null,
            _instance?.RequestedBackend, _resolver);
        if (_instance is not null)
        {
            settings.InstanceObservation = await _instance.GetObservationAsync(cancellationToken).ConfigureAwait(false);
        }
        return settings;
    }

    /// <summary>Reads the host's model list and the instance's current engine observation into Tensor settings. Other settings are left unchanged.</summary>
    public async Task RefreshStatusAsync(InferenceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings is not TensorSettings tensor) { return; }
        tensor.Listing = await _models.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (_instance is not null)
        {
            tensor.InstanceObservation = await _instance.GetObservationAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async Task<SetupActionResult> RunCoreAsync(TensorSettings settings, SetupActionDescriptor action,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        switch (action.Id)
        {
            case "backend-check": return await CheckBackendAsync(settings, cancellationToken).ConfigureAwait(false);
            case "resolve": return await ResolveAsync(settings, progress, cancellationToken).ConfigureAwait(false);
            case "import": return await ImportAsync(settings, progress, cancellationToken).ConfigureAwait(false);
            case "download": return await InstallAsync(settings, progress, cancellationToken).ConfigureAwait(false);
            case "remove":
                // An unreported saved model is not in the pack manager's catalog, and removal needs it there.
                await _packs.RegisterAsync(settings.SelectedDefinition()!, cancellationToken).ConfigureAwait(false);
                await _packs.RemoveAsync(settings.Model!, cancellationToken).ConfigureAwait(false);
                await RefreshStatusAsync(settings, CancellationToken.None).ConfigureAwait(false);
                return SetupActionResult.Completed;
            case "verify": return await VerifyAsync(settings, cancellationToken).ConfigureAwait(false);
            case "load":
                await _instance!.LoadAsync(settings.Model!, cancellationToken).ConfigureAwait(false);
                await RefreshStatusAsync(settings, CancellationToken.None).ConfigureAwait(false);
                return SetupActionResult.Completed;
            case "unload":
                await _instance!.UnloadAsync(cancellationToken).ConfigureAwait(false);
                await RefreshStatusAsync(settings, CancellationToken.None).ConfigureAwait(false);
                return SetupActionResult.Completed;
            case "stop":
                await _instance!.StopAsync(cancellationToken).ConfigureAwait(false);
                await RefreshStatusAsync(settings, CancellationToken.None).ConfigureAwait(false);
                return SetupActionResult.Completed;
            default:
                return new(SetupActionState.Unavailable);
        }
    }

    /// <inheritdoc />
    protected override async Task<SetupChoicePage> QueryProviderChoicesAsync(TensorSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken)
    {
        if (_resolver?.Source is not { } source) { return new([], State: SetupChoiceState.Unavailable); }
        switch (fieldId)
        {
            case "repository":
                return SourcePage(query, await source.SearchAsync(new(query.Search, query.Continuation, Math.Min(query.PageSize, 100)),
                    cancellationToken).ConfigureAwait(false), model => new(model.Repository, model.Repository, model.License), fieldId);
            case "source-revision":
                // Source paging remains authoritative; filtering must not invent a replacement cursor.
                return SourcePage(query, await source.ListRevisionsAsync(new(settings.Repository, query.Continuation, Math.Min(query.PageSize, 100)),
                    cancellationToken).ConfigureAwait(false), item => new(item.Revision, item.Revision), fieldId);
            case "variant":
                var variants = await source.ListVariantsAsync(new(settings.Repository, settings.SourceRevision), cancellationToken).ConfigureAwait(false);
                return variants.State == TensorSourceState.Available && variants.Value is not null
                    ? InferenceSettings.Page(query, variants.Value.Items.Select(item => new SetupChoice(item.Path, item.Path,
                        item.Length is long length ? $"{length} bytes" : "Size unknown")))
                    : SourcePage(query, variants, item => new(item.Path, item.Path), fieldId);
            default:
                return new([], State: SetupChoiceState.Unavailable);
        }
    }

    private static SetupChoicePage SourcePage<T>(SetupChoiceQuery query, TensorSourceResult<TensorSourcePage<T>> result,
        Func<T, SetupChoice> choice, string field) => result.State == TensorSourceState.Available && result.Value is not null
        ? new(result.Value.Items.Select(choice).Where(item => item.Value.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).ToArray(), result.Value.Continuation)
        : new([], State: SetupChoiceState.Failed,
            Issues: (result.Issues ?? [new("source-" + result.State.ToString().ToLowerInvariant(), "The source could not provide choices.")])
                .Select(issue => new SetupIssue(issue.Code, issue.Message, field)).ToArray());

    private async Task<SetupActionResult> ResolveAsync(TensorSettings settings, IProgress<SetupProgress>? progress, CancellationToken token)
    {
        TensorModelSelection selection = settings.CreateSelection(_resolver!.Source?.Id);
        settings.ClearResolution();
        progress?.Report(new("Resolving", "Inspecting bounded model metadata and required companions."));
        TensorModelResolution result = await _resolver.ResolveAsync(selection, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        TensorModelPackDefinition? definition = result.Evaluation.State == TensorCompatibilityState.Supported && result.Definition is not null
            ? new TensorModelCatalog([result.Definition]).GetRequired(result.Definition.Key) : null;
        settings.Resolve(result.Evaluation, definition);
        return definition is not null ? SetupActionResult.Completed : new(SetupActionState.Failed,
            result.Evaluation.Issues.Select(issue => new SetupIssue(issue.Code, issue.Message, "variant")).DefaultIfEmpty(
                new("model-unresolved", "Compatibility and the complete installable pack have not been established.", "variant")).ToArray(), IsRetryable: true);
    }

    private async Task<SetupActionResult> ImportAsync(TensorSettings settings, IProgress<SetupProgress>? progress, CancellationToken token)
    {
        TensorModelSelection selection = settings.CreateSelection(_resolver!.Source?.Id);
        settings.ClearResolution();
        TensorModelImportResult result = await _packs.ImportAsync(new(selection, _resolver), new InstallProgress(progress), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        TensorModelResolution resolution = result.Resolution;
        TensorModelPackDefinition? resolved = resolution.Evaluation.State == TensorCompatibilityState.Supported && resolution.Definition is not null
            ? await _packs.GetRequiredDefinitionAsync(resolution.Definition.Key, token).ConfigureAwait(false) : null;
        settings.Resolve(resolution.Evaluation, resolved);
        if (settings.Model is null)
        {
            return new(SetupActionState.Failed, resolution.Evaluation.Issues.Select(issue => new SetupIssue(issue.Code, issue.Message)).ToArray(), IsRetryable: true);
        }
        await RefreshStatusAsync(settings, token).ConfigureAwait(false);
        return await VerifyAsync(settings, token).ConfigureAwait(false);
    }

    private async Task<SetupActionResult> CheckBackendAsync(TensorSettings settings, CancellationToken token)
    {
        (long revision, TensorBackendProbeRequest request) = settings.ReserveBackendCheck();
        TensorBackendAvailability observation = await _backendProbe!.CheckAsync(request, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (observation.OperationId != request.OperationId || observation.ImplementationId != request.ImplementationId
            || observation.RequestedBackend != request.RequestedBackend
            || !string.Equals(observation.TargetRid, request.TargetRid, StringComparison.Ordinal))
            return new(SetupActionState.Failed, [new("backend-probe-mismatch", "The probe returned an observation for another request.", "backend")]);
        if (!settings.PublishBackendCheck(revision, observation, token)) return new(SetupActionState.Unavailable);
        return observation.IsAvailable ? SetupActionResult.Completed : new(SetupActionState.Failed,
            [new(observation.State switch
            {
                TensorBackendAvailabilityState.Unsupported => "backend-unsupported",
                TensorBackendAvailabilityState.RequiresProcessRestart => "backend-process-restart",
                TensorBackendAvailabilityState.Unknown => "backend-unverified",
                _ => "backend-unavailable"
            }, observation.Reason, "backend")],
            IsRetryable: observation.State is TensorBackendAvailabilityState.Unknown or TensorBackendAvailabilityState.Unavailable);
    }

    private async Task<SetupActionResult> InstallAsync(TensorSettings settings, IProgress<SetupProgress>? progress, CancellationToken token)
    {
        await _packs.RegisterAsync(settings.SelectedDefinition()!, token).ConfigureAwait(false);
        await _packs.InstallAsync(settings.Model!, new InstallProgress(progress), token).ConfigureAwait(false);
        return await VerifyAsync(settings, token).ConfigureAwait(false);
    }

    private async Task<SetupActionResult> VerifyAsync(TensorSettings settings, CancellationToken token)
    {
        if (!(await _packs.GetDefinitionsAsync(token).ConfigureAwait(false)).Any(definition => definition.Key == settings.Model))
        { return new(SetupActionState.Failed, [new("pack-not-installed", "Install the resolved pack explicitly before verifying it.", "model")]); }
        TensorModelPackStatus status = await _packs.VerifyAsync(settings.Model!, token).ConfigureAwait(false);
        bool valid = status.State == TensorModelPackState.Installed;
        await RefreshStatusAsync(settings, token).ConfigureAwait(false);
        return valid ? SetupActionResult.Completed : new(SetupActionState.Failed,
            [new("pack-unverified", "The selected pack is missing or failed integrity verification.", "model")], IsRetryable: true);
    }

    private void OnEngineStatusChanged(object? sender, TensorEngineStatus status) => Relay();

    private void OnModelsChanged(object? sender, EventArgs e) => Relay();

    private void Relay()
    {
        EventHandler? listeners;
        lock (_listeners) { listeners = _statusChanged; }
        if (listeners is null) { return; }
        foreach (EventHandler listener in listeners.GetInvocationList().Cast<EventHandler>())
        {
            try { listener(this, EventArgs.Empty); }
            catch (Exception)
            {
                // An editor's refresh does not own the engine; its failure cannot reach other listeners.
            }
        }
    }

    private sealed class InstallProgress(IProgress<SetupProgress>? progress) : IProgress<TensorModelInstallProgress>
    {
        public void Report(TensorModelInstallProgress value) => progress?.Report(
            new(value.Stage.ToString(), value.ArtifactRole, value.OverallBytesTransferred, value.OverallTotalBytes));
    }
}
