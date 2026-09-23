using System.Globalization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.TTS;

public sealed record TtsSetupConfiguration(TtsConnection Connection, string? VoiceId = null,
    double? Speed = null, TtsAudioFormat Format = TtsAudioFormat.Wav);
public sealed record TtsSetupHeaderChanges(IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Remove);
public sealed record TtsSetupChanges(SetupConfigurationIdentity Identity, TtsSetupConfiguration Configuration, TtsSetupHeaderChanges Headers);

public sealed class TtsSetupSession : InferenceSetupSession<TtsSetupChanges>
{
    private readonly ITtsProvider _provider;
    private readonly SetupConfigurationIdentity _identity;
    private TtsSetupConfiguration _draft;
    private Uri? _endpoint;
    private long? _timeout;
    private readonly List<Header> _headers = [];
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    internal TtsSetupSession(ITtsProvider provider, SetupConfigurationIdentity identity,
        ISetupCommitTarget<TtsSetupChanges> target, TtsSetupConfiguration? existing)
        : base(provider.SetupDefinition, target)
    {
        identity.Validate();
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || existing is not null && existing.Connection.Transport != provider.Transport)
        { throw new ArgumentException("An edit requires the matching saved TTS configuration.", nameof(existing)); }
        _identity = identity;
        _provider = provider;
        _draft = existing ?? new(new() { Transport = provider.Transport, Endpoint = new("http://127.0.0.1:8020") });
        _endpoint = _draft.Connection.Endpoint;
        _timeout = (long)_draft.Connection.RequestTimeout.TotalSeconds;
        foreach ((string name, string value) in _draft.Connection.Headers) { _headers.Add(new(name, value)); }
        _draft = _draft with { Connection = _draft.Connection with { Headers = new Dictionary<string, string>() } };
        Status = new() { Configured = SetupObservation.Known(existing is not null) };
    }

    protected override IReadOnlyList<SetupStep> CreateSteps(long revision)
    {
        PocketTtsEndpointOptions options = _draft.Connection.PocketTts;
        SetupTextField Path(string id, string label, string value, string defaultValue,
            Func<PocketTtsEndpointOptions, string, PocketTtsEndpointOptions> set) => new(new(id, label) { IsRequired = true }, value, defaultValue,
                replacement => Edit(revision, () => _draft = _draft with { Connection = _draft.Connection with { PocketTts = set(_draft.Connection.PocketTts, replacement) } }));
        return
        [
            new("connection", "External endpoint", "The host or administrator runs PocketTTS. This library only sends HTTP requests.",
            [new("connection", "Connection",
            [
                new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true }, _endpoint, new("http://127.0.0.1:8020"), value => Edit(revision, () => _endpoint = value)),
                new SetupIntegerField(new("timeout", "Timeout (seconds)") { IsRequired = true }, _timeout, 300, value => Edit(revision, () => _timeout = value), 1, 86400),
                new SetupChoiceField(new("protocol", "Protocol mode") { IsRequired = true }, options.Protocol.ToString(), PocketTtsProtocol.Multipart.ToString(),
                    value => Enum.TryParse(value, out PocketTtsProtocol mode) && Enum.IsDefined(mode)
                        ? Edit(revision, () => _draft = _draft with { Connection = _draft.Connection with { PocketTts = options with { Protocol = mode } } })
                        : new(SetupEditState.Invalid, [new("protocol-invalid", "Select a supported protocol.", "protocol")]), false,
                    (query, token) => QueryChoicesAsync(query, _ => Task.FromResult(Page(query,
                        Enum.GetValues<PocketTtsProtocol>().Select(mode => new SetupChoice(mode.ToString(), mode.ToString())))), token), revision)
            ]), new("paths", "Endpoint options",
            [
                Path("voices-path", "Voices path", options.VoicesPath, "/voices", (current, value) => current with { VoicesPath = value }),
                Path("speak-path", "Speech path", options.SpeakPath, "/tts", (current, value) => current with { SpeakPath = value }),
                Path("health-path", "Empty-list fallback path", options.HealthPath, "/health", (current, value) => current with { HealthPath = value }),
                Path("voice-field", "Multipart voice field", options.VoiceFormField, "voice_url", (current, value) => current with { VoiceFormField = value }),
                Headers(revision)
            ], IsAdvanced: true)]),
            new("speech", "Speech defaults", "Voice listing does not synthesize speech. The explicit synthesis test can incur usage charges.",
            [new("speech", "Defaults",
            [
                new SetupChoiceField(new("voice", "Voice") { HelpText = "Use a listed voice or enter the exact server voice ID; blank uses the server default." },
                    _draft.VoiceId ?? string.Empty, string.Empty, value => Edit(revision, () => _draft = _draft with { VoiceId = value }), true,
                    VoicesAsync, revision, SetupSideEffect.Network),
                new SetupDecimalField(new("speed", "Speed") { HelpText = options.Protocol == PocketTtsProtocol.Multipart
                    ? "Multipart supports only default speed (1)." : "A positive speed is passed to the configured bridge." },
                    _draft.Speed is { } speed && double.IsFinite(speed) && speed <= (double)decimal.MaxValue && speed >= (double)decimal.MinValue ? (decimal)speed : null,
                    null, value => Edit(revision, () => _draft = _draft with { Speed = value is null ? null : (double)value }),
                    options.Protocol == PocketTtsProtocol.Multipart ? 1 : 0, options.Protocol == PocketTtsProtocol.Multipart ? 1 : null),
                new SetupChoiceField(new("format", "Audio format"), _draft.Format.ToString(), TtsAudioFormat.Wav.ToString(),
                    value => value == nameof(TtsAudioFormat.Wav) ? Edit(revision, () => _draft = _draft with { Format = TtsAudioFormat.Wav })
                        : new(SetupEditState.Invalid, [new("format-unsupported", "Only WAV is supported.", "format")]), false,
                    (query, token) => QueryChoicesAsync(query, _ => Task.FromResult(Page(query, [new(nameof(TtsAudioFormat.Wav), "WAV")])), token), revision)
            ])])
        ];
    }

    private SetupRepeatableGroupField Headers(long revision) => new(new("headers", "Protected HTTP headers"),
        _headers.Select(header => new SetupFieldRow(header.Id,
        [
            new SetupTextField(new(header.Id + ".name", "Name"), header.Name, string.Empty, value => Edit(revision, () => header.Name = value)),
            new SetupSecretField(new(header.Id + ".value", "Value"), header.Value is not null, header.Edit.Kind, value => Edit(revision, () => header.Edit = value))
        ])).ToArray(), () => Edit(revision, () => _headers.Add(new(string.Empty, null))),
        id => Edit(revision, () =>
        {
            Header? header = _headers.Find(item => item.Id == id);
            if (header is null) { return; }
            if (header.OriginalName.Length > 0) { _removed.Add(header.OriginalName); }
            _headers.Remove(header);
        }));

    private Task<SetupChoicePage> VoicesAsync(SetupChoiceQuery query, CancellationToken token) => QueryChoicesAsync(query, async ct =>
    {
        IReadOnlyList<TtsVoice> voices = await _provider.Bind(Connection()).ListVoicesAsync(ct).ConfigureAwait(false);
        return Page(query, voices.Select(voice => new SetupChoice(voice.Id, voice.DisplayName, voice.Description)));
    }, token);

    private static SetupChoicePage Page(SetupChoiceQuery query, IEnumerable<SetupChoice> choices)
    {
        if (!int.TryParse(query.Continuation ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out int offset))
        { return new(query.ExpectedRevision, [], State: SetupChoiceState.Failed); }
        SetupChoice[] matches = choices.Where(choice => choice.Value.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || choice.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new(query.ExpectedRevision, matches.Skip(offset).Take(query.PageSize).ToArray(),
            offset + query.PageSize < matches.Length ? (offset + query.PageSize).ToString(CultureInfo.InvariantCulture) : null);
    }

    protected override IReadOnlyList<ISetupAction> CreateActions(long revision) =>
    [
        BindAction(revision, new("check", "Check endpoint and voices", SetupActionPurpose.CheckReadiness, SetupSideEffect.Network, ConnectionValid()), async (_, token) =>
        {
            Publish(() => Status = Status with { Verified = SetupObservation.Unknown });
            await _provider.Bind(Connection()).ListVoicesAsync(token).ConfigureAwait(false);
            Publish(() => Status = Status with { Verified = SetupObservation.Known(true) });
            return SetupActionResult.Completed;
        }),
        BindAction(revision, new("test", "Synthesize a test clip", SetupActionPurpose.Test, SetupSideEffect.Network | SetupSideEffect.PotentialBilling,
            ValidateDraft().IsValid), async (_, token) =>
        {
            Publish(() => Status = Status with { Ready = SetupObservation.Unknown });
            await _provider.Bind(Connection()).SpeakAsync(new() { Text = "This is a speech test.", VoiceId = _draft.VoiceId, Speed = _draft.Speed, Format = _draft.Format }, token).ConfigureAwait(false);
            Publish(() => Status = Status with { Ready = SetupObservation.Known(true), Verified = SetupObservation.Known(true) });
            return SetupActionResult.Completed;
        })
    ];

    private TtsConnection Connection() => _draft.Connection with
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
        return new(_identity, _draft with { Connection = Connection() with { Headers = new Dictionary<string, string>() } }, new(set, removed.ToArray()));
    }

    protected override void ClearSensitiveState() => _headers.Clear();

    private sealed class Header(string name, string? originalValue)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = name;
        public string OriginalName { get; } = name;
        public SetupSecretEdit Edit { get; set; } = SetupSecretEdit.Keep;
        public string? Value => Edit.Kind switch { SetupSecretEditKind.Replace => Edit.Value, SetupSecretEditKind.Clear => null, _ => originalValue };
    }
}
