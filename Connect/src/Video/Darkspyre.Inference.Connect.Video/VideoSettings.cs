using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Video;

public sealed record VideoSetupConfiguration(VideoConnection Connection, string? ModelId = null)
{
    [JsonRequired] public VideoConnection Connection { get; init => field = VideoContractData.Required(value); } = VideoContractData.Required(Connection);
    public override string ToString() => "VideoSetupConfiguration { <configuration> }";
}

public sealed record VideoSetupHeaderChanges(IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Remove)
{
    [JsonRequired] public IReadOnlyDictionary<string, string> Set { get; init => field = VideoContractData.Headers(value); } = VideoContractData.Headers(Set);
    [JsonRequired] public IReadOnlyList<string> Remove { get; init => field = Copy(value); } = Copy(Remove);
    private static System.Collections.ObjectModel.ReadOnlyCollection<string> Copy(IReadOnlyList<string> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Array.AsReadOnly(value.Select(VideoContractData.Identity).ToArray());
    }
    public override string ToString() => "VideoSetupHeaderChanges { <protected changes> }";
}

public sealed record VideoSetupChanges(SetupConfigurationIdentity Identity, VideoSetupConfiguration Configuration,
    VideoSetupHeaderChanges Headers)
{
    [JsonRequired] public SetupConfigurationIdentity Identity { get; init => field = VideoContractData.Required(value); } = VideoContractData.Required(Identity);
    [JsonRequired] public VideoSetupConfiguration Configuration { get; init => field = VideoContractData.Required(value); } = VideoContractData.Required(Configuration);
    [JsonRequired] public VideoSetupHeaderChanges Headers { get; init => field = VideoContractData.Required(value); } = VideoContractData.Required(Headers);
    public override string ToString() => "VideoSetupChanges { <changes> }";
}

/// <summary>Plain unsaved Video selection. Field edits do not acquire credentials or contact a provider.</summary>
public sealed class VideoSettings : InferenceSettings<VideoSetupChanges>
{
    private readonly VideoSetupConfiguration _baseline;
    private Uri? _endpoint;
    private long? _timeout;
    private string _model;
    private string _selection;
    private readonly List<Header> _headers = [];
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    internal long Generation { get; private set; }
    internal VideoTransport Transport { get; }

