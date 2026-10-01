using System.Globalization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.STT;

/// <summary>Editable defaults. Every edit is effect-free and preserves provider-specific nullable values.</summary>
public sealed class SttSettings : InferenceSettings<SttSetupChanges>
{
    private SttSetupConfiguration _draft;
    private readonly SttSetupConfiguration _baseline;
    private readonly List<Header> _headers = [];
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private long? _events, _payload, _message;
    private Uri? _endpoint;
    private long? _timeout;
    private long _generation;

    internal SttSettings(InferenceSetupDefinition definition, SttTransport transport,
        SetupConfigurationIdentity identity, SttSetupConfiguration? existing) : base(definition, identity)
    {
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || existing is not null && existing.Connection.Transport != transport)
            throw new ArgumentException("An edit requires the matching saved hosted-STT configuration.", nameof(existing));
        _draft = existing ?? new(new() { Transport = transport, Endpoint = new("https://api.x.ai") });
        foreach ((string name, string value) in _draft.Connection.AdditionalHeaders) _headers.Add(new(name, value));
        _draft = _draft with { Connection = _draft.Connection with { AdditionalHeaders = new Dictionary<string, string>() } };
        _baseline = _draft;
        _endpoint = _draft.Connection.Endpoint;
        _timeout = _draft.Connection.RequestTimeout.Ticks;
        _events = _draft.StreamLimits?.MaximumBufferedEvents; _payload = _draft.StreamLimits?.MaximumBufferedPayloadBytes; _message = _draft.StreamLimits?.MaximumMessageBytes;
        Status = new() { Configured = SetupObservation.Known(existing is not null) };
    }
    internal long Generation => _generation;
    internal SttSetupConfiguration Draft => _draft;
    internal SttConnection Connection() => _draft.Connection with { Endpoint = _endpoint!, RequestTimeout = TimeSpan.FromTicks(_timeout ?? 0),
        AdditionalHeaders = _headers.Where(header => header.Value is not null).ToDictionary(header => header.Name, header => header.Value!, StringComparer.OrdinalIgnoreCase) };
    protected override void OnDraftEdited() { _generation++; base.OnDraftEdited(); }

    protected override IReadOnlyList<SetupStep> CreateSteps() =>
    [
        new("connection", "Hosted transcription", "The selected runtime supplies credentials. Edits do not contact the provider.",
        [new("connection", "Connection",
        [
            new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true }, _endpoint, new("https://api.x.ai"), value => Edit(() => _endpoint = value)),
            new SetupIntegerField(new("timeout", "HTTP timeout (ticks)") { IsRequired = true }, _timeout, TimeSpan.FromMinutes(5).Ticks, value => Edit(() => _timeout = value), 1),
            new SetupTextField(new("xai-connection", "xAI connection ID") { IsRequired = true }, _draft.Connection.Xai?.ConnectionId ?? "", "", value => Edit(() => _draft = _draft with { Connection = _draft.Connection with { Xai = value.Length == 0 ? null : new(value) } })),
            Headers()
        ])]),
        new("transcription", "Transcription defaults", "Blank values leave the provider option omitted.", [new("transcription", "REST", TranscriptionFields())]),
        new("streaming", "Streaming defaults", "Budgets are caller-local and must be explicit before opening a stream.",
        [new("streaming", "Audio stream", StreamingFields()), new("limits", "Explicit budgets",
        [
            new SetupIntegerField(new("maximum-buffered-events", "Buffered events"), _events, null, value => Edit(() => _events = value), 1, int.MaxValue),
            new SetupIntegerField(new("maximum-buffered-payload-bytes", "Buffered payload bytes"), _payload, null, value => Edit(() => _payload = value), 1),
            new SetupIntegerField(new("maximum-message-bytes", "Assembled message bytes"), _message, null, value => Edit(() => _message = value), 1)
        ])])
    ];

    private IReadOnlyList<SetupField> TranscriptionFields()
    {
        SpeechTranscriptionOptions options = _draft.Transcription;
        void Set(SpeechTranscriptionOptions value) => _draft = _draft with { Transcription = value };
        return
        [
            Text("model", "Model", options.ModelId, value => Set(_draft.Transcription with { ModelId = value })),
            Text("language", "Language", options.Language, value => Set(_draft.Transcription with { Language = value })),
            Encoding("raw-format", options.RawAudioFormat, value => Set(_draft.Transcription with { RawAudioFormat = value })),
            Integer("sample-rate", "Sample rate (Hz)", options.SampleRate, value => Set(_draft.Transcription with { SampleRate = value })),
            Flag("diarization", options.Diarization, value => Set(_draft.Transcription with { Diarization = value })),
            Flag("multichannel", options.Multichannel, value => Set(_draft.Transcription with { Multichannel = value })),
            Integer("channels", "Channels", options.Channels, value => Set(_draft.Transcription with { Channels = value })),
            TermsMode("key-terms-mode", options.KeyTerms, value => Set(_draft.Transcription with { KeyTerms = value })),
            Terms("key-terms", options.KeyTerms, value => Set(_draft.Transcription with { KeyTerms = value })),
            Flag("filler-words", options.IncludeFillerWords, value => Set(_draft.Transcription with { IncludeFillerWords = value })),
            Number("vad", options.VadThreshold, value => Set(_draft.Transcription with { VadThreshold = value })),
            Flag("text-normalization", options.TextNormalization, value => Set(_draft.Transcription with { TextNormalization = value }))
        ];
    }
    private IReadOnlyList<SetupField> StreamingFields()
    {
        SpeechStreamOptions options = _draft.Streaming;
        void Set(SpeechStreamOptions value) => _draft = _draft with { Streaming = value };
        return
        [
            Text("stream-model", "Model", options.ModelId, value => Set(_draft.Streaming with { ModelId = value })),
            Text("stream-language", "Language", options.Language, value => Set(_draft.Streaming with { Language = value })),
            Encoding("stream-encoding", options.Encoding, value => Set(_draft.Streaming with { Encoding = value })),
            Integer("stream-sample-rate", "Sample rate (Hz)", options.SampleRate, value => Set(_draft.Streaming with { SampleRate = value })),
            Flag("stream-diarization", options.Diarization, value => Set(_draft.Streaming with { Diarization = value })),
            Flag("stream-multichannel", options.Multichannel, value => Set(_draft.Streaming with { Multichannel = value })),
            Integer("stream-channels", "Channels", options.Channels, value => Set(_draft.Streaming with { Channels = value })),
            TermsMode("stream-key-terms-mode", options.KeyTerms, value => Set(_draft.Streaming with { KeyTerms = value })),
            Terms("stream-key-terms", options.KeyTerms, value => Set(_draft.Streaming with { KeyTerms = value })),
            Flag("stream-filler-words", options.IncludeFillerWords, value => Set(_draft.Streaming with { IncludeFillerWords = value })),
            Number("stream-vad", options.VadThreshold, value => Set(_draft.Streaming with { VadThreshold = value })),
            Flag("stream-text-normalization", options.TextNormalization, value => Set(_draft.Streaming with { TextNormalization = value })),
            Flag("interim-results", options.InterimResults, value => Set(_draft.Streaming with { InterimResults = value })),
            Duration("endpointing", options.Endpointing, value => Set(_draft.Streaming with { Endpointing = value })),
            Number("smart-turn", options.SmartTurnThreshold, value => Set(_draft.Streaming with { SmartTurnThreshold = value })),
            Duration("smart-turn-timeout", options.SmartTurnTimeout, value => Set(_draft.Streaming with { SmartTurnTimeout = value }))
        ];
    }
    private SetupTextField Text(string id, string label, string? value, Action<string?> set) => new(new(id, label), value ?? "", "", replacement => Edit(() => set(replacement.Length == 0 ? null : replacement)));
    private SetupIntegerField Integer(string id, string label, int? value, Action<int?> set) => new(new(id, label), value, null, replacement => replacement is < int.MinValue or > int.MaxValue
        ? new(SetupEditState.Invalid, [new("integer-width", "Use a 32-bit integer.", id)]) : Edit(() => set((int?)replacement)), int.MinValue, int.MaxValue);
    private SetupIntegerField Duration(string id, TimeSpan? value, Action<TimeSpan?> set) => new(new(id, id + " (ticks)"), value?.Ticks, null, replacement => Edit(() => set(replacement is null ? null : TimeSpan.FromTicks(replacement.Value))));
    private SetupTextField Number(string id, double? value, Action<double?> set) => new(new(id, id), value?.ToString("R", CultureInfo.InvariantCulture) ?? "", "", replacement => replacement.Length == 0
        ? Edit(() => set(null)) : double.TryParse(replacement, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
            ? Edit(() => set(number)) : new(SetupEditState.Invalid, [new("number-invalid", "Use a finite number.", id)]));
    private SetupChoiceField Flag(string id, bool? value, Action<bool?> set) => new(new(id, id), value?.ToString() ?? "", "", replacement => replacement.Length == 0 ? Edit(() => set(null))
        : bool.TryParse(replacement, out bool flag) ? Edit(() => set(flag)) : new(SetupEditState.Invalid, [new("flag-invalid", "Select true, false or omitted.", id)]), false);
    private SetupChoiceField Encoding(string id, SpeechAudioEncoding? value, Action<SpeechAudioEncoding?> set) => new(new(id, "Audio encoding"), value?.ToString() ?? "", "", replacement => replacement.Length == 0 ? Edit(() => set(null))
        : Enum.TryParse(replacement, out SpeechAudioEncoding encoding) && Enum.IsDefined(encoding) ? Edit(() => set(encoding)) : new(SetupEditState.Invalid, [new("encoding-invalid", "Select an audio encoding.", id)]), false);
    private SetupRepeatableGroupField Terms(string id, IReadOnlyList<string>? value, Action<IReadOnlyList<string>?> set) => new(new(id, "Key terms"),
        (value ?? []).Select((term, index) => new SetupFieldRow(index.ToString(CultureInfo.InvariantCulture),
        [new SetupTextField(new(id + "." + index, "Term"), term, "", replacement => Edit(() => { string[] copy = CurrentTerms(id).ToArray(); copy[index] = replacement; set(copy); }))])).ToArray(),
        () => Edit(() => set([.. CurrentTerms(id), ""])), key => Edit(() => set(CurrentTerms(id).Where((_, index) => index.ToString(CultureInfo.InvariantCulture) != key).ToArray())));
    private IReadOnlyList<string> CurrentTerms(string id) => (id == "key-terms" ? _draft.Transcription.KeyTerms : _draft.Streaming.KeyTerms) ?? [];
    private SetupChoiceField TermsMode(string id, IReadOnlyList<string>? value, Action<IReadOnlyList<string>?> set) => new(new(id, "Key terms mode"), value is null ? "Omitted" : "List", "Omitted",
        replacement => replacement switch { "Omitted" => Edit(() => set(null)), "List" => Edit(() => set(value ?? [])), _ => new(SetupEditState.Invalid, [new("terms-mode-invalid", "Select omitted or list.", id)]) }, false);
    private SetupRepeatableGroupField Headers() => new(new("headers", "Protected headers"), _headers.Select(header => new SetupFieldRow(header.Id,
        [new SetupTextField(new(header.Id + ".name", "Name"), header.Name, "", value => Edit(() => header.Name = value)),
         new SetupSecretField(new(header.Id + ".value", "Value"), header.Value is not null, header.Edit.Kind, value => Edit(() => header.Edit = value))])).ToArray(),
        () => Edit(() => _headers.Add(new("", null))), id => Edit(() => { Header? header = _headers.Find(item => item.Id == id); if (header is not null) { if (header.OriginalName.Length > 0) _removed.Add(header.OriginalName); _headers.Remove(header); } }));
    protected override SetupChoicePage? GetLocalChoices(string fieldId, SetupChoiceQuery query)
    {
        if (fieldId is "raw-format" or "stream-encoding") return Page(query, new[] { new SetupChoice("", "Omitted") }.Concat(Enum.GetValues<SpeechAudioEncoding>().Select(value => new SetupChoice(value.ToString(), value.ToString()))));
        if (fieldId is "key-terms-mode" or "stream-key-terms-mode") return Page(query, [new("Omitted", "Omitted"), new("List", "Explicit list")]);
        return fieldId is "diarization" or "multichannel" or "filler-words" or "text-normalization" or "stream-diarization" or "stream-multichannel" or "stream-filler-words" or "stream-text-normalization" or "interim-results"
            ? Page(query, [new("", "Omitted"), new("True", "True"), new("False", "False")]) : null;
    }
    protected override IReadOnlyList<SetupActionDescriptor> CreateActions() => [new("check", "Check capabilities", SetupActionPurpose.CheckReadiness, SetupSideEffect.Network, ValidateDraft().IsValid)];
    protected override SetupValidation ValidateDraft()
    {
        var issues = new List<SetupIssue>();
        if (_endpoint is null || !_endpoint.IsAbsoluteUri || _endpoint.Scheme != Uri.UriSchemeHttps || _endpoint.UserInfo.Length != 0 || _endpoint.Query.Length != 0 || _endpoint.Fragment.Length != 0 || _timeout is null or <= 0)
            issues.Add(new("connection-invalid", "Use an absolute HTTPS endpoint and positive request timeout.", StepId: "connection"));
        if (string.IsNullOrWhiteSpace(_draft.Connection.Xai?.ConnectionId)) issues.Add(new("binding-required", "Select the xAI connection.", "xai-connection"));
        if (_headers.Any(header => string.IsNullOrWhiteSpace(header.Name)) || _headers.Select(header => header.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _headers.Count)
            issues.Add(new("headers-invalid", "Header names must be nonempty and distinct.", "headers"));
        if (_headers.Any(header => header.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || header.Name.Equals("api-key", StringComparison.OrdinalIgnoreCase)))
            issues.Add(new("headers-authentication", "Static headers cannot replace the selected xAI authorization.", "headers"));
        if ((_events is not null || _payload is not null || _message is not null) && (_events is null or <= 0 or > int.MaxValue || _payload is null or <= 0 || _message is null or <= 0))
            issues.Add(new("limits-invalid", "Supply all three positive stream budgets, or leave them all omitted.", StepId: "streaming"));
        return new(issues.AsReadOnly());
    }
    protected override bool IsDraftDirty => _draft != _baseline || _endpoint != _baseline.Connection.Endpoint || _timeout != _baseline.Connection.RequestTimeout.Ticks
        || _events != _baseline.StreamLimits?.MaximumBufferedEvents || _payload != _baseline.StreamLimits?.MaximumBufferedPayloadBytes || _message != _baseline.StreamLimits?.MaximumMessageBytes
        || _removed.Count != 0 || _headers.Any(header => header.Name != header.OriginalName || header.OriginalName.Length == 0 || header.Edit.Kind != SetupSecretEditKind.Keep);
    protected override SttSetupChanges CreateChanges()
    {
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var remove = new HashSet<string>(_removed, StringComparer.OrdinalIgnoreCase);
        foreach (Header header in _headers)
        {
            bool renamed = !string.Equals(header.Name, header.OriginalName, StringComparison.OrdinalIgnoreCase);
            if (header.OriginalName.Length > 0 && (renamed || header.Edit.Kind == SetupSecretEditKind.Clear)) remove.Add(header.OriginalName);
            if (header.Value is not null && (renamed || header.Edit.Kind == SetupSecretEditKind.Replace)) set[header.Name] = header.Value;
        }
        remove.ExceptWith(set.Keys);
        return new(Identity, _draft with { Connection = Connection() with { AdditionalHeaders = new Dictionary<string, string>() },
            StreamLimits = _events is null ? null : new((int)_events.Value, _payload!.Value, _message!.Value) }, new(set, remove.ToArray()));
    }
    private sealed class Header(string name, string? original)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = name;
        public string OriginalName { get; } = name;
        public SetupSecretEdit Edit { get; set; } = SetupSecretEdit.Keep;
        public string? Value => Edit.Kind switch { SetupSecretEditKind.Replace => Edit.Value, SetupSecretEditKind.Clear => null, _ => original };
    }
}

