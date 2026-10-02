using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.TTS;

public sealed record TtsSetupConfiguration(TtsConnection Connection, string? VoiceId = null,
    double? Speed = null, TtsAudioFormat Format = TtsAudioFormat.Wav)
{
    [JsonRequired] public TtsConnection Connection { get; init => field = TtsData.Required(value); } = TtsData.Required(Connection);
    public string? Language { get; init; }
    public int? SampleRate { get; init; }
    public int? BitRate { get; init; }
    public bool? IncludeTimestamps { get; init; }
    internal TtsRequest Request(string text) => new()
    {
        Text = text,
        VoiceId = VoiceId,
        Speed = Speed,
        Format = Format,
        Language = Language,
        SampleRate = SampleRate,
        BitRate = BitRate,
        IncludeTimestamps = IncludeTimestamps,
        Xai = TtsData.EffectiveOptions(Connection.XaiDefaults, null)
    };
    public override string ToString() => "TtsSetupConfiguration { <data> }";
}
public sealed record TtsSetupHeaderChanges(IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Remove)
{
    [JsonRequired] public IReadOnlyDictionary<string, string> Set { get; init => field = TtsData.Copy(TtsData.Required(value), StringComparer.OrdinalIgnoreCase)!; } = TtsData.Copy(TtsData.Required(Set), StringComparer.OrdinalIgnoreCase)!;
    [JsonRequired] public IReadOnlyList<string> Remove { get; init => field = TtsData.Copy(value); } = TtsData.Copy(Remove);
    public override string ToString() => "TtsSetupHeaderChanges { <data> }";
}
public sealed record TtsSetupChanges(SetupConfigurationIdentity Identity, TtsSetupConfiguration Configuration, TtsSetupHeaderChanges Headers)
{
    [JsonRequired] public SetupConfigurationIdentity Identity { get; init => field = TtsData.Required(value); } = TtsData.Required(Identity);
    [JsonRequired] public TtsSetupConfiguration Configuration { get; init => field = TtsData.Required(value); } = TtsData.Required(Configuration);
    [JsonRequired] public TtsSetupHeaderChanges Headers { get; init => field = TtsData.Required(value); } = TtsData.Required(Headers);
    public override string ToString() => "TtsSetupChanges { <data> }";
}

/// <summary>Editable TTS settings. Plain data: edits change only this object.</summary>
public sealed class TtsSettings : InferenceSettings<TtsSetupChanges>
{
    private TtsSetupConfiguration _draft;
    private readonly TtsSetupConfiguration _baseline;
    private Uri? _endpoint;
    private long? _timeout;
    private readonly List<Header> _headers = [];
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;

