using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Image;

/// <summary>Supplies the host's image workflow catalog to image setup.</summary>
public interface IImageWorkflowChoiceSource
{
    /// <summary>Returns one page of workflow choices for a transport.</summary>
    Task<SetupChoicePage> GetChoicesAsync(ImageTransport transport, SetupChoiceQuery query, CancellationToken cancellationToken = default);
    /// <summary>Returns the workflow with the given identifier, or null when the catalog has none.</summary>
    Task<ImageWorkflow?> GetWorkflowAsync(string workflowId, CancellationToken cancellationToken = default);
}

/// <summary>The saved connection settings for an image provider.</summary>
/// <param name="Transport">The image transport.</param>
/// <param name="Endpoint">The provider base URL.</param>
/// <param name="RequestTimeout">The time limit for one provider request.</param>
/// <param name="Workflow">The selected workflow.</param>
public sealed record ImageSetupConfiguration(ImageTransport Transport, Uri Endpoint, TimeSpan RequestTimeout, ImageWorkflowSelection Workflow);

/// <summary>Protected header mutations; omission is not deletion.</summary>
public sealed record ImageSetupHeaderChanges(IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Remove);

/// <summary>The changes an image setup session commits.</summary>
/// <param name="Identity">The saved setup entry the changes apply to.</param>
/// <param name="Configuration">The new connection settings.</param>
/// <param name="Headers">The protected header values to set and remove.</param>
public sealed record ImageSetupChanges(
    SetupConfigurationIdentity Identity, ImageSetupConfiguration Configuration, ImageSetupHeaderChanges Headers);

/// <summary>Editable image-provider settings. Plain data: edits change only this object.</summary>
public sealed class ImageSettings : InferenceSettings<ImageSetupChanges>
{
    private readonly bool _hasWorkflowCatalog;
    private readonly List<HeaderDraft> _headers = [];
    private readonly HashSet<string> _removedHeaders = new(StringComparer.OrdinalIgnoreCase);
    private Uri? _endpoint;
    private long? _timeoutSeconds;
    private string _workflowId;
    private ImageWorkflowOrigin _origin;
    private long _generation;
    private readonly (Uri? Endpoint, long? Timeout, string Workflow, ImageWorkflowOrigin Origin) _baseline;

