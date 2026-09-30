using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Resolves one configured connection and dispatches to exactly one matching transport. It never
/// retries against, or falls back to, another provider.
/// </summary>
public sealed class LlmProviderService : IDisposable, IAsyncDisposable
{
    private readonly ILlmConnectionSource _connections;
    private readonly Dictionary<LlmTransport, ILlmClient> _clients;
    private readonly Dictionary<LlmTransport, ILlmAccountUsageClient> _accountUsageClients;
    private readonly Dictionary<LlmTransport, ILlmAuthenticator> _authenticators;
    private readonly IReadOnlyDictionary<LlmTransport, ILlmSetupEnvironment> _setupEnvironments;
    private readonly LlmModelListCache _modelLists;
    private readonly bool _ownsModelLists;
    private readonly LlmConnectionAccess _connectionAccess;
    private readonly bool _ownsConnectionAccess;
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly Lock _disposeGate = new();
    private Task? _disposal;

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
        LlmModelListCache? modelLists = null,
        LlmConnectionAccess? connectionAccess = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(authenticators);
        if (connectionAccess is not null && !connectionAccess.UsesSource(connections))
            throw new ArgumentException("Credential access must use the facade's connection source.", nameof(connectionAccess));

        _connections = connections;
        // A service built without the application's shared cache keeps its own lists.
        _modelLists = modelLists ?? new LlmModelListCache();
        _ownsModelLists = modelLists is null;
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
        ILlmAuthenticator[] authenticatorArray = [.. authenticators];
        _authenticators = authenticatorArray.ToDictionary(authenticator => authenticator.Transport);
        _connectionAccess = connectionAccess ?? new LlmConnectionAccess(connections, authenticatorArray, clientArray);
        _ownsConnectionAccess = connectionAccess is null;
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

    /// <summary>Creates the settings manager that opens, acts on and saves LLM settings through <paramref name="target"/>.</summary>
    public LlmSettingsManager CreateSettingsManager(Common.Setup.ISetupCommitTarget<LlmSetupChanges> target)
    {
        _lifetime.ThrowIfStopping();
        return new(_clients, _authenticators.Values, _setupEnvironments, target);
    }

    public const string ClaudeManualRedirectUri =
        Providers.ClaudeAuthenticator.ManualRedirectUri;

    /// <summary>
    /// Binds the portable inference contract to one explicitly selected Connect connection. The
    /// returned adapter carries opaque continuation state unchanged and omits provider diagnostics.
    /// </summary>
    public ILlmInference Bind(string connectionId)
    {
        _lifetime.ThrowIfStopping();
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return new ConnectLlmInference(this, connectionId);
    }

    public async Task<bool> IsAuthenticatedAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
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
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
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

