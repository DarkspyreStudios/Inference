using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.TTS;

/// <summary>A saved TTS setup: the connection and the speech options each request uses.</summary>
/// <param name="Connection">The connection to the TTS server.</param>
/// <param name="VoiceId">The voice to speak with, or null for the provider default.</param>
/// <param name="Speed">The speaking speed, or null for the provider default.</param>
/// <param name="Format">The audio format of the generated speech.</param>
public sealed record TtsSetupConfiguration(TtsConnection Connection, string? VoiceId = null,
    double? Speed = null, TtsAudioFormat Format = TtsAudioFormat.Wav)
{
    /// <summary>The connection to the TTS server. Setting it to null throws.</summary>
    [JsonRequired] public TtsConnection Connection { get; init => field = TtsData.Required(value); } = TtsData.Required(Connection);
    /// <summary>The language to speak, or null for the provider default.</summary>
    public string? Language { get; init; }
    /// <summary>The output sample rate, or null for the provider default.</summary>
    public int? SampleRate { get; init; }
    /// <summary>The output bit rate, or null for the provider default.</summary>
    public int? BitRate { get; init; }
    /// <summary>Whether to request word timestamps, or null for the provider default.</summary>
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
        IncludeTimestamps = IncludeTimestamps
    };
    /// <summary>Returns a diagnostic string without the configuration values.</summary>
    public override string ToString() => "TtsSetupConfiguration { <data> }";
}
/// <summary>The protected header edits a TTS save applies to the stored connection.</summary>
/// <param name="Set">The headers to add or replace, by case-insensitive name.</param>
/// <param name="Remove">The names of the headers to delete.</param>
public sealed record TtsSetupHeaderChanges(IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Remove)
{
    /// <summary>The headers to add or replace, by case-insensitive name.</summary>
    [JsonRequired] public IReadOnlyDictionary<string, string> Set { get; init => field = TtsData.Copy(TtsData.Required(value), StringComparer.OrdinalIgnoreCase)!; } = TtsData.Copy(TtsData.Required(Set), StringComparer.OrdinalIgnoreCase)!;
    /// <summary>The names of the headers to delete.</summary>
    [JsonRequired] public IReadOnlyList<string> Remove { get; init => field = TtsData.Copy(value); } = TtsData.Copy(Remove);
    /// <summary>Returns a diagnostic string without header names or values.</summary>
    public override string ToString() => "TtsSetupHeaderChanges { <data> }";
}
/// <summary>The changes a TTS settings save commits: the target entry, the configuration without protected headers, and the header edits.</summary>
/// <param name="Identity">The setup entry the changes create or edit.</param>
/// <param name="Configuration">The configuration to store, with authorization and headers removed.</param>
/// <param name="Headers">The protected header edits to apply.</param>
public sealed record TtsSetupChanges(SetupConfigurationIdentity Identity, TtsSetupConfiguration Configuration, TtsSetupHeaderChanges Headers)
{
    /// <summary>The setup entry the changes create or edit.</summary>
    [JsonRequired] public SetupConfigurationIdentity Identity { get; init => field = TtsData.Required(value); } = TtsData.Required(Identity);
    /// <summary>The configuration to store, with authorization and headers removed.</summary>
    [JsonRequired] public TtsSetupConfiguration Configuration { get; init => field = TtsData.Required(value); } = TtsData.Required(Configuration);
    /// <summary>The protected header edits to apply.</summary>
    [JsonRequired] public TtsSetupHeaderChanges Headers { get; init => field = TtsData.Required(value); } = TtsData.Required(Headers);
    /// <summary>Returns a diagnostic string without the configuration values.</summary>
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
    /// <inheritdoc />
    protected override void OnDraftEdited() { Interlocked.Increment(ref _generation); base.OnDraftEdited(); }
    private static Uri DefaultEndpoint(TtsTransport transport) => new("http://127.0.0.1:8020");

    /// <inheritdoc />
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

    /// <inheritdoc />
    protected override SetupChoicePage? GetLocalChoices(string fieldId, SetupChoiceQuery query) => fieldId switch
    {
        "protocol" => Page(query, Enum.GetValues<PocketTtsProtocol>().Select(mode => new SetupChoice(mode.ToString(), mode.ToString()))),
        "format" => Page(query, Transport == TtsTransport.PocketTtsHttp ? [new(nameof(TtsAudioFormat.Wav), "WAV")]
            : Enum.GetValues<TtsAudioFormat>().Select(format => new SetupChoice(format.ToString(), format.ToString()))),
        "timestamps" or "normalization" => Page(query, [new("", "Omitted"), new("True", "True"), new("False", "False")]),
        "replace-mode" => Page(query, [new("Omitted", "Omitted"), new("Map", "Explicit map")]),
        _ => null
    };