    internal ImageSettings(ImageTransportDescriptor transport, SetupConfigurationIdentity identity,
        bool hasWorkflowCatalog, ImageConnection? existing, ImageWorkflowSelection? workflow)
        : base(DefinitionFor(transport), identity)
    {
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || (existing is not null && existing.Transport != transport.Transport))
        {
            throw new ArgumentException("An edit requires the matching existing image connection.", nameof(existing));
        }
        Transport = transport.Transport;
        _hasWorkflowCatalog = hasWorkflowCatalog;
        _endpoint = Uri.TryCreate(existing?.Endpoint ?? "http://127.0.0.1:8188", UriKind.Absolute, out Uri? endpoint) ? endpoint : null;
        _timeoutSeconds = (long)(existing?.RequestTimeout ?? TimeSpan.FromMinutes(10)).TotalSeconds;
        _workflowId = workflow?.Id ?? string.Empty;
        _origin = workflow?.Origin ?? ImageWorkflowOrigin.Host;
        _baseline = (_endpoint, _timeoutSeconds, _workflowId, _origin);
        foreach ((string name, string value) in existing?.AdditionalHeaders ?? new Dictionary<string, string>())
        {
            _headers.Add(new(name, value));
        }
        Status = new() { Configured = SetupObservation.Known(existing is not null) };
    }

    internal ImageTransport Transport { get; }

    internal string WorkflowId => _workflowId;
    internal ImageWorkflowOrigin Origin => _origin;
    internal long Generation => _generation;

    /// <summary>The workflow the validate action resolved for <see cref="WorkflowId"/>.</summary>
    internal ImageWorkflow? Workflow { get; set; }

    /// <inheritdoc />
    protected override void OnDraftEdited()
    {
        _generation++;
        Workflow = null;
        base.OnDraftEdited();
    }

    /// <inheritdoc />
    protected override SetupChoicePage? GetLocalChoices(string fieldId, SetupChoiceQuery query)
        => fieldId == "workflow-origin" ? Page(query, [new("Host", "Host"), new("BuiltIn", "Built-in")]) : null;

    internal static InferenceSetupDefinition DefinitionFor(ImageTransportDescriptor descriptor) => new(
        new("darkspyre.connect.image." + descriptor.Transport.ToString().ToLowerInvariant(), descriptor.DisplayName,
            InferenceImplementationKind.Connect, InferenceTask.ImageGeneration), descriptor.Description);

    /// <inheritdoc />
    protected override IReadOnlyList<SetupStep> CreateSteps() =>
    [
        new("connection", "Connection", "Configure the externally managed image provider.",
        [
            new("endpoint", "Endpoint",
            [
                new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true }, _endpoint,
                    new("http://127.0.0.1:8188"), value => Edit(() => _endpoint = value)),
                new SetupIntegerField(new("timeout", "Request timeout (seconds)") { IsRequired = true },
                    _timeoutSeconds, 600, value => Edit(() => _timeoutSeconds = value), 1, 86400)
            ]),
            new("advanced", "Advanced", [HeaderField()], IsAdvanced: true)
        ]),
        new("workflow", "Workflow", "Select a workflow origin and validate the selection.",
        [new("workflow", "Workflow",
        [new SetupChoiceField(new("workflow-origin", "Workflow origin"), _origin.ToString(), "Host",
            value => Edit(() => { _origin = Enum.Parse<ImageWorkflowOrigin>(value); _workflowId = string.Empty; Workflow = null; }), false),
        new SetupChoiceField(new("workflow", "Workflow") { IsRequired = true, IsEnabled = _origin == ImageWorkflowOrigin.BuiltIn || _hasWorkflowCatalog,
            HelpText = _origin == ImageWorkflowOrigin.Host && !_hasWorkflowCatalog ? "The host must supply a workflow catalog." : string.Empty },
            _workflowId, string.Empty, value => Edit(() => { _workflowId = value; Workflow = null; }), false,
            capabilities: SetupChoiceCapabilities.Searchable)])]),
        new("verify", "Check and test", "Checking health does not generate an image. The separate generation test can consume provider resources.", [])
    ];

    private SetupRepeatableGroupField HeaderField() => new(new("headers", "Protected request headers"),
        _headers.Select(header => new SetupFieldRow(header.Id,
        [
            new SetupTextField(new(header.Id + ".name", "Header name") { IsRequired = true }, header.Name, string.Empty,
                value => Edit(() => header.Name = value)),
            new SetupSecretField(new(header.Id + ".value", "Header value"), header.Value is not null, header.Change.Kind,
                value => Edit(() => header.Change = value))
        ])).ToArray(),
        () => Edit(() => _headers.Add(new(string.Empty, null))),
        rowId => Edit(() =>
        {
            HeaderDraft? header = _headers.Find(row => row.Id == rowId);
            if (header is null) { return; }
            if (header.OriginalName.Length > 0) { _removedHeaders.Add(header.OriginalName); }
            _headers.Remove(header);
        }));

    /// <inheritdoc />
    protected override IReadOnlyList<SetupActionDescriptor> CreateActions() =>
    [
        new("validate-workflow", "Validate workflow selection", SetupActionPurpose.CheckPrerequisites,
            _origin == ImageWorkflowOrigin.BuiltIn ? SetupSideEffect.Network : SetupSideEffect.None,
            (_origin == ImageWorkflowOrigin.BuiltIn || _hasWorkflowCatalog) && _workflowId.Length > 0,
            _origin == ImageWorkflowOrigin.Host && !_hasWorkflowCatalog ? "The host has not supplied a workflow catalog." : null),
        new("health", "Check provider health", SetupActionPurpose.CheckReadiness,
            SetupSideEffect.Network, ConnectionIssues().Count == 0),
        new("generate-test", "Generate a test image", SetupActionPurpose.Test,
            SetupSideEffect.Network | SetupSideEffect.PotentialBilling,
            ValidateDraft().IsValid && Workflow?.RequiresReferenceImage == false,
            Workflow?.RequiresReferenceImage == true ? "This workflow requires a host-supplied reference image; use the existing transport API to test it." : null)
    ];

    /// <inheritdoc />
    protected override SetupValidation ValidateDraft()
    {
        List<SetupIssue> issues = ConnectionIssues();
        if (Workflow is null || Workflow.Id != _workflowId)
        {
            issues.Add(new("workflow-required", "Select and validate a workflow.", "workflow", "workflow"));
        }
        return new(issues.AsReadOnly());
    }

    internal List<SetupIssue> ConnectionIssues()
    {
        var issues = new List<SetupIssue>();
        if (!Enum.IsDefined(_origin)) issues.Add(new("workflow-origin-invalid", "Select a defined workflow origin.", "workflow-origin"));
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

    internal ImageConnection Connection() => new()
    {
        Transport = Transport,
        Endpoint = _endpoint!.AbsoluteUri,
        RequestTimeout = TimeSpan.FromSeconds(_timeoutSeconds!.Value),
        AdditionalHeaders = _headers.Where(header => header.Value is not null)
            .ToDictionary(header => header.Name, header => header.Value!, StringComparer.OrdinalIgnoreCase)
    };

    /// <inheritdoc />
    protected override bool IsDraftDirty => (_endpoint, _timeoutSeconds, _workflowId, _origin) != _baseline
        || _removedHeaders.Count != 0 || _headers.Any(header => header.OriginalName.Length == 0
            || header.Name != header.OriginalName || header.Change.Kind != SetupSecretEditKind.Keep);

    /// <inheritdoc />
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
        return new(Identity, new(Transport, _endpoint!, TimeSpan.FromSeconds(_timeoutSeconds!.Value), new(_origin, _workflowId)),
            new(set, removed.ToArray()));
    }

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