    internal TtsSettings(InferenceSetupDefinition definition, TtsTransport transport, SetupConfigurationIdentity identity,
        TtsSetupConfiguration? existing)
        : base(definition, identity)
    {
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || existing is not null && existing.Connection.Transport != transport)
        { throw new ArgumentException("An edit requires the matching saved TTS configuration.", nameof(existing)); }
        Transport = transport;
        _draft = existing ?? new(new() { Transport = transport, Endpoint = DefaultEndpoint(transport) });
        _endpoint = _draft.Connection.Endpoint;
        _timeout = (long)_draft.Connection.RequestTimeout.TotalSeconds;
        foreach ((string name, string value) in _draft.Connection.Headers) { _headers.Add(new(name, value)); }
        _draft = _draft with { Connection = _draft.Connection with { Headers = new Dictionary<string, string>() } };
        _baseline = _draft;
        Status = new() { Configured = SetupObservation.Known(existing is not null) };
    }

    internal TtsTransport Transport { get; }

    internal TtsSetupConfiguration Draft => _draft;
    internal long Generation => Interlocked.Read(ref _generation);
    protected override void OnDraftEdited() { Interlocked.Increment(ref _generation); base.OnDraftEdited(); }
    private static Uri DefaultEndpoint(TtsTransport transport) => new(transport == TtsTransport.PocketTtsHttp ? "http://127.0.0.1:8020" : "https://api.x.ai/v1");

    protected override IReadOnlyList<SetupStep> CreateSteps()
    {
        if (Transport == TtsTransport.XaiTts) return CreateXaiSteps();
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

    private IReadOnlyList<SetupStep> CreateXaiSteps() =>
    [
        new("connection", "Connection", string.Empty, [new("connection", "Connection",
        [
            new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true }, _endpoint, DefaultEndpoint(Transport), value => Edit(() => _endpoint = value)),
            new SetupIntegerField(new("timeout", "Timeout (seconds)") { IsRequired = true }, _timeout, 300, value => Edit(() => _timeout = value), 1),
            new SetupTextField(new("xai-connection", "Signed-in connection") { IsRequired = true }, _draft.Connection.Xai?.ConnectionId ?? string.Empty,
                string.Empty, value => Edit(() => _draft = _draft with { Connection = _draft.Connection with { Xai = value.Length == 0 ? null : new(value) } })),
            Headers()
        ])]),
        new("speech", "Speech defaults", string.Empty, [new("speech", "Defaults",
        [
            new SetupChoiceField(new("voice", "Voice"), _draft.VoiceId ?? string.Empty, string.Empty, value => Edit(() => _draft = _draft with { VoiceId = value }),
                true, SetupSideEffect.Network, SetupChoiceCapabilities.Searchable),
            new SetupDecimalField(new("speed", "Speed"), _draft.Speed is { } speed && double.IsFinite(speed) && speed <= (double)decimal.MaxValue && speed >= (double)decimal.MinValue ? (decimal)speed : null,
                null, value => Edit(() => _draft = _draft with { Speed = value is null ? null : (double)value })),
            new SetupChoiceField(new("format", "Audio format"), _draft.Format.ToString(), TtsAudioFormat.Wav.ToString(), value =>
                Enum.TryParse(value, out TtsAudioFormat format) && Enum.IsDefined(format) ? Edit(() => _draft = _draft with { Format = format })
                    : new(SetupEditState.Invalid, [new("format-invalid", "Select an audio format.", "format")]), false),
            new SetupTextField(new("language", "Language") { IsRequired = true }, _draft.Language ?? string.Empty, string.Empty,
                value => Edit(() => _draft = _draft with { Language = value.Length == 0 ? null : value })),
            Number("sample-rate", "Sample rate (Hz)", _draft.SampleRate, value => _draft = _draft with { SampleRate = value }),
            Number("bit-rate", "Bit rate", _draft.BitRate, value => _draft = _draft with { BitRate = value }),
            Flag("timestamps", "Timestamps", _draft.IncludeTimestamps, value => _draft = _draft with { IncludeTimestamps = value }),
            Number("latency", "Streaming latency", _draft.Connection.XaiDefaults?.OptimizeStreamingLatency,
                value => SetXai((_draft.Connection.XaiDefaults ?? new()) with { OptimizeStreamingLatency = value })),
            Flag("normalization", "Text normalization", _draft.Connection.XaiDefaults?.TextNormalization,
                value => SetXai((_draft.Connection.XaiDefaults ?? new()) with { TextNormalization = value })),
            ReplacementMode(), Replacements()
        ])])
    ];

    private SetupIntegerField Number(string id, string label, int? value, Action<int?> set) => new(new(id, label), value, null, replacement =>
        replacement is < int.MinValue or > int.MaxValue ? new(SetupEditState.Invalid, [new("integer-width", "Use a 32-bit integer.", id)])
            : Edit(() => set((int?)replacement)), int.MinValue, int.MaxValue);
    private SetupChoiceField Flag(string id, string label, bool? value, Action<bool?> set) => new(new(id, label), value?.ToString() ?? string.Empty, string.Empty, replacement =>
        replacement.Length == 0 ? Edit(() => set(null)) : bool.TryParse(replacement, out bool flag) ? Edit(() => set(flag))
            : new(SetupEditState.Invalid, [new("flag-invalid", "Select true, false or omitted.", id)]), false);
    private void SetXai(XaiTtsOptions options) => _draft = _draft with { Connection = _draft.Connection with { XaiDefaults = options } };
    private SetupChoiceField ReplacementMode() => new(new("replace-mode", "Text replacement"), _draft.Connection.XaiDefaults?.Replace is null ? "Omitted" : "Map", "Omitted", value => value switch
    {
        "Omitted" => Edit(() => SetXai((_draft.Connection.XaiDefaults ?? new()) with { Replace = null })),
        "Map" => Edit(() => SetXai((_draft.Connection.XaiDefaults ?? new()) with { Replace = _draft.Connection.XaiDefaults?.Replace ?? new Dictionary<string, string>() })),
        _ => new(SetupEditState.Invalid, [new("replace-mode-invalid", "Select omitted or map.", "replace-mode")])
    }, false);
    private SetupRepeatableGroupField Replacements()
    {
        Dictionary<string, string> Copy() => new(_draft.Connection.XaiDefaults?.Replace ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        void Set(Dictionary<string, string> map) => SetXai((_draft.Connection.XaiDefaults ?? new()) with { Replace = map });
        SetupFieldRow Row(KeyValuePair<string, string> pair)
        {
            string key = pair.Key;
            return new(pair.Key,
            [
                new SetupTextField(new("replacement." + pair.Key + ".text", "Text"), pair.Key, string.Empty, value =>
                {
                    var map = Copy();
                    if (!map.TryGetValue(key, out string? replacement) || value != key && map.ContainsKey(value))
                        return new(SetupEditState.Invalid, [new("replacement-key-conflict", "Use a distinct replacement key.", "replacements")]);
                    return Edit(() => { map.Remove(key); map[value] = replacement; key = value; Set(map); });
                }),
                new SetupTextField(new("replacement." + pair.Key + ".value", "Replacement"), pair.Value, string.Empty, value =>
                {
                    var map = Copy();
                    if (!map.ContainsKey(key)) return new(SetupEditState.Invalid, [new("replacement-stale", "Read the current replacement entries.", "replacements")]);
                    return Edit(() => { map[key] = value; Set(map); });
                })
            ]);
        }
        return new(new("replacements", "Replacement entries"), Copy().Select(Row).ToArray(), () =>
        {
            var map = Copy();
            if (map.ContainsKey(string.Empty)) return new(SetupEditState.Invalid, [new("replacement-pending", "Name the existing new entry first.", "replacements")]);
            return Edit(() => { map[string.Empty] = string.Empty; Set(map); });
        }, key => Edit(() => { var map = Copy(); map.Remove(key); Set(map); }));
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
        "format" => Page(query, Transport == TtsTransport.PocketTtsHttp ? [new(nameof(TtsAudioFormat.Wav), "WAV")]
            : Enum.GetValues<TtsAudioFormat>().Select(format => new SetupChoice(format.ToString(), format.ToString()))),
        "timestamps" or "normalization" => Page(query, [new("", "Omitted"), new("True", "True"), new("False", "False")]),
        "replace-mode" => Page(query, [new("Omitted", "Omitted"), new("Map", "Explicit map")]),
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
        if (_endpoint is null || _timeout is null or < 1 || _timeout > TimeSpan.MaxValue.TotalSeconds
            || _headers.Select(header => header.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _headers.Count
            || Transport == TtsTransport.XaiTts && string.IsNullOrWhiteSpace(_draft.Connection.Xai?.ConnectionId)) { return false; }
        try { TtsProviderService.ValidateConnection(Connection()); return true; }
        catch (Exception error) when (error is InferenceException or ArgumentException or InvalidOperationException or FormatException) { return false; }
    }

    protected override SetupValidation ValidateDraft()
    {
        var issues = new List<SetupIssue>();
        if (!ConnectionValid()) { issues.Add(new("connection-invalid", "Check the HTTP endpoint, paths, timeout and protected headers.", StepId: "connection")); }
        if (_draft.Speed is { } speed && (!double.IsFinite(speed) || speed <= 0
            || Transport == TtsTransport.PocketTtsHttp && _draft.Connection.PocketTts.Protocol == PocketTtsProtocol.Multipart && speed != 1))
        { issues.Add(new("speed-invalid", "Use a positive speed supported by the selected protocol.", "speed")); }
        if (!Enum.IsDefined(_draft.Format) || Transport == TtsTransport.PocketTtsHttp && _draft.Format != TtsAudioFormat.Wav) { issues.Add(new("format-unsupported", "Select a supported audio format.", "format")); }
        if (Transport == TtsTransport.PocketTtsHttp && (_draft.Language is not null || _draft.SampleRate is not null || _draft.BitRate is not null
            || _draft.IncludeTimestamps is not null || _draft.Connection.XaiDefaults is not null))
        { issues.Add(new("controls-unsupported", "PocketTTS does not implement these explicit speech controls.", StepId: "speech")); }
        if (Transport == TtsTransport.XaiTts && string.IsNullOrWhiteSpace(_draft.Language))
            issues.Add(new("language-required", "Supply the speech language.", "language"));
        return new(issues.AsReadOnly());
    }

    protected override bool IsDraftDirty => _draft.Connection != _baseline.Connection
        || (_draft.VoiceId ?? string.Empty) != (_baseline.VoiceId ?? string.Empty)
        || _draft.Speed != _baseline.Speed || _draft.Format != _baseline.Format || _endpoint != _baseline.Connection.Endpoint
        || _draft.Language != _baseline.Language || _draft.SampleRate != _baseline.SampleRate
        || _draft.BitRate != _baseline.BitRate || _draft.IncludeTimestamps != _baseline.IncludeTimestamps
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
        return new(Identity, _draft with { Connection = Connection() with { Authorization = null, Headers = new Dictionary<string, string>() } }, new(set, removed.ToArray()));
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

/// <summary>Owns setup admission and drain. Providers, binding factory, resource access and commit target remain borrowed.</summary>
public sealed class TtsSettingsManager : InferenceSettingsManager<TtsSettings, TtsSetupChanges>, IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private TtsProviderService? _service;
    private Func<XaiConnectionSelection, XaiFamilyBinding>? _xaiBindings;
    private IInferenceResourceAccess? _resources;
    private readonly List<(Exception Failure, ITtsClient Owner)> _cleanupFailures = [];

    internal TtsSettingsManager(TtsProviderService service, ISetupCommitTarget<TtsSetupChanges> target,
        Func<XaiConnectionSelection, XaiFamilyBinding>? xaiBindings, IInferenceResourceAccess? resources)
        : base(target) { _service = service; _xaiBindings = xaiBindings; _resources = resources; }

    /// <summary>Returns settings for <paramref name="transport"/>, blank or from the saved entry.</summary>
    public Task<TtsSettings> OpenAsync(TtsTransport transport, SetupConfigurationIdentity identity,
        TtsSetupConfiguration? existing = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        ITtsProvider provider = _service!.Provider(transport);
        InferenceSetupDefinition definition = provider.SetupDefinition;
        Fence(lease.Token);
        var settings = new TtsSettings(definition, transport, identity, existing);
        Fence(lease.Token);
        return Task.FromResult(settings);
    }

    public override async Task<SetupActionResult> RunAsync(InferenceSettings settings, string actionId,
        IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        SetupActionResult result = await base.RunAsync(settings, actionId, progress, lease.Token).ConfigureAwait(false);
        return IsStopping(lease.Token) ? new(SetupActionState.Cancelled) : result;
    }
    public override async Task<SetupChoicePage> QueryChoicesAsync(InferenceSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        SetupChoicePage result = await base.QueryChoicesAsync(settings, fieldId, query, lease.Token).ConfigureAwait(false);
        return IsStopping(lease.Token) ? new([], State: SetupChoiceState.Cancelled) : result;
    }
    public override async Task<SetupCommitResult> SaveAsync(InferenceSettings settings, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        return await base.SaveAsync(settings, lease.Token).ConfigureAwait(false);
    }

    protected override async Task<SetupActionResult> RunCoreAsync(TtsSettings settings, SetupActionDescriptor action,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        long generation = settings.Generation;
        TtsConnection connection = settings.Connection();
        TtsSetupConfiguration draft = settings.Draft;
        if (action.Id == "test" && settings.Transport == TtsTransport.PocketTtsHttp)
            PocketTtsHttpProvider.ValidateRequest(connection, draft.Request("This is a speech test."));
        return await UseClientAsync<SetupActionResult>(connection, async client =>
        {
            if (client is null) return new(SetupActionState.Unavailable);
            if (settings.Generation != generation) return new(SetupActionState.Cancelled);
            switch (action.Id)
            {
            case "check":
                settings.Status = settings.Status with { Verified = SetupObservation.Unknown };
                await client.ListVoicesAsync(cancellationToken).ConfigureAwait(false);
                Fence(cancellationToken);
                if (settings.Generation != generation) return new(SetupActionState.Cancelled);
                settings.Status = settings.Status with { Verified = SetupObservation.Known(true) };
                return SetupActionResult.Completed;
            case "test":
                settings.Status = settings.Status with { Ready = SetupObservation.Unknown };
                await client.SpeakAsync(draft.Request("This is a speech test."),
                    cancellationToken).ConfigureAwait(false);
                Fence(cancellationToken);
                if (settings.Generation != generation) return new(SetupActionState.Cancelled);
                settings.Status = settings.Status with { Ready = SetupObservation.Known(true), Verified = SetupObservation.Known(true) };
                return SetupActionResult.Completed;
            default:
                return new(SetupActionState.Unavailable);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<SetupChoicePage> QueryProviderChoicesAsync(TtsSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken)
    {
        if (fieldId != "voice") { return new([], State: SetupChoiceState.Unavailable); }
        long generation = settings.Generation;
        TtsConnection connection = settings.Connection();
        return await UseClientAsync<SetupChoicePage>(connection, async client =>
        {
            if (client is null) return new([], State: SetupChoiceState.Unavailable);
            if (settings.Generation != generation) return new([], State: SetupChoiceState.Cancelled);
            IReadOnlyList<TtsVoice> voices = await client.ListVoicesAsync(cancellationToken).ConfigureAwait(false);
            Fence(cancellationToken);
            if (settings.Generation != generation) return new([], State: SetupChoiceState.Cancelled);
            return InferenceSettings.Page(query, voices.Select(voice => new SetupChoice(voice.Id, voice.DisplayName, voice.Description)));
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> UseClientAsync<T>(TtsConnection connection, Func<ITtsClient?, Task<T>> action, CancellationToken token)
    {
        ITtsClient? client = await BindAsync(connection, token).ConfigureAwait(false);
        Exception? failure = null;
        T result = default!;
        try { result = await action(client).ConfigureAwait(false); }
        catch (Exception original) { failure = original; }
        if (client is not null)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
                lock (_cleanupFailures) _cleanupFailures.Add((failure, client));
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private async Task<ITtsClient?> BindAsync(TtsConnection connection, CancellationToken token)
    {
        TtsProviderService.ValidateConnection(connection);
        Fence(token);
        XaiFamilyBinding? binding = null;
        if (connection.Transport == TtsTransport.XaiTts)
        {
            if (_xaiBindings is null || connection.Xai is null) return null;
            binding = _xaiBindings(connection.Xai);
            Fence(token);
        }
        // Readiness checks and sample clips do not select persistent session artifacts.
        ITtsClient result = _service!.Bind(connection, binding);
        try { Fence(token); return result; }
        catch (Exception original)
        {
            try { await result.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                var failure = new AggregateException(original, cleanup);
                lock (_cleanupFailures) _cleanupFailures.Add((failure, result));
                throw failure;
            }
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
    }
    private void Fence(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("The TTS settings owner is retiring.", error, token); }
    }
    private bool IsStopping(CancellationToken token)
    {
        if (token.IsCancellationRequested) return true;
        try { _lifetime.ThrowIfStopping(); return false; }
        catch (ObjectDisposedException) { return true; }
    }
    public void Dispose() => Retire().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(Retire());
    private Task Retire() => _lifetime.RetireAsync(() =>
    {
        _service = null; _xaiBindings = null; _resources = null; DetachCommitTarget();
        if (_cleanupFailures.Count == 1) return Task.FromException(_cleanupFailures[0].Failure);
        if (_cleanupFailures.Count > 1) return Task.FromException(new AggregateException(_cleanupFailures.Select(item => item.Failure)));
        return Task.CompletedTask;
    });
}
