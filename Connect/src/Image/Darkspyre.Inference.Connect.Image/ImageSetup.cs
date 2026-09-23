using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.Image;

public interface IImageWorkflowChoiceSource
{
    Task<SetupChoicePage> GetChoicesAsync(ImageTransport transport, SetupChoiceQuery query, CancellationToken cancellationToken = default);
    Task<ImageWorkflow?> GetWorkflowAsync(string workflowId, CancellationToken cancellationToken = default);
}

public sealed record ImageSetupConfiguration(ImageTransport Transport, Uri Endpoint, TimeSpan RequestTimeout, string WorkflowId);

/// <summary>Protected header mutations; omission is not deletion.</summary>
public sealed record ImageSetupHeaderChanges(IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Remove);

public sealed record ImageSetupChanges(
    SetupConfigurationIdentity Identity, ImageSetupConfiguration Configuration, ImageSetupHeaderChanges Headers);

public sealed class ImageSetupSession : InferenceSetupSession<ImageSetupChanges>
{
    private readonly ImageProviderService _service;
    private readonly IImageTransport _transport;
    private readonly SetupConfigurationIdentity _identity;
    private readonly IImageWorkflowChoiceSource? _workflows;
    private readonly List<HeaderDraft> _headers = [];
    private readonly HashSet<string> _removedHeaders = new(StringComparer.OrdinalIgnoreCase);
    private Uri? _endpoint;
    private long? _timeoutSeconds;
    private string _workflowId;
    private ImageWorkflow? _workflow;
    private readonly (Uri? Endpoint, long? Timeout, string Workflow) _baseline;

