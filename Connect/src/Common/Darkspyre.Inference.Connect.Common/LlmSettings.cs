using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Common.Setup;
using System.Globalization;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The saved configuration of an LLM setup entry.</summary>
/// <param name="Connection">The provider connection.</param>
/// <param name="ModelId">The selected model.</param>
public sealed record LlmSetupConfiguration(LlmConnection Connection, string ModelId);

/// <summary>The credential mutation is separate from ordinary persisted configuration.</summary>
/// <param name="Identity">The saved setup entry the changes apply to.</param>
/// <param name="Configuration">The new configuration.</param>
/// <param name="Credential">The change to the protected credential.</param>
/// <param name="RequiresReauthentication">True when the changes require signing in again.</param>
/// <param name="RequiresRetest">True when the changes require testing the connection again.</param>
public sealed record LlmSetupChanges(
    SetupConfigurationIdentity Identity, LlmSetupConfiguration Configuration,
    SetupSecretEdit Credential, bool RequiresReauthentication, bool RequiresRetest);

/// <summary>Optional provider SDK operations implemented in the SDK-owning adapter assembly.</summary>
public interface ILlmSetupEnvironment
{
    /// <summary>The transport this environment serves.</summary>
    LlmTransport Transport { get; }
    /// <summary>Returns the credential profiles the provider SDK finds on this machine.</summary>
    Task<IReadOnlyList<SetupChoice>> GetProfilesAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns the regions available for a profile, or for the default profile when null.</summary>
    Task<IReadOnlyList<SetupChoice>> GetRegionsAsync(string? profile, CancellationToken cancellationToken = default);
    /// <summary>Validates the provider SDK settings of a connection.</summary>
    SetupValidation Validate(LlmConnection connection);
}

/// <summary>
/// Editable LLM connection settings. Plain data: edits change only this object. A sign-in's credential
/// and its in-progress state are held here until Save.
/// </summary>
public sealed partial class LlmSettings : InferenceSettings<LlmSetupChanges>
{
    /// <summary>The connection version the settings present to their own sign-in; a draft has no saved version.</summary>
    internal const string DraftVersion = "draft";

    private static readonly SetupChoice[] OverrideChoices =
        [new("default", "Default"), new("enabled", "Enabled"), new("disabled", "Disabled")];

    private static readonly SetupChoice[] SupportChoices =
        [new("default", "Default"), new("unknown", "Unknown"), new("enabled", "Supported"), new("disabled", "Unsupported")];

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
    private string _cacheBucket = string.Empty;
    private string _cachePrefix = string.Empty;
    private readonly AzureEditorDraft? _azure;
    private readonly List<ContextLengthRow> _contextRows = [];

