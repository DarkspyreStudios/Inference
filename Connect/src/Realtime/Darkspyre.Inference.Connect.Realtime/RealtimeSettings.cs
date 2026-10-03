using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Realtime;

public sealed record RealtimeSetupConfiguration(RealtimeConnection Connection,
    RealtimeSessionConfig Session, InferenceStreamLimits? StreamLimits = null)
{
    [JsonRequired] public RealtimeConnection Connection { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Connection);
    [JsonRequired]
    public RealtimeSessionConfig Session { get; init => field = RealtimeRequestData.Snapshot(RealtimeData.Required(value)); }
        = RealtimeRequestData.Snapshot(RealtimeData.Required(Session));
    public override string ToString() => "RealtimeSetupConfiguration { <configuration> }";
}

public sealed record RealtimeSetupHeaderChanges(IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Remove)
{
    [JsonRequired]
    public IReadOnlyDictionary<string, string> Set { get; init => field = RealtimeData.Map(value, StringComparer.OrdinalIgnoreCase); }
        = RealtimeData.Map(Set, StringComparer.OrdinalIgnoreCase);
    [JsonRequired] public IReadOnlyList<string> Remove { get; init => field = RealtimeData.List(value); } = RealtimeData.List(Remove);
    public override string ToString() => "RealtimeSetupHeaderChanges { <changes> }";
}

public sealed record RealtimeSetupChanges(SetupConfigurationIdentity Identity,
    RealtimeSetupConfiguration Configuration, RealtimeSetupHeaderChanges Headers)
{
    [JsonRequired] public SetupConfigurationIdentity Identity { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Identity);
    [JsonRequired] public RealtimeSetupConfiguration Configuration { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Configuration);
    [JsonRequired] public RealtimeSetupHeaderChanges Headers { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Headers);
    public override string ToString() => "RealtimeSetupChanges { <changes> }";
}

/// <summary>Local configuration edits. Authentication belongs to the selected LLM connection.</summary>
public sealed class RealtimeSettings : InferenceSettings<RealtimeSetupChanges>
{
    private RealtimeSetupConfiguration _draft;
    private readonly JsonElement _baseline;
    private Uri? _endpoint;
    private decimal? _timeout;
    private long? _events;
    private long? _payloadBytes;
    private long? _messageBytes;
    private readonly List<Header> _headers = [];
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;