    internal ImageSetupSession(ImageProviderService service, ImageTransport transport,
        SetupConfigurationIdentity identity, ISetupCommitTarget<ImageSetupChanges> target,
        IImageWorkflowChoiceSource? workflows, ImageConnection? existing, string? workflowId)
        : base(DefinitionFor(service.GetRequiredTransport(transport).Descriptor), target)
    {
        identity.Validate();
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || (existing is not null && existing.Transport != transport))
        {
            throw new ArgumentException("An edit requires the matching existing image connection.", nameof(existing));
        }
        _service = service;
        _transport = service.GetRequiredTransport(transport);
        _identity = identity;
        _workflows = workflows;
        _endpoint = Uri.TryCreate(existing?.Endpoint ?? "http://127.0.0.1:8188", UriKind.Absolute, out Uri? endpoint) ? endpoint : null;
        _timeoutSeconds = (long)(existing?.RequestTimeout ?? TimeSpan.FromMinutes(10)).TotalSeconds;
        _workflowId = workflowId ?? string.Empty;
        _baseline = (_endpoint, _timeoutSeconds, _workflowId);
        foreach ((string name, string value) in existing?.AdditionalHeaders ?? new Dictionary<string, string>())
        {
            _headers.Add(new(name, value));
        }
        Status = new() { Configured = SetupObservation.Known(existing is not null) };
    }

    internal static InferenceSetupDefinition DefinitionFor(ImageTransportDescriptor descriptor) => new(
        new("darkspyre.connect.image." + descriptor.Transport.ToString().ToLowerInvariant(), descriptor.DisplayName,
            InferenceImplementationKind.Connect, InferenceTask.ImageGeneration), descriptor.Description);

    protected override IReadOnlyList<SetupStep> CreateSteps(long revision) =>
    [
        new("connection", "Connection", "Configure the externally managed image provider.",
        [
            new("endpoint", "Endpoint",
            [
                new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true }, _endpoint,
                    new("http://127.0.0.1:8188"), value => Edit(revision, () => _endpoint = value)),
                new SetupIntegerField(new("timeout", "Request timeout (seconds)") { IsRequired = true },
                    _timeoutSeconds, 600, value => Edit(revision, () => _timeoutSeconds = value), 1, 86400)
            ]),
            new("advanced", "Advanced", [HeaderField(revision)], IsAdvanced: true)
        ]),
        new("workflow", "Workflow", "Select a host-owned workflow, then validate the selection.",
        [new("workflow", "Workflow",
        [new SetupChoiceField(new("workflow", "Workflow") { IsRequired = true, IsEnabled = _workflows is not null,
            HelpText = _workflows is null ? "The host must supply a workflow catalog." : "Saved workflows supplied by the host." },
            _workflowId, string.Empty, value => Edit(revision, () => { _workflowId = value; _workflow = null; }), false,
            (query, token) => _workflows is null
                ? Task.FromResult(new SetupChoicePage(query.ExpectedRevision, [], State: SetupChoiceState.Unavailable))
                : QueryChoicesAsync(query, ct => _workflows.GetChoicesAsync(_transport.Descriptor.Transport, query, ct), token), revision,
            capabilities: SetupChoiceCapabilities.Searchable)])]),
        new("verify", "Check and test", "Checking health does not generate an image. The separate generation test can consume provider resources.", [])
    ];

    private SetupRepeatableGroupField HeaderField(long revision) => new(new("headers", "Protected request headers"),
        _headers.Select(header => new SetupFieldRow(header.Id,
        [
            new SetupTextField(new(header.Id + ".name", "Header name") { IsRequired = true }, header.Name, string.Empty,
                value => Edit(revision, () => header.Name = value)),
            new SetupSecretField(new(header.Id + ".value", "Header value"), header.Value is not null, header.Change.Kind,
                value => Edit(revision, () => header.Change = value))
        ])).ToArray(),
        () => Edit(revision, () => _headers.Add(new(string.Empty, null))),
        rowId => Edit(revision, () =>
        {
            HeaderDraft? header = _headers.Find(row => row.Id == rowId);
            if (header is null) { return; }
            if (header.OriginalName.Length > 0) { _removedHeaders.Add(header.OriginalName); }
            _headers.Remove(header);
        }));

    protected override IReadOnlyList<ISetupAction> CreateActions(long revision) =>
    [
        BindAction(revision, new("validate-workflow", "Validate workflow selection", SetupActionPurpose.CheckPrerequisites,
            SetupSideEffect.None, _workflows is not null && _workflowId.Length > 0,
            _workflows is null ? "The host has not supplied a workflow catalog." : null), ResolveWorkflowAsync),
        BindAction(revision, new("health", "Check provider health", SetupActionPurpose.CheckReadiness,
            SetupSideEffect.Network, ConnectionIssues().Count == 0), CheckHealthAsync),
        BindAction(revision, new("generate-test", "Generate a test image", SetupActionPurpose.Test,
            SetupSideEffect.Network | SetupSideEffect.PotentialBilling,
            ValidateDraft().IsValid && _workflow?.RequiresReferenceImage == false,
            _workflow?.RequiresReferenceImage == true ? "This workflow requires a host-supplied reference image; use the existing transport API to test it." : null), TestAsync)
    ];

    private async Task<SetupActionResult> ResolveWorkflowAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        ImageWorkflow? workflow = await _workflows!.GetWorkflowAsync(_workflowId, token).ConfigureAwait(false);
        if (workflow is null || workflow.Id != _workflowId || workflow.Transport != _transport.Descriptor.Transport)
        {
            return new(SetupActionState.Failed, [new("workflow-unavailable", "Select a workflow for this transport.", "workflow")]);
        }
        Publish(() => _workflow = workflow with { TransportConfiguration = workflow.TransportConfiguration.Clone() });
        return SetupActionResult.Completed;
    }

    private async Task<SetupActionResult> CheckHealthAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        Publish(() => Status = Status with { Verified = SetupObservation.Unknown, Ready = SetupObservation.Unknown });
        ImageProviderHealth result = await _transport.TestAsync(Connection(), _workflow, token).ConfigureAwait(false);
        Publish(() => Status = Status with { Verified = SetupObservation.Known(result.IsHealthy) });
        return result.IsHealthy ? SetupActionResult.Completed
            : new(SetupActionState.Failed, [new("health-failed", "The provider or selected workflow did not pass its health check.")], IsRetryable: true);
    }

    private async Task<SetupActionResult> TestAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        Publish(() => Status = Status with { Ready = SetupObservation.Unknown });
        SetupActionResult resolved = await ResolveWorkflowAsync(progress, token).ConfigureAwait(false);
        if (resolved.State != SetupActionState.Completed) { return resolved; }
        if (_workflow!.RequiresReferenceImage)
        {
            return new(SetupActionState.Unavailable, [new("reference-required", "This test needs a reference image.")]);
        }
        progress?.Report(new("generating", "Generating one test image."));
        ImageGenerationResult result = await _service.Bind(Connection(), _workflow).GenerateAsync(
            new() { ModelId = _workflow.Id, Prompt = "A small blue circle on a white background.", Seed = 42 },
            cancellationToken: token).ConfigureAwait(false);
        bool ready = result.Images.Count > 0 && result.Images.All(image => !image.Data.IsEmpty);
        Publish(() => Status = Status with { Ready = SetupObservation.Known(ready) });
        return ready ? SetupActionResult.Completed : new(SetupActionState.Failed, [new("empty-image", "The provider returned no image.")]);
    }

    protected override SetupValidation ValidateDraft()
    {
        List<SetupIssue> issues = ConnectionIssues();
        if (_workflow is null || _workflow.Id != _workflowId)
        {
            issues.Add(new("workflow-required", "Select and validate a workflow.", "workflow", "workflow"));
        }
        return new(issues.AsReadOnly());
    }

    private List<SetupIssue> ConnectionIssues()
    {
        var issues = new List<SetupIssue>();
        if (_endpoint is not { IsAbsoluteUri: true } || _endpoint.Scheme is not ("http" or "https")
            || _endpoint.UserInfo.Length > 0 || _endpoint.Fragment.Length > 0 || _endpoint.Query.Length > 0)
        {
            issues.Add(new("endpoint-invalid", "Use an HTTP(S) base URL without credentials, query or fragment.", "endpoint"));
        }
        if (_timeoutSeconds is null or < 1 or > 86400) { issues.Add(new("timeout-invalid", "Timeout must be 1–86400 seconds.", "timeout")); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HeaderDraft header in _headers)
        {
            if (header.Name.Length == 0 || !header.Name.All(character => char.IsAsciiLetterOrDigit(character)
                || "!#$%&'*+-.^_`|~".Contains(character, StringComparison.Ordinal)) || !names.Add(header.Name))
            {
                issues.Add(new("header-name-invalid", "Supply a unique HTTP header name.", header.Id + ".name"));
            }
            if (header.Value?.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                issues.Add(new("header-value-invalid", "Header values cannot contain line breaks or nulls.", header.Id + ".value"));
            }
        }
        return issues;
    }

    private ImageConnection Connection() => new()
    {
        Transport = _transport.Descriptor.Transport,
        Endpoint = _endpoint!.AbsoluteUri,
        RequestTimeout = TimeSpan.FromSeconds(_timeoutSeconds!.Value),
        AdditionalHeaders = _headers.Where(header => header.Value is not null)
            .ToDictionary(header => header.Name, header => header.Value!, StringComparer.OrdinalIgnoreCase)
    };

    protected override bool IsDraftDirty => (_endpoint, _timeoutSeconds, _workflowId) != _baseline
        || _removedHeaders.Count != 0 || _headers.Any(header => header.OriginalName.Length == 0
            || header.Name != header.OriginalName || header.Change.Kind != SetupSecretEditKind.Keep);

    protected override ImageSetupChanges CreateChanges()
    {
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(_removedHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (HeaderDraft header in _headers)
        {
            bool renamed = !string.Equals(header.Name, header.OriginalName, StringComparison.OrdinalIgnoreCase);
            if (header.OriginalName.Length > 0 && (renamed || header.Change.Kind == SetupSecretEditKind.Clear)) { removed.Add(header.OriginalName); }
            if (header.Value is not null && (renamed || header.Change.Kind == SetupSecretEditKind.Replace)) { set[header.Name] = header.Value; }
        }
        // A rename into a removed name is an explicit set, not an ambiguous delete-and-set pair.
        removed.ExceptWith(set.Keys);
        return new(_identity, new(_transport.Descriptor.Transport, _endpoint!, TimeSpan.FromSeconds(_timeoutSeconds!.Value), _workflowId),
            new(set, removed.ToArray()));
    }

    protected override void ClearSensitiveState() => _headers.Clear();

    private sealed class HeaderDraft(string name, string? originalValue)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string OriginalName { get; } = name;
        public string Name { get; set; } = name;
        public SetupSecretEdit Change { get; set; } = SetupSecretEdit.Keep;
        public string? Value => Change.Kind switch
        {
            SetupSecretEditKind.Replace => Change.Value,
            SetupSecretEditKind.Clear => null,
            _ => originalValue
        };
    }
}
