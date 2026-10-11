using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.Audio;

/// <summary>A saved audio configuration: the connection and the generation defaults.</summary>
/// <param name="Connection">The connection to the TangoFlux endpoint.</param>
public sealed record AudioSetupConfiguration(AudioConnection Connection)
{
    /// <summary>The default clip length. The default is five seconds.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>The default number of sampling steps, or null for the adapter default.</summary>
    public int? Steps { get; init; }
    /// <summary>The default guidance scale, or null for the adapter default.</summary>
    public double? GuidanceScale { get; init; }
    /// <summary>The default seed, or null to leave seed selection to the server.</summary>
    public int? Seed { get; init; }
    /// <summary>True to allow the provider's cache, false to bypass it, or null for the server default.</summary>
    public bool? UseCache { get; init; }
}

/// <summary>The protected HTTP header edits a save applies.</summary>
/// <param name="Set">The headers to add or replace, by name.</param>
/// <param name="Remove">The names of headers to remove.</param>
public sealed record AudioSetupHeaderChanges(IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Remove);
/// <summary>The changes the host commits when audio settings are saved.</summary>
/// <param name="Identity">The entry being created or edited and its expected version.</param>
/// <param name="Configuration">The configuration to save, without protected headers.</param>
/// <param name="Headers">The protected header edits to apply.</param>
public sealed record AudioSetupChanges(SetupConfigurationIdentity Identity, AudioSetupConfiguration Configuration, AudioSetupHeaderChanges Headers);

