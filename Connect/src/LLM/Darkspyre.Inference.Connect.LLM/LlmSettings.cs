using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
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

/// <summary>
/// Editable LLM connection settings. Plain data: edits change only this object. A sign-in's credential
/// and its in-progress state are held here until Save.
/// </summary>
public sealed class LlmSettings : InferenceSettings<LlmSetupChanges>
{
    /// <summary>The connection version the settings present to their own sign-in; a draft has no saved version.</summary>
    internal const string DraftVersion = "draft";

    private static readonly SetupChoice[] OverrideChoices =
        [new("default", "Default"), new("enabled", "Enabled"), new("disabled", "Disabled")];

    private readonly LlmProviderDefinition _definition;
    private readonly Func<LlmConnection, SetupValidation>? _validateEnvironment;
    private readonly bool _hasEnvironment;
    private readonly LlmConnection? _original;
    private readonly LlmConnection _baseline;
    private readonly string _originalModel;
    private readonly HashSet<string> _overrideFields = new(StringComparer.Ordinal);
    private LlmConnection _draft;
    private string _model;
    private SetupSecretEditKind _credentialChange;
    private string _callback = string.Empty;
    private bool _editingCallback;
    private string _callbackFieldId = string.Empty;

    internal LlmSettings(LlmProviderDefinition definition, LlmTransport transport, SetupConfigurationIdentity identity,
        LlmConnection? existing, string? modelId, ILlmSetupEnvironment? environment)
        : base(definition.SetupDefinition, identity)
    {
        if ((identity.Intent == SetupChangeIntent.Edit) != (existing is not null)
            || (existing is not null && (existing.Transport != transport || existing.Version != identity.ExpectedVersion)))
        {
            throw new ArgumentException("An edit requires the exact saved connection and version.", nameof(existing));
        }
        _definition = definition;
        _hasEnvironment = environment is not null;
        _validateEnvironment = environment is null ? null : environment.Validate;
        _original = existing;
        LlmConnection opened = existing is null ? new() { Version = "0", Transport = transport, Endpoint = definition.DefaultEndpoint }
            : existing with
            {
                AdditionalHeaders = new Dictionary<string, string>(existing.AdditionalHeaders, StringComparer.OrdinalIgnoreCase),
                ProviderOptions = existing.ProviderOptions.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
                ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>(existing.ModelCapabilityOverrides, StringComparer.Ordinal)
            };
        // Direct Codex has one official endpoint and no endpoint field. A saved former value, such as a
        // CLI executable, differs from the baseline, so the next save replaces it.
        _draft = UsesFixedEndpoint(transport) ? opened with { Endpoint = definition.DefaultEndpoint } : opened;
        _model = _originalModel = modelId ?? existing?.Azure?.DeploymentId ?? definition.DefaultModelId;
        if (UsesAzure && _draft.Azure is null) _draft = _draft with { Azure = new() { DeploymentId = _model } };
        _baseline = opened;
        Status = new() { Configured = SetupObservation.Known(existing is not null), Authenticated = SetupObservation.Unknown };
    }

    internal LlmTransport Transport => _draft.Transport;

    internal string ModelId => _model;

    internal bool UsesCli => _draft.Transport is LlmTransport.AnthropicClaudeCli or LlmTransport.GoogleAntigravityCli;
    internal bool UsesApple => _draft.Transport == LlmTransport.AppleFoundationModels;
    internal string? ApplePrerequisite { get; set; }
    // A SelfHost connection's endpoint is the ID of an engine the host runs in its own process.
    private bool UsesSelfHost => _draft.Transport == LlmTransport.SelfHost;

    internal string? AwsProfile => Credential.Aws?.Profile;

    /// <summary>The device sign-in in progress, if any.</summary>
    internal LlmDeviceCode? DeviceCode { get; private set; }

    /// <summary>The browser sign-in in progress, if any.</summary>
    internal LlmAuthorizationSession? BrowserAuthorization { get; private set; }

    internal string AuthorizationCallback => _callback;

    /// <summary>A CLI provider reported it is not authenticated; the settings show its prerequisite.</summary>
    internal bool CliPrerequisite { get; set; }

    private static bool UsesFixedEndpoint(LlmTransport transport) => transport is LlmTransport.OpenAiCodex or LlmTransport.GoogleVertexAi or LlmTransport.GoogleGeminiApi;
    private bool UsesVertex => _draft.Transport == LlmTransport.GoogleVertexAi;
    private bool UsesAzure => _draft.Transport is LlmTransport.AzureOpenAiResponses or LlmTransport.AzureOpenAiChat;
    private LlmVertexConfiguration Vertex => _draft.Vertex ?? new();
    private bool UsesAws => _draft.Transport is LlmTransport.AwsBedrockConverse or LlmTransport.AwsBedrockAgentCore;
    private bool UsesApiKey => _draft.Transport is LlmTransport.OpenAiResponses or LlmTransport.OpenAiCompatibleChat or LlmTransport.AnthropicMessages
        or LlmTransport.GoogleGeminiApi || UsesVertex && Vertex.AuthenticationMode == LlmGoogleAuthenticationMode.ApiKey || UsesAzure;
    private LlmCredential Credential => LlmCredential.Parse(_draft.Credential) ?? new();