    internal RealtimeSettings(InferenceSetupDefinition definition, RealtimeTransport transport,
        SetupConfigurationIdentity identity, RealtimeSetupConfiguration? existing) : base(definition, identity)
    {
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || existing is not null && existing.Connection.Transport != transport)
            throw new ArgumentException("An edit requires the matching saved realtime configuration.", nameof(existing));
        _draft = existing ?? new(new() { Transport = transport, Endpoint = DefaultEndpoint }, new());
        _draft = _draft with { Session = _draft.Session };
        _endpoint = _draft.Connection.Endpoint;
        _timeout = _draft.Connection.RequestTimeout.Ticks / (decimal)TimeSpan.TicksPerSecond;
        _events = _draft.StreamLimits?.MaximumBufferedEvents;
        _payloadBytes = _draft.StreamLimits?.MaximumBufferedPayloadBytes;
        _messageBytes = _draft.StreamLimits?.MaximumMessageBytes;
        foreach ((string name, string value) in _draft.Connection.AdditionalHeaders) _headers.Add(new(name, value));
        _draft = _draft with { Connection = _draft.Connection with { AdditionalHeaders = new Dictionary<string, string>() } };
        _baseline = Portable(_draft);
        Status = new() { Configured = SetupObservation.Known(existing is not null), Authenticated = SetupObservation.Unknown };
    }

    private static Uri DefaultEndpoint => new("wss://api.x.ai/v1");
    internal long Generation => Interlocked.Read(ref _generation);
    internal RealtimeSetupConfiguration Draft => _draft;
    protected override void OnDraftEdited() { Interlocked.Increment(ref _generation); base.OnDraftEdited(); }

    protected override IReadOnlyList<SetupStep> CreateSteps() =>
    [
        new("connection", "Connection", string.Empty,
        [new("connection", "Connection",
        [
            new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true }, _endpoint, DefaultEndpoint, value => Edit(() => _endpoint = value)),
            new SetupDecimalField(new("timeout", "Timeout (seconds)") { IsRequired = true }, _timeout, 300, value => Edit(() => _timeout = value), 0),
            new SetupTextField(new("xai-connection", "Signed-in connection") { IsRequired = true }, _draft.Connection.Xai?.ConnectionId ?? string.Empty,
                string.Empty, value => Edit(() => _draft = _draft with { Connection = _draft.Connection with { Xai = new(value) } })),
            Headers()
        ])]),
        new("session", "Session defaults", string.Empty,
        [new("session", "Defaults",
        [
            Text("model", "Model", _draft.Session.ModelId, (current, value) => current with { ModelId = value }),
            Text("voice", "Voice", _draft.Session.Voice, (current, value) => current with { Voice = value }),
            Text("instructions", "Instructions", _draft.Session.Instructions, (current, value) => current with { Instructions = value }),
            Text("transcription-model", "Transcription model", (_draft.Session.ProviderOptions as XaiRealtimeOptions)?.TranscriptionModel,
                (current, value) => current with { ProviderOptions = (current.ProviderOptions as XaiRealtimeOptions ?? new()) with { TranscriptionModel = value } })
        ]), new("buffers", "Caller buffering budgets",
        [
            new SetupIntegerField(new("buffered-events", "Buffered events"), _events, null, value => Edit(() => _events = value), 1, int.MaxValue),
            new SetupIntegerField(new("buffered-bytes", "Buffered payload bytes"), _payloadBytes, null, value => Edit(() => _payloadBytes = value), 1),
            new SetupIntegerField(new("message-bytes", "Message bytes"), _messageBytes, null, value => Edit(() => _messageBytes = value), 1)
        ], IsAdvanced: true)])
    ];

    private SetupTextField Text(string id, string label, string? value, Func<RealtimeSessionConfig, string, RealtimeSessionConfig> set)
        => new(new(id, label), value ?? string.Empty, string.Empty,
            replacement => Edit(() => _draft = _draft with { Session = set(_draft.Session, replacement) }));

    private SetupRepeatableGroupField Headers() => new(new("headers", "Protected headers"),
        _headers.Select(header => new SetupFieldRow(header.Id,
        [
            new SetupTextField(new(header.Id + ".name", "Name"), header.Name, string.Empty, value => Edit(() => header.Name = value)),
            new SetupSecretField(new(header.Id + ".value", "Value"), header.Value is not null, header.Edit.Kind, value => Edit(() => header.Edit = value))
        ])).ToArray(), () => Edit(() => _headers.Add(new(string.Empty, null))),
        id => Edit(() =>
        {
            Header? header = _headers.Find(item => item.Id == id);
            if (header is null) return;
            if (header.OriginalName.Length > 0) _removed.Add(header.OriginalName);
            _headers.Remove(header);
        }));

    protected override IReadOnlyList<SetupActionDescriptor> CreateActions() =>
    [new("capabilities", "Inspect capabilities", SetupActionPurpose.CheckReadiness, SetupSideEffect.Network, ConnectionValid())];

    internal RealtimeConnection Connection() => _draft.Connection with
    {
        Endpoint = _endpoint!,
        RequestTimeout = TimeSpan.FromTicks((long)((_timeout ?? 0) * TimeSpan.TicksPerSecond)),
        AdditionalHeaders = _headers.Where(header => header.Value is not null).ToDictionary(header => header.Name, header => header.Value!, StringComparer.OrdinalIgnoreCase)
    };

    private bool ConnectionValid() => _endpoint is { IsAbsoluteUri: true, Scheme: "wss" }
        && string.IsNullOrEmpty(_endpoint.UserInfo) && string.IsNullOrEmpty(_endpoint.Query) && string.IsNullOrEmpty(_endpoint.Fragment)
        && _timeout >= 1m / TimeSpan.TicksPerSecond && _timeout <= long.MaxValue / (decimal)TimeSpan.TicksPerSecond
        && !string.IsNullOrWhiteSpace(_draft.Connection.Xai?.ConnectionId)
        && _headers.All(header => !string.IsNullOrWhiteSpace(header.Name))
        && _headers.Select(header => header.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == _headers.Count;

    private bool LimitsValid() => _events is null && _payloadBytes is null && _messageBytes is null
        || _events is > 0 and <= int.MaxValue && _payloadBytes is > 0 && _messageBytes is > 0;
    private InferenceStreamLimits? Limits() => _events is null && _payloadBytes is null && _messageBytes is null ? null
        : new((int)_events!.Value, _payloadBytes!.Value, _messageBytes!.Value);

    protected override SetupValidation ValidateDraft()
    {
        var issues = new List<SetupIssue>();
        if (!ConnectionValid()) issues.Add(new("connection-invalid", "Check the secure endpoint, timeout, selected connection and headers.", StepId: "connection"));
        if (!LimitsValid()) issues.Add(new("limits-invalid", "Supply all three positive buffering budgets or leave all three unset.", StepId: "session"));
        return new(issues.AsReadOnly());
    }

    protected override bool IsDraftDirty => !ConnectionValid() || !LimitsValid()
        || !JsonElement.DeepEquals(Portable(_draft with { Connection = Connection() with { AdditionalHeaders = new Dictionary<string, string>() }, StreamLimits = Limits() }), _baseline)
        || _removed.Count != 0 || _headers.Any(header => header.OriginalName.Length == 0 || header.Name != header.OriginalName || header.Edit.Kind != SetupSecretEditKind.Keep);

    private static JsonElement Portable(RealtimeSetupConfiguration configuration) => JsonSerializer.SerializeToElement(
        configuration with { Connection = configuration.Connection with { Authorization = null, AdditionalHeaders = new Dictionary<string, string>() } },
        ConnectRealtimeJson.RealtimeSetupConfiguration);

    protected override RealtimeSetupChanges CreateChanges()
    {
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(_removed, StringComparer.OrdinalIgnoreCase);
        foreach (Header header in _headers)
        {
            bool renamed = !header.Name.Equals(header.OriginalName, StringComparison.OrdinalIgnoreCase);
            if (header.OriginalName.Length > 0 && (renamed || header.Edit.Kind == SetupSecretEditKind.Clear)) removed.Add(header.OriginalName);
            if (header.Value is not null && (renamed || header.Edit.Kind == SetupSecretEditKind.Replace)) set[header.Name] = header.Value;
        }
        removed.ExceptWith(set.Keys);
        return new(Identity, _draft with
        {
            Connection = Connection() with { Authorization = null, AdditionalHeaders = new Dictionary<string, string>() },
            StreamLimits = Limits()
        }, new(set, removed.ToArray()));
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

/// <summary>Owns setup operations and borrows the provider facade and conditional commit target.</summary>
public sealed class RealtimeSettingsManager : InferenceSettingsManager<RealtimeSettings, RealtimeSetupChanges>, IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private RealtimeProviderService? _service;
    internal RealtimeSettingsManager(RealtimeProviderService service, ISetupCommitTarget<RealtimeSetupChanges> target) : base(target) => _service = service;

    public Task<RealtimeSettings> OpenAsync(RealtimeTransport transport, SetupConfigurationIdentity identity,
        RealtimeSetupConfiguration? existing = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        InferenceSetupDefinition definition = _service!.GetRequiredProvider(transport).SetupDefinition;
        Fence(lease.Token);
        var settings = new RealtimeSettings(definition, transport, identity, existing);
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
        // Cancellation cannot replace the observed outcome of an admitted conditional commit.
        return await base.SaveAsync(settings, lease.Token).ConfigureAwait(false);
    }

    protected override async Task<SetupActionResult> RunCoreAsync(RealtimeSettings settings, SetupActionDescriptor action,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        if (action.Id != "capabilities") return new(SetupActionState.Unavailable);
        long generation = settings.Generation;
        RealtimeConnection connection = settings.Connection();
        Fence(cancellationToken);
        await _service!.GetCapabilitiesAsync(connection, settings.Draft.Session.ModelId, cancellationToken).ConfigureAwait(false);
        Fence(cancellationToken);
        // Capability facts do not prove authentication, session readiness or successful generation.
        return settings.Generation == generation ? SetupActionResult.Completed : new(SetupActionState.Cancelled);
    }

    private void Fence(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("The realtime settings owner is retiring.", error, token); }
    }
    private bool IsStopping(CancellationToken token)
    {
        if (token.IsCancellationRequested) return true;
        try { _lifetime.ThrowIfStopping(); return false; }
        catch (ObjectDisposedException) { return true; }
    }
    public void Dispose() => RetireAsync().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(RetireAsync());
    private Task RetireAsync() => _lifetime.RetireAsync(() => { _service = null; DetachCommitTarget(); return Task.CompletedTask; });
}