/// <summary>Editable TangoFlux settings. Plain data: edits change only this object.</summary>
public sealed class AudioSettings : InferenceSettings<AudioSetupChanges>
{
    private AudioSetupConfiguration _draft;
    private readonly AudioSetupConfiguration _baseline;
    private Uri? _endpoint;
    private decimal? _timeout;
    private decimal? _duration;
    private long? _steps;
    private long? _seed;
    private readonly List<Header> _headers = [];
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    internal AudioSettings(InferenceSetupDefinition definition, AudioTransport transport, SetupConfigurationIdentity identity,
        AudioSetupConfiguration? existing)
        : base(definition, identity)
    {
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || existing is not null && existing.Connection.Transport != transport)
        { throw new ArgumentException("An edit requires the matching saved audio configuration.", nameof(existing)); }
        Transport = transport;
        _draft = existing ?? new(new() { Transport = transport, Endpoint = new("http://127.0.0.1:8769") });
        _endpoint = _draft.Connection.Endpoint;
        _timeout = (decimal)_draft.Connection.RequestTimeout.TotalSeconds;
        _duration = (decimal)_draft.Duration.TotalSeconds;
        _steps = _draft.Steps;
        _seed = _draft.Seed;
        foreach ((string name, string value) in _draft.Connection.Headers) { _headers.Add(new(name, value)); }
        _draft = _draft with { Connection = _draft.Connection with { Headers = new Dictionary<string, string>() } };
        _baseline = _draft;
        Status = new() { Configured = SetupObservation.Known(existing is not null) };
    }

    internal AudioTransport Transport { get; }

    /// <inheritdoc />
    protected override IReadOnlyList<SetupStep> CreateSteps() =>
    [
        new("connection", "External endpoint", "The host or administrator runs TangoFlux. Model listing and a separate health check are unavailable.",
        [new("connection", "Connection",
        [
            new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true }, _endpoint, new("http://127.0.0.1:8769"), value => Edit(() => _endpoint = value)),
            new SetupDecimalField(new("timeout", "Timeout (seconds)") { IsRequired = true, HelpText = "A positive per-request timeout of at most one day." },
                _timeout, 12, value => Edit(() => _timeout = value), 0, 86400, 0.1m)
        ]), new("options", "Endpoint options",
        [
            new SetupTextField(new("generate-path", "Generation path") { IsRequired = true }, _draft.Connection.TangoFlux.GeneratePath, "/generate",
                value => Edit(() => _draft = _draft with { Connection = _draft.Connection with { TangoFlux = new() { GeneratePath = value } } })),
            Headers()
        ], IsAdvanced: true)]),
        new("audio", "Audio defaults", "Saving does not generate audio. The explicit generation test can incur usage charges.",
        [new("generation", "Generation",
        [
            new SetupDecimalField(new("duration", "Duration (seconds)") { IsRequired = true }, _duration, 5, value => Edit(() => _duration = value), 1, 30, 0.1m),
            new SetupIntegerField(new("steps", "Steps") { HelpText = "Blank uses the adapter default of 10." }, _steps, null,
                value => Edit(() => _steps = value), 10, 100),
            new SetupDecimalField(new("guidance", "Guidance scale") { HelpText = "Blank uses the adapter default of 4.5." },
                _draft.GuidanceScale is { } guidance && double.IsFinite(guidance) && guidance > (double)decimal.MinValue && guidance < (double)decimal.MaxValue
                    ? (decimal)guidance : null, null,
                value => Edit(() => _draft = _draft with { GuidanceScale = value is null ? null : (double)value }), 0.1m, 20, 0.1m),
            new SetupIntegerField(new("seed", "Seed") { HelpText = "Blank leaves seed selection to the server. Cache preference never changes the seed." },
                _seed, null, value => Edit(() => _seed = value), int.MinValue, int.MaxValue),
            new SetupChoiceField(new("cache", "Provider cache") { HelpText = "Controls only the provider's cache, not an application cache." },
                CacheValue(_draft.UseCache), "default", value => value is "default" or "use" or "bypass"
                    ? Edit(() => _draft = _draft with { UseCache = value == "default" ? null : value == "use" })
                    : new(SetupEditState.Invalid, [new("cache-invalid", "Select a supported cache preference.", "cache")]),
                false)
        ])])
    ];

    private static string CacheValue(bool? value) => value switch { true => "use", false => "bypass", _ => "default" };
    /// <inheritdoc />
    protected override SetupChoicePage? GetLocalChoices(string fieldId, SetupChoiceQuery query) => fieldId == "cache"
        ? Page(query, [new("default", "Server default"), new("use", "Allow provider cache"), new("bypass", "Bypass provider cache")])
        : null;

    private SetupRepeatableGroupField Headers() => new(new("headers", "Protected HTTP headers"),
        _headers.Select(header => new SetupFieldRow(header.Id,
        [
            new SetupTextField(new(header.Id + ".name", "Name"), header.Name, string.Empty, value => Edit(() => header.Name = value)),
            new SetupSecretField(new(header.Id + ".value", "Value"), header.Value is not null, header.Edit.Kind, value => Edit(() => header.Edit = value))
        ])).ToArray(), () => Edit(() => _headers.Add(new(string.Empty, null))),
        id => Edit(() =>
        {
            Header? header = _headers.Find(item => item.Id == id);
            if (header is null) { return; }
            if (header.OriginalName.Length > 0) { _removed.Add(header.OriginalName); }
            _headers.Remove(header);
        }));

    /// <inheritdoc />
    protected override IReadOnlyList<SetupActionDescriptor> CreateActions() =>
    [
        new("test", "Generate a test clip", SetupActionPurpose.Test, SetupSideEffect.Network | SetupSideEffect.PotentialBilling,
            ValidateDraft().IsValid)
    ];

    private AudioConnection Connection() => _draft.Connection with
    {
        Endpoint = _endpoint!,
        RequestTimeout = TimeSpan.FromSeconds((double)(_timeout ?? 0)),
        Headers = _headers.Where(header => header.Value is not null).ToDictionary(header => header.Name, header => header.Value!, StringComparer.OrdinalIgnoreCase)
    };

    internal AudioSetupConfiguration Configuration() => _draft with
    {
        Connection = Connection(),
        Duration = TimeSpan.FromSeconds((double)_duration!),
        Steps = (int?)_steps,
        Seed = (int?)_seed
    };

    private bool ConnectionValid()
    {
        if (_endpoint is null || _timeout is null or <= 0 or > 86400
            || _headers.Any(header => string.IsNullOrWhiteSpace(header.Name))
            || _headers.Select(header => header.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _headers.Count) { return false; }
        try { TangoFluxHttpProvider.ValidateConnection(Connection()); return true; }
        catch (InferenceException) { return false; }
    }

    /// <inheritdoc />
    protected override SetupValidation ValidateDraft()
    {
        var issues = new List<SetupIssue>();
        if (!ConnectionValid()) { issues.Add(new("connection-invalid", "Check the HTTP endpoint, path, timeout and protected headers.", StepId: "connection")); }
        if (_duration is null or < 1 or > 30) { issues.Add(new("duration-invalid", "Use a duration of 1–30 seconds.", "duration")); }
        if (_steps is < 10 or > 100) { issues.Add(new("steps-invalid", "Use 10–100 steps or leave blank for the default.", "steps")); }
        if (_draft.GuidanceScale is { } guidance && (!double.IsFinite(guidance) || guidance is < 0.1 or > 20))
        { issues.Add(new("guidance-invalid", "Use finite guidance of 0.1–20 or leave blank for the default.", "guidance")); }
        if (_seed is < int.MinValue or > int.MaxValue) { issues.Add(new("seed-invalid", "Use a 32-bit seed or leave blank for server selection.", "seed")); }
        return new(issues.AsReadOnly());
    }

    /// <inheritdoc />
    protected override bool IsDraftDirty => _draft != _baseline || _endpoint != _baseline.Connection.Endpoint
        || _timeout != (decimal)_baseline.Connection.RequestTimeout.TotalSeconds
        || _duration != (decimal)_baseline.Duration.TotalSeconds || _steps != _baseline.Steps || _seed != _baseline.Seed
        || _removed.Count != 0 || _headers.Any(header => header.OriginalName.Length == 0
            || header.Name != header.OriginalName || header.Edit.Kind != SetupSecretEditKind.Keep);

    /// <inheritdoc />
    protected override AudioSetupChanges CreateChanges()
    {
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(_removed, StringComparer.OrdinalIgnoreCase);
        foreach (Header header in _headers)
        {
            bool renamed = !header.Name.Equals(header.OriginalName, StringComparison.OrdinalIgnoreCase);
            if (header.OriginalName.Length > 0 && (renamed || header.Edit.Kind == SetupSecretEditKind.Clear)) { removed.Add(header.OriginalName); }
            if (header.Value is not null && (renamed || header.Edit.Kind == SetupSecretEditKind.Replace)) { set[header.Name] = header.Value; }
        }
        removed.ExceptWith(set.Keys);
        AudioSetupConfiguration settings = Configuration();
        return new(Identity, settings with { Connection = settings.Connection with { Headers = new Dictionary<string, string>() } }, new(set, removed.ToArray()));
    }

    private sealed class Header(string name, string? originalValue)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = name;
        public string OriginalName { get; } = name;
        public SetupSecretEdit Edit { get; set; } = SetupSecretEdit.Keep;
        public string? Value => Edit.Kind switch { SetupSecretEditKind.Replace => Edit.Value, SetupSecretEditKind.Clear => null, _ => originalValue };
    }
}

