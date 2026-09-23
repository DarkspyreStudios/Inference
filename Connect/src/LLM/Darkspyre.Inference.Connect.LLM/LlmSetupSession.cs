using System.Globalization;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM;

public sealed record LlmSetupConfiguration(LlmConnection Connection, string ModelId);

/// <summary>The credential mutation is separate from ordinary persisted configuration.</summary>
public sealed record LlmSetupChanges(
    SetupConfigurationIdentity Identity, LlmSetupConfiguration Configuration,
    SetupSecretEdit Credential, bool RequiresReauthentication, bool RequiresRetest);

/// <summary>Optional provider SDK operations implemented in the SDK-owning adapter assembly.</summary>
public interface ILlmSetupEnvironment
{
    LlmTransport Transport { get; }
    Task<IReadOnlyList<SetupChoice>> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SetupChoice>> GetRegionsAsync(string? profile, CancellationToken cancellationToken = default);
    SetupValidation Validate(LlmConnection connection);
}

public sealed class LlmSetupSession : InferenceSetupSession<LlmSetupChanges>
{
    private readonly SetupConfigurationIdentity _identity;
    private readonly LlmProviderDefinition _definition;
    private readonly ILlmClient _client;
    private readonly ILlmSetupEnvironment? _environment;
    private readonly LlmProviderService _service;
    private LlmConnection _draft;
    private LlmConnection? _original;
    private string _model;
    private readonly string _originalModel;
    private SetupSecretEditKind _credentialChange;
    private LlmDeviceCode? _deviceCode;
    private LlmAuthorizationSession? _browserAuthorization;
    private string _callback = string.Empty;
    private bool _editingCallback;