/// <summary>Owns setup operation admission and drain; supplied access, factory and commit target remain borrowed.</summary>
public sealed class SttSettingsManager : InferenceSettingsManager<SttSettings, SttSetupChanges>, IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private SttProviderService? _service;
    private Func<XaiConnectionSelection, XaiFamilyBinding>? _xaiBindings;
    private IInferenceResourceAccess? _resources;
    internal SttSettingsManager(SttProviderService service, ISetupCommitTarget<SttSetupChanges> target,
        Func<XaiConnectionSelection, XaiFamilyBinding>? xaiBindings, IInferenceResourceAccess? resources) : base(target)
    { _service = service; _xaiBindings = xaiBindings; _resources = resources; }
    public Task<SttSettings> OpenAsync(SttTransport transport, SetupConfigurationIdentity identity,
        SttSetupConfiguration? existing = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        return Task.FromResult(new SttSettings(_service!.GetRequiredProvider(transport).SetupDefinition, transport, identity, existing));
    }
    public override async Task<SetupActionResult> RunAsync(InferenceSettings settings, string actionId,
        IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        return await base.RunAsync(settings, actionId, progress, lease.Token).ConfigureAwait(false);
    }
    public override async Task<SetupChoicePage> QueryChoicesAsync(InferenceSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        return await base.QueryChoicesAsync(settings, fieldId, query, lease.Token).ConfigureAwait(false);
    }
    public override async Task<SetupCommitResult> SaveAsync(InferenceSettings settings, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        // Preserve the host's admitted commit outcome, including a late committed or uncertain save.
        return await base.SaveAsync(settings, lease.Token).ConfigureAwait(false);
    }
    protected override async Task<SetupActionResult> RunCoreAsync(SttSettings settings, SetupActionDescriptor action,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        if (action.Id != "check") return new(SetupActionState.Unavailable);
        long generation = settings.Generation;
        SttConnection connection = settings.Connection();
        if (_xaiBindings is null || connection.Xai is null) return new(SetupActionState.Unavailable);
        XaiFamilyBinding binding = _xaiBindings(connection.Xai);
        Fence(cancellationToken);
        if (settings.Generation != generation) return new(SetupActionState.Cancelled);
        ISpeechTranscriptionClient client = _service!.Bind(connection, binding, _resources);
        SpeechTranscriptionCapabilities capabilities = await client.GetCapabilitiesAsync(settings.Draft.Transcription.ModelId, cancellationToken).ConfigureAwait(false);
        Fence(cancellationToken);
        if (settings.Generation != generation) return new(SetupActionState.Cancelled);
        settings.Status = settings.Status with { Verified = SetupObservation.Known(capabilities.Transcription.Available), Ready = SetupObservation.Known(capabilities.Transcription.Available) };
        return SetupActionResult.Completed;
    }
    private void Fence(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("The hosted-STT settings owner is retiring.", error, token); }
    }
    public void Dispose() => RetireAsync().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(RetireAsync());
    private Task RetireAsync() => _lifetime.RetireAsync(() => { _service = null; _xaiBindings = null; _resources = null; DetachCommitTarget(); return Task.CompletedTask; });
}