    internal VideoSettings(InferenceSetupDefinition definition, VideoTransport transport, SetupConfigurationIdentity identity,
        VideoSetupConfiguration? existing) : base(definition, identity)
    {
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || existing is not null && existing.Connection.Transport != transport)
            throw new ArgumentException("An edit requires its matching saved Video configuration.");
        Transport = VideoContractData.Defined(transport);
        _baseline = existing ?? new(new() { Transport = transport, Endpoint = "https://api.x.ai/v1" });
        _endpoint = Uri.TryCreate(_baseline.Connection.Endpoint, UriKind.Absolute, out Uri? parsed) ? parsed : null;
        _timeout = (long)_baseline.Connection.RequestTimeout.TotalSeconds;
        _model = _baseline.ModelId ?? string.Empty;
        _selection = _baseline.Connection.Xai?.ConnectionId ?? string.Empty;
        foreach ((string name, string value) in _baseline.Connection.AdditionalHeaders) _headers.Add(new(name, value));
        // The editable snapshot retains no runtime authorization or protected-header data graph.
        _baseline = _baseline with { Connection = _baseline.Connection with { Authorization = null, AdditionalHeaders = new Dictionary<string, string>() } };
        Status = new() { Configured = SetupObservation.Known(existing is not null), Authenticated = SetupObservation.Unknown };
    }

    internal VideoConnection Connection() => new()
    {
        Transport = Transport,
        Endpoint = _endpoint!.AbsoluteUri.TrimEnd('/'),
        RequestTimeout = TimeSpan.FromSeconds(_timeout ?? 0),
        Xai = new(_selection),
        AdditionalHeaders = _headers.Where(header => header.Value is not null).ToDictionary(header => header.Name, header => header.Value!, StringComparer.OrdinalIgnoreCase)
    };
    internal string? ModelId => _model.Length == 0 ? null : _model;

    protected override IReadOnlyList<SetupStep> CreateSteps() =>
    [
        new("connection", "Video connection", "Use an explicit protected xAI connection selection.",
        [new("connection", "Connection",
        [
            new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true }, _endpoint, new("https://api.x.ai/v1"), value => Edit(() => _endpoint = value)),
            new SetupIntegerField(new("timeout", "Timeout (seconds)") { IsRequired = true }, _timeout, 600, value => Edit(() => _timeout = value), 1),
            new SetupTextField(new("xai-connection", "Protected xAI connection") { IsRequired = true }, _selection, string.Empty, value => Edit(() => _selection = value)),
            new SetupTextField(new("model", "Model ID"), _model, string.Empty, value => Edit(() => _model = value)),
            new SetupRepeatableGroupField(new("headers", "Protected HTTP headers"), _headers.Select(header => new SetupFieldRow(header.Id,
            [
                new SetupTextField(new(header.Id + ".name", "Name"), header.Name, string.Empty, value => Edit(() => header.Name = value)),
                new SetupSecretField(new(header.Id + ".value", "Value"), header.Value is not null, header.Edit.Kind, value => Edit(() => header.Edit = value))
            ])).ToArray(), () => Edit(() => _headers.Add(new(string.Empty, null))), id => Edit(() =>
            {
                Header? header = _headers.Find(item => item.Id == id);
                if (header is null) return;
                if (header.OriginalName.Length > 0) _removed.Add(header.OriginalName);
                _headers.Remove(header);
            }))
        ])])
    ];

    protected override void OnDraftEdited() { Generation++; base.OnDraftEdited(); }
    protected override IReadOnlyList<SetupActionDescriptor> CreateActions() =>
    [new("capabilities", "Check video capabilities", SetupActionPurpose.CheckReadiness, SetupSideEffect.Network, Validate().IsValid)];
    protected override SetupValidation ValidateDraft()
    {
        var issues = new List<SetupIssue>();
        if (_endpoint is null || _endpoint.Scheme != Uri.UriSchemeHttps || _endpoint.UserInfo.Length > 0 || _endpoint.Query.Length > 0 || _endpoint.Fragment.Length > 0)
            issues.Add(new("endpoint-invalid", "Use an explicit HTTPS endpoint without user information, query or fragment.", "endpoint"));
        if (_timeout is null or < 1 || _timeout > (uint.MaxValue - 1) / 1000)
            issues.Add(new("timeout-invalid", "Use a positive request timeout of at most 4294967 seconds.", "timeout"));
        if (string.IsNullOrWhiteSpace(_selection)) issues.Add(new("selection-required", "Select the protected xAI connection.", "xai-connection"));
        if (_headers.Any(header => string.IsNullOrWhiteSpace(header.Name)) || _headers.Select(header => header.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _headers.Count)
            issues.Add(new("headers-invalid", "HTTP header names must be nonempty and distinct.", "headers"));
        if (issues.Count == 0)
        {
            try { ProviderHttp.ValidateAdditionalHeaders(Connection().AdditionalHeaders); }
            catch (InvalidOperationException) { issues.Add(new("headers-invalid", "Use valid nonreserved HTTP headers.", "headers")); }
        }
        return new(issues.AsReadOnly());
    }
    protected override bool IsDraftDirty => _endpoint?.AbsoluteUri.TrimEnd('/') != _baseline.Connection.Endpoint.TrimEnd('/')
        || _timeout != (long)_baseline.Connection.RequestTimeout.TotalSeconds || ModelId != _baseline.ModelId
        || _selection != (_baseline.Connection.Xai?.ConnectionId ?? string.Empty) || _removed.Count > 0
        || _headers.Any(header => header.Name != header.OriginalName || header.Edit.Kind != SetupSecretEditKind.Keep);

    protected override VideoSetupChanges CreateChanges()
    {
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var remove = new HashSet<string>(_removed, StringComparer.OrdinalIgnoreCase);
        foreach (Header header in _headers)
        {
            bool renamed = !header.Name.Equals(header.OriginalName, StringComparison.OrdinalIgnoreCase);
            if (header.OriginalName.Length > 0 && (renamed || header.Edit.Kind == SetupSecretEditKind.Clear)) remove.Add(header.OriginalName);
            if (header.Value is not null && (renamed || header.Edit.Kind == SetupSecretEditKind.Replace)) set[header.Name] = header.Value;
        }
        remove.ExceptWith(set.Keys);
        return new(Identity, new(Connection() with { AdditionalHeaders = new Dictionary<string, string>() }, ModelId), new(set, remove.ToArray()));
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

/// <summary>Owns admitted setup work, and borrows service, binding factory and commit target.</summary>
public sealed class VideoSettingsManager : InferenceSettingsManager<VideoSettings, VideoSetupChanges>, IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private VideoProviderService? _service;
    private Func<XaiConnectionSelection, XaiFamilyBinding>? _bindings;
    internal VideoSettingsManager(VideoProviderService service, ISetupCommitTarget<VideoSetupChanges> target,
        Func<XaiConnectionSelection, XaiFamilyBinding>? bindings) : base(target) { _service = service; _bindings = bindings; }

    public Task<VideoSettings> OpenAsync(VideoTransport transport, SetupConfigurationIdentity identity,
        VideoSetupConfiguration? existing = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        IVideoTransport registered = _service!.GetRequiredTransport(transport);
        var settings = new VideoSettings(registered.SetupDefinition, transport, identity, existing);
        Fence(lease.Token);
        return Task.FromResult(settings);
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
        return await base.SaveAsync(settings, lease.Token).ConfigureAwait(false);
    }
    protected override async Task<SetupActionResult> RunCoreAsync(VideoSettings settings, SetupActionDescriptor action,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        if (action.Id != "capabilities") return new(SetupActionState.Unavailable);
        VideoConnection connection = settings.Connection();
        string? model = settings.ModelId;
        long generation = settings.Generation;
        Func<XaiConnectionSelection, XaiFamilyBinding> factory = _bindings ?? throw new InvalidOperationException("Video readiness requires a borrowed binding factory.");
        XaiFamilyBinding binding = factory(connection.Xai!);
        Fence(cancellationToken);
        if (generation != settings.Generation) return new(SetupActionState.Cancelled);
        IVideoInference inference = _service!.Bind(connection, binding);
        VideoCapabilities capabilities;
        try { capabilities = await inference.GetCapabilitiesAsync(model, cancellationToken).ConfigureAwait(false); }
        catch (Exception original)
        {
            try { await inference.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
        await inference.DisposeAsync().ConfigureAwait(false);
        Fence(cancellationToken);
        if (generation != settings.Generation) return new(SetupActionState.Cancelled);
        settings.Status = settings.Status with { Verified = SetupObservation.Known(capabilities.Operations.Count > 0) };
        return SetupActionResult.Completed;
    }
    private void Fence(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("The video setup owner is retiring.", error, token); }
    }
    public void Dispose() => Retire().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(Retire());
    private Task Retire() => _lifetime.RetireAsync(() => { _service = null; _bindings = null; DetachCommitTarget(); return Task.CompletedTask; });
}
