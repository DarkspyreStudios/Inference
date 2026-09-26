using System.Globalization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.TTS;

public sealed record TtsSetupConfiguration(TtsConnection Connection, string? VoiceId = null,
    double? Speed = null, TtsAudioFormat Format = TtsAudioFormat.Wav);
public sealed record TtsSetupHeaderChanges(IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Remove);
public sealed record TtsSetupChanges(SetupConfigurationIdentity Identity, TtsSetupConfiguration Configuration, TtsSetupHeaderChanges Headers);

/// <summary>Editable PocketTTS settings. Plain data: edits change only this object.</summary>
public sealed class TtsSettings : InferenceSettings<TtsSetupChanges>
{
    private TtsSetupConfiguration _draft;
    private readonly TtsSetupConfiguration _baseline;
    private Uri? _endpoint;
    private long? _timeout;
    private readonly List<Header> _headers = [];
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    internal TtsSettings(InferenceSetupDefinition definition, TtsTransport transport, SetupConfigurationIdentity identity,
        TtsSetupConfiguration? existing)
        : base(definition, identity)
    {
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || existing is not null && existing.Connection.Transport != transport)
        { throw new ArgumentException("An edit requires the matching saved TTS configuration.", nameof(existing)); }
        Transport = transport;
        _draft = existing ?? new(new() { Transport = transport, Endpoint = new("http://127.0.0.1:8020") });
        _endpoint = _draft.Connection.Endpoint;
        _timeout = (long)_draft.Connection.RequestTimeout.TotalSeconds;
        foreach ((string name, string value) in _draft.Connection.Headers) { _headers.Add(new(name, value)); }
        _draft = _draft with { Connection = _draft.Connection with { Headers = new Dictionary<string, string>() } };
        _baseline = _draft;
        Status = new() { Configured = SetupObservation.Known(existing is not null) };
    }

    internal TtsTransport Transport { get; }

    internal TtsSetupConfiguration Draft => _draft;

    protected override IReadOnlyList<SetupStep> CreateSteps()
    {
        PocketTtsEndpointOptions options = _draft.Connection.PocketTts;
        SetupTextField Path(string id, string label, string value, string defaultValue,
            Func<PocketTtsEndpointOptions, string, PocketTtsEndpointOptions> set) => new(new(id, label) { IsRequired = true }, value, defaultValue,
                replacement => Edit(() => _draft = _draft with { Connection = _draft.Connection with { PocketTts = set(_draft.Connection.PocketTts, replacement) } }));
        return
        [
            new("connection", "External endpoint", "The host or administrator runs PocketTTS. This library only sends HTTP requests.",
            [new("connection", "Connection",
            [
                new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true }, _endpoint, new("http://127.0.0.1:8020"), value => Edit(() => _endpoint = value)),
                new SetupIntegerField(new("timeout", "Timeout (seconds)") { IsRequired = true }, _timeout, 300, value => Edit(() => _timeout = value), 1, 86400),
                new SetupChoiceField(new("protocol", "Protocol mode") { IsRequired = true }, options.Protocol.ToString(), PocketTtsProtocol.Multipart.ToString(),
                    value => Enum.TryParse(value, out PocketTtsProtocol mode) && Enum.IsDefined(mode)
                        ? Edit(() => _draft = _draft with { Connection = _draft.Connection with { PocketTts = options with { Protocol = mode } } })
                        : new(SetupEditState.Invalid, [new("protocol-invalid", "Select a supported protocol.", "protocol")]), false)
            ]), new("paths", "Endpoint options",
            [
                Path("voices-path", "Voices path", options.VoicesPath, "/voices", (current, value) => current with { VoicesPath = value }),
                Path("speak-path", "Speech path", options.SpeakPath, "/tts", (current, value) => current with { SpeakPath = value }),
                Path("health-path", "Empty-list fallback path", options.HealthPath, "/health", (current, value) => current with { HealthPath = value }),
                Path("voice-field", "Multipart voice field", options.VoiceFormField, "voice_url", (current, value) => current with { VoiceFormField = value }),
                Headers()
            ], IsAdvanced: true)]),
            new("speech", "Speech defaults", "Voice listing does not synthesize speech. The explicit synthesis test can incur usage charges.",
            [new("speech", "Defaults",
            [
                new SetupChoiceField(new("voice", "Voice") { HelpText = "Use a listed voice or enter the exact server voice ID; blank uses the server default." },
                    _draft.VoiceId ?? string.Empty, string.Empty, value => Edit(() => _draft = _draft with { VoiceId = value }), true,
                    SetupSideEffect.Network, SetupChoiceCapabilities.Searchable),
                new SetupDecimalField(new("speed", "Speed") { HelpText = options.Protocol == PocketTtsProtocol.Multipart
                    ? "Multipart supports only default speed (1)." : "A positive speed is passed to the configured bridge." },
                    _draft.Speed is { } speed && double.IsFinite(speed) && speed <= (double)decimal.MaxValue && speed >= (double)decimal.MinValue ? (decimal)speed : null,
                    null, value => Edit(() => _draft = _draft with { Speed = value is null ? null : (double)value }),
                    options.Protocol == PocketTtsProtocol.Multipart ? 1 : 0, options.Protocol == PocketTtsProtocol.Multipart ? 1 : null, 0.1m),
                new SetupChoiceField(new("format", "Audio format"), _draft.Format.ToString(), TtsAudioFormat.Wav.ToString(),
                    value => value == nameof(TtsAudioFormat.Wav) ? Edit(() => _draft = _draft with { Format = TtsAudioFormat.Wav })
                        : new(SetupEditState.Invalid, [new("format-unsupported", "Only WAV is supported.", "format")]), false)
            ])])
        ];
    }

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

    protected override SetupChoicePage? GetLocalChoices(string fieldId, SetupChoiceQuery query) => fieldId switch
    {
        "protocol" => Page(query, Enum.GetValues<PocketTtsProtocol>().Select(mode => new SetupChoice(mode.ToString(), mode.ToString()))),
        "format" => Page(query, [new(nameof(TtsAudioFormat.Wav), "WAV")]),
        _ => null
    };

    protected override IReadOnlyList<SetupActionDescriptor> CreateActions() =>
    [
        new("check", "Check endpoint and voices", SetupActionPurpose.CheckReadiness, SetupSideEffect.Network, ConnectionValid()),
        new("test", "Synthesize a test clip", SetupActionPurpose.Test, SetupSideEffect.Network | SetupSideEffect.PotentialBilling,
            ValidateDraft().IsValid)
    ];

    internal TtsConnection Connection() => _draft.Connection with
    {
        Endpoint = _endpoint!,
        RequestTimeout = TimeSpan.FromSeconds(_timeout ?? 0),
        Headers = _headers.Where(header => header.Value is not null).ToDictionary(header => header.Name, header => header.Value!, StringComparer.OrdinalIgnoreCase)
    };

    private bool ConnectionValid()
    {
        if (_endpoint is null || _timeout is null or < 1 or > 86400 || _headers.Select(header => header.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _headers.Count) { return false; }
        try { PocketTtsHttpProvider.ValidateConnection(Connection()); return true; }
        catch (InferenceException) { return false; }
    }

    protected override SetupValidation ValidateDraft()
    {
        var issues = new List<SetupIssue>();
        if (!ConnectionValid()) { issues.Add(new("connection-invalid", "Check the HTTP endpoint, paths, timeout and protected headers.", StepId: "connection")); }
        if (_draft.Speed is { } speed && (!double.IsFinite(speed) || speed <= 0
            || _draft.Connection.PocketTts.Protocol == PocketTtsProtocol.Multipart && speed != 1))
        { issues.Add(new("speed-invalid", "Use a positive speed supported by the selected protocol.", "speed")); }
        if (_draft.Format != TtsAudioFormat.Wav) { issues.Add(new("format-unsupported", "Only WAV is supported.", "format")); }
        return new(issues.AsReadOnly());
    }

    protected override bool IsDraftDirty => _draft.Connection != _baseline.Connection
        || (_draft.VoiceId ?? string.Empty) != (_baseline.VoiceId ?? string.Empty)
        || _draft.Speed != _baseline.Speed || _draft.Format != _baseline.Format || _endpoint != _baseline.Connection.Endpoint
        || _timeout != (long)_baseline.Connection.RequestTimeout.TotalSeconds || _removed.Count != 0
        || _headers.Any(header => header.OriginalName.Length == 0 || header.Name != header.OriginalName
            || header.Edit.Kind != SetupSecretEditKind.Keep);

    protected override TtsSetupChanges CreateChanges()
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
        return new(Identity, _draft with { Connection = Connection() with { Headers = new Dictionary<string, string>() } }, new(set, removed.ToArray()));
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

/// <summary>Opens, acts on and saves PocketTTS settings. The check and test actions call the endpoint.</summary>
public sealed class TtsSettingsManager : InferenceSettingsManager<TtsSettings, TtsSetupChanges>
{
    private readonly IReadOnlyDictionary<TtsTransport, ITtsProvider> _providers;

    internal TtsSettingsManager(IReadOnlyDictionary<TtsTransport, ITtsProvider> providers, ISetupCommitTarget<TtsSetupChanges> target)
        : base(target) => _providers = providers;

    /// <summary>Returns settings for <paramref name="transport"/>, blank or from the saved entry.</summary>
    public Task<TtsSettings> OpenAsync(TtsTransport transport, SetupConfigurationIdentity identity,
        TtsSetupConfiguration? existing = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ITtsProvider provider = _providers[transport];
        return Task.FromResult(new TtsSettings(provider.SetupDefinition, provider.Transport, identity, existing));
    }

    protected override async Task<SetupActionResult> RunCoreAsync(TtsSettings settings, SetupActionDescriptor action,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        ITtsClient client = _providers[settings.Transport].Bind(settings.Connection());
        switch (action.Id)
        {
            case "check":
                settings.Status = settings.Status with { Verified = SetupObservation.Unknown };
                await client.ListVoicesAsync(cancellationToken).ConfigureAwait(false);
                settings.Status = settings.Status with { Verified = SetupObservation.Known(true) };
                return SetupActionResult.Completed;
            case "test":
                settings.Status = settings.Status with { Ready = SetupObservation.Unknown };
                TtsSetupConfiguration draft = settings.Draft;
                await client.SpeakAsync(new() { Text = "This is a speech test.", VoiceId = draft.VoiceId, Speed = draft.Speed, Format = draft.Format },
                    cancellationToken).ConfigureAwait(false);
                settings.Status = settings.Status with { Ready = SetupObservation.Known(true), Verified = SetupObservation.Known(true) };
                return SetupActionResult.Completed;
            default:
                return new(SetupActionState.Unavailable);
        }
    }

    protected override async Task<SetupChoicePage> QueryProviderChoicesAsync(TtsSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken)
    {
        if (fieldId != "voice") { return new([], State: SetupChoiceState.Unavailable); }
        IReadOnlyList<TtsVoice> voices = await _providers[settings.Transport].Bind(settings.Connection())
            .ListVoicesAsync(cancellationToken).ConfigureAwait(false);
        return InferenceSettings.Page(query, voices.Select(voice => new SetupChoice(voice.Id, voice.DisplayName, voice.Description)));
    }
}