    public async Task<LlmDeviceCode> StartCodexLoginAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        return await CodexLogin().StartAsync(operation.Token).ConfigureAwait(false);
    }

    public async Task CompleteCodexLoginAsync(
        string connectionId,
        string deviceAuthId,
        string userCode,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
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
    public Task<LlmAuthorizationSession> StartClaudeLoginAsync(CancellationToken cancellationToken = default) =>
        StartClaudeLoginAsync(ClaudeManualRedirectUri, cancellationToken);

    /// <summary>Starts the optional OAuth flow used only for Claude subscription model discovery.</summary>
    public Task<LlmAuthorizationSession> StartClaudeLoginAsync(
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        _lifetime.ThrowIfStopping();
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            throw new ArgumentException("A Claude redirect URI is required.", nameof(redirectUri));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ClaudeLogin().Start(redirectUri));
    }

    public async Task CompleteClaudeLoginAsync(
        string connectionId,
        LlmAuthorizationSession session,
        string pastedCode,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
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
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
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
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
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
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
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
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            _connections,
            connectionId,
            cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        if (client is not IStreamingCredentialRefreshPolicy refreshPolicy
            || refreshPolicy.RequiresFreshOAuthCredentialForStreaming(connection))
        {
            connection = await _connectionAccess.RefreshAsync(
                connectionId,
                connection,
                cancellationToken);
        }
        if (client is ILlmConnectionPreparation preparation)
        {
            connection = await preparation.PrepareAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        if (expectedScope is not null && ProviderContinuationStateCodec.Scope(connection) != expectedScope)
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
        (request, int omitted) = ContinuationStateProjection.Project(request, connection);
        connection = await AttachCatalogAsync(connectionId, connection, client, cancellationToken);
        await foreach (LlmStreamEvent streamEvent in client.StreamAsync(
                           connection,
                           request,
                           cancellationToken))
        {
            yield return streamEvent is LlmChatCompletionEvent completion
                ? new LlmChatCompletionEvent(SanitizeDiagnostics(completion.Result, connection) with
                {
                    OmittedContinuationStateCount = checked(omitted + completion.Result.OmittedContinuationStateCount)
                })
                : streamEvent;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Explicitly calls the provider's input-count API using generation's resolved connection and projection.
    /// Unsupported adapters throw; this operation never substitutes an estimate or creates a completion.
    /// </summary>
    public async Task<LlmProviderInputTokenCount> CountInputTokensAsync(string connectionId, LlmChatRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(_connections, connectionId, cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        if (client is not ILlmProviderTokenCounter counter)
            throw new NotSupportedException("This route does not implement provider input-token counting.");
        connection = await _connectionAccess.RefreshAsync(connectionId, connection, cancellationToken);
        if (client is ILlmConnectionPreparation preparation)
            connection = await preparation.PrepareAsync(connection, cancellationToken).ConfigureAwait(false);
        (request, int omitted) = ContinuationStateProjection.Project(request, connection);
        connection = await AttachCatalogAsync(connectionId, connection, client, cancellationToken);
        LlmProviderInputTokenCount result;
        try { result = await counter.CountInputTokensAsync(connection, request, cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        cancellationToken.ThrowIfCancellationRequested();
        return result with { OmittedContinuationStateCount = checked(omitted + result.OmittedContinuationStateCount) };
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
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        if (request.MaxOutputTokens is <= 0) { throw new ArgumentOutOfRangeException(nameof(request)); }
        policy ??= new();
        ManagedAgentPolicy.Validate(new() { ContextPolicy = policy }, null);
        var connection = await LlmConnectionResolver.ResolveAsync(_connections, connectionId, cancellationToken);
        var client = ClientFor(connection.Transport);
        // Measure what would be sent: the same projection as the request path.
        // Ambient-credential adapters cannot decide state binding without resolving identity.
        // Their nonmutating estimator reports unknown for opaque state instead of dropping it.
        if (client is not ILlmConnectionPreparation)
        { request = ContinuationStateProjection.Project(request, connection).Request; }
        connection = await AttachCatalogAsync(connectionId, connection, client, cancellationToken);
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
            // A catalog-capability adapter already had its listing attached above; a failed one is not repeated.
            IReadOnlyList<LlmModel>? models = connection.Catalog;
            if (models is null && client is not ILlmCatalogCapabilities)
            {
                try
                {
                    models = await _modelLists.GetAsync(connectionId, connection.Version,
                        token => client.ListModelsAsync(connection, token), cancellationToken);
                }
                catch (Exception error) when (error is HttpRequestException or LlmProviderException or NotSupportedException)
                {
                    models = null;
                }
            }
            model = models?.FirstOrDefault(candidate => candidate.Id == request.ModelId);
            if (models is null)
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
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        LlmChatResult? result = null;
        await foreach (LlmStreamEvent streamEvent in StreamAsync(
                           connectionId,
                           request,
                           cancellationToken))
        {
            if (streamEvent is not LlmChatCompletionEvent completion)
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
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        LlmConnection connection = await ResolveFreshConnectionAsync(connectionId, cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        if (client is ILlmConnectionPreparation preparation)
        {
            connection = await preparation.PrepareAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        return await _modelLists.GetAsync(connectionId, connection.Version + connection.ResolvedContinuationScope,
            token => client.ListModelsAsync(connection, token), cancellationToken);
    }

    /// <summary>Asks the provider for the connection's models again and keeps the new list.</summary>
    public async Task<IReadOnlyList<LlmModel>> RefreshModelsAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        LlmConnection connection = await ResolveFreshConnectionAsync(connectionId, cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        if (client is ILlmConnectionPreparation preparation)
        {
            connection = await preparation.PrepareAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        return await _modelLists.RefreshAsync(connectionId, connection.Version + connection.ResolvedContinuationScope,
            token => client.ListModelsAsync(connection, token), cancellationToken);
    }

    public async Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
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
            connection = await _connectionAccess.RefreshAsync(connectionId, connection, cancellationToken);
        }
        return await usageClient.GetAccountUsageAsync(connection, cancellationToken);
    }

    public async Task<LlmProviderCapabilities> GetCapabilitiesAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        LlmConnection connection = await ResolveFreshConnectionAsync(connectionId, cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        connection = await AttachCatalogAsync(connectionId, connection, client, cancellationToken);
        return client.GetCapabilities(connection);
    }

    /// <summary>
    /// What one tool output sends on this connection, model and lane: one entry per part, in order. Reads the
    /// connection and its capabilities only and refreshes no credential. An adapter whose capabilities depend on
    /// its provider catalog may list the connection's models once, as generation does.
    /// </summary>
    public async Task<LlmToolOutputProjection> ProjectToolOutputAsync(
        string connectionId,
        string modelId,
        LlmToolOutput output,
        LlmToolOutputLane lane = LlmToolOutputLane.Chat,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(output);
        if (!Enum.IsDefined(lane))
        {
            throw new ArgumentOutOfRangeException(nameof(lane));
        }
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(_connections, connectionId, cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        connection = await AttachCatalogAsync(connectionId, connection, client, cancellationToken);
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
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        LlmConnection connection = await ResolveFreshConnectionAsync(connectionId, cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        connection = await AttachCatalogAsync(connectionId, connection, client, cancellationToken);
        return client.GetCapabilities(connection, modelId);
    }

    /// <summary>
    /// Publishes the connection's kept model list to an adapter whose capabilities depend on its provider catalog.
    /// The list shares the cache entry <see cref="ListModelsAsync"/> uses, so the first call on a new connection
    /// version lists once. A failed listing does not fail the operation: the adapter then uses its family table.
    /// </summary>
    internal async Task<LlmConnection> AttachCatalogAsync(
        string connectionId,
        LlmConnection connection,
        ILlmClient client,
        CancellationToken cancellationToken)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        if (client is not ILlmCatalogCapabilities)
        {
            return connection;
        }
        try
        {
            IReadOnlyList<LlmModel> models = await _modelLists.GetAsync(connectionId,
                connection.Version + connection.ResolvedContinuationScope,
                token => client.ListModelsAsync(connection, token), cancellationToken);
            return connection with { Catalog = models };
        }
        catch (Exception error) when (error is HttpRequestException or LlmProviderException or System.Text.Json.JsonException)
        {
            return connection;
        }
    }

    private async Task<LlmConnection> ResolveFreshConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            _connections,
            connectionId,
            cancellationToken);
        _ = ClientFor(connection.Transport);
        return await _connectionAccess.RefreshAsync(connectionId, connection, cancellationToken);
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

    /// <summary>
    /// Fences new operations, cancels and awaits active facade work, and retires a privately owned model cache.
    /// Supplied clients, authenticators, the connection source and an injected cache remain borrowed.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) { return new(_disposal ??= RetireAsync()); }
    }

    private async Task RetireAsync()
    {
        Task operations = _lifetime.RetireAsync();
        Task credentials = _ownsConnectionAccess ? _connectionAccess.DisposeAsync().AsTask() : Task.CompletedTask;
        try
        {
            if (_ownsModelLists) await _modelLists.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(operations, credentials).ConfigureAwait(false);
            _clients.Clear();
            _authenticators.Clear();
            _accountUsageClients.Clear();
        }
    }

    /// <summary>Synchronously retires the facade. Use asynchronous disposal while provider work is active.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