    /// <inheritdoc />
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
            || _headers.Select(header => header.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _headers.Count) { return false; }
        try { TtsProviderService.ValidateConnection(Connection()); return true; }
        catch (Exception error) when (error is InferenceException or ArgumentException or InvalidOperationException or FormatException) { return false; }
    }

    /// <inheritdoc />
    protected override SetupValidation ValidateDraft()
    {
        var issues = new List<SetupIssue>();
        if (!ConnectionValid()) { issues.Add(new("connection-invalid", "Check the HTTP endpoint, paths, timeout and protected headers.", StepId: "connection")); }
        if (_draft.Speed is { } speed && (!double.IsFinite(speed) || speed <= 0
            || Transport == TtsTransport.PocketTtsHttp && _draft.Connection.PocketTts.Protocol == PocketTtsProtocol.Multipart && speed != 1))
        { issues.Add(new("speed-invalid", "Use a positive speed supported by the selected protocol.", "speed")); }
        if (!Enum.IsDefined(_draft.Format) || Transport == TtsTransport.PocketTtsHttp && _draft.Format != TtsAudioFormat.Wav) { issues.Add(new("format-unsupported", "Select a supported audio format.", "format")); }
        if (Transport == TtsTransport.PocketTtsHttp && (_draft.Language is not null || _draft.SampleRate is not null || _draft.BitRate is not null
            || _draft.IncludeTimestamps is not null))
        { issues.Add(new("controls-unsupported", "PocketTTS does not implement these explicit speech controls.", StepId: "speech")); }
        return new(issues.AsReadOnly());
    }

    /// <inheritdoc />
    protected override bool IsDraftDirty => _draft.Connection != _baseline.Connection
        || (_draft.VoiceId ?? string.Empty) != (_baseline.VoiceId ?? string.Empty)
        || _draft.Speed != _baseline.Speed || _draft.Format != _baseline.Format || _endpoint != _baseline.Connection.Endpoint
        || _draft.Language != _baseline.Language || _draft.SampleRate != _baseline.SampleRate
        || _draft.BitRate != _baseline.BitRate || _draft.IncludeTimestamps != _baseline.IncludeTimestamps
        || _timeout != (long)_baseline.Connection.RequestTimeout.TotalSeconds || _removed.Count != 0
        || _headers.Any(header => header.OriginalName.Length == 0 || header.Name != header.OriginalName
            || header.Edit.Kind != SetupSecretEditKind.Keep);

    /// <inheritdoc />
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
    private IInferenceResourceAccess? _resources;
    private readonly List<(Exception Failure, ITtsClient Owner)> _cleanupFailures = [];

    internal TtsSettingsManager(TtsProviderService service, ISetupCommitTarget<TtsSetupChanges> target,
        IInferenceResourceAccess? resources)
        : base(target) { _service = service; _resources = resources; }

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

    /// <inheritdoc />
    public override async Task<SetupActionResult> RunAsync(InferenceSettings settings, string actionId,
        IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        SetupActionResult result = await base.RunAsync(settings, actionId, progress, lease.Token).ConfigureAwait(false);
        return IsStopping(lease.Token) ? new(SetupActionState.Cancelled) : result;
    }
    /// <inheritdoc />
    public override async Task<SetupChoicePage> QueryChoicesAsync(InferenceSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        SetupChoicePage result = await base.QueryChoicesAsync(settings, fieldId, query, lease.Token).ConfigureAwait(false);
        return IsStopping(lease.Token) ? new([], State: SetupChoiceState.Cancelled) : result;
    }
    /// <inheritdoc />
    public override async Task<SetupCommitResult> SaveAsync(InferenceSettings settings, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        return await base.SaveAsync(settings, lease.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
        // Readiness checks and sample clips do not select persistent session artifacts.
        ITtsClient result = _service!.Bind(connection);
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
    /// <inheritdoc />
    public void Dispose() => Retire().GetAwaiter().GetResult();
    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(Retire());
    private Task Retire() => _lifetime.RetireAsync(() =>
    {
        _service = null; _resources = null; DetachCommitTarget();
        if (_cleanupFailures.Count == 1) return Task.FromException(_cleanupFailures[0].Failure);
        if (_cleanupFailures.Count > 1) return Task.FromException(new AggregateException(_cleanupFailures.Select(item => item.Failure)));
        return Task.CompletedTask;
    });
}