/// <summary>Opens, acts on and saves TangoFlux settings. The test action calls the endpoint.</summary>
public sealed class AudioSettingsManager : InferenceSettingsManager<AudioSettings, AudioSetupChanges>
{
    private readonly IReadOnlyDictionary<AudioTransport, IAudioProvider> _providers;

    internal AudioSettingsManager(IReadOnlyDictionary<AudioTransport, IAudioProvider> providers, ISetupCommitTarget<AudioSetupChanges> target)
        : base(target) => _providers = providers;

    /// <summary>Returns settings for <paramref name="transport"/>, blank or from the saved entry.</summary>
    public Task<AudioSettings> OpenAsync(AudioTransport transport, SetupConfigurationIdentity identity,
        AudioSetupConfiguration? existing = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IAudioProvider provider = _providers[transport];
        return Task.FromResult(new AudioSettings(provider.SetupDefinition, provider.Transport, identity, existing));
    }

    /// <inheritdoc />
    protected override async Task<SetupActionResult> RunCoreAsync(AudioSettings settings, SetupActionDescriptor action,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        if (action.Id != "test") { return new(SetupActionState.Unavailable); }
        settings.Status = settings.Status with { Ready = SetupObservation.Unknown, Verified = SetupObservation.Unknown };
        AudioSetupConfiguration configuration = settings.Configuration();
        await _providers[settings.Transport].Bind(configuration.Connection).GenerateAsync(new()
        {
            Prompt = "A gentle breeze rustling leaves.",
            Duration = configuration.Duration,
            Steps = configuration.Steps,
            GuidanceScale = configuration.GuidanceScale,
            Seed = configuration.Seed,
            UseCache = configuration.UseCache
        }, cancellationToken).ConfigureAwait(false);
        settings.Status = settings.Status with { Ready = SetupObservation.Known(true), Verified = SetupObservation.Known(true) };
        return SetupActionResult.Completed;
    }
}