/// <summary>
/// Opens, acts on and saves image-provider settings. The host's workflow catalog answers the workflow
/// choices and resolves the selected workflow; health and generation tests call the provider.
/// </summary>
public sealed class ImageSettingsManager : InferenceSettingsManager<ImageSettings, ImageSetupChanges>, IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private ImageProviderService? _service;
    private IImageWorkflowChoiceSource? _workflows;

    internal ImageSettingsManager(ImageProviderService service, ISetupCommitTarget<ImageSetupChanges> target,
        IImageWorkflowChoiceSource? workflows)
        : base(target)
    {
        _service = service;
        _workflows = workflows;
    }

    /// <summary>Returns settings for <paramref name="transport"/>, blank or from the saved entry.</summary>
    public Task<ImageSettings> OpenAsync(ImageTransport transport, SetupConfigurationIdentity identity,
        ImageConnection? existing = null, ImageWorkflowSelection? workflow = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        return Task.FromResult(new ImageSettings(_service!.GetRequiredTransport(transport).Descriptor, identity,
            _workflows is not null, existing, workflow));
    }

    /// <inheritdoc />
    public override async Task<SetupActionResult> RunAsync(InferenceSettings settings, string actionId,
        IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        return await base.RunAsync(settings, actionId, progress, lease.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<SetupChoicePage> QueryChoicesAsync(InferenceSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        return await base.QueryChoicesAsync(settings, fieldId, query, lease.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<SetupCommitResult> SaveAsync(InferenceSettings settings, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        // An admitted conditional commit preserves its observed committed or uncertain result.
        return await base.SaveAsync(settings, lease.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task<SetupActionResult> RunCoreAsync(ImageSettings settings, SetupActionDescriptor action,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken) => action.Id switch
        {
            "validate-workflow" => await ResolveWorkflowAsync(settings, cancellationToken).ConfigureAwait(false),
            "health" => await CheckHealthAsync(settings, cancellationToken).ConfigureAwait(false),
            "generate-test" => await TestAsync(settings, progress, cancellationToken).ConfigureAwait(false),
            _ => new(SetupActionState.Unavailable)
        };

    /// <inheritdoc />
    protected override async Task<SetupChoicePage> QueryProviderChoicesAsync(ImageSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken)
    {
        if (fieldId == "workflow-origin") return new([new("Host", "Host"), new("BuiltIn", "Built-in")]);
        if (fieldId != "workflow") return new([], State: SetupChoiceState.Unavailable);
        long generation = settings.Generation;
        if (settings.Origin == ImageWorkflowOrigin.Host)
        {
            if (_workflows is null) return new([], State: SetupChoiceState.Unavailable);
            SetupChoicePage page = await _workflows.GetChoicesAsync(settings.Transport, query, cancellationToken).ConfigureAwait(false);
            Fence(cancellationToken);
            if (page.Choices.GroupBy(choice => choice.Value, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new InvalidOperationException("The host workflow choices contain duplicate identities.");
            if (settings.Generation != generation) return new([], State: SetupChoiceState.Cancelled);
            return page;
        }
        ImageConnection connection = settings.Connection();
        Fence(cancellationToken);
        if (settings.Generation != generation) return new([], State: SetupChoiceState.Cancelled);
        IReadOnlyList<ImageWorkflow> available = await _service!.GetWorkflowsAsync(connection, cancellationToken).ConfigureAwait(false);
        Fence(cancellationToken);
        ValidateWorkflowIds(available);
        if (settings.Generation != generation) return new([], State: SetupChoiceState.Cancelled);
        return InferenceSettings.Page(query, available.Select(workflow => new SetupChoice(workflow.Id, workflow.Label)));
    }

    private async Task<SetupActionResult> ResolveWorkflowAsync(ImageSettings settings, CancellationToken token)
    {
        string workflowId = settings.WorkflowId;
        long generation = settings.Generation;
        ImageWorkflowOrigin origin = settings.Origin;
        ImageTransport transport = settings.Transport;
        ImageWorkflow? workflow;
        if (origin == ImageWorkflowOrigin.Host)
            workflow = _workflows is null ? null : await _workflows.GetWorkflowAsync(workflowId, token).ConfigureAwait(false);
        else
        {
            ImageConnection connection = settings.Connection();
            Fence(token);
            if (settings.Generation != generation) return new(SetupActionState.Cancelled);
            IReadOnlyList<ImageWorkflow> available = await _service!.GetWorkflowsAsync(connection, token).ConfigureAwait(false);
            Fence(token);
            ValidateWorkflowIds(available);
            workflow = available.SingleOrDefault(item => item.Id == workflowId);
        }
        Fence(token);
        if (settings.Generation != generation || settings.WorkflowId != workflowId || settings.Origin != origin || settings.Transport != transport)
            return new(SetupActionState.Cancelled);
        if (workflow is null || workflow.Id != workflowId || workflow.Transport != settings.Transport)
        {
            return new(SetupActionState.Failed, [new("workflow-unavailable", "Select a workflow for this transport.", "workflow")]);
        }
        settings.Workflow = workflow with { TransportConfiguration = workflow.TransportConfiguration.Clone() };
        return SetupActionResult.Completed;
    }

    private async Task<SetupActionResult> CheckHealthAsync(ImageSettings settings, CancellationToken token)
    {
        settings.Status = settings.Status with { Verified = SetupObservation.Unknown, Ready = SetupObservation.Unknown };
        long generation = settings.Generation;
        ImageConnection connection = settings.Connection();
        ImageWorkflow? workflow = settings.Workflow;
        Fence(token);
        if (settings.Generation != generation) return new(SetupActionState.Cancelled);
        ImageProviderHealth result = await _service!.GetRequiredTransport(settings.Transport)
            .TestAsync(connection, workflow, token).ConfigureAwait(false);
        Fence(token);
        if (settings.Generation != generation) return new(SetupActionState.Cancelled);
        settings.Status = settings.Status with { Verified = SetupObservation.Known(result.IsHealthy) };
        return result.IsHealthy ? SetupActionResult.Completed
            : new(SetupActionState.Failed, [new("health-failed", "The provider or selected workflow did not pass its health check.")], IsRetryable: true);
    }

    private async Task<SetupActionResult> TestAsync(ImageSettings settings, IProgress<SetupProgress>? progress, CancellationToken token)
    {
        settings.Status = settings.Status with { Ready = SetupObservation.Unknown };
        SetupActionResult resolved = await ResolveWorkflowAsync(settings, token).ConfigureAwait(false);
        if (resolved.State != SetupActionState.Completed) { return resolved; }
        ImageWorkflow workflow = settings.Workflow!;
        if (workflow.RequiresReferenceImage)
        {
            return new(SetupActionState.Unavailable, [new("reference-required", "This test needs a reference image.")]);
        }
        long generation = settings.Generation;
        ImageConnection connection = settings.Connection();
        progress?.Report(new("generating", "Generating one test image."));
        Fence(token);
        if (settings.Generation != generation) return new(SetupActionState.Cancelled);
        IImageInference inference = _service!.Bind(connection, workflow);
        ImageGenerationResult result;
        try
        {
            result = await inference.GenerateAsync(
                new() { ModelId = workflow.Id, Prompt = "A small blue circle on a white background.", Seed = connection.Transport == ImageTransport.ComfyUi ? 42 : null },
                cancellationToken: token).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            try { await inference.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
        await inference.DisposeAsync().ConfigureAwait(false);
        Fence(token);
        if (settings.Generation != generation) return new(SetupActionState.Cancelled);
        bool ready = result.Images.Count > 0 && result.Images.All(image => !image.Data.IsEmpty);
        settings.Status = settings.Status with { Ready = SetupObservation.Known(ready) };
        return ready ? SetupActionResult.Completed : new(SetupActionState.Failed, [new("empty-image", "The provider returned no image.")]);
    }

    private static void ValidateWorkflowIds(IReadOnlyList<ImageWorkflow> workflows)
    {
        if (workflows.GroupBy(workflow => workflow.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("The built-in workflows contain duplicate identities.");
    }

    private void Fence(CancellationToken token) { token.ThrowIfCancellationRequested(); _lifetime.ThrowIfStopping(); }

    /// <summary>Retires the manager and blocks until in-flight operations finish.</summary>
    public void Dispose() => RetireAsync().GetAwaiter().GetResult();
    /// <summary>Retires the manager once in-flight operations finish.</summary>
    public ValueTask DisposeAsync() => new(RetireAsync());
    private Task RetireAsync() => _lifetime.RetireAsync(() =>
    {
        _service = null; _workflows = null;
        DetachCommitTarget();
        return Task.CompletedTask;
    });
}
