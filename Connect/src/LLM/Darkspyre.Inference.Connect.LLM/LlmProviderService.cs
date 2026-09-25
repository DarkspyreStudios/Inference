using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Resolves one configured connection and dispatches to exactly one matching transport. It never
/// retries against, or falls back to, another provider.
/// </summary>
public sealed class LlmProviderService
{
    private readonly ILlmConnectionSource _connections;
    private readonly IReadOnlyDictionary<LlmTransport, ILlmClient> _clients;
    private readonly Dictionary<LlmTransport, ILlmAccountUsageClient> _accountUsageClients;
    private readonly Dictionary<LlmTransport, ILlmAuthenticator> _authenticators;
    private readonly IReadOnlyDictionary<LlmTransport, ILlmSetupEnvironment> _setupEnvironments;
    private readonly LlmModelListCache _modelLists;

    public LlmProviderService(
        ILlmConnectionSource connections,
        IEnumerable<ILlmClient> clients)
        : this(connections, clients, [])
    {
    }

    public LlmProviderService(
        ILlmConnectionSource connections,
        IEnumerable<ILlmClient> clients,
        IEnumerable<ILlmAuthenticator> authenticators,
        IEnumerable<ILlmSetupEnvironment>? setupEnvironments = null,
        LlmModelListCache? modelLists = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clients);