    protected override bool IsDraftDirty =>
        _draft.Endpoint != _baseline.Endpoint || _draft.Vertex != _baseline.Vertex || _draft.Azure != _baseline.Azure || _draft.ChatCompletions != _baseline.ChatCompletions || _model != _originalModel
            || (_draft.CapabilityOverrides ?? new()) != (_baseline.CapabilityOverrides ?? new()) || _callback.Length != 0
            || (UsesAws ? NormalizeAws(Credential.Aws) != NormalizeAws(LlmCredential.Parse(_baseline.Credential)?.Aws)
                : _credentialChange != SetupSecretEditKind.Keep);

    /// <summary>Direct Codex requires its ChatGPT sign-in, or a saved credential that is being kept.</summary>
    protected override bool IsSignInSatisfied => _draft.Transport != LlmTransport.OpenAiCodex
        || Status.Authenticated.Value == true
        || _credentialChange == SetupSecretEditKind.Keep && !string.IsNullOrEmpty(LlmCredential.Parse(_original?.Credential)?.Serialize());

    private static LlmAwsCredentials NormalizeAws(LlmAwsCredentials? value) => (value ?? new()) with
    { Profile = value?.Profile ?? string.Empty, Region = value?.Region ?? string.Empty };

    protected override IReadOnlyList<SetupStep> CreateSteps()
    {
        var fields = new List<SetupField>();
        if (UsesCli || UsesApple)
        {
            fields.Add(new SetupExecutableField(new("executable", UsesApple ? "Apple helper executable or absolute path" : "CLI executable or absolute path") { IsRequired = true },
                new(_draft.Endpoint), new(_definition.DefaultEndpoint), value => Edit(() => _draft = _draft with { Endpoint = value.Value })));
        }
        else if (UsesSelfHost)
        {
            fields.Add(new SetupChoiceField(new("engine", "Engine")
            {
                IsRequired = true,
                HelpText = "An engine this application runs in its own process. The application loads it."
            },
                _draft.Endpoint, string.Empty, value => Edit(() => _draft = _draft with { Endpoint = value }), false,
                SetupSideEffect.None, SetupChoiceCapabilities.Searchable));
        }
        else if (_draft.Transport == LlmTransport.AwsBedrockAgentCore)
        {
            fields.Add(new SetupTextField(new("harness-arn", "Harness ARN") { IsRequired = true }, _draft.Endpoint,
                _definition.DefaultEndpoint, value => Edit(() => _draft = _draft with { Endpoint = value })));
        }
        else if (!UsesFixedEndpoint(_draft.Transport))
        {
            fields.Add(new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true },
                Uri.TryCreate(_draft.Endpoint, UriKind.Absolute, out Uri? uri) ? uri : null,
                Uri.TryCreate(_definition.DefaultEndpoint, UriKind.Absolute, out Uri? defaultUri) ? defaultUri : null,
                value => Edit(() => _draft = _draft with { Endpoint = value?.ToString() ?? string.Empty })));
        }
        if (UsesApiKey)
        {
            fields.Add(new SetupSecretField(new("api-key", "API key")
            {
                IsRequired = _draft.Transport != LlmTransport.OpenAiCompatibleChat,
                HelpText = "Keep preserves the saved key; replace and clear are committed only when saved."
            }, !string.IsNullOrEmpty(Credential.ApiKey), _credentialChange,
                value => Edit(() =>
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
        if (UsesVertex)
        {
            if (Vertex.AuthenticationMode != LlmGoogleAuthenticationMode.ApiKey)
            {
                fields.Add(new SetupTextField(new("google-project", "Google Cloud project") { IsRequired = true }, Vertex.ProjectId ?? string.Empty,
                    string.Empty, value => Edit(() => _draft = _draft with { Vertex = Vertex with { ProjectId = value } })));
                fields.Add(new SetupTextField(new("google-location", "Location")
                {
                    IsRequired = true,
                    HelpText = "Enter an explicit region or global. Global routing does not pin processing to one region."
                }, Vertex.Location ?? string.Empty,
                    string.Empty, value => Edit(() => _draft = _draft with { Vertex = Vertex with { Location = value } })));
            }
            fields.Add(new SetupChoiceField(new("google-authentication", "Authentication"), Vertex.AuthenticationMode.ToString(),
                LlmGoogleAuthenticationMode.ApplicationDefault.ToString(), value =>
                {
                    if (!Enum.TryParse<LlmGoogleAuthenticationMode>(value, out var mode) || !Enum.IsDefined(mode))
                    { return new(SetupEditState.Invalid, [new("google-authentication-invalid", "Select a Google authentication mode.")]); }
                    return Edit(() =>
                    {
                        _draft = _draft with
                        {
                            Vertex = mode == LlmGoogleAuthenticationMode.ApiKey
                                ? Vertex with { AuthenticationMode = mode, ProjectId = null, Location = null }
                                : Vertex with { AuthenticationMode = mode }
                        };
                    });
                }, false));
            if (Vertex.AuthenticationMode != LlmGoogleAuthenticationMode.ApiKey
                && (Vertex.AuthenticationMode == LlmGoogleAuthenticationMode.ServiceAccount || Credential.Google is not null))
            {
                fields.Add(new SetupSecretField(new("google-service-account", "Service-account JSON key")
                {
                    IsRequired = Vertex.AuthenticationMode == LlmGoogleAuthenticationMode.ServiceAccount,
                    HelpText = "Stored only by the host's protected credential store. Clear a saved key when switching to ADC."
                },
                    Credential.Google is not null, _credentialChange, value => Edit(() =>
                    {
                        _credentialChange = value.Kind;
                        LlmGoogleCredentials? google = value.Kind switch
                        {
                            SetupSecretEditKind.Replace => new() { ServiceAccountJson = value.Value ?? string.Empty },
                            SetupSecretEditKind.Clear => null,
                            _ => LlmCredential.Parse(_original?.Credential)?.Google
                        };
                        _draft = _draft with { Credential = (Credential with { Google = google }).Serialize() };
                    })));
            }
        }
        if (UsesAws)
        {
            LlmAwsCredentials aws = Credential.Aws ?? new();
            fields.Add(new SetupChoiceField(new("aws-profile", "AWS profile") { IsRequired = true, IsEnabled = _hasEnvironment }, aws.Profile ?? string.Empty,
                string.Empty, value => Edit(() => SetAws((Credential.Aws ?? new()) with { Profile = value })), true,
                SetupSideEffect.LocalVerification, SetupChoiceCapabilities.Searchable));
            fields.Add(new SetupChoiceField(new("aws-region", "AWS region") { IsRequired = true, IsEnabled = _hasEnvironment }, aws.Region ?? string.Empty,
                string.Empty, value => Edit(() => SetAws((Credential.Aws ?? new()) with { Region = value })), true,
                SetupSideEffect.LocalVerification, SetupChoiceCapabilities.Searchable));
        }
        SetupStep connectionStep = _definition.SetupSteps.Count > 0 ? _definition.SetupSteps[0] : new("connection", "Connection", "Configure the provider.", []);
        SetupStep modelStep = _definition.SetupSteps.Count > 1 ? _definition.SetupSteps[^2] : new("model", "Model", "Select a model.", []);
        SetupStep testStep = _definition.SetupSteps.Count > 2 ? _definition.SetupSteps[^1] : new("test", "Test", "Explicitly test the model.", []);
        if (UsesAzure)
        {
            fields.Add(new SetupTextField(new("azure-model-profile", "Underlying model profile"),
                _draft.Azure?.ModelProfileId ?? string.Empty, string.Empty,
                value => Edit(() => _draft = _draft with { Azure = (_draft.Azure ?? new()) with { ModelProfileId = string.IsNullOrWhiteSpace(value) ? null : value } })));
        }
        return
        [
            connectionStep with { Sections = _draft.Transport == LlmTransport.OpenAiCompatibleChat || UsesAzure
                ? [new("connection", "Connection", fields.AsReadOnly()), AdvancedCapabilities()]
                : fields.Count == 0 ? [] : [new("connection", "Connection", fields.AsReadOnly())] },
            modelStep with { Sections = [new("model", "Model",
            [new SetupChoiceField(new("model", UsesAzure ? "Deployment" : "Model") { IsRequired = true,
                HelpText = UsesAzure ? "Enter the exact Azure deployment name, not its underlying public model ID." : "Discover available models where supported, or enter an exact model ID." }, _model,
                _definition.DefaultModelId, value => Edit(() => SetModel(value)), true,
                UsesApple ? SetupSideEffect.LocalVerification | SetupSideEffect.ExternalCli : UsesSelfHost || UsesAzure ? SetupSideEffect.None : SetupSideEffect.Network | (UsesCli ? SetupSideEffect.ExternalCli : SetupSideEffect.None),
                SetupChoiceCapabilities.Searchable)])] },
            testStep
        ];
    }

    private SetupSection AdvancedCapabilities()
    {
        LlmCapabilityOverrides current = _draft.CapabilityOverrides ?? new();
        SetupChoiceField Override(string id, string label, bool? value,
            Func<LlmCapabilityOverrides, bool?, LlmCapabilityOverrides> update, string? helpText = null)
        {
            _overrideFields.Add(id);
            return new(
                new(id, label) { HelpText = helpText ?? "Override only when the endpoint's protocol support is known. Default uses the adapter's detection." },
                value is null ? "default" : value.Value ? "enabled" : "disabled", "default", selection =>
                {
                    if (selection is not ("default" or "enabled" or "disabled"))
                    { return new(SetupEditState.Invalid, [new("override-invalid", "Select default, enabled or disabled.", id)]); }
                    return Edit(() => _draft = _draft with
                    { CapabilityOverrides = update(_draft.CapabilityOverrides ?? new(), selection == "default" ? null : selection == "enabled") });
                }, false);
        }
        SetupSection section = new("capabilities", "Endpoint capability overrides",
        [
            new SetupChoiceField(new("output-limit-field", "Output-limit field")
                { HelpText = UsesAzure ? "Default uses max_completion_tokens on Azure v1." : "Default uses max_completion_tokens on api.openai.com and max_tokens elsewhere." },
                _draft.ChatCompletions?.OutputLimitField switch
                {
                    LlmChatCompletionsOutputLimitField.MaxTokens => "max_tokens",
                    LlmChatCompletionsOutputLimitField.MaxCompletionTokens => "max_completion_tokens",
                    _ => "default"
                }, "default", selection =>
                {
                    if (selection is not ("default" or "max_tokens" or "max_completion_tokens"))
                        return new(SetupEditState.Invalid, [new("output-limit-invalid", "Select default, max_tokens or max_completion_tokens.", "output-limit-field")]);
                    return Edit(() => SetChatCompletions((_draft.ChatCompletions ?? new()) with
                    {
                        OutputLimitField = selection == "default" ? null : selection == "max_tokens"
                            ? LlmChatCompletionsOutputLimitField.MaxTokens : LlmChatCompletionsOutputLimitField.MaxCompletionTokens
                    }));
                }, false),
            new SetupChoiceField(new("prompt-cache-key", "Conversation cache key")
                { HelpText = UsesAzure ? "Default omits the conversation cache key. Enable only when the deployment supports prompt_cache_key." : "Default sends the conversation key only to api.openai.com. Enable only when the endpoint supports prompt_cache_key." },
                _draft.ChatCompletions?.UsePromptCacheKey is { } cacheKey ? cacheKey ? "enabled" : "disabled" : "default",
                "default", selection =>
                {
                    if (selection is not ("default" or "enabled" or "disabled"))
                        return new(SetupEditState.Invalid, [new("cache-key-invalid", "Select default, enabled or disabled.", "prompt-cache-key")]);
                    return Edit(() => SetChatCompletions((_draft.ChatCompletions ?? new()) with
                    { UsePromptCacheKey = selection == "default" ? null : selection == "enabled" }));
                }, false),
            Override("streaming", "Streaming", current.SupportsStreaming, (value, selection) => value with { SupportsStreaming = selection },
                "Disabled sends the endpoint's non-streaming request. The reply arrives in one piece when generation ends."),
            Override("streaming-usage", "Streaming usage", current.SupportsStreamingUsage, (value, selection) => value with { SupportsStreamingUsage = selection }),
            Override("usage-deltas", "Running usage", current.SupportsUsageDeltas, (value, selection) => value with { SupportsUsageDeltas = selection },
                "Enabled asks a vLLM-compatible endpoint for usage on every chunk."),
            Override("model-listing", "Model listing", current.SupportsModelListing, (value, selection) => value with { SupportsModelListing = selection }),
            Override("json-schema", "JSON schema output", current.SupportsJsonSchema, (value, selection) => value with { SupportsJsonSchema = selection }),
            Override("json-object", "JSON object output", current.SupportsJsonObject, (value, selection) => value with { SupportsJsonObject = selection }),
            Override("reasoning-text", "Reasoning text", current.SupportsReasoningText, (value, selection) => value with { SupportsReasoningText = selection }),
            .. Enum.GetValues<LlmReasoningLevel>().Select(level => Override(
                "reasoning-level-" + level.ToString().ToLowerInvariant(), "Reasoning level: " + level,
                current.ReasoningLevels?.Contains(level),
                (value, selection) => value with { ReasoningLevels = WithReasoningLevel(value.ReasoningLevels, level, selection) })),
            Override("temperature", "Temperature", current.SupportsTemperature, (value, selection) => value with { SupportsTemperature = selection }),
            Override("top-p", "Top-p", current.SupportsTopP, (value, selection) => value with { SupportsTopP = selection }),
            Override("top-k", "Top-k", current.SupportsTopK, (value, selection) => value with { SupportsTopK = selection }),
            Override("seed", "Seed", current.SupportsSeed, (value, selection) => value with { SupportsSeed = selection }),
            Override("presence-penalty", "Presence penalty", current.SupportsPresencePenalty, (value, selection) => value with { SupportsPresencePenalty = selection }),
            Override("frequency-penalty", "Frequency penalty", current.SupportsFrequencyPenalty, (value, selection) => value with { SupportsFrequencyPenalty = selection }),
            new SetupIntegerField(new("max-stop-sequences", "Maximum stop sequences") { HelpText = "Blank uses the adapter default. Zero disables stop sequences." },
                current.MaxStopSequences, null, selection =>
                {
                    if (selection is < 0 or > int.MaxValue)
                        return new(SetupEditState.Invalid, [new("stop-limit-invalid", "Enter a non-negative 32-bit integer.", "max-stop-sequences")]);
                    return Edit(() => _draft = _draft with
                    { CapabilityOverrides = (_draft.CapabilityOverrides ?? new()) with { MaxStopSequences = (int?)selection } });
                }, 0, int.MaxValue),
            Override("tools", "Tool use", current.SupportsToolUse, (value, selection) => value with { SupportsToolUse = selection }),
            Override("prompt-images", "Prompt images", current.SupportsPromptImages, (value, selection) => value with { SupportsPromptImages = selection }),
            Override("prompt-documents", "Prompt documents", current.SupportsPromptDocuments, (value, selection) => value with { SupportsPromptDocuments = selection }),
            Override("tool-result-images", "Tool-result images", current.SupportsToolResultImages, (value, selection) => value with { SupportsToolResultImages = selection }),
            Override("tool-result-documents", "Tool-result documents", current.SupportsToolResultDocuments, (value, selection) => value with { SupportsToolResultDocuments = selection })
        ], IsAdvanced: true);
        return !UsesAzure ? section : section with
        {
            Fields = [.. section.Fields.Where(field => field.Description.Id != "model-listing"
                && (_draft.Transport != LlmTransport.AzureOpenAiResponses || field.Description.Id is not ("output-limit-field" or "prompt-cache-key")))]
        };
    }

    /// <summary>
    /// Updates the accepted-level override for one level. An explicit Enabled or Disabled makes the
    /// override explicit: the endpoint then accepts exactly the enabled levels. Default removes the level,
    /// and the override returns to the adapter's detection once no level is enabled.
    /// </summary>
    private static HashSet<LlmReasoningLevel>? WithReasoningLevel(
        IReadOnlySet<LlmReasoningLevel>? levels, LlmReasoningLevel level, bool? selection)
    {
        HashSet<LlmReasoningLevel> updated = levels is null ? [] : [.. levels];
        if (selection is true) { updated.Add(level); }
        else { updated.Remove(level); }
        return selection is null && updated.Count == 0 ? null : updated;
    }

    private void SetAws(LlmAwsCredentials aws)
    {
        _draft = _draft with { Credential = (Credential with { Aws = aws }).Serialize() };
        _credentialChange = SetupSecretEditKind.Replace;
    }

    private void SetChatCompletions(LlmChatCompletionsConfiguration configuration) =>
        _draft = _draft with { ChatCompletions = configuration.OutputLimitField is null && configuration.UsePromptCacheKey is null ? null : configuration };

    private void SetModel(string value)
    {
        _model = value;
        if (UsesAzure) _draft = _draft with { Azure = (_draft.Azure ?? new()) with { DeploymentId = value } };
    }

    protected override SetupChoicePage? GetLocalChoices(string fieldId, SetupChoiceQuery query)
    {
        _ = CreateSteps(); // Registers the override field IDs for the current transport.
        return fieldId == "google-authentication" && UsesVertex
            ? Page(query, [new(LlmGoogleAuthenticationMode.ApplicationDefault.ToString(), "Application default credentials"),
                new(LlmGoogleAuthenticationMode.ServiceAccount.ToString(), "Service-account key"),
                new(LlmGoogleAuthenticationMode.ApiKey.ToString(), "API key (express mode)")])
            : fieldId == "output-limit-field" && _draft.Transport is LlmTransport.OpenAiCompatibleChat or LlmTransport.AzureOpenAiChat
                ? Page(query, [new("default", "Default"), new("max_tokens", "max_tokens"), new("max_completion_tokens", "max_completion_tokens")])
            : fieldId == "prompt-cache-key" && _draft.Transport is LlmTransport.OpenAiCompatibleChat or LlmTransport.AzureOpenAiChat ? Page(query, OverrideChoices)
            : _overrideFields.Contains(fieldId) ? Page(query, OverrideChoices) : null;
    }

    protected override IReadOnlyList<SetupActionDescriptor> CreateActions()
    {
        var actions = new List<SetupActionDescriptor>();
        if (_draft.Transport == LlmTransport.OpenAiCodex)
        {
            actions.Add(new("authorize", "Sign in with ChatGPT", SetupActionPurpose.Authenticate,
                SetupSideEffect.Network | SetupSideEffect.BrowserInteraction));
        }
        if (_draft.Transport == LlmTransport.AnthropicClaudeCli)
        {
            actions.Add(new("authorize-catalog", "Authorize optional model catalog", SetupActionPurpose.Authenticate,
                SetupSideEffect.Network | SetupSideEffect.BrowserInteraction));
        }
        if (DeviceCode is not null || BrowserAuthorization is not null)
        {
            actions.Add(ContinueAction);
            actions.Add(CancelAction);
        }
        actions.Add(new("check", UsesApple ? "Check helper and on-device model" : UsesCli ? "Check CLI and authentication" : UsesSelfHost ? "Check the engine's models" : "Check authentication and model access",
            SetupActionPurpose.CheckPrerequisites,
            UsesApple ? SetupSideEffect.LocalVerification | SetupSideEffect.ExternalCli : UsesSelfHost ? SetupSideEffect.LocalVerification : SetupSideEffect.Network | (UsesCli ? SetupSideEffect.ExternalCli : SetupSideEffect.None),
            ConnectionValidation().IsValid));
        actions.Add(new("test", "Test selected model", SetupActionPurpose.Test,
            UsesApple ? SetupSideEffect.LocalVerification | SetupSideEffect.ExternalCli : UsesSelfHost ? SetupSideEffect.LocalVerification
                : SetupSideEffect.Network | SetupSideEffect.PotentialBilling | (UsesCli ? SetupSideEffect.ExternalCli : SetupSideEffect.None),
            ValidateDraft().IsValid));
        return actions.AsReadOnly();
    }

    private static SetupActionDescriptor ContinueAction { get; } =
        new("continue-authorization", "Continue authorization", SetupActionPurpose.ContinueAuthentication, SetupSideEffect.Network);

    private static SetupActionDescriptor CancelAction { get; } =
        new("cancel-authorization", "Cancel authorization", SetupActionPurpose.CancelAuthentication, SetupSideEffect.None);

    /// <summary>Rebuilds <see cref="InferenceSettings.Interaction"/> from the sign-in in progress.</summary>
    internal void RefreshInteraction()
    {
        if (ApplePrerequisite is { } prerequisite)
        {
            Interaction = new SetupExternalPrerequisite(prerequisite,
                new("check", "Recheck on-device model", SetupActionPurpose.CheckPrerequisites,
                    SetupSideEffect.ExternalCli | SetupSideEffect.LocalVerification));
        }
        else if (CliPrerequisite)
        {
            Interaction = new SetupExternalPrerequisite(_definition.SetupSteps.Count > 0
                ? _definition.SetupSteps[0].Instructions : "Install and authenticate the provider's CLI, then recheck.",
                new("check", "Recheck authentication", SetupActionPurpose.CheckPrerequisites,
                    SetupSideEffect.ExternalCli | SetupSideEffect.Network));
        }
        else if (DeviceCode is not null)
        {
            Interaction = new SetupDeviceAuthorization(new(DeviceCode.VerificationUrl), DeviceCode.UserCode, null, ContinueAction, CancelAction);
        }
        else if (BrowserAuthorization is not null)
        {
            // The attempt-scoped ID lets a renderer retain one-way input for this sign-in only.
            var callback = new SetupTextField(new(_callbackFieldId, "Complete authorization code") { IsRequired = true },
                string.Empty, string.Empty, value => Edit(() => { _callback = value; _editingCallback = true; }));
            Interaction = new SetupBrowserAuthorization(new(BrowserAuthorization.AuthorizeUrl), callback, ContinueAction, CancelAction);
        }
        else
        {
            Interaction = null;
        }
    }

    internal void BeginDeviceAuthorization(LlmDeviceCode code)
    {
        ClearAuthorization();
        DeviceCode = code;
        RefreshInteraction();
    }

    internal void BeginBrowserAuthorization(LlmAuthorizationSession authorization)
    {
        ClearAuthorization();
        BrowserAuthorization = authorization;
        _callbackFieldId = "authorization-callback-" + Guid.NewGuid().ToString("N");
        RefreshInteraction();
    }

    internal void ClearAuthorization()
    {
        DeviceCode = null; BrowserAuthorization = null; _callback = string.Empty;
        _callbackFieldId = string.Empty; CliPrerequisite = false; ApplePrerequisite = null;
        Interaction = null;
    }

    /// <summary>Stores a sign-in's credential in these settings; Save commits it.</summary>
    internal void AcceptCredential(string credentialJson)
    {
        _draft = _draft with { Credential = credentialJson };
        _credentialChange = SetupSecretEditKind.Replace;
        Status = Status with
        {
            Authenticated = UsesCli ? Status.Authenticated : SetupObservation.Unknown,
            Verified = SetupObservation.Unknown,
            Ready = SetupObservation.Unknown
        };
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
        if (UsesApple)
        {
            try { AppleFoundationModelsClient.ValidateConnection(_draft); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            { issues.Add(new("apple-invalid", error.Message, "executable")); }
        }
        else if (UsesCli)
        {
            if (string.IsNullOrWhiteSpace(_draft.Endpoint) || _draft.Endpoint.IndexOfAny(['\r', '\n', '\0']) >= 0)
            { issues.Add(new("executable-invalid", "Enter an executable name or path, not a shell command.", "executable")); }
        }
        else if (UsesSelfHost)
        {
            if (string.IsNullOrWhiteSpace(_draft.Endpoint)) { issues.Add(new("engine-required", "Choose an engine.", "engine")); }
            if (Providers.SelfHostLlmClient.UnsupportedOptions(_draft) is { } unsupported)
            { issues.Add(new(unsupported.Code, unsupported.Message)); }
        }
        else if (_draft.Transport != LlmTransport.AwsBedrockAgentCore
            && (!Uri.TryCreate(_draft.Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https")
                || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0))
        { issues.Add(new("endpoint-invalid", "Use an HTTP(S) base URL without credentials, query or fragment.", "endpoint")); }
        if (UsesApiKey && _draft.Transport != LlmTransport.OpenAiCompatibleChat
            && string.IsNullOrWhiteSpace(Credential.ApiKey) && Credential.SecretHeaders.Count == 0)
        { issues.Add(new("key-required", "An API credential is required.", "api-key")); }
        if (UsesAzure && _draft.Azure?.DeploymentId != _model)
            issues.Add(new("azure-deployment-conflict", "The selected model must match the configured Azure deployment.", "model"));
        if (UsesAws || UsesVertex || UsesAzure)
        {
            if (_validateEnvironment is null) { issues.Add(new("setup-environment-missing", "Register the provider's setup support.")); }
            else { issues.AddRange(_validateEnvironment(CurrentConnection()).Issues); }
        }
        return new(issues.AsReadOnly());
    }

    protected override void OnDraftEdited()
    {
        if (_editingCallback) { _editingCallback = false; return; }
        base.OnDraftEdited();
        ClearAuthorization();
    }

    /// <summary>The connection these settings describe, as their own sign-in and tests use it.</summary>
    internal LlmConnection CurrentConnection() => _draft with { Version = DraftVersion };

    protected override LlmSetupChanges CreateChanges() => new(Identity,
        new(_draft with { Credential = null, Version = Identity.ExpectedVersion ?? string.Empty }, _model),
        _credentialChange == SetupSecretEditKind.Keep ? SetupSecretEdit.Keep
            : _draft.Credential is null ? SetupSecretEdit.Clear : SetupSecretEdit.Replace(_draft.Credential),
        _original is not null && (_original.Endpoint != _draft.Endpoint || _original.Vertex != _draft.Vertex || _original.Azure != _draft.Azure),
        _original is null || _original.Endpoint != _draft.Endpoint || _original.Vertex != _draft.Vertex || _original.Azure != _draft.Azure || _originalModel != _model
            || _original.ChatCompletions != _draft.ChatCompletions || _original.CapabilityOverrides != _draft.CapabilityOverrides || _credentialChange != SetupSecretEditKind.Keep);
}

/// <summary>
/// Opens, acts on and saves LLM connection settings. Sign-in, checks, tests and model lists run against
/// the settings' current values through a provider service that reads its connection from the settings.
/// </summary>
public sealed class LlmSettingsManager : InferenceSettingsManager<LlmSettings, LlmSetupChanges>
{
    private readonly IReadOnlyDictionary<LlmTransport, ILlmClient> _clients;
    private readonly IReadOnlyList<ILlmAuthenticator> _authenticators;
    private readonly IReadOnlyDictionary<LlmTransport, ILlmSetupEnvironment> _environments;

    internal LlmSettingsManager(IReadOnlyDictionary<LlmTransport, ILlmClient> clients, IEnumerable<ILlmAuthenticator> authenticators,
        IReadOnlyDictionary<LlmTransport, ILlmSetupEnvironment> environments, ISetupCommitTarget<LlmSetupChanges> target)
        : base(target)
    {
        _clients = clients;
        _authenticators = [.. authenticators];
        _environments = environments;
    }

    /// <summary>Returns settings for <paramref name="transport"/>, blank or from the saved connection.</summary>
    public Task<LlmSettings> OpenAsync(LlmTransport transport, SetupConfigurationIdentity identity,
        LlmConnection? existing = null, string? modelId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ILlmClient client = Client(transport);
        return Task.FromResult(new LlmSettings(client.ProviderDefinition, client.Transport, identity, existing, modelId,
            _environments.GetValueOrDefault(transport)));
    }

    protected override async Task<SetupActionResult> RunCoreAsync(LlmSettings settings, SetupActionDescriptor action,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        await using LlmProviderService service = Service(settings);
        switch (action.Id)
        {
            case "authorize":
                settings.BeginDeviceAuthorization(await service.StartCodexLoginAsync(cancellationToken).ConfigureAwait(false));
                return new(SetupActionState.NeedsInteraction, Interaction: settings.Interaction);
            case "authorize-catalog":
                settings.BeginBrowserAuthorization(await service.StartClaudeLoginAsync(cancellationToken).ConfigureAwait(false));
                return new(SetupActionState.NeedsInteraction, Interaction: settings.Interaction);
            case "continue-authorization":
                return await ContinueAuthorizationAsync(settings, service, progress, cancellationToken).ConfigureAwait(false);
            case "cancel-authorization":
                settings.ClearAuthorization();
                return SetupActionResult.Completed;
            case "check":
                return await CheckAsync(settings, service, cancellationToken).ConfigureAwait(false);
            case "test":
                return await TestAsync(settings, service, cancellationToken).ConfigureAwait(false);
            default:
                return new(SetupActionState.Unavailable);
        }
    }

    protected override async Task<SetupChoicePage> QueryProviderChoicesAsync(LlmSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken)
    {
        ILlmSetupEnvironment? environment = _environments.GetValueOrDefault(settings.Transport);
        switch (fieldId)
        {
            case "engine" when _clients.GetValueOrDefault(LlmTransport.SelfHost) is Providers.SelfHostLlmClient selfHost:
                return InferenceSettings.Page(query, (await selfHost.ListEnginesAsync(cancellationToken).ConfigureAwait(false))
                    .Select(engine => new SetupChoice(engine.Id, engine.DisplayName)));
            case "aws-profile" when environment is not null:
                return InferenceSettings.Page(query, await environment.GetProfilesAsync(cancellationToken).ConfigureAwait(false));
            case "aws-region" when environment is not null:
                return InferenceSettings.Page(query, await environment.GetRegionsAsync(settings.AwsProfile, cancellationToken).ConfigureAwait(false));
            case "model":
                {
                    await using LlmProviderService service = Service(settings);
                    LlmProviderCapabilities capabilities = await service.GetCapabilitiesAsync(settings.Identity.Id, cancellationToken).ConfigureAwait(false);
                    if (!capabilities.SupportsModelListing)
                    {
                        return new([], State: SetupChoiceState.Unavailable,
                            Issues: [new("manual-model", "This connection requires manual model entry.", "model", Severity: SetupIssueSeverity.Warning)]);
                    }
                    IReadOnlyList<LlmModel> models = await service.ListModelsAsync(settings.Identity.Id, cancellationToken).ConfigureAwait(false);
                    return InferenceSettings.Page(query, models.Select(model => new SetupChoice(model.Id, model.DisplayName, model.AvailabilityReason ?? model.OwnedBy))
                        .OrderBy(choice => choice.Value, StringComparer.Ordinal));
                }
            default:
                return new([], State: SetupChoiceState.Unavailable);
        }
    }

    private static async Task<SetupActionResult> ContinueAuthorizationAsync(LlmSettings settings, LlmProviderService service,
        IProgress<SetupProgress>? progress, CancellationToken token)
    {
        if (settings.DeviceCode is { } code)
        {
            progress?.Report(new("authorizing", "Waiting for device authorization."));
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, code.IntervalSeconds)), token).ConfigureAwait(false);
            try
            {
                await service.CompleteCodexLoginAsync(settings.Identity.Id, code.DeviceAuthId, code.UserCode,
                    LlmSettings.DraftVersion, token).ConfigureAwait(false);
            }
            catch (LlmAuthPendingException) { return new(SetupActionState.NeedsInteraction, Interaction: settings.Interaction); }
        }
        else if (settings.BrowserAuthorization is { } authorization)
        {
            await service.CompleteClaudeLoginAsync(settings.Identity.Id, authorization, settings.AuthorizationCallback,
                LlmSettings.DraftVersion, token).ConfigureAwait(false);
        }
        else { return new(SetupActionState.Unavailable); }
        settings.ClearAuthorization();
        // Catalog OAuth does not establish CLI authentication.
        if (settings.Transport == LlmTransport.OpenAiCodex)
        {
            settings.Status = settings.Status with { Authenticated = SetupObservation.Known(true) };
        }
        return SetupActionResult.Completed;
    }

    private async Task<SetupActionResult> CheckAsync(LlmSettings settings, LlmProviderService service, CancellationToken token)
    {
        settings.Status = settings.Status with { Authenticated = SetupObservation.Unknown, Verified = SetupObservation.Unknown };
        if (settings.UsesApple)
        {
            settings.ApplePrerequisite = null;
            settings.Status = settings.Status with { Ready = SetupObservation.Unknown };
            try
            {
                AppleProtocol.Ready(await ((AppleFoundationModelsClient)Client(settings.Transport))
                    .ProbeAsync(settings.CurrentConnection(), token).ConfigureAwait(false));
                settings.Status = settings.Status with { Verified = SetupObservation.Known(true) };
                settings.RefreshInteraction();
                return SetupActionResult.Completed;
            }
            catch (InferenceException error) when (error.Code is InferenceFailureCode.BackendUnavailable or InferenceFailureCode.ModelUnavailable)
            {
                settings.Status = settings.Status with { Verified = SetupObservation.Known(false), Ready = SetupObservation.Known(false) };
                settings.ApplePrerequisite = error.Message;
                settings.RefreshInteraction();
                return new(SetupActionState.NeedsInteraction, Interaction: settings.Interaction);
            }
        }
        bool authenticated;
        if (settings.UsesCli)
        {
            authenticated = await service.IsAuthenticatedAsync(settings.Identity.Id, token).ConfigureAwait(false);
        }
        else
        {
            LlmProviderCapabilities capabilities = await service.GetCapabilitiesAsync(settings.Identity.Id, token).ConfigureAwait(false);
            if (!capabilities.SupportsModelListing)
            {
                return new(SetupActionState.Unavailable, [new("explicit-test-required", "This provider requires an explicit inference test to confirm remote access.")]);
            }
            await service.ListModelsAsync(settings.Identity.Id, token).ConfigureAwait(false);
            authenticated = true;
        }
        settings.Status = settings.Status with { Authenticated = SetupObservation.Known(authenticated), Verified = settings.Transport is LlmTransport.GoogleVertexAi or LlmTransport.AzureOpenAiResponses or LlmTransport.AzureOpenAiChat ? SetupObservation.Unknown : SetupObservation.Known(authenticated) };
        settings.CliPrerequisite = !authenticated;
        settings.RefreshInteraction();
        return authenticated ? SetupActionResult.Completed : new(SetupActionState.NeedsInteraction, Interaction: settings.Interaction);
    }

    private async Task<SetupActionResult> TestAsync(LlmSettings settings, LlmProviderService service, CancellationToken token)
    {
        settings.Status = settings.Status with { Ready = SetupObservation.Unknown };
        bool completed;
        if (settings.Transport == LlmTransport.AwsBedrockAgentCore && Client(settings.Transport) is ILlmAgentClient agent)
        {
            await using ILlmAgentSession session = await agent.StartSessionAsync(settings.Identity.Id, settings.CurrentConnection(), new()
            {
                ModelId = settings.ModelId,
                Runtime = new() { WorkingDirectory = string.Empty, Policy = new LlmAgentHostRuntimePolicy { Sandbox = LlmAgentSandboxMode.ReadOnly } },
                ToolHandler = (_, _) => throw new NotSupportedException("Setup tests grant no host tools.")
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
            LlmChatResult result = await service.CompleteAsync(settings.Identity.Id, new()
            { ModelId = settings.ModelId, Messages = [new LlmChatMessage("user", "Reply with OK.")] }, token).ConfigureAwait(false);
            completed = result.Status == LlmCompletionStatus.Completed
                || settings.UsesApple && result.Status == LlmCompletionStatus.Unknown && !string.IsNullOrWhiteSpace(result.Text);
        }
        settings.Status = settings.Status with { Ready = SetupObservation.Known(completed), Verified = SetupObservation.Known(completed) };
        return completed ? SetupActionResult.Completed : new(SetupActionState.Failed, [new("test-incomplete", "The provider did not complete the test.")]);
    }

    private ILlmClient Client(LlmTransport transport) => _clients.TryGetValue(transport, out ILlmClient? client)
        ? client
        : throw new NotSupportedException($"No LLM client is registered for transport {transport}.");

    /// <summary>A provider service whose one connection is these settings' current connection.</summary>
    private LlmProviderService Service(LlmSettings settings) =>
        new(new SettingsSource(settings), [Client(settings.Transport)], _authenticators);

    private sealed class SettingsSource(LlmSettings settings) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(connectionId == settings.Identity.Id ? settings.CurrentConnection() : null);
        }

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (connectionId != settings.Identity.Id || expectedVersion != LlmSettings.DraftVersion)
            {
                throw new InvalidOperationException("The sign-in does not belong to these settings.");
            }
            settings.AcceptCredential(credentialJson);
            return Task.CompletedTask;
        }
    }
}