    internal LlmSetupSession(ILlmClient client, IEnumerable<ILlmAuthenticator> authenticators,
        SetupConfigurationIdentity identity, ISetupCommitTarget<LlmSetupChanges> target,
        LlmConnection? existing, string? modelId, ILlmSetupEnvironment? environment)
        : base(client.ProviderDefinition.SetupDefinition, target)
    {
        identity.Validate();
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || (existing is not null && (existing.Transport != client.Transport || existing.Version != identity.ExpectedVersion)))
        {
            throw new ArgumentException("An edit requires the exact saved connection and version.", nameof(existing));
        }
        _identity = identity;
        _client = client;
        _definition = client.ProviderDefinition;
        _environment = environment;
        _original = existing;
        _draft = existing is null ? new() { Version = "0", Transport = client.Transport, Endpoint = _definition.DefaultEndpoint }
            : existing with
            {
                AdditionalHeaders = new Dictionary<string, string>(existing.AdditionalHeaders, StringComparer.OrdinalIgnoreCase),
                ProviderOptions = existing.ProviderOptions.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
                ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>(existing.ModelCapabilityOverrides, StringComparer.Ordinal)
            };
        _model = _originalModel = modelId ?? _definition.DefaultModelId;
        _service = new(new DraftSource(this), [client], authenticators);
        Status = new() { Configured = SetupObservation.Known(existing is not null), Authenticated = SetupObservation.Unknown };
    }

    private bool UsesCli => _draft.Transport is LlmTransport.AnthropicClaudeCli or LlmTransport.GoogleAntigravityCli;
    private bool UsesAws => _draft.Transport is LlmTransport.AwsBedrockConverse or LlmTransport.AwsBedrockAgentCore;
    private bool UsesApiKey => _draft.Transport is LlmTransport.OpenAiResponses or LlmTransport.OpenAiCompatibleChat or LlmTransport.AnthropicMessages;
    private LlmCredential Credential => LlmCredential.Parse(_draft.Credential) ?? new();

    protected override IReadOnlyList<SetupStep> CreateSteps(long revision)
    {
        var fields = new List<SetupField>();
        if (UsesCli)
        {
            fields.Add(new SetupExecutableField(new("executable", "CLI executable or absolute path") { IsRequired = true },
                new(_draft.Endpoint), new(_definition.DefaultEndpoint), value => Edit(revision, () => _draft = _draft with { Endpoint = value.Value })));
        }
        else if (_draft.Transport == LlmTransport.AwsBedrockAgentCore)
        {
            fields.Add(new SetupTextField(new("harness-arn", "Harness ARN") { IsRequired = true }, _draft.Endpoint,
                _definition.DefaultEndpoint, value => Edit(revision, () => _draft = _draft with { Endpoint = value })));
        }
        else
        {
            fields.Add(new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true },
                Uri.TryCreate(_draft.Endpoint, UriKind.Absolute, out Uri? uri) ? uri : null,
                Uri.TryCreate(_definition.DefaultEndpoint, UriKind.Absolute, out Uri? defaultUri) ? defaultUri : null,
                value => Edit(revision, () => _draft = _draft with { Endpoint = value?.ToString() ?? string.Empty })));
        }
        if (UsesApiKey)
        {
            fields.Add(new SetupSecretField(new("api-key", "API key")
            {
                IsRequired = _draft.Transport != LlmTransport.OpenAiCompatibleChat,
                HelpText = "Keep preserves the saved key; replace and clear are committed only when saved."
            }, !string.IsNullOrEmpty(Credential.ApiKey), _credentialChange,
                value => Edit(revision, () =>
                {
                    _credentialChange = value.Kind;
                    string? key = value.Kind switch
                    {
                        SetupSecretEditKind.Replace => value.Value,
                        SetupSecretEditKind.Clear => null,
                        _ => LlmCredential.Parse(_original?.Credential)?.ApiKey
                    };
                    _draft = _draft with { Credential = (Credential with { ApiKey = key }).Serialize() };
                })));
        }
        if (UsesAws)
        {
            LlmAwsCredentials aws = Credential.Aws ?? new();
            fields.Add(new SetupChoiceField(new("aws-profile", "AWS profile") { IsRequired = true }, aws.Profile ?? string.Empty,
                string.Empty, value => Edit(revision, () => SetAws((Credential.Aws ?? new()) with { Profile = value })), true,
                (query, token) => EnvironmentChoices(query, ct => _environment!.GetProfilesAsync(ct), token), revision, SetupSideEffect.LocalVerification));
            fields.Add(new SetupChoiceField(new("aws-region", "AWS region") { IsRequired = true }, aws.Region ?? string.Empty,
                string.Empty, value => Edit(revision, () => SetAws((Credential.Aws ?? new()) with { Region = value })), true,
                (query, token) => EnvironmentChoices(query, ct => _environment!.GetRegionsAsync(aws.Profile, ct), token), revision, SetupSideEffect.LocalVerification));
        }
        SetupStep connectionStep = _definition.SetupSteps.Count > 0 ? _definition.SetupSteps[0] : new("connection", "Connection", "Configure the provider.", []);
        SetupStep modelStep = _definition.SetupSteps.Count > 1 ? _definition.SetupSteps[^2] : new("model", "Model", "Select a model.", []);
        SetupStep testStep = _definition.SetupSteps.Count > 2 ? _definition.SetupSteps[^1] : new("test", "Test", "Explicitly test the model.", []);
        return
        [
            connectionStep with { Sections = _draft.Transport == LlmTransport.OpenAiCompatibleChat
                ? [new("connection", "Connection", fields.AsReadOnly()), AdvancedCapabilities(revision)]
                : [new("connection", "Connection", fields.AsReadOnly())] },
            modelStep with { Sections = [new("model", "Model",
            [new SetupChoiceField(new("model", "Model") { IsRequired = true,
                HelpText = "Discover available models where supported, or enter an exact model ID." }, _model,
                _definition.DefaultModelId, value => Edit(revision, () => _model = value), true, ListModelsAsync, revision,
                SetupSideEffect.Network | (UsesCli ? SetupSideEffect.ExternalCli : SetupSideEffect.None))])] },
            testStep
        ];
    }

    private SetupSection AdvancedCapabilities(long revision)
    {
        LlmCapabilityOverrides current = _draft.CapabilityOverrides ?? new();
        SetupChoiceField Override(string id, string label, bool? value,
            Func<LlmCapabilityOverrides, bool?, LlmCapabilityOverrides> update) => new(
            new(id, label) { HelpText = "Override only when the endpoint's protocol support is known. Default uses the adapter's detection." },
            value is null ? "default" : value.Value ? "enabled" : "disabled", "default", selection =>
            {
                if (selection is not ("default" or "enabled" or "disabled"))
                { return new(SetupEditState.Invalid, [new("override-invalid", "Select default, enabled or disabled.", id)]); }
                return Edit(revision, () => _draft = _draft with
                { CapabilityOverrides = update(_draft.CapabilityOverrides ?? new(), selection == "default" ? null : selection == "enabled") });
            }, false, (query, token) => QueryChoicesAsync(query,
                _ => Task.FromResult(Page(query, [new("default", "Default"), new("enabled", "Enabled"), new("disabled", "Disabled")])), token), revision);
        return new("capabilities", "Endpoint capability overrides",
        [
            Override("streaming", "Streaming", current.SupportsStreaming, (value, selection) => value with { SupportsStreaming = selection }),
            Override("streaming-usage", "Streaming usage", current.SupportsStreamingUsage, (value, selection) => value with { SupportsStreamingUsage = selection }),
            Override("model-listing", "Model listing", current.SupportsModelListing, (value, selection) => value with { SupportsModelListing = selection }),
            Override("json-schema", "JSON schema output", current.SupportsJsonSchema, (value, selection) => value with { SupportsJsonSchema = selection }),
            Override("json-object", "JSON object output", current.SupportsJsonObject, (value, selection) => value with { SupportsJsonObject = selection }),
            Override("reasoning-effort", "Reasoning effort", current.SupportsReasoningEffort, (value, selection) => value with { SupportsReasoningEffort = selection }),
            Override("temperature", "Temperature", current.SupportsTemperature, (value, selection) => value with { SupportsTemperature = selection }),
            Override("tools", "Tool use", current.SupportsToolUse, (value, selection) => value with { SupportsToolUse = selection }),
            Override("prompt-images", "Prompt images", current.SupportsPromptImages, (value, selection) => value with { SupportsPromptImages = selection }),
            Override("prompt-documents", "Prompt documents", current.SupportsPromptDocuments, (value, selection) => value with { SupportsPromptDocuments = selection }),
            Override("tool-result-images", "Tool-result images", current.SupportsToolResultImages, (value, selection) => value with { SupportsToolResultImages = selection }),
            Override("tool-result-documents", "Tool-result documents", current.SupportsToolResultDocuments, (value, selection) => value with { SupportsToolResultDocuments = selection })
        ], IsAdvanced: true);
    }

    private void SetAws(LlmAwsCredentials aws)
    {
        _draft = _draft with { Credential = (Credential with { Aws = aws }).Serialize() };
        _credentialChange = SetupSecretEditKind.Replace;
    }

    private Task<SetupChoicePage> EnvironmentChoices(SetupChoiceQuery query,
        Func<CancellationToken, Task<IReadOnlyList<SetupChoice>>> get, CancellationToken token) =>
        _environment is null ? Task.FromResult(new SetupChoicePage(query.ExpectedRevision, [], State: SetupChoiceState.Unavailable))
        : QueryChoicesAsync(query, async ct => Page(query, await get(ct).ConfigureAwait(false)), token);

    private Task<SetupChoicePage> ListModelsAsync(SetupChoiceQuery query, CancellationToken token) =>
        QueryChoicesAsync(query, async ct =>
        {
            LlmProviderCapabilities capabilities = await _service.GetCapabilitiesAsync(_identity.Id, ct).ConfigureAwait(false);
            if (!capabilities.SupportsModelListing)
            {
                return new(query.ExpectedRevision, [], State: SetupChoiceState.Unavailable,
                    Issues: [new("manual-model", "This connection requires manual model entry.", "model", Severity: SetupIssueSeverity.Warning)]);
            }
            IReadOnlyList<LlmModel> models = await _service.ListModelsAsync(_identity.Id, ct).ConfigureAwait(false);
            return Page(query, models.Select(model => new SetupChoice(model.Id, model.DisplayName, model.OwnedBy)));
        }, token);

    private static SetupChoicePage Page(SetupChoiceQuery query, IEnumerable<SetupChoice> choices)
    {
        if (!int.TryParse(query.Continuation ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out int offset))
        {
            return new(query.ExpectedRevision, [], State: SetupChoiceState.Failed);
        }
        SetupChoice[] matches = choices.Where(choice => choice.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || choice.Value.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).OrderBy(choice => choice.Value, StringComparer.Ordinal).ToArray();
        return new(query.ExpectedRevision, matches.Skip(offset).Take(query.PageSize).ToArray(),
            offset + query.PageSize < matches.Length ? (offset + query.PageSize).ToString(CultureInfo.InvariantCulture) : null);
    }

    protected override IReadOnlyList<ISetupAction> CreateActions(long revision)
    {
        var actions = new List<ISetupAction>();
        if (_draft.Transport == LlmTransport.OpenAiCodex)
        {
            actions.Add(BindAction(revision, new("authorize", "Sign in with ChatGPT", SetupActionPurpose.Authenticate,
                SetupSideEffect.Network | SetupSideEffect.BrowserInteraction), StartDeviceAsync));
        }
        if (_draft.Transport == LlmTransport.AnthropicClaudeCli)
        {
            actions.Add(BindAction(revision, new("authorize-catalog", "Authorize optional model catalog", SetupActionPurpose.Authenticate,
                SetupSideEffect.Network | SetupSideEffect.BrowserInteraction), StartBrowserAsync));
        }
        if (_deviceCode is not null || _browserAuthorization is not null)
        {
            actions.Add(ContinueAction(revision));
            actions.Add(CancelAction(revision));
        }
        actions.Add(BindAction(revision, new("check", UsesCli ? "Check CLI and authentication" : "Check authentication and model access",
            SetupActionPurpose.CheckPrerequisites, SetupSideEffect.Network | (UsesCli ? SetupSideEffect.ExternalCli : SetupSideEffect.None),
            ConnectionValidation().IsValid), CheckAsync));
        actions.Add(BindAction(revision, new("test", "Test selected model", SetupActionPurpose.Test,
            SetupSideEffect.Network | SetupSideEffect.PotentialBilling | (UsesCli ? SetupSideEffect.ExternalCli : SetupSideEffect.None),
            ValidateDraft().IsValid), TestAsync));
        return actions.AsReadOnly();
    }

    private ISetupAction ContinueAction(long revision) => BindAction(revision,
        new("continue-authorization", "Continue authorization", SetupActionPurpose.ContinueAuthentication, SetupSideEffect.Network), ContinueAuthorizationAsync);
    private ISetupAction CancelAction(long revision) => BindAction(revision,
        new("cancel-authorization", "Cancel authorization", SetupActionPurpose.CancelAuthentication, SetupSideEffect.None), (_, _) =>
        {
            Publish(ClearAuthorization);
            return Task.FromResult(SetupActionResult.Completed);
        });

    private async Task<SetupActionResult> StartDeviceAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        LlmDeviceCode code = await _service.StartCodexLoginAsync(token).ConfigureAwait(false);
        Publish(() => { ClearAuthorization(); _deviceCode = code; });
        return Interaction();
    }

    private Task<SetupActionResult> StartBrowserAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LlmAuthorizationSession authorization = _service.StartClaudeLogin();
        Publish(() => { ClearAuthorization(); _browserAuthorization = authorization; });
        return Task.FromResult(Interaction());
    }

    private SetupActionResult Interaction()
        => new(SetupActionState.NeedsInteraction, Interaction: GetInteraction());

    protected override SetupInteraction? GetInteraction()
    {
        long revision = Revision;
        if (_deviceCode is not null)
        {
            return new SetupDeviceAuthorization(
                new(_deviceCode.VerificationUrl), _deviceCode.UserCode, null, ContinueAction(revision), CancelAction(revision));
        }
        if (_browserAuthorization is null) { return null; }
        var callback = new SetupTextField(new("authorization-callback", "Complete authorization code") { IsRequired = true },
            string.Empty, string.Empty, value => Edit(revision, () => { _callback = value; _editingCallback = true; }));
        return new SetupBrowserAuthorization(
            new(_browserAuthorization.AuthorizeUrl), callback, ContinueAction(revision), CancelAction(revision));
    }

    private async Task<SetupActionResult> ContinueAuthorizationAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        if (_deviceCode is not null)
        {
            progress?.Report(new("authorizing", "Waiting for device authorization."));
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _deviceCode.IntervalSeconds)), token).ConfigureAwait(false);
            try
            {
                await _service.CompleteCodexLoginAsync(_identity.Id, _deviceCode.DeviceAuthId, _deviceCode.UserCode,
                    Revision.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
            }
            catch (LlmAuthPendingException) { return Interaction(); }
        }
        else if (_browserAuthorization is not null)
        {
            // Callback editing changes the draft revision, but not the captured authorization identity.
            await _service.CompleteClaudeLoginAsync(_identity.Id, _browserAuthorization, _callback,
                Revision.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
        }
        else { return new(SetupActionState.Unavailable); }
        Publish(() =>
        {
            ClearAuthorization();
            // Catalog OAuth does not establish CLI authentication.
            if (_draft.Transport == LlmTransport.OpenAiCodex) { Status = Status with { Authenticated = SetupObservation.Known(true) }; }
        });
        return SetupActionResult.Completed;
    }

    private async Task<SetupActionResult> CheckAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        Publish(() => Status = Status with { Authenticated = SetupObservation.Unknown, Verified = SetupObservation.Unknown });
        bool authenticated;
        if (UsesCli)
        {
            authenticated = await _service.IsAuthenticatedAsync(_identity.Id, token).ConfigureAwait(false);
        }
        else
        {
            LlmProviderCapabilities capabilities = await _service.GetCapabilitiesAsync(_identity.Id, token).ConfigureAwait(false);
            if (!capabilities.SupportsModelListing)
            {
                return new(SetupActionState.Unavailable, [new("explicit-test-required", "This provider requires an explicit inference test to confirm remote access.")]);
            }
            await _service.ListModelsAsync(_identity.Id, token).ConfigureAwait(false);
            authenticated = true;
        }
        Publish(() => Status = Status with { Authenticated = SetupObservation.Known(authenticated), Verified = SetupObservation.Known(authenticated) });
        return authenticated ? SetupActionResult.Completed : new(SetupActionState.NeedsInteraction,
            Interaction: new SetupExternalPrerequisite(_definition.SetupSteps.Count > 0
                ? _definition.SetupSteps[0].Instructions : "Install and authenticate the provider's CLI, then recheck.",
                BindAction(Revision, new("check", "Recheck authentication", SetupActionPurpose.CheckPrerequisites,
                    SetupSideEffect.ExternalCli | SetupSideEffect.Network), CheckAsync)));
    }

    private async Task<SetupActionResult> TestAsync(IProgress<SetupProgress>? progress, CancellationToken token)
    {
        Publish(() => Status = Status with { Ready = SetupObservation.Unknown });
        bool completed;
        if (_draft.Transport == LlmTransport.AwsBedrockAgentCore && _client is ILlmAgentClient agent)
        {
            await using ILlmAgentSession session = await agent.StartSessionAsync(CurrentConnection(), new()
            {
                ModelId = _model,
                WorkingDirectory = string.Empty,
                Sandbox = LlmAgentSandboxMode.ReadOnly,
                ApprovalPolicy = LlmAgentApprovalPolicy.OnRequest,
                Handlers = new() { ToolHandler = (_, _) => throw new NotSupportedException("Setup tests grant no host tools.") }
            }, token).ConfigureAwait(false);
            completed = false;
            await foreach (LlmAgentEvent item in session.RunTurnAsync(new()
            { Input = [new LlmAgentTextContent("Reply with OK. Do not use tools.")] }, token).ConfigureAwait(false))
            {
                if (item is LlmAgentTurnCompletedEvent done) { completed = done.Status == LlmAgentTurnStatus.Completed; }
            }
        }
        else
        {
            LlmChatResult result = await _service.CompleteAsync(_identity.Id, new()
            { ModelId = _model, Messages = [new LlmChatMessage("user", "Reply with OK.")] }, token).ConfigureAwait(false);
            completed = result.Status == LlmCompletionStatus.Completed;
        }
        Publish(() => Status = Status with { Ready = SetupObservation.Known(completed), Verified = SetupObservation.Known(completed) });
        return completed ? SetupActionResult.Completed : new(SetupActionState.Failed, [new("test-incomplete", "The provider did not complete the test.")]);
    }

    protected override SetupValidation ValidateDraft()
    {
        List<SetupIssue> issues = [.. ConnectionValidation().Issues];
        if (string.IsNullOrWhiteSpace(_model)) { issues.Add(new("model-required", "Select or enter a model.", "model")); }
        return new(issues.AsReadOnly());
    }

    private SetupValidation ConnectionValidation()
    {
        var issues = new List<SetupIssue>();
        if (UsesCli)
        {
            if (string.IsNullOrWhiteSpace(_draft.Endpoint) || _draft.Endpoint.IndexOfAny(['\r', '\n', '\0']) >= 0)
            { issues.Add(new("executable-invalid", "Enter an executable name or path, not a shell command.", "executable")); }
        }
        else if (_draft.Transport != LlmTransport.AwsBedrockAgentCore
            && (!Uri.TryCreate(_draft.Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https")
                || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0))
        { issues.Add(new("endpoint-invalid", "Use an HTTP(S) base URL without credentials, query or fragment.", "endpoint")); }
        if (UsesApiKey && _draft.Transport != LlmTransport.OpenAiCompatibleChat
            && string.IsNullOrWhiteSpace(Credential.ApiKey) && Credential.SecretHeaders.Count == 0)
        { issues.Add(new("key-required", "An API credential is required.", "api-key")); }
        if (UsesAws)
        {
            if (_environment is null) { issues.Add(new("setup-environment-missing", "Register the provider's AWS setup support.")); }
            else { issues.AddRange(_environment.Validate(CurrentConnection()).Issues); }
        }
        return new(issues.AsReadOnly());
    }

    protected override void OnDraftEdited()
    {
        if (_editingCallback) { _editingCallback = false; return; }
        base.OnDraftEdited();
        ClearAuthorization();
    }

    private LlmConnection CurrentConnection() => Read(() => _draft with { Version = Revision.ToString(CultureInfo.InvariantCulture) });

    protected override LlmSetupChanges CreateChanges() => new(_identity,
        new(_draft with { Credential = null, Version = _identity.ExpectedVersion ?? string.Empty }, _model),
        _credentialChange == SetupSecretEditKind.Keep ? SetupSecretEdit.Keep
            : _draft.Credential is null ? SetupSecretEdit.Clear : SetupSecretEdit.Replace(_draft.Credential),
        _original is not null && _original.Endpoint != _draft.Endpoint,
        _original is null || _original.Endpoint != _draft.Endpoint || _originalModel != _model
            || _original.CapabilityOverrides != _draft.CapabilityOverrides || _credentialChange != SetupSecretEditKind.Keep);

    private void ClearAuthorization() { _deviceCode = null; _browserAuthorization = null; _callback = string.Empty; }

    protected override void ClearSensitiveState()
    {
        _original = null;
        _draft = _draft with { Credential = null, AdditionalHeaders = new Dictionary<string, string>() };
        ClearAuthorization();
    }

    private sealed class DraftSource(LlmSetupSession owner) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(connectionId == owner._identity.Id ? owner.CurrentConnection() : null);
        }

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (connectionId != owner._identity.Id || !long.TryParse(expectedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out long revision)
                || !owner.PublishIfCurrent(revision, () =>
                {
                    owner._draft = owner._draft with { Credential = credentialJson };
                    owner._credentialChange = SetupSecretEditKind.Replace;
                    owner.Status = owner.Status with
                    {
                        Authenticated = owner.UsesCli ? owner.Status.Authenticated : SetupObservation.Unknown,
                        Verified = SetupObservation.Unknown,
                        Ready = SetupObservation.Unknown
                    };
                }))
            { throw new InvalidOperationException("The setup draft changed during authentication."); }
            return Task.CompletedTask;
        }
    }
}