        _connections = connections;
        // A service built without the application's shared cache keeps its own lists.
        _modelLists = modelLists ?? new LlmModelListCache();
        ILlmClient[] clientArray = [.. clients];
        IGrouping<LlmTransport, ILlmClient>? duplicate = clientArray
            .GroupBy(client => client.Transport)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Multiple LLM clients are registered for transport {duplicate.Key}.");
        }
        ILlmClient? inconsistent = clientArray.FirstOrDefault(
            client => client.ProviderDefinition.Transport != client.Transport);
        if (inconsistent is not null)
        {
            throw new InvalidOperationException(
                $"Provider definition '{inconsistent.ProviderDefinition.Id}' does not match transport {inconsistent.Transport}.");
        }
        IGrouping<string, ILlmClient>? duplicateProvider = clientArray
            .GroupBy(client => client.ProviderDefinition.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicateProvider is not null)
        {
            throw new InvalidOperationException(
                $"Multiple LLM clients are registered for provider '{duplicateProvider.Key}'.");
        }

        _clients = clientArray.ToDictionary(client => client.Transport);
        _accountUsageClients = clientArray
            .OfType<ILlmAccountUsageClient>()
            .ToDictionary(client => client.Transport);
        _authenticators = authenticators.ToDictionary(authenticator => authenticator.Transport);
        _setupEnvironments = (setupEnvironments ?? []).ToDictionary(environment => environment.Transport);
        SupportedTransports = [.. _clients.Keys.OrderBy(transport => transport)];
        SupportedProviders =
        [
            .. clientArray
                .Select(client => client.ProviderDefinition)
                .OrderBy(provider => provider.DisplayOrder)
                .ThenBy(provider => provider.DisplayName, StringComparer.Ordinal)
        ];
    }

    public IReadOnlyList<LlmTransport> SupportedTransports { get; }

    /// <summary>Canonical provider metadata and guided setup supplied by registered adapters.</summary>
    public IReadOnlyList<LlmProviderDefinition> SupportedProviders { get; }

    public IReadOnlyList<Common.Setup.InferenceSetupDefinition> SetupDefinitions =>
        SupportedProviders.Select(provider => provider.SetupDefinition).ToArray();

    public LlmSetupSession CreateSetupSession(LlmTransport transport,
        Common.Setup.SetupConfigurationIdentity identity,
        Common.Setup.ISetupCommitTarget<LlmSetupChanges> target,
        LlmConnection? existing = null, string? modelId = null) =>
        new(ClientFor(transport), _authenticators.Values, identity, target, existing, modelId,
            _setupEnvironments.GetValueOrDefault(transport));

    public const string ClaudeManualRedirectUri =
        Providers.ClaudeAuthenticator.ManualRedirectUri;

    /// <summary>
    /// Binds the portable inference contract to one explicitly selected Connect connection. The
    /// returned adapter does not retain or expose provider response state or diagnostics.
    /// </summary>
    public ILlmInference Bind(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return new ConnectLlmInference(this, connectionId);
    }

    public async Task<bool> IsAuthenticatedAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        LlmConnection? connection = await _connections.GetConnectionAsync(connectionId, cancellationToken);
        if (connection is not null
            && _clients.TryGetValue(connection.Transport, out ILlmClient? client)
            && client is IExternalLlmAuthenticationProbe externalProbe)
        {
            return await externalProbe.IsAuthenticatedAsync(connection, cancellationToken);
        }

        LlmCredential? credential = LlmCredential.Parse(connection?.Credential);
        return credential is not null
            && (!string.IsNullOrWhiteSpace(credential.ApiKey)
                || !string.IsNullOrWhiteSpace(credential.OAuth?.AccessToken)
                || credential.Aws is not null
                || credential.SecretHeaders.Count > 0);
    }

    public async Task SetApiKeyAsync(
        string connectionId,
        string apiKey,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(apiKey));
        }

        LlmConnection connection = await RequireExpectedVersionAsync(
            connectionId,
            expectedVersion,
            cancellationToken);
        await _connections.SaveCredentialAsync(
            connectionId,
            new LlmCredential { ApiKey = apiKey }.Serialize(),
            connection.Version,
            cancellationToken);
    }

    public Task<LlmDeviceCode> StartCodexLoginAsync(
        CancellationToken cancellationToken = default) =>
        CodexLogin().StartAsync(cancellationToken);

    public async Task CompleteCodexLoginAsync(
        string connectionId,
        string deviceAuthId,
        string userCode,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        LlmConnection connection = await RequireExpectedVersionAsync(
            connectionId,
            expectedVersion,
            cancellationToken);
        LlmCredential credential = await CodexLogin().CompleteAsync(
            deviceAuthId,
            userCode,
            cancellationToken);
        await _connections.SaveCredentialAsync(
            connectionId,
            credential.Serialize(),
            connection.Version,
            cancellationToken);
    }

    /// <summary>Starts the optional OAuth flow used only for Claude subscription model discovery.</summary>
    public LlmAuthorizationSession StartClaudeLogin() =>
        ClaudeLogin().Start(ClaudeManualRedirectUri);

    /// <summary>Starts the optional OAuth flow used only for Claude subscription model discovery.</summary>
    public LlmAuthorizationSession StartClaudeLogin(string redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            throw new ArgumentException("A Claude redirect URI is required.", nameof(redirectUri));
        }

        return ClaudeLogin().Start(redirectUri);
    }

    public async Task CompleteClaudeLoginAsync(
        string connectionId,
        LlmAuthorizationSession session,
        string pastedCode,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        LlmRedirectResult redirect = LlmRedirectResult.ParsePastedCode(pastedCode);
        await CompleteClaudeLoginAsync(
            connectionId,
            session,
            redirect.Code,
            redirect.State,
            expectedVersion,
            cancellationToken);
    }

    public async Task CompleteClaudeLoginAsync(
        string connectionId,
        LlmAuthorizationSession session,
        string code,
        string state,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        LlmConnection connection = await RequireExpectedVersionAsync(
            connectionId,
            expectedVersion,
            cancellationToken);
        if (connection.Transport != LlmTransport.AnthropicClaudeCli)
        {
            throw new InvalidOperationException(
                "Claude catalog OAuth can be attached only to a Claude subscription CLI connection.");
        }
        LlmCredential credential = await ClaudeLogin().CompleteAsync(
            session,
            code,
            state,
            cancellationToken);
        await _connections.SaveCredentialAsync(
            connectionId,
            credential.Serialize(),
            connection.Version,
            cancellationToken);
    }

    public async Task SetBedrockCredentialsAsync(
        string connectionId,
        LlmAwsCredentials aws,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aws);
        LlmConnection connection = await RequireExpectedVersionAsync(
            connectionId,
            expectedVersion,
            cancellationToken);
        await _connections.SaveCredentialAsync(
            connectionId,
            new LlmCredential { Aws = aws }.Serialize(),
            connection.Version,
            cancellationToken);
    }

    /// <summary>
    /// Runs the interactive sign-in flow for a Codex or Claude connection. The connection version is
    /// captured before authentication starts, so a delayed result cannot overwrite a connection that
    /// the host changed or replaced while the user was signing in.
    /// </summary>
    public async Task SignInAsync(
        string connectionId,
        ILlmAuthPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            _connections,
            connectionId,
            cancellationToken);
        switch (connection.Transport)
        {
            case LlmTransport.OpenAiCodex:
                LlmDeviceCode code = await CodexLogin().StartAsync(cancellationToken);
                await prompt.ShowDeviceCodeAsync(code, cancellationToken);
                LlmCredential codexCredential = await PollCodexAsync(code, cancellationToken);
                await _connections.SaveCredentialAsync(
                    connectionId,
                    codexCredential.Serialize(),
                    connection.Version,
                    cancellationToken);
                break;

            case LlmTransport.AnthropicClaudeCli:
                if (_clients.TryGetValue(connection.Transport, out ILlmClient? claudeClient)
                    && claudeClient is IExternalLlmAuthenticationProbe claudeProbe)
                {
                    if (await claudeProbe.IsAuthenticatedAsync(connection, cancellationToken))
                    {
                        break;
                    }

                    throw new InvalidOperationException(
                        "Claude CLI is not authenticated. Run 'claude' in a terminal, complete sign-in, then retry.");
                }

                throw new InvalidOperationException(
                    "Claude CLI authentication status is unavailable. Run 'claude' in a terminal and complete sign-in.");

            default:
                throw new NotSupportedException(
                    $"{connection.Transport} does not use interactive sign-in; use SetApiKeyAsync.");
        }
    }

    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        string connectionId,
        LlmChatRequest request,
        CancellationToken cancellationToken = default) =>
        StreamCoreAsync(connectionId, request, expectedScope: null, cancellationToken);

    internal IAsyncEnumerable<LlmStreamEvent> StreamAgentAsync(
        string connectionId, LlmChatRequest request, string expectedScope, CancellationToken cancellationToken) =>
        StreamCoreAsync(connectionId, request, expectedScope, cancellationToken);

    private async IAsyncEnumerable<LlmStreamEvent> StreamCoreAsync(
        string connectionId, LlmChatRequest request, string? expectedScope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            _connections,
            connectionId,
            cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        if (client is not IStreamingCredentialRefreshPolicy refreshPolicy
            || refreshPolicy.RequiresFreshOAuthCredentialForStreaming(connection))
        {
            connection = await RefreshConnectionAsync(
                connectionId,
                connection,
                cancellationToken);
        }
        if (expectedScope is not null && ProviderResponseStateCodec.Scope(connection) != expectedScope)
        {
            throw new LlmProviderException("The agent connection/account changed. Open a new session explicitly.", connection.Transport);
        }
        if (expectedScope is not null)
        {
            var capabilities = client.GetCapabilities(connection, request.ModelId);
            if (!capabilities.SupportsAgentSessions || (request.Tools.Count > 0 && !capabilities.SupportsAgentToolUse))
            {
                throw new LlmProviderException("The connection now disables the requested agent operation.", connection.Transport);
            }
        }
        (request, int omitted) = ResponseStateProjection.Project(request, connection);
        await foreach (LlmStreamEvent streamEvent in client.StreamAsync(
                           connection,
                           request,
                           cancellationToken))
        {
            yield return streamEvent is LlmCompletionEvent completion
                ? new LlmCompletionEvent(SanitizeDiagnostics(completion.Result, connection) with
                {
                    OmittedResponseStateCount = omitted
                })
                : streamEvent;
        }
    }

    /// <summary>
    /// Accounts a standalone request without generation, credential refresh, persistence or a session.
    /// May read model discovery metadata; unavailable limits remain unknown. The host source is read only.
    /// </summary>
    public async Task<LlmRequestAccounting> MeasureRequestAsync(
        string connectionId,
        LlmChatRequest request,
        LlmAgentContextPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        if (request.MaxOutputTokens is <= 0) { throw new ArgumentOutOfRangeException(nameof(request)); }
        policy ??= new();
        ManagedAgentPolicy.Validate(new() { ContextPolicy = policy }, null);
        var connection = await LlmConnectionResolver.ResolveAsync(_connections, connectionId, cancellationToken);
        var client = ClientFor(connection.Transport);
        // Measure what would be sent: the same projection as the request path.
        request = ResponseStateProjection.Project(request, connection).Request;
        var capabilities = client.GetCapabilities(connection, request.ModelId);
        LlmRequestAccounting Unsupported(string diagnostic) => new()
        {
            ModelId = request.ModelId,
            Diagnostic = diagnostic,
            OutputLimitEnforcement = capabilities.OutputLimitEnforcement
        };
        if (request.MaxOutputTokens is not null
            && capabilities.OutputLimitEnforcement == LlmOutputLimitEnforcement.Unsupported)
        {
            return Unsupported("This route cannot enforce MaxOutputTokens; a context reserve is not an output cap.");
        }
        if (client is not ILlmContextEstimator estimator)
        {
            return Unsupported("This route does not implement nonmutating request accounting.");
        }
        ContextEstimate? used;
        try { used = estimator.EstimateInputTokens(connection, request); }
        catch (NotSupportedException)
        {
            return Unsupported("This route cannot project the requested features for accounting.");
        }
        if (request.Messages.Any(message => message.OmittedAttachments.Count > 0)) { used = null; }
        LlmModel? model = null;
        string? metadataDiagnostic = null;
        if (capabilities.SupportsModelListing)
        {
            try
            {
                model = (await _modelLists.GetAsync(connectionId, connection.Version,
                        () => client.ListModelsAsync(connection, CancellationToken.None), cancellationToken))
                    .FirstOrDefault(candidate => candidate.Id == request.ModelId);
            }
            catch (Exception error) when (error is HttpRequestException or LlmProviderException or NotSupportedException)
            {
                metadataDiagnostic = "Model metadata is unavailable; provider limits remain unknown.";
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        // An explicit enforced cap budgets at least that much output, even if a smaller reserve was supplied.
        if (request.MaxOutputTokens is int cap
            && capabilities.OutputLimitEnforcement is LlmOutputLimitEnforcement.ProviderEnforced or LlmOutputLimitEnforcement.EngineEnforced)
        {
            if (model?.MaxOutputTokens is long maximum && cap > maximum)
            {
                throw new ArgumentException("Requested output cap exceeds the known model output limit.", nameof(request));
            }
            policy = policy with { ReservedOutputTokens = Math.Max(policy.ReservedOutputTokens ?? 0, cap) };
        }
        var accounting = ManagedAgentPolicy.Account(request.ModelId, used, policy, model);
        string[] diagnostics = [.. new[] { accounting.Diagnostic, used is null ? null : estimator.EstimateDiagnostic, metadataDiagnostic }
            .OfType<string>()];
        return accounting with
        {
            Diagnostic = diagnostics.Length == 0 ? null : string.Join(" ", diagnostics),
            OutputLimitEnforcement = capabilities.OutputLimitEnforcement
        };
    }

    public async Task<LlmChatResult> CompleteAsync(
        string connectionId,
        LlmChatRequest request,
        CancellationToken cancellationToken = default)
    {
        LlmChatResult? result = null;
        await foreach (LlmStreamEvent streamEvent in StreamAsync(
                           connectionId,
                           request,
                           cancellationToken))
        {
            if (streamEvent is not LlmCompletionEvent completion)
            {
                continue;
            }

            if (result is not null)
            {
                throw new InvalidOperationException("The LLM transport emitted more than one completion event.");
            }

            result = completion.Result;
        }

        return result ?? throw new InvalidOperationException(
            "The LLM transport ended without emitting a completion event.");
    }

    /// <summary>
    /// The connection's models. A list is kept until the connection's version changes or
    /// <see cref="RefreshModelsAsync"/> replaces it, so repeated calls do not ask the provider again.
    /// </summary>
    public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        LlmConnection connection = await ResolveFreshConnectionAsync(connectionId, cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        return await _modelLists.GetAsync(connectionId, connection.Version,
            () => client.ListModelsAsync(connection, CancellationToken.None), cancellationToken);
    }

    /// <summary>Asks the provider for the connection's models again and keeps the new list.</summary>
    public async Task<IReadOnlyList<LlmModel>> RefreshModelsAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        LlmConnection connection = await ResolveFreshConnectionAsync(connectionId, cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        return await _modelLists.RefreshAsync(connectionId, connection.Version,
            () => client.ListModelsAsync(connection, CancellationToken.None), cancellationToken);
    }

    public async Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            _connections,
            connectionId,
            cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        if (!client.GetCapabilities(connection).SupportsAccountUsage
            || !_accountUsageClients.TryGetValue(
                connection.Transport,
                out ILlmAccountUsageClient? usageClient))
        {
            throw new NotSupportedException(
                $"The {connection.Transport} connection does not expose account usage.");
        }

        if (connection.Transport == LlmTransport.OpenAiCodex)
        {
            connection = await RefreshConnectionAsync(connectionId, connection, cancellationToken);
        }
        return await usageClient.GetAccountUsageAsync(connection, cancellationToken);
    }

    public async Task<LlmProviderCapabilities> GetCapabilitiesAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        LlmConnection connection = await ResolveFreshConnectionAsync(connectionId, cancellationToken);
        return ClientFor(connection.Transport).GetCapabilities(connection);
    }

    /// <summary>
    /// What one tool output sends on this connection, model and lane: one entry per part, in order. Reads the
    /// connection and its capabilities only; it performs no network call and refreshes no credential.
    /// </summary>
    public async Task<LlmToolOutputProjection> ProjectToolOutputAsync(
        string connectionId,
        string modelId,
        LlmToolOutput output,
        LlmToolOutputLane lane = LlmToolOutputLane.Chat,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(output);
        if (!Enum.IsDefined(lane))
        {
            throw new ArgumentOutOfRangeException(nameof(lane));
        }
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(_connections, connectionId, cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        LlmProviderCapabilities route = client.GetCapabilities(connection);
        LlmProviderCapabilities model = client.GetCapabilities(connection, modelId);
        return lane == LlmToolOutputLane.Chat
            ? ToolOutputProjector.Project(output, route.RichContentSupported, model.RichContentSupported).Report
            : ToolOutputProjector.Project(output, route.Agent.RichContentSupported, model.Agent.RichContentSupported).Report;
    }

    public async Task<LlmProviderCapabilities> GetModelCapabilitiesAsync(
        string connectionId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        LlmConnection connection = await ResolveFreshConnectionAsync(connectionId, cancellationToken);
        return ClientFor(connection.Transport).GetCapabilities(connection, modelId);
    }

    private async Task<LlmConnection> ResolveFreshConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            _connections,
            connectionId,
            cancellationToken);
        return await RefreshConnectionAsync(connectionId, connection, cancellationToken);
    }

    private async Task<LlmConnection> RefreshConnectionAsync(
        string connectionId,
        LlmConnection connection,
        CancellationToken cancellationToken)
    {
        if (ClientFor(connection.Transport) is IIgnoresStoredCredential)
        {
            return connection;
        }

        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if (credential?.OAuth is null
            || !_authenticators.TryGetValue(connection.Transport, out ILlmAuthenticator? authenticator))
        {
            return connection;
        }

        LlmCredential fresh = await authenticator.EnsureFreshAsync(credential, cancellationToken);
        if (fresh == credential)
        {
            return connection;
        }

        string json = fresh.Serialize();
        await _connections.SaveCredentialAsync(
            connectionId,
            json,
            connection.Version,
            cancellationToken);
        return connection with { Credential = json };
    }

    private async Task<LlmConnection> RequireExpectedVersionAsync(
        string connectionId,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            throw new ArgumentException("An expected connection version is required.", nameof(expectedVersion));
        }

        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            _connections,
            connectionId,
            cancellationToken);
        return string.Equals(connection.Version, expectedVersion, StringComparison.Ordinal)
            ? connection
            : throw new InvalidOperationException("The LLM connection changed during authentication.");
    }

    private async Task<LlmCredential> PollCodexAsync(
        LlmDeviceCode code,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(code.IntervalSeconds), cancellationToken);
            try
            {
                return await CodexLogin().CompleteAsync(
                    code.DeviceAuthId,
                    code.UserCode,
                    cancellationToken);
            }
            catch (LlmAuthPendingException)
            {
                // Device authorization is intentionally polled until approval or cancellation.
            }
        }
    }

    private ILlmClient ClientFor(LlmTransport transport) =>
        _clients.TryGetValue(transport, out ILlmClient? client)
            ? client
            : throw new NotSupportedException($"No LLM client is registered for transport {transport}.");

    private static LlmChatResult SanitizeDiagnostics(LlmChatResult result, LlmConnection connection)
    {
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        LlmProviderDiagnostics diagnostics = result.Diagnostics;
        return result with
        {
            Diagnostics = diagnostics with
            {
                RequestId = diagnostics.RequestId is null
                    ? null
                    : SecretRedactor.Sanitize(diagnostics.RequestId, credential),
                RawFinalResponseJson = diagnostics.RawFinalResponseJson is null
                    ? null
                    : SecretRedactor.Sanitize(diagnostics.RawFinalResponseJson, credential)
            }
        };
    }

    private ICodexDeviceLogin CodexLogin() =>
        _authenticators.TryGetValue(LlmTransport.OpenAiCodex, out ILlmAuthenticator? authenticator)
            && authenticator is ICodexDeviceLogin login
                ? login
                : throw new NotSupportedException("Codex login is not registered.");

    private IClaudePkceLogin ClaudeLogin() =>
        _authenticators.TryGetValue(LlmTransport.AnthropicClaudeCli, out ILlmAuthenticator? authenticator)
            && authenticator is IClaudePkceLogin login
                ? login
                : throw new NotSupportedException("Claude login is not registered.");
}