    /// <summary>One Context lengths row of an OpenAI API connection; a complete row sets the model's context length.</summary>
    private sealed record ContextLengthRow(string Id, string? ModelId, long? Length);

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
        LlmConnection opened = existing is null ? new()
        {
            Version = "0",
            Transport = transport,
            Endpoint = definition.DefaultEndpoint
        }
            : existing with
            {
                AdditionalHeaders = new Dictionary<string, string>(existing.AdditionalHeaders, StringComparer.OrdinalIgnoreCase),
                ProviderOptions = existing.ProviderOptions.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
                ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>(existing.ModelCapabilityOverrides, StringComparer.Ordinal)
            };
        _draft = UsesFixedEndpoint(transport) ? opened with { Endpoint = definition.DefaultEndpoint } : opened;
        _model = _originalModel = modelId ?? (UsesAzure ? existing?.Azure?.DeploymentId : null) ?? definition.DefaultModelId;
        if (UsesAzure && _draft.Azure is null) _draft = _draft with { Azure = new() { DeploymentId = _model } };
        if (UsesAzure) _azure = new(_draft.Azure!, LlmCredential.Parse(existing?.Credential) ?? new());
        _baseline = opened;
        _cacheBucket = opened.Vertex?.CacheStaging?.Bucket ?? string.Empty;
        _cachePrefix = opened.Vertex?.CacheStaging?.ObjectPrefix ?? string.Empty;
        _contextRows.AddRange(opened.ModelCapabilityOverrides
            .Where(pair => pair.Value.MaxContextTokens is not null)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ContextLengthRow(Guid.NewGuid().ToString("N"), pair.Key, pair.Value.MaxContextTokens)));
        Status = new()
        {
            Configured = SetupObservation.Known(existing is not null),
            Authenticated = SetupObservation.Unknown,
            Installed = UsesCli ? SetupObservation.Unknown : SetupObservation.NotApplicable
        };
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

    /// <summary>A CLI provider reported it is not installed or not authenticated; the settings show its prerequisite.</summary>
    internal bool CliPrerequisite { get; set; }

    /// <summary>The display names of the models the provider last listed for the model field, by model id.</summary>
    internal IReadOnlyDictionary<string, string> ModelNames { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>True when the settings offer an action that signs in.</summary>
    internal bool HasSignInAction => SignInActionId is not null;

    /// <summary>The id of the action that signs in, or null when the transport signs in some other way.</summary>
    private string? SignInActionId => _draft.Transport switch
    {
        LlmTransport.OpenAiCodex or LlmTransport.XaiResponses => "authorize",
        LlmTransport.AnthropicClaudeCli => "sign-in-claude",
        _ => null
    };

    private static bool UsesFixedEndpoint(LlmTransport transport) => transport is LlmTransport.OpenAiCodex or LlmTransport.GoogleVertexAi
        or LlmTransport.GoogleGeminiApi or LlmTransport.XaiResponses;
    private bool UsesXai => _draft.Transport == LlmTransport.XaiResponses;
    private bool UsesVertex => _draft.Transport == LlmTransport.GoogleVertexAi;
    private bool UsesAzure => _draft.Transport is LlmTransport.AzureOpenAiResponses or LlmTransport.AzureOpenAiChat;
    private LlmVertexConfiguration Vertex => _draft.Vertex ?? new();
    private bool UsesAws => _draft.Transport is LlmTransport.AwsBedrockConverse or LlmTransport.AwsBedrockAgentCore;
    private bool UsesApiKey => _draft.Transport is LlmTransport.OpenAiResponses or LlmTransport.OpenAiCompatibleChat or LlmTransport.AnthropicMessages
        or LlmTransport.GoogleGeminiApi || UsesVertex && Vertex.AuthenticationMode == LlmGoogleAuthenticationMode.ApiKey
        || UsesAzure && _azure!.Authentication == LlmAzureAuthenticationMode.ApiKey;
    private LlmCredential Credential => LlmCredential.Parse(_draft.Credential) ?? new();

    /// <inheritdoc />
    protected override bool IsDraftDirty =>
        _draft.Endpoint != _baseline.Endpoint || _draft.Vertex != _baseline.Vertex || (UsesAzure ? _azure!.IsDirty : _draft.Azure != _baseline.Azure) || _draft.ChatCompletions != _baseline.ChatCompletions || _draft.Xai != _baseline.Xai || _model != _originalModel
            || (_draft.CapabilityOverrides ?? new()) != (_baseline.CapabilityOverrides ?? new()) || _callback.Length != 0
            || !SameModelOverrides(_draft.ModelCapabilityOverrides, _baseline.ModelCapabilityOverrides)
            || UsesVertex && (_cacheBucket != (_baseline.Vertex?.CacheStaging?.Bucket ?? string.Empty)
                || _cachePrefix != (_baseline.Vertex?.CacheStaging?.ObjectPrefix ?? string.Empty))
            || (UsesAws ? NormalizeAws(Credential.Aws) != NormalizeAws(LlmCredential.Parse(_baseline.Credential)?.Aws)
                : UsesAzure ? _azure!.HasCredentialEdits : _credentialChange != SetupSecretEditKind.Keep);

    /// <summary>Subscription API connections require sign-in or a retained saved credential. A Claude subscription
    /// connection requires its own account token from the app's sign-in; the Claude CLI's own login does not count.</summary>
    protected override bool IsSignInSatisfied => _draft.Transport == LlmTransport.AnthropicClaudeCli ? HasClaudeAccountToken :
        _draft.Transport is not (LlmTransport.OpenAiCodex or LlmTransport.XaiResponses) || Status.Authenticated.Value == true
            || _credentialChange == SetupSecretEditKind.Keep && !string.IsNullOrEmpty(LlmCredential.Parse(_original?.Credential)?.Serialize());

    /// <summary>The draft holds a Claude account token: a new one from "Sign in to a Claude account", or the saved one it keeps.</summary>
    internal bool HasClaudeAccountToken => !string.IsNullOrWhiteSpace(Credential.OAuth?.AccessToken)
        || _credentialChange == SetupSecretEditKind.Keep && !string.IsNullOrWhiteSpace(LlmCredential.Parse(_original?.Credential)?.OAuth?.AccessToken);

    private static LlmAwsCredentials NormalizeAws(LlmAwsCredentials? value) => (value ?? new()) with
    { Profile = value?.Profile ?? string.Empty, Region = value?.Region ?? string.Empty };

    /// <inheritdoc />
    protected override IReadOnlyList<SetupStep> CreateSteps()
    {
        var fields = new List<SetupField>();
        SetupExecutableField? executable = UsesCli || UsesApple
            ? new(new("executable", UsesApple ? "Apple helper executable or absolute path" : "CLI executable or absolute path") { IsRequired = !UsesCli },
                new(_draft.Endpoint), new(_definition.DefaultEndpoint), value => Edit(() => _draft = _draft with { Endpoint = value.Value }))
            : null;
        if (UsesSelfHost)
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
        else if (executable is null && !UsesFixedEndpoint(_draft.Transport))
        {
            fields.Add(new SetupUriField(new("endpoint", "Endpoint") { IsRequired = true },
                Uri.TryCreate(_draft.Endpoint, UriKind.Absolute, out Uri? uri) ? uri : null,
                Uri.TryCreate(_definition.DefaultEndpoint, UriKind.Absolute, out Uri? defaultUri) ? defaultUri : null,
                value => Edit(() => _draft = _draft with { Endpoint = value?.ToString() ?? string.Empty })));
        }
        if (UsesAzure) AddAzureFields(fields);
        if (UsesApiKey && !UsesAzure)
        {
            fields.Add(new SetupSecretField(new("api-key", "API key")
            {
                IsRequired = _draft.Transport is not (LlmTransport.OpenAiCompatibleChat or LlmTransport.OpenAiResponses),
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
        if (_draft.Transport == LlmTransport.OpenAiResponses)
        {
            fields.Add(ServiceTiersField());
            fields.Add(ContextLengthsField());
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
            fields.Add(new SetupTextField(new("google-cache-bucket", "Cache staging bucket"), _cacheBucket,
                string.Empty, value => Edit(() => _cacheBucket = value)));
            fields.Add(new SetupTextField(new("google-cache-prefix", "Cache staging object prefix"), _cachePrefix,
                string.Empty, value => Edit(() => _cachePrefix = value)));
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
        SetupStep modelStep = _definition.SetupSteps.Count > 1 ? _definition.SetupSteps[1] : new("model", "Model", "Select a model.", []);
        if (UsesAzure)
        {
            fields.Add(new SetupTextField(new("azure-model-profile", "Underlying model profile"),
                _azure!.Profile, string.Empty,
                value => Edit(() => _azure.Profile = value)));
        }
        var connectionSections = new List<SetupSection>();
        if (_draft.Transport == LlmTransport.OpenAiCompatibleChat || UsesAzure)
        {
            connectionSections.Add(new("connection", "Connection", fields.AsReadOnly()));
            connectionSections.Add(AdvancedCapabilities());
        }
        else if (fields.Count > 0) connectionSections.Add(new("connection", "Connection", fields.AsReadOnly()));
        if (executable is not null) connectionSections.Add(new("more-options", "More options", [executable], IsAdvanced: true));
        return
        [
            connectionStep with
            {
                Sections = connectionSections.AsReadOnly(),
                ActionIds = SignInActionId is { } signIn ? [signIn, "check"] : ["check"]
            },
            modelStep with { ActionIds = ["test"], Sections = [new("model", "Model",
            [new SetupChoiceField(new("model", UsesAzure ? "Deployment" : "Model") { IsRequired = true,
                HelpText = UsesAzure ? "Enter the exact Azure deployment name, not its underlying public model ID." : "Discover available models where supported, or enter an exact model ID." }, _model,
                _definition.DefaultModelId, value => Edit(() => SetModel(value)), true,
                UsesApple ? SetupSideEffect.LocalVerification | SetupSideEffect.ExternalCli : UsesSelfHost || UsesAzure ? SetupSideEffect.None : SetupSideEffect.Network | (UsesCli ? SetupSideEffect.ExternalCli : SetupSideEffect.None),
                SetupChoiceCapabilities.Searchable)])] }
        ];
    }

    /// <summary>The Service tiers checkbox. A value equal to the endpoint's default stores null, so the default follows a
    /// later endpoint change (B13).</summary>
    private SetupBooleanField ServiceTiersField()
    {
        bool defaultValue = ResponsesLlmClientBase.IsOpenAiEndpoint(_draft);
        return new(new("service-tiers", "Service tiers")
        {
            HelpText = "Show the service-tier choice in the chat panel and send the chosen tier. OpenAI's own service offers tiers."
        }, _draft.CapabilityOverrides?.SupportsServiceTiers is { } set ? set == InferenceSupport.Supported : defaultValue, defaultValue,
            value => Edit(() => _draft = _draft with
            {
                CapabilityOverrides = (_draft.CapabilityOverrides ?? new()) with
                {
                    SupportsServiceTiers = value == ResponsesLlmClientBase.IsOpenAiEndpoint(_draft) ? null
                        : value ? InferenceSupport.Supported : InferenceSupport.Unsupported
                }
            }));
    }

    /// <summary>The Context lengths rows. Each edit rewrites the model overrides from the complete rows (B6).</summary>
    private SetupRepeatableGroupField ContextLengthsField() => new(new("context-lengths", "Context lengths")
    {
        HelpText = "A length set here is the model's limit on this connection. A model left out uses the listed, published or learned limit."
    },
        _contextRows.Select(row => new SetupFieldRow(row.Id,
        [
            new SetupChoiceField(new("context-model-" + row.Id, "Model"), row.ModelId ?? string.Empty, string.Empty,
                value => Edit(() => SetContextRow(row.Id, current => current with
                { ModelId = string.IsNullOrWhiteSpace(value) ? null : value.Trim() })),
                true, SetupSideEffect.Network, SetupChoiceCapabilities.Searchable),
            new SetupIntegerField(new("context-length-" + row.Id, "Context length (tokens)"), row.Length, null,
                value =>
                {
                    if (value is < 1)
                        return new(SetupEditState.Invalid, [new("context-length-invalid", "Enter a whole number of tokens of at least 1.", "context-length-" + row.Id)]);
                    return Edit(() => SetContextRow(row.Id, current => current with { Length = value }));
                }, 1)
        ])).ToArray(),
        () => Edit(() => _contextRows.Add(new(Guid.NewGuid().ToString("N"), null, null))),
        rowId => Edit(() =>
        {
            if (_contextRows.RemoveAll(row => row.Id == rowId) > 0) WriteContextLengths();
        }));

    private void SetContextRow(string rowId, Func<ContextLengthRow, ContextLengthRow> change)
    {
        int index = _contextRows.FindIndex(row => row.Id == rowId);
        if (index < 0) return;
        _contextRows[index] = change(_contextRows[index]);
        WriteContextLengths();
    }

    /// <summary>
    /// Rewrites the model overrides from the complete rows: a row's model gets its length; a model whose row is gone or
    /// incomplete loses its length, and an entry left with no value is removed (L11: the user removed that row).
    /// </summary>
    private void WriteContextLengths()
    {
        var overrides = new Dictionary<string, LlmCapabilityOverrides>(_draft.ModelCapabilityOverrides, StringComparer.Ordinal);
        var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (ContextLengthRow row in _contextRows)
        {
            if (row is { ModelId: { } model, Length: { } length }) lengths[model] = length;
        }
        foreach ((string model, LlmCapabilityOverrides value) in _draft.ModelCapabilityOverrides)
        {
            if (value.MaxContextTokens is null || lengths.ContainsKey(model)) continue;
            LlmCapabilityOverrides cleared = value with { MaxContextTokens = null };
            if (cleared == new LlmCapabilityOverrides()) overrides.Remove(model);
            else overrides[model] = cleared;
        }
        foreach ((string model, long length) in lengths)
        {
            overrides[model] = (overrides.GetValueOrDefault(model) ?? new()) with { MaxContextTokens = length };
        }
        _draft = _draft with { ModelCapabilityOverrides = overrides };
    }

    private static bool SameModelOverrides(IReadOnlyDictionary<string, LlmCapabilityOverrides> first,
        IReadOnlyDictionary<string, LlmCapabilityOverrides> second) =>
        first.Count == second.Count
        && first.All(pair => second.TryGetValue(pair.Key, out LlmCapabilityOverrides? other) && other == pair.Value);

    private SetupSection AdvancedCapabilities()
    {
        LlmCapabilityOverrides current = _draft.CapabilityOverrides ?? new();
        SetupChoiceField ReasoningOverride(string id, string label, bool? value,
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
        SetupChoiceField Override(string id, string label, InferenceSupport? value,
            Func<LlmCapabilityOverrides, InferenceSupport?, LlmCapabilityOverrides> update, string? helpText = null)
        {
            _overrideFields.Add(id);
            return new(new(id, label) { HelpText = helpText ?? "Default uses the adapter's evidence. Unknown permits an explicit attempt." },
                value switch { null => "default", InferenceSupport.Unknown => "unknown", InferenceSupport.Supported => "enabled", _ => "disabled" },
                "default", selection =>
                {
                    if (selection is not ("default" or "unknown" or "enabled" or "disabled"))
                        return new(SetupEditState.Invalid, [new("override-invalid", "Select default, unknown, enabled or disabled.", id)]);
                    InferenceSupport? support = selection switch
                    {
                        "default" => null,
                        "unknown" => InferenceSupport.Unknown,
                        "enabled" => InferenceSupport.Supported,
                        _ => InferenceSupport.Unsupported
                    };
                    return Edit(() => _draft = _draft with { CapabilityOverrides = update(_draft.CapabilityOverrides ?? new(), support) });
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
            .. Enum.GetValues<LlmReasoningLevel>().Select(level => ReasoningOverride(
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
        if (UsesAzure) _azure!.DeploymentEdited = true;
    }

    /// <inheritdoc />
    protected internal override SetupChoicePage? GetLocalChoices(string fieldId, SetupChoiceQuery query)
    {
        _ = CreateSteps(); // Registers the override field IDs for the current transport.
        if (UsesAzure && AzureEditorDraft.Choices(fieldId) is { } azureChoices) return Page(query, azureChoices);
        return fieldId == "google-authentication" && UsesVertex
            ? Page(query, [new(LlmGoogleAuthenticationMode.ApplicationDefault.ToString(), "Application default credentials"),
                new(LlmGoogleAuthenticationMode.ServiceAccount.ToString(), "Service-account key"),
                new(LlmGoogleAuthenticationMode.ApiKey.ToString(), "API key (express mode)")])
            : fieldId == "output-limit-field" && _draft.Transport is LlmTransport.OpenAiCompatibleChat or LlmTransport.AzureOpenAiChat
                ? Page(query, [new("default", "Default"), new("max_tokens", "max_tokens"), new("max_completion_tokens", "max_completion_tokens")])
            : fieldId == "prompt-cache-key" && _draft.Transport is LlmTransport.OpenAiCompatibleChat or LlmTransport.AzureOpenAiChat ? Page(query, OverrideChoices)
            : _overrideFields.Contains(fieldId) ? Page(query, fieldId.StartsWith("reasoning-level-", StringComparison.Ordinal) ? OverrideChoices : SupportChoices) : null;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<SetupActionDescriptor> CreateActions()
    {
        var actions = new List<SetupActionDescriptor>();
        if (_draft.Transport == LlmTransport.OpenAiCodex)
        {
            actions.Add(new("authorize", "Sign in with ChatGPT", SetupActionPurpose.Authenticate,
                SetupSideEffect.Network | SetupSideEffect.BrowserInteraction));
        }
        if (UsesXai)
        {
            actions.Add(new("authorize", "Sign in with Grok", SetupActionPurpose.Authenticate,
                SetupSideEffect.Network | SetupSideEffect.BrowserInteraction));
        }
        if (_draft.Transport == LlmTransport.AnthropicClaudeCli)
        {
            actions.Add(new("sign-in-claude", "Sign in to a Claude account", SetupActionPurpose.Authenticate,
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
            Interaction = new SetupDeviceAuthorization(new(DeviceCode.VerificationUrl), DeviceCode.UserCode, DeviceCode.ExpiresAt, ContinueAction, CancelAction);
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

    /// <summary>Keeps the device authorization open with the code to poll next.</summary>
    internal void ContinueDeviceAuthorization(LlmDeviceCode code)
    {
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

    /// <inheritdoc />
    protected override SetupValidation ValidateDraft()
    {
        List<SetupIssue> issues = [.. ConnectionValidation().Issues];
        if (string.IsNullOrWhiteSpace(_model)) { issues.Add(new("model-required", "Select or enter a model.", "model")); }
        return new(issues.AsReadOnly());
    }

    internal SetupValidation ConnectionValidation()
    {
        var issues = new List<SetupIssue>();
        if (UsesVertex)
        {
            try { _ = CacheStaging(); }
            catch (ArgumentException)
            { return new([new("google-cache-staging-invalid", "Enter a valid staging bucket, or clear both staging fields.", "google-cache-bucket")]); }
        }
        try { RuntimeInputAdmission.Validate(_draft); }
        catch (NotSupportedException error)
        { return new([new("runtime-inputs-unsupported", error.Message)]); }
        if (UsesCli)
        {
            if (_draft.Endpoint.IndexOfAny(['\r', '\n', '\0']) >= 0)
            { issues.Add(new("executable-invalid", "Enter an executable name or path, not a shell command.", "executable")); }
        }
        else if (_draft.Transport != LlmTransport.AwsBedrockAgentCore
            && (!Uri.TryCreate(_draft.Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https")
                || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0))
        { issues.Add(new("endpoint-invalid", "Use an HTTP(S) base URL without credentials, query or fragment.", "endpoint")); }
        if (UsesApiKey && !UsesAzure && _draft.Transport != LlmTransport.OpenAiCompatibleChat
            && _draft.Transport != LlmTransport.OpenAiResponses
            && string.IsNullOrWhiteSpace(Credential.ApiKey) && Credential.SecretHeaders.Count == 0)
        { issues.Add(new("key-required", "An API credential is required.", "api-key")); }
        if (_draft.Transport == LlmTransport.OpenAiResponses)
        {
            if (_contextRows.Any(row => row.ModelId is null || row.Length is null))
            { issues.Add(new("context-length-incomplete", "Enter a model and a context length, or remove the row.", "context-lengths")); }
            if (_contextRows.Where(row => row.ModelId is not null).GroupBy(row => row.ModelId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            { issues.Add(new("context-length-duplicate", "Each model takes one context length.", "context-lengths")); }
        }
        if (UsesAzure)
        {
            SetupIssue[] azureIssues = _azure!.Validate(_model);
            issues.AddRange(azureIssues);
            if (azureIssues.Length > 0) return new(issues.AsReadOnly());
        }
        if (UsesAws || UsesVertex || UsesAzure)
        {
            if (_validateEnvironment is null) { issues.Add(new("setup-environment-missing", "Register the provider's setup support.")); }
            else { issues.AddRange(_validateEnvironment(CurrentConnection()).Issues); }
        }
        return new(issues.AsReadOnly());
    }

    /// <inheritdoc />
    protected override void OnDraftEdited()
    {
        if (_editingCallback) { _editingCallback = false; return; }
        base.OnDraftEdited();
        ClearAuthorization();
    }

    /// <summary>The connection these settings describe, as their own sign-in and tests use it.</summary>
    internal LlmConnection CurrentConnection()
    {
        LlmConnection current = _draft with
        {
            Version = DraftVersion,
            Endpoint = UsesCli && string.IsNullOrWhiteSpace(_draft.Endpoint) ? _definition.DefaultEndpoint : _draft.Endpoint,
            Vertex = UsesVertex ? Vertex with { CacheStaging = CacheStaging() } : _draft.Vertex
        };
        return !UsesAzure ? current : current with
        { Azure = _azure!.Configuration(_model), Credential = _azure.ComposeCredential().Serialize() };
    }

    private LlmVertexCacheStaging? CacheStaging()
    {
        if (_cacheBucket.Length != 0) return new(_cacheBucket, _cachePrefix);
        if (_cachePrefix.Length != 0) throw new ArgumentException("Staging prefixes require a bucket.");
        return null;
    }

    /// <inheritdoc />
    protected internal override LlmSetupChanges CreateChanges()
    {
        LlmConnection draft = CurrentConnection();
        return new(Identity,
        new(draft with { Credential = null, Version = Identity.ExpectedVersion ?? string.Empty }, _model),
        UsesAzure ? _azure!.CredentialChange() : _credentialChange == SetupSecretEditKind.Keep ? SetupSecretEdit.Keep
            : _draft.Credential is null ? SetupSecretEdit.Clear : SetupSecretEdit.Replace(_draft.Credential),
        _original is not null && (_original.Endpoint != _draft.Endpoint || _original.Vertex != draft.Vertex || _original.Azure != draft.Azure)
            || UsesAzure && _azure!.HasCredentialEdits,
        _original is null || _original.Endpoint != _draft.Endpoint || _original.Vertex != draft.Vertex || _original.Azure != draft.Azure || _original.Xai != _draft.Xai || _originalModel != _model
            || _original.ChatCompletions != _draft.ChatCompletions || _original.CapabilityOverrides != _draft.CapabilityOverrides
            || !SameModelOverrides(_original.ModelCapabilityOverrides, _draft.ModelCapabilityOverrides)
            || (UsesAzure ? _azure!.HasCredentialEdits : _credentialChange != SetupSecretEditKind.Keep));
    }
}

/// <summary>
/// Opens, acts on and saves LLM connection settings. Sign-in, checks, tests and model lists run against
/// the settings' current values through a provider service that reads its connection from the settings.
/// </summary>
public sealed class LlmSettingsManager : InferenceSettingsManager<LlmSettings, LlmSetupChanges>, IDisposable, IAsyncDisposable
{
    private IReadOnlyDictionary<LlmTransport, ILlmClient>? _clients;
    private IReadOnlyList<ILlmAuthenticator>? _authenticators;
    private IReadOnlyDictionary<LlmTransport, ILlmSetupEnvironment>? _environments;
    private LlmAgentProviderService? _agents;
    private readonly LlmOperationLifetime _lifetime = new();

    internal LlmSettingsManager(IReadOnlyDictionary<LlmTransport, ILlmClient> clients, IEnumerable<ILlmAuthenticator> authenticators,
        IReadOnlyDictionary<LlmTransport, ILlmSetupEnvironment> environments, ISetupCommitTarget<LlmSetupChanges> target,
        LlmAgentProviderService? agents = null)
        : base(target)
    {
        _clients = clients;
        _authenticators = [.. authenticators];
        _environments = environments;
        _agents = agents;
    }

    /// <summary>Returns settings for <paramref name="transport"/>, blank or from the saved connection.</summary>
    public Task<LlmSettings> OpenAsync(LlmTransport transport, SetupConfigurationIdentity identity,
        LlmConnection? existing = null, string? modelId = null, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        ILlmClient client = Client(transport);
        LlmProviderDefinition definition = client.ProviderDefinition;
        Fence(operation.Token);
        LlmTransport actualTransport = client.Transport;
        Fence(operation.Token);
        var settings = new LlmSettings(definition, actualTransport, identity, existing, modelId,
            _environments!.GetValueOrDefault(transport));
        Fence(operation.Token);
        return Task.FromResult(settings);
    }

    /// <inheritdoc />
    public override async Task<SetupActionResult> RunAsync(InferenceSettings settings, string actionId,
        IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        SetupActionResult result = await base.RunAsync(settings, actionId, progress, operation.Token).ConfigureAwait(false);
        return IsStopping(operation.Token) ? new(SetupActionState.Cancelled) : result;
    }

    /// <inheritdoc />
    public override async Task<SetupChoicePage> QueryChoicesAsync(InferenceSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        SetupChoicePage result = await base.QueryChoicesAsync(settings, fieldId, query, operation.Token).ConfigureAwait(false);
        return IsStopping(operation.Token) ? new([], State: SetupChoiceState.Cancelled) : result;
    }

    /// <inheritdoc />
    public override async Task<SetupCommitResult> SaveAsync(InferenceSettings settings, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        return await base.SaveAsync(settings, operation.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task<SetupActionResult> RunCoreAsync(LlmSettings settings, SetupActionDescriptor action,
        IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        Fence(cancellationToken);
        RuntimeInputAdmission.Validate(settings.CurrentConnection());
        await using LlmProviderService service = Service(settings, cancellationToken);
        Fence(cancellationToken);
        switch (action.Id)
        {
            case "authorize":
                LlmDeviceCode code = await service.StartDeviceLoginAsync(settings.Transport, cancellationToken).ConfigureAwait(false);
                Fence(cancellationToken);
                settings.BeginDeviceAuthorization(code);
                return new(SetupActionState.NeedsInteraction, Interaction: settings.Interaction);
            case "sign-in-claude":
                LlmAuthorizationSession authorization = await service.StartClaudeLoginAsync(cancellationToken).ConfigureAwait(false);
                Fence(cancellationToken);
                settings.BeginBrowserAuthorization(authorization);
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

    /// <inheritdoc />
    protected override async Task<SetupChoicePage> QueryProviderChoicesAsync(LlmSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken)
    {
        Fence(cancellationToken);
        RuntimeInputAdmission.Validate(settings.CurrentConnection());
        ILlmSetupEnvironment? environment = _environments!.GetValueOrDefault(settings.Transport);
        switch (fieldId)
        {
            case "aws-profile" when environment is not null:
                var profiles = await environment.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
                Fence(cancellationToken);
                return InferenceSettings.Page(query, profiles);
            case "aws-region" when environment is not null:
                var regions = await environment.GetRegionsAsync(settings.AwsProfile, cancellationToken).ConfigureAwait(false);
                Fence(cancellationToken);
                return InferenceSettings.Page(query, regions);
            case var modelField when modelField == "model" || modelField.StartsWith("context-model-", StringComparison.Ordinal):
                {
                    // A Claude connection lists its account's models with its own token, never through the CLI's login.
                    if (settings.Transport == LlmTransport.AnthropicClaudeCli && !settings.HasClaudeAccountToken)
                    {
                        return new([], State: SetupChoiceState.Unavailable,
                            Issues: [new("claude-sign-in-required", "Sign in to a Claude account in the previous step to list its models.", "model")]);
                    }
                    await using LlmProviderService service = Service(settings, cancellationToken);
                    Fence(cancellationToken);
                    LlmProviderCapabilities capabilities = await service.GetCapabilitiesAsync(settings.Identity.Id, cancellationToken).ConfigureAwait(false);
                    Fence(cancellationToken);
                    if ((capabilities.SupportsModelListing == InferenceSupport.Unsupported))
                    {
                        return new([], State: SetupChoiceState.Unavailable,
                            Issues: [new("manual-model", "This connection requires manual model entry.", "model", Severity: SetupIssueSeverity.Warning)]);
                    }
                    IReadOnlyList<LlmModel> models = await service.ListModelsAsync(settings.Identity.Id, cancellationToken).ConfigureAwait(false);
                    Fence(cancellationToken);
                    if (fieldId == "model")
                    {
                        settings.ModelNames = models.DistinctBy(model => model.Id, StringComparer.Ordinal)
                            .ToDictionary(model => model.Id, model => model.DisplayName, StringComparer.Ordinal);
                    }
                    return InferenceSettings.Page(query, models.Select(model => new SetupChoice(model.Id, model.DisplayName, model.AvailabilityReason ?? model.OwnedBy)
                    { Tags = ModelTags(model) })
                        .OrderBy(choice => choice.Value, StringComparer.Ordinal));
                }
            default:
                return new([], State: SetupChoiceState.Unavailable);
        }
    }

    /// <summary>The model's chips: its context size, and Images and Thinking when its capabilities support them.</summary>
    private static string[] ModelTags(LlmModel model)
    {
        if (model.Capabilities is not { } capabilities) return [];
        var tags = new List<string>();
        if (model.MaxContextTokens is { } context) tags.Add(string.Create(CultureInfo.InvariantCulture, $"{context / 1000}K context"));
        if (capabilities.SupportsPromptImages == InferenceSupport.Supported) tags.Add("Images");
        if (capabilities.SupportsReasoning == InferenceSupport.Supported) tags.Add("Thinking");
        return [.. tags];
    }

    private async Task<SetupActionResult> ContinueAuthorizationAsync(LlmSettings settings, LlmProviderService service,
        IProgress<SetupProgress>? progress, CancellationToken token)
    {
        if (settings.DeviceCode is { } code)
        {
            progress?.Report(new("authorizing", "Waiting for device authorization."));
            Fence(token);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, code.IntervalSeconds)), token).ConfigureAwait(false);
                Fence(token);
                await service.CompleteDeviceLoginAsync(settings.Transport, settings.Identity.Id, code.DeviceAuthId,
                    code.UserCode, LlmSettings.DraftVersion, token).ConfigureAwait(false);
            }
            catch (LlmAuthPendingException pending)
            {
                Fence(token);
                settings.ContinueDeviceAuthorization(code.AfterPending(pending));
                return new(SetupActionState.NeedsInteraction, Interaction: settings.Interaction);
            }
        }
        else if (settings.BrowserAuthorization is { } authorization)
        {
            await service.CompleteClaudeLoginAsync(settings.Identity.Id, authorization, settings.AuthorizationCallback,
                LlmSettings.DraftVersion, token).ConfigureAwait(false);
        }
        else { return new(SetupActionState.Unavailable); }
        Fence(token);
        settings.ClearAuthorization();
        if (settings.Transport is LlmTransport.OpenAiCodex or LlmTransport.AnthropicClaudeCli or LlmTransport.XaiResponses)
        {
            settings.Status = settings.Status with { Authenticated = SetupObservation.Known(true) };
        }
        return SetupActionResult.Completed;
    }

    private async Task<SetupActionResult> CheckAsync(LlmSettings settings, LlmProviderService service, CancellationToken token)
    {
        Fence(token);
        settings.Status = settings.Status with { Authenticated = SetupObservation.Unknown, Verified = SetupObservation.Unknown };
        if (settings.UsesCli) return await CheckCliAsync(settings, service, token).ConfigureAwait(false);
        LlmProviderCapabilities capabilities = await service.GetCapabilitiesAsync(settings.Identity.Id, token).ConfigureAwait(false);
        Fence(token);
        if ((capabilities.SupportsModelListing == InferenceSupport.Unsupported))
        {
            return new(SetupActionState.Unavailable, [new("explicit-test-required", "This provider requires an explicit inference test to confirm remote access.")]);
        }
        await service.ListModelsAsync(settings.Identity.Id, token).ConfigureAwait(false);
        Fence(token);
        settings.Status = settings.Status with
        {
            Authenticated = SetupObservation.Known(true),
            Verified = settings.Transport is LlmTransport.GoogleVertexAi or LlmTransport.AzureOpenAiResponses or LlmTransport.AzureOpenAiChat
                ? SetupObservation.Unknown : SetupObservation.Known(true)
        };
        settings.CliPrerequisite = false;
        settings.RefreshInteraction();
        return SetupActionResult.Completed;
    }

    /// <summary>
    /// Checks a CLI provider's tool and sign-in. A missing tool, or a signed-out tool with no sign-in action of its own,
    /// shows the provider's prerequisite; a signed-out tool with a sign-in action completes so its sign-in is offered.
    /// </summary>
    private async Task<SetupActionResult> CheckCliAsync(LlmSettings settings, LlmProviderService service, CancellationToken token)
    {
        settings.Status = settings.Status with { Installed = SetupObservation.Unknown };
        ExternalAuthenticationState state = await service.CheckAuthenticationAsync(settings.Identity.Id, token).ConfigureAwait(false);
        Fence(token);
        bool installed = state != ExternalAuthenticationState.NotInstalled;
        // A Claude connection is signed in only through its own account token; the CLI's own login is not used.
        bool signedIn = state == ExternalAuthenticationState.SignedIn
            && (settings.Transport != LlmTransport.AnthropicClaudeCli || settings.HasClaudeAccountToken);
        settings.Status = settings.Status with
        {
            Installed = SetupObservation.Known(installed),
            Authenticated = SetupObservation.Known(signedIn),
            Verified = SetupObservation.Known(signedIn)
        };
        settings.CliPrerequisite = !installed || !signedIn && !settings.HasSignInAction;
        settings.RefreshInteraction();
        return settings.CliPrerequisite ? new(SetupActionState.NeedsInteraction, Interaction: settings.Interaction) : SetupActionResult.Completed;
    }

    private async Task<SetupActionResult> TestAsync(LlmSettings settings, LlmProviderService service, CancellationToken token)
    {
        Fence(token);
        settings.Status = settings.Status with { Ready = SetupObservation.Unknown };
        bool completed;
        var reply = new System.Text.StringBuilder();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (settings.Transport == LlmTransport.AwsBedrockAgentCore && Client(settings.Transport) is ILlmAgentClient agent)
        {
            await using ILlmAgentSession session = await agent.StartSessionAsync(settings.Identity.Id, settings.CurrentConnection(), new()
            {
                ModelId = settings.ModelId,
                Runtime = new() { WorkingDirectory = string.Empty, Policy = new LlmAgentHostRuntimePolicy { Sandbox = LlmAgentSandboxMode.ReadOnly } },
                ToolHandler = (_, _) => throw new NotSupportedException("Setup tests grant no host tools.")
            }, token).ConfigureAwait(false);
            Fence(token);
            completed = false;
            await foreach (LlmAgentEvent item in session.RunTurnAsync(new()
            { Input = [new LlmAgentTextContent("Reply with OK. Do not use tools.")] }, token).ConfigureAwait(false))
            {
                Fence(token);
                if (item is LlmAgentTextDeltaEvent delta) { reply.Append(delta.Delta); }
                if (item is LlmAgentTurnCompletedEvent done) { completed = done.Status == LlmAgentTurnStatus.Completed; }
            }
        }
        else
        {
            LlmChatResult result = await service.CompleteAsync(settings.Identity.Id, new()
            { ModelId = settings.ModelId, Messages = [new LlmChatMessage("user", "Reply with OK.")] }, token).ConfigureAwait(false);
            Fence(token);
            completed = result.Status == LlmCompletionStatus.Completed
                || settings.UsesApple && result.Status == LlmCompletionStatus.Unknown && !string.IsNullOrWhiteSpace(result.Text);
            reply.Append(result.Text);
        }
        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Fence(token);
        settings.Status = settings.Status with { Ready = SetupObservation.Known(completed), Verified = SetupObservation.Known(completed) };
        if (!completed) return new(SetupActionState.Failed, [new("test-incomplete", "The provider did not complete the test.")]) { Elapsed = elapsed };
        string text = reply.ToString().Trim();
        string name = settings.ModelNames.GetValueOrDefault(settings.ModelId) ?? settings.ModelId;
        return new(SetupActionState.Completed)
        {
            Message = $"{name} answered \"{(text.Length > 120 ? text[..120] : text)}\"",
            Elapsed = elapsed
        };
    }

    private ILlmClient Client(LlmTransport transport) => _clients!.TryGetValue(transport, out ILlmClient? client)
        ? client
        : throw new NotSupportedException($"No LLM client is registered for transport {transport}.");

    /// <summary>A provider service whose one connection is these settings' current connection.</summary>
    private LlmProviderService Service(LlmSettings settings, CancellationToken token)
    {
        Fence(token);
        return new(new SettingsSource(settings, this), [Client(settings.Transport)], _authenticators!,
            () => Fence(token));
    }

    private sealed class SettingsSource(LlmSettings settings, LlmSettingsManager owner) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            owner.Fence(cancellationToken);
            return Task.FromResult(connectionId == settings.Identity.Id ? settings.CurrentConnection() : null);
        }

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            owner.Fence(cancellationToken);
            if (connectionId != settings.Identity.Id || expectedVersion != LlmSettings.DraftVersion)
            {
                throw new InvalidOperationException("The sign-in does not belong to these settings.");
            }
            settings.AcceptCredential(credentialJson);
            return Task.CompletedTask;
        }
    }

    private void Fence(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("The LLM settings owner is retiring.", error, token); }
    }

    private bool IsStopping(CancellationToken token)
    {
        if (token.IsCancellationRequested) return true;
        try { _lifetime.ThrowIfStopping(); return false; }
        catch (ObjectDisposedException) { return true; }
    }

    /// <summary>Retires the manager and blocks until in-flight operations finish.</summary>
    public void Dispose() => Retire().GetAwaiter().GetResult();
    /// <summary>Retires the manager once in-flight operations finish.</summary>
    public ValueTask DisposeAsync() => new(Retire());
    private Task Retire() => _lifetime.RetireAsync(() =>
    {
        _clients = null; _authenticators = null; _environments = null;
        Interlocked.Exchange(ref _agents, null);
        DetachCommitTarget();
        return Task.CompletedTask;
    });
}
