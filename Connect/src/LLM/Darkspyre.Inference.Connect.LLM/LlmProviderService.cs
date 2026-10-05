using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Resolves one configured connection and dispatches to exactly one matching transport. It never
/// retries against, or falls back to, another provider.
/// </summary>
public sealed partial class LlmProviderService : ILlmTextTokenizer, ILlmFileClient, IXaiFilePublicUrlClient,
    IXaiSkillClient, IDocumentSearchClient, IDisposable, IAsyncDisposable
{
    private ILlmConnectionSource? _connections;
    private ILlmConnectionSource Connections => _connections ?? throw new ObjectDisposedException(nameof(LlmProviderService));
    private readonly Dictionary<LlmTransport, ILlmClient> _clients;
    private readonly Dictionary<LlmTransport, ILlmAccountUsageClient> _accountUsageClients;
    private readonly Dictionary<LlmTransport, ILlmAuthenticator> _authenticators;
    private readonly Dictionary<LlmTransport, ILlmSetupEnvironment> _setupEnvironments;
    private readonly LlmModelListCache _modelLists;
    private readonly bool _ownsModelLists;
    private readonly LlmAccountUsageObservations _observations;
    private readonly bool _ownsObservations;
    private LlmConnectionAccess? _connectionAccess;
    private LlmConnectionAccess ConnectionAccess => _connectionAccess ?? throw new ObjectDisposedException(nameof(LlmProviderService));
    private readonly bool _ownsConnectionAccess;
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly Lock _disposeGate = new();
    private Task? _disposal;
    private Providers.XaiFileClient? _xaiFiles;
    private readonly GeminiCacheBindings _cacheBindings = new();
    private readonly LlmCleanupOutcomes _cleanupOutcomes = new();

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
        LlmAccountUsageObservations? observations = null,
        LlmConnectionAccess? connectionAccess = null)
        : this(connections, clients, authenticators, setupEnvironments, modelLists, observations, connectionAccess, null)
    {
    }

    internal LlmProviderService(ILlmConnectionSource connections, IEnumerable<ILlmClient> clients,
        IEnumerable<ILlmAuthenticator> authenticators, Action admission)
        : this(connections, clients, authenticators, null, null, null, null, admission)
    {
    }

    private LlmProviderService(ILlmConnectionSource connections, IEnumerable<ILlmClient> clients,
        IEnumerable<ILlmAuthenticator> authenticators, IEnumerable<ILlmSetupEnvironment>? setupEnvironments,
        LlmModelListCache? modelLists, LlmAccountUsageObservations? observations,
        LlmConnectionAccess? connectionAccess, Action? admission)
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
        _observations = observations ?? new();
        _ownsObservations = observations is null;
        ILlmClient[] clientArray = [.. clients];
        IGrouping<LlmTransport, ILlmClient>? duplicate = clientArray
            .GroupBy(client => LlmConstructionAdmission.Read(admission, () => client.Transport))
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Multiple LLM clients are registered for transport {duplicate.Key}.");
        }
        ILlmClient? inconsistent = clientArray.FirstOrDefault(
            client => LlmConstructionAdmission.Read(admission, () => client.ProviderDefinition).Transport
                != LlmConstructionAdmission.Read(admission, () => client.Transport));
        if (inconsistent is not null)
        {
            throw new InvalidOperationException(
                $"Provider definition '{LlmConstructionAdmission.Read(admission, () => inconsistent.ProviderDefinition).Id}' does not match transport {LlmConstructionAdmission.Read(admission, () => inconsistent.Transport)}.");
        }
        IGrouping<string, ILlmClient>? duplicateProvider = clientArray
            .GroupBy(client => LlmConstructionAdmission.Read(admission, () => client.ProviderDefinition).Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicateProvider is not null)
        {
            throw new InvalidOperationException(
                $"Multiple LLM clients are registered for provider '{duplicateProvider.Key}'.");
        }

        _clients = clientArray.ToDictionary(client => LlmConstructionAdmission.Read(admission, () => client.Transport));
        _accountUsageClients = clientArray
            .OfType<ILlmAccountUsageClient>()
            .ToDictionary(client => LlmConstructionAdmission.Read(admission, () => client.Transport));
        ILlmAuthenticator[] authenticatorArray = [.. authenticators];
        _authenticators = authenticatorArray.ToDictionary(authenticator => LlmConstructionAdmission.Read(admission, () => authenticator.Transport));
        _connectionAccess = connectionAccess ?? new LlmConnectionAccess(connections, authenticatorArray, clientArray, admission);
        _ownsConnectionAccess = connectionAccess is null;
        _setupEnvironments = (setupEnvironments ?? []).ToDictionary(environment => LlmConstructionAdmission.Read(admission, () => environment.Transport));
        SupportedTransports = [.. _clients.Keys.OrderBy(transport => transport)];
        SupportedProviders =
        [
            .. clientArray
                .Select(client => LlmConstructionAdmission.Read(admission, () => client.ProviderDefinition))
                .OrderBy(provider => provider.DisplayOrder)
                .ThenBy(provider => provider.DisplayName, StringComparer.Ordinal)
        ];
        _xaiFiles = clientArray.OfType<Providers.XaiResponsesLlmClient>().SingleOrDefault()?.CreateFileClient();
    }

    public IReadOnlyList<LlmTransport> SupportedTransports { get; }

    /// <summary>Canonical provider metadata and guided setup supplied by registered adapters.</summary>
    public IReadOnlyList<LlmProviderDefinition> SupportedProviders { get; }

    public IReadOnlyList<Common.Setup.InferenceSetupDefinition> SetupDefinitions =>
        SupportedProviders.Select(provider => provider.SetupDefinition).ToArray();

    /// <summary>Creates the settings manager that opens, acts on and saves LLM settings through <paramref name="target"/>.</summary>
    public LlmSettingsManager CreateSettingsManager(Common.Setup.ISetupCommitTarget<LlmSetupChanges> target,
        LlmAgentProviderService? agents = null)
    {
        using var operation = _lifetime.Enter(CancellationToken.None);
        agents?.ValidateSource(Connections);
        return new(_clients, _authenticators.Values, _setupEnvironments, target, agents);
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

    /// <summary>Validates an explicit connection without refreshing credentials or invoking a provider.</summary>
    public async Task<ILlmContextCacheClient> BindContextCacheAsync(
        string connectionId, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        LlmConnection connection = await ResolveConnectionAsync(connectionId, operation.Token)
            .ConfigureAwait(false);
        ThrowIfOperationCanceled(operation.Token);
        connection = EmbeddingRequestData.Snapshot(connection);
        ILlmClient client = ClientFor(connection.Transport);
        ThrowIfOperationCanceled(operation.Token);
        if (client is not ILlmContextCacheAdapter cache) throw new NotSupportedException("No explicit context cache adapter is implemented.");
        cache.ValidateCacheConnection(connection);
        ThrowIfOperationCanceled(operation.Token);
        return new BoundContextCache(this, connectionId);
    }

    /// <summary>Uploads borrowed source bytes through the selected implemented native file route.</summary>
    public Task<LlmProviderFile> UploadAsync(string connectionId, Stream source, string fileName,
        LlmFileUploadOptions? options = null, CancellationToken cancellationToken = default) =>
        FileOperationAsync(connectionId, () => { _ = XaiFileRequestData.Upload(source, fileName, options); },
            (client, connection, fence, token) => client.UploadAsync(connection, source, fileName, options, fence, token), cancellationToken);

    public Task<LlmFilePage> ListAsync(string connectionId, LlmFileListRequest request,
        CancellationToken cancellationToken = default) => FileOperationAsync(connectionId,
            () => { _ = XaiFileRequestData.List(request); },
            (client, connection, fence, token) => client.ListAsync(connection, request, fence, token), cancellationToken);

    public Task<LlmProviderFile> GetAsync(string connectionId, string fileId,
        CancellationToken cancellationToken = default) => FileOperationAsync(connectionId,
            () => { _ = XaiFileRequestData.Path(fileId); },
            (client, connection, fence, token) => client.GetAsync(connection, fileId, fence, token), cancellationToken);

    public Task<ResourceDownloadReceipt> DownloadAsync(string connectionId, string fileId, Stream destination,
        LlmFileDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        FileOperationAsync(connectionId, () => { _ = XaiFileRequestData.Download(fileId, destination, options); },
            (client, connection, fence, token) => client.DownloadAsync(connection, fileId, destination, options, fence, token), cancellationToken);

    public Task<LlmFileDeletion> DeleteAsync(string connectionId, string fileId,
        CancellationToken cancellationToken = default) => FileOperationAsync(connectionId,
            () => { _ = XaiFileRequestData.Path(fileId); },
            (client, connection, fence, token) => client.DeleteAsync(connection, fileId, fence, token), cancellationToken);

    public Task<XaiPublicUrl> CreatePublicUrlAsync(string connectionId, string fileId,
        XaiPublicUrlOptions? options = null, CancellationToken cancellationToken = default) =>
        FileOperationAsync(connectionId, () => { _ = XaiFileRequestData.Path(fileId); _ = XaiFileRequestData.Expiry(options?.ExpiresAfter); },
            (client, connection, fence, token) => client.CreatePublicUrlAsync(connection, fileId, options, fence, token), cancellationToken);

    public Task<XaiPublicUrlRevocation> RevokePublicUrlAsync(string connectionId, string fileId,
        CancellationToken cancellationToken = default) => FileOperationAsync(connectionId,
            () => { _ = XaiFileRequestData.Path(fileId); },
            (client, connection, fence, token) => client.RevokePublicUrlAsync(connection, fileId, fence, token), cancellationToken);

    private async Task<T> FileOperationAsync<T>(string connectionId, Action validate,
        Func<Providers.XaiFileClient, LlmConnection, Action, CancellationToken, Task<T>> dispatch,
        CancellationToken cancellationToken)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        Providers.XaiFileClient files = _xaiFiles ?? throw new NotSupportedException("No Files resource adapter is implemented.");
        LlmConnection connection = await ResolveConnectionAsync(connectionId, cancellationToken).ConfigureAwait(false);
        ThrowIfOperationCanceled(cancellationToken);
        connection = EmbeddingRequestData.Snapshot(connection);
        ILlmClient client = ClientFor(connection.Transport);
        if (!HasFileRoute(client))
            throw new NotSupportedException("The selected transport has no Files resource adapter.");
        validate();
        ThrowIfOperationCanceled(cancellationToken);
        XaiFileRequestData.Connection(connection);
        ThrowIfOperationCanceled(cancellationToken);
        connection = await RefreshConnectionAsync(connectionId, connection, client, cancellationToken).ConfigureAwait(false);
        ThrowIfOperationCanceled(cancellationToken);
        T result = await dispatch(files, connection, () => ThrowIfOperationCanceled(cancellationToken), cancellationToken).ConfigureAwait(false);
        ThrowIfOperationCanceled(cancellationToken);
        return result;
    }

    private bool HasFileRoute(ILlmClient client) => _xaiFiles is not null && client is Providers.XaiResponsesLlmClient;

    private LlmProviderCapabilities FileCapabilities(ILlmClient client, LlmProviderCapabilities capabilities) =>
        HasFileRoute(client) ? capabilities : capabilities with { Files = new() };

    Task<XaiSkill> IXaiSkillClient.UploadAsync(string connectionId, XaiSkillUpload upload,
        CancellationToken cancellationToken) => UnavailableResourceAsync<XaiSkill>(cancellationToken);

    Task<XaiSkillPage> IXaiSkillClient.ListAsync(string connectionId, XaiSkillListRequest request,
        CancellationToken cancellationToken) => FileOperationAsync(connectionId,
            () => { _ = XaiFileClient.SkillListPath(request); },
            (client, connection, fence, token) => client.ListSkillsAsync(connection, request, fence, token), cancellationToken);

    Task<XaiSkill> IXaiSkillClient.GetAsync(string connectionId, string skillId,
        CancellationToken cancellationToken) => FileOperationAsync(connectionId,
            () => { _ = XaiFileClient.SkillPath(skillId); },
            (client, connection, fence, token) => client.GetSkillAsync(connection, skillId, fence, token), cancellationToken);

    Task<ResourceDownloadReceipt> IXaiSkillClient.DownloadAsync(string connectionId, string skillId, Stream destination,
        CancellationToken cancellationToken) => FileOperationAsync(connectionId,
            () => { _ = XaiFileClient.SkillDownloadPath(skillId, destination); },
            (client, connection, fence, token) => client.DownloadSkillAsync(connection, skillId, destination, fence, token), cancellationToken);

    Task<XaiSkillDeletion> IXaiSkillClient.DeleteAsync(string connectionId, string skillId,
        CancellationToken cancellationToken) => FileOperationAsync(connectionId,
            () => { _ = XaiFileClient.SkillPath(skillId); },
            (client, connection, fence, token) => client.DeleteSkillAsync(connection, skillId, fence, token), cancellationToken);

    /// <summary>Searches existing collections through the selected implemented document-search route.</summary>
    public async Task<DocumentSearchResult> SearchAsync(string connectionId, DocumentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        try
        {
            request = XaiDocumentSearch.Snapshot(request);
            Fence();
            LlmConnection connection = await ResolveConnectionAsync(connectionId, cancellationToken).ConfigureAwait(false);
            Fence();
            connection = EmbeddingRequestData.Snapshot(connection);
            Fence();
            if (ClientFor(connection.Transport) is not XaiResponsesLlmClient client)
                throw new NotSupportedException("The selected transport has no document-search adapter.");
            Validate(connection);
            connection = await ConnectionAccess.RefreshAsync(connectionId, connection, Validate, Fence,
                cancellationToken).ConfigureAwait(false);
            Fence();
            DocumentSearchResult result = await XaiDocumentSearch.SearchAsync(client.SharedHttp, connection,
                request, Fence, cancellationToken).ConfigureAwait(false);
            Fence();
            return result;
        }
        catch (Exception error) { _cleanupOutcomes.Record(error); throw; }

        void Fence() => ThrowIfOperationCanceled(cancellationToken);
        void Validate(LlmConnection snapshot)
        {
            Fence();
            XaiDocumentSearch.ValidateConnection(snapshot);
            Fence();
        }
    }

    private Task<T> UnavailableResourceAsync<T>(CancellationToken cancellationToken,
        string message = "No Files or Skills resource adapter is implemented.")
    {
        using var operation = _lifetime.Enter(cancellationToken);
        return Task.FromException<T>(new NotSupportedException(message));
    }

    public async Task<bool> IsAuthenticatedAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        LlmConnection? connection = await Connections.GetConnectionAsync(connectionId, cancellationToken);
        ThrowIfOperationCanceled(cancellationToken);
        if (connection is not null) RuntimeInputAdmission.Validate(connection);
        LlmCredential? credential = LlmCredential.Parse(connection?.Credential);
        if (connection?.Transport == LlmTransport.XaiResponses
            && (!string.IsNullOrEmpty(credential?.ApiKey) || !XaiAuthorization.IsValid(credential)))
            return false;
        if (connection is not null
            && _clients.TryGetValue(connection.Transport, out ILlmClient? client)
            && client is IExternalLlmAuthenticationProbe externalProbe)
        {
            return await externalProbe.IsAuthenticatedAsync(connection, cancellationToken);
        }

        if (connection?.Transport == LlmTransport.XaiResponses)
            return true;
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
        if (connection.Transport == LlmTransport.XaiResponses)
            throw new NotSupportedException("Grok requires OAuth sign-in; an API key is not supported.");
        if (connection.Transport is LlmTransport.AzureOpenAiResponses or LlmTransport.AzureOpenAiChat
            && connection.Azure is { AuthenticationMode: not LlmAzureAuthenticationMode.ApiKey })
            throw new NotSupportedException("The selected Azure authentication mode does not accept an API key.");
        await Connections.SaveCredentialAsync(
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
        ThrowIfOperationCanceled(cancellationToken);
        RuntimeInputAdmission.ValidateCredential(connection.Transport, credential);
        await Connections.SaveCredentialAsync(
            connectionId,
            credential.Serialize(),
            connection.Version,
            cancellationToken);
    }

    /// <summary>Starts Grok device authorization. The xAI authenticator must be registered.</summary>
    public async Task<LlmDeviceCode> StartXaiLoginAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        LlmDeviceCode code = await XaiLogin().StartAsync(operation.Token).ConfigureAwait(false);
        ThrowIfOperationCanceled(operation.Token);
        return code;
    }

    /// <summary>Performs one Grok token poll and saves only against the supplied connection version.</summary>
    public async Task CompleteXaiLoginAsync(
        string connectionId,
        string deviceAuthId,
        string userCode,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceAuthId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userCode);
        LlmConnection connection = await RequireExpectedVersionAsync(connectionId, expectedVersion, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfOperationCanceled(cancellationToken);
        if (connection.Transport != LlmTransport.XaiResponses)
            throw new InvalidOperationException("Grok OAuth can be attached only to an xAI connection.");
        LlmCredential credential = await XaiLogin().CompleteAsync(deviceAuthId, userCode, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfOperationCanceled(cancellationToken);
        await SaveXaiCredentialAsync(connectionId, connection.Version, credential, cancellationToken).ConfigureAwait(false);
        ThrowIfOperationCanceled(cancellationToken);
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

        using var operation = _lifetime.Enter(cancellationToken);
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
        ThrowIfOperationCanceled(cancellationToken);
        RuntimeInputAdmission.ValidateCredential(connection.Transport, credential);
        await Connections.SaveCredentialAsync(
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
        await Connections.SaveCredentialAsync(
            connectionId,
            new LlmCredential { Aws = aws }.Serialize(),
            connection.Version,
            cancellationToken);
    }

    /// <summary>
    /// Runs the interactive sign-in flow for a Codex, Grok or Claude connection. The connection version is
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
        LlmConnection connection = await ResolveConnectionAsync(
            connectionId,
            cancellationToken);
        switch (connection.Transport)
        {
            case LlmTransport.OpenAiCodex:
                LlmDeviceCode code = await CodexLogin().StartAsync(cancellationToken);
                await prompt.ShowDeviceCodeAsync(code, cancellationToken);
                LlmCredential codexCredential = await PollCodexAsync(code, cancellationToken);
                ThrowIfOperationCanceled(cancellationToken);
                RuntimeInputAdmission.ValidateCredential(connection.Transport, codexCredential);
                await Connections.SaveCredentialAsync(
                    connectionId,
                    codexCredential.Serialize(),
                    connection.Version,
                    cancellationToken);
                break;

            case LlmTransport.XaiResponses:
                ThrowIfOperationCanceled(cancellationToken);
                LlmDeviceCode xaiCode = await XaiLogin().StartAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfOperationCanceled(cancellationToken);
                await prompt.ShowDeviceCodeAsync(xaiCode, cancellationToken).ConfigureAwait(false);
                ThrowIfOperationCanceled(cancellationToken);
                LlmCredential xaiCredential = await PollXaiAsync(xaiCode, cancellationToken).ConfigureAwait(false);
                ThrowIfOperationCanceled(cancellationToken);
                await SaveXaiCredentialAsync(connectionId, connection.Version, xaiCredential, cancellationToken).ConfigureAwait(false);
                ThrowIfOperationCanceled(cancellationToken);
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

    internal async Task<ProviderCompactionResult> CompactAgentAsync(
        string connectionId, LlmChatRequest request, string expectedScope, CancellationToken cancellationToken)
    {
        ProviderCompactionResult? result = null;
        try
        {
            await foreach (LlmStreamEvent _ in StreamCoreAsync(connectionId, request, expectedScope, cancellationToken,
                compacted => result = compacted).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Provider compaction cannot dispatch generation events.");
            }
            return result ?? throw new InvalidOperationException("Provider compaction returned no result.");
        }
        catch (Exception error) { _cleanupOutcomes.Record(error); throw; }
    }

    private IAsyncEnumerable<LlmStreamEvent> StreamCoreAsync(
        string connectionId, LlmChatRequest request, string? expectedScope,
        CancellationToken cancellationToken, Action<ProviderCompactionResult>? publishCompaction = null) =>
        _lifetime.StreamAsync(token => ProduceStreamAsync(connectionId, request, expectedScope, token, publishCompaction), cancellationToken);

    private async IAsyncEnumerable<LlmStreamEvent> ProduceStreamAsync(
        string connectionId, LlmChatRequest request, string? expectedScope,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Action<ProviderCompactionResult>? publishCompaction = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ClientTools.Snapshot(request with { HostedTools = HostedTools.Snapshot(request.HostedTools) });
        LlmAccountUsageObservations.Reservation observationReservation = _observations.Reserve(connectionId);
        long cacheSequence = _cacheBindings.BeginOperation();
        LlmConnection connection = await ResolveConnectionAsync(
            connectionId,
            cancellationToken);
        ThrowIfOperationCanceled(cancellationToken);
        RequestJson.ValidateSamplingOptions(connection, request);
        request = ProviderControlPolicy.Resolve(connection, request);
        ILlmClient client = ClientFor(connection.Transport);
        if (publishCompaction is not null && client is not (OpenAiCodexLlmClient or XaiResponsesLlmClient))
            throw new NotSupportedException("The selected route has no provider compactor.");
        string? resolvedCredential = connection.Credential;
        connection = WithContextCache(EmbeddingRequestData.Snapshot(connection), client);
        if (client is GeminiApiLlmClient) GeminiApiLlmClient.ValidateCachedSuffix(request);
        if (request.ExplicitCache is not null)
            ExplicitCaches.Validate(request, client.GetCapabilities(connection, request.ModelId).SupportsExplicitCache);
        LlmAttachments.ValidateReferences(request, () => client.GetCapabilities(connection, request.ModelId).SupportsFileReferences,
            client is Providers.XaiResponsesLlmClient);
        HostedTools.Validate(request, client.GetCapabilities(connection, request.ModelId));
        ClientTools.Validate(request, client.GetCapabilities(connection, request.ModelId).SupportsShellTools);
        if (client is Providers.XaiResponsesLlmClient resourceDestination)
        {
            if (publishCompaction is not null)
                resourceDestination.PreflightCompactionRequest(connection, request);
            else resourceDestination.PreflightResourceRequest(connection, request);
        }
        ThrowIfOperationCanceled(cancellationToken);
        (connection, request) = await AdmitContextCacheAsync(connectionId, connection, client, request, cacheSequence, cancellationToken).ConfigureAwait(false);
        if (client is not IStreamingCredentialRefreshPolicy refreshPolicy
            || refreshPolicy.RequiresFreshOAuthCredentialForStreaming(connection))
        {
            connection = await RefreshConnectionAsync(
                connectionId,
                connection,
                client,
                cancellationToken);
        }
        LlmAccountUsageObservations.Binding? observationBinding;
        if (connection.Credential == resolvedCredential)
        {
            observationBinding = observationReservation.Bind(connection);
        }
        else
        {
            observationReservation = _observations.Reserve(connectionId);
            LlmConnection? persisted = null;
            try
            {
                persisted = await Connections.GetConnectionAsync(connectionId, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Failed observation confirmation drops evidence, not an otherwise valid generation.
                cancellationToken.ThrowIfCancellationRequested();
            }
            cancellationToken.ThrowIfCancellationRequested();
            observationBinding = persisted is not null && !string.IsNullOrWhiteSpace(persisted.Version)
                && persisted.Transport == connection.Transport && persisted.Credential == connection.Credential
                && ProviderContinuationStateCodec.Scope(persisted) == ProviderContinuationStateCodec.Scope(connection)
                ? observationReservation.Bind(persisted) : null;
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
        connection = await AttachCatalogCoreAsync(connectionId, connection, client, cancellationToken);
        ThrowIfOperationCanceled(cancellationToken);
        if (publishCompaction is not null && client is XaiResponsesLlmClient xaiCompactor)
        {
            ProviderCompactionResult compacted = await xaiCompactor.CompactWithResourcesAsync(connection, request,
                _xaiFiles!, () => ThrowIfOperationCanceled(cancellationToken), cancellationToken).ConfigureAwait(false);
            ThrowIfOperationCanceled(cancellationToken);
            connection.CacheAdmission?.Check(connection, request.ExplicitCache);
            publishCompaction(compacted);
            yield break;
        }
        IAsyncEnumerable<LlmStreamEvent> stream = publishCompaction is not null && client is OpenAiCodexLlmClient codex
            ? codex.StreamCompactionAsync(connection, request, cancellationToken)
            : client is Providers.XaiResponsesLlmClient xai
            ? xai.StreamWithResourcesAsync(connection, request, _xaiFiles!, () => ThrowIfOperationCanceled(cancellationToken),
                _cleanupOutcomes.Record, cancellationToken)
            : client.StreamAsync(connection, request, cancellationToken);
        ProviderCompactionResult? compactionResult = null;
        await foreach (LlmStreamEvent streamEvent in stream)
        {
            ThrowIfOperationCanceled(cancellationToken);
            connection.CacheAdmission?.Check(connection, request.ExplicitCache);
            if (streamEvent is AccountUsageObservedEvent observed)
            { observationBinding?.Record(observed.Snapshot); continue; }
            if (publishCompaction is not null)
            {
                if (compactionResult is not null || streamEvent is not LlmChatCompletionEvent compacted)
                    throw new InvalidOperationException("Provider compaction returned an unexpected event.");
                LlmChatResult result = SanitizeDiagnostics(compacted.Result, connection);
                if (result.Status != LlmCompletionStatus.Completed || result.ContinuationState is null
                    || string.IsNullOrWhiteSpace(result.ProviderResponseId))
                    throw new InvalidOperationException("Provider compaction returned no completed continuation.");
                compactionResult = new(result.ContinuationState, result.ProviderResponseId, result.Usage);
                continue;
            }
            yield return streamEvent is LlmChatCompletionEvent completion
                ? new LlmChatCompletionEvent(SanitizeDiagnostics(completion.Result, connection) with
                {
                    OmittedContinuationStateCount = checked(omitted + completion.Result.OmittedContinuationStateCount)
                })
                : streamEvent;
        }
        ThrowIfOperationCanceled(cancellationToken);
        connection.CacheAdmission?.Check(connection, request.ExplicitCache);
        if (publishCompaction is not null)
            publishCompaction(compactionResult ?? throw new InvalidOperationException("Provider compaction returned no result."));
    }

    /// <summary>Tokenizes only the supplied plain text. No request projection, catalog, usage or estimate is substituted.</summary>
    public async Task<LlmTokenization> TokenizeAsync(string connectionId, string modelId, string text,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(text);
        LlmConnection connection = await ResolveConnectionAsync(connectionId, cancellationToken);
        ThrowIfOperationCanceled(cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        if (client is not ILlmTextTokenizerClient tokenizer)
            throw new NotSupportedException("This route does not implement plain-text tokenization.");
        connection = await RefreshConnectionAsync(connectionId, connection, client, cancellationToken);
        ThrowIfOperationCanceled(cancellationToken);
        LlmTokenization result;
        try { result = await tokenizer.TokenizeAsync(connection, modelId, text, cancellationToken).ConfigureAwait(false); }
        catch
        {
            ThrowIfOperationCanceled(cancellationToken);
            throw;
        }
        ThrowIfOperationCanceled(cancellationToken);
        return result;
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
        request = ClientTools.Snapshot(request with { HostedTools = HostedTools.Snapshot(request.HostedTools) });
        long cacheSequence = _cacheBindings.BeginOperation();
        LlmConnection connection = await ResolveConnectionAsync(connectionId, cancellationToken);
        ThrowIfOperationCanceled(cancellationToken);
        RequestJson.ValidateSamplingOptions(connection, request);
        request = ProviderControlPolicy.Resolve(connection, request);
        ExplicitCaches.ValidateProviderTokenCount(connection, request);
        ILlmClient client = ClientFor(connection.Transport);
        if (client is not ILlmProviderTokenCounter counter)
            throw new NotSupportedException("This route does not implement provider input-token counting.");
        connection = WithContextCache(EmbeddingRequestData.Snapshot(connection), client);
        if (client is GeminiApiLlmClient) GeminiApiLlmClient.ValidateCachedSuffix(request);
        if (request.ExplicitCache is not null)
            ExplicitCaches.Validate(request, client.GetCapabilities(connection, request.ModelId).SupportsExplicitCache);
        LlmAttachments.ValidateReferences(request, () => client.GetCapabilities(connection, request.ModelId).SupportsFileReferences);
        HostedTools.Validate(request, client.GetCapabilities(connection, request.ModelId));
        ClientTools.Validate(request, client.GetCapabilities(connection, request.ModelId).SupportsShellTools);
        (connection, request) = await AdmitContextCacheAsync(connectionId, connection, client, request, cacheSequence, cancellationToken).ConfigureAwait(false);
        connection = await RefreshConnectionAsync(connectionId, connection, client, cancellationToken);
        if (client is ILlmConnectionPreparation preparation)
            connection = await preparation.PrepareAsync(connection, cancellationToken).ConfigureAwait(false);
        (request, int omitted) = ContinuationStateProjection.Project(request, connection);
        connection = await AttachCatalogAsync(connectionId, connection, client, cancellationToken);
        LlmProviderInputTokenCount result;
        try { result = await counter.CountInputTokensAsync(connection, request, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("The operation was canceled.", error, cancellationToken); }
        ThrowIfOperationCanceled(cancellationToken);
        connection.CacheAdmission?.Check(connection, request.ExplicitCache);
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
        request = ClientTools.Snapshot(request with { HostedTools = HostedTools.Snapshot(request.HostedTools) });
        long cacheSequence = _cacheBindings.BeginOperation();
        var connection = await ResolveConnectionAsync(connectionId, cancellationToken);
        ThrowIfOperationCanceled(cancellationToken);
        RequestJson.ValidateSamplingOptions(connection, request);
        request = ProviderControlPolicy.Resolve(connection, request);
        var client = ClientFor(connection.Transport);
        connection = WithContextCache(connection, client);
        ValidateCacheEstimate(connectionId, connection, client, request, cacheSequence);
        // Measure what would be sent: the same projection as the request path.
        if (request.ExplicitCache is not null)
            ExplicitCaches.Validate(request, client.GetCapabilities(connection, request.ModelId).SupportsExplicitCache);
        LlmAttachments.ValidateReferences(request, () => client.GetCapabilities(connection, request.ModelId).SupportsFileReferences);
        HostedTools.Validate(request, client.GetCapabilities(connection, request.ModelId));
        ClientTools.Validate(request, client.GetCapabilities(connection, request.ModelId).SupportsShellTools);
        // Ambient-credential adapters cannot decide state binding without resolving identity.
        // Their nonmutating estimator reports unknown for opaque state instead of dropping it.
        if (client is not ILlmConnectionPreparation)
        { request = ContinuationStateProjection.Project(request, connection).Request; }
        if (client is XaiResponsesLlmClient resourceDestination)
            resourceDestination.PreflightResourceRequest(connection, request);
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
                    models = await _modelLists.GetAsync(connectionId, new(connection.Version, connection.ResolvedContinuationScope),
                        client, connection, ModelCatalogFamily.Chat, cancellationToken);
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
    /// The connection's models. A list is kept until its exact version/scope or admitted xAI configuration changes or
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
        ThrowIfOperationCanceled(cancellationToken);
        IReadOnlyList<LlmModel> models = await _modelLists.GetAsync(connectionId, new(connection.Version, connection.ResolvedContinuationScope),
            client, connection, ModelCatalogFamily.Chat, cancellationToken);
        ThrowIfOperationCanceled(cancellationToken);
        return models;
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
        ThrowIfOperationCanceled(cancellationToken);
        IReadOnlyList<LlmModel> models = await _modelLists.RefreshAsync(connectionId, new(connection.Version, connection.ResolvedContinuationScope),
            client, connection, ModelCatalogFamily.Chat, cancellationToken);
        ThrowIfOperationCanceled(cancellationToken);
        return models;
    }

    /// <summary>Reads passive quota evidence without a provider call, or null when none is observed for this identity.</summary>
    public async Task<LlmAccountUsageSnapshot?> GetObservedAccountUsageAsync(
        string connectionId, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        LlmAccountUsageObservations.Reservation reservation = _observations.Reserve(connectionId);
        LlmConnection connection = await ResolveConnectionAsync(connectionId, operation.Token);
        ThrowIfOperationCanceled(operation.Token);
        LlmAccountUsageSnapshot? snapshot = reservation.Get(connection);
        ThrowIfOperationCanceled(operation.Token);
        return snapshot;
    }

    public async Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        LlmConnection connection = await ResolveConnectionAsync(
            connectionId,
            cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        LlmProviderCapabilities capabilities = client.GetCapabilities(connection);
        ThrowIfOperationCanceled(cancellationToken);
        if (!capabilities.SupportsAccountUsage
            || !_accountUsageClients.TryGetValue(
                connection.Transport,
                out ILlmAccountUsageClient? usageClient))
        {
            throw new NotSupportedException(
                $"The {connection.Transport} connection does not expose account usage.");
        }

        if (connection.Transport == LlmTransport.OpenAiCodex)
        {
            connection = await RefreshConnectionAsync(connectionId, connection, client, cancellationToken);
        }
        ThrowIfOperationCanceled(cancellationToken);
        LlmAccountUsageSnapshot usage = await usageClient.GetAccountUsageAsync(connection, cancellationToken);
        ThrowIfOperationCanceled(cancellationToken);
        return usage;
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
        connection = WithContextCache(connection, client);
        LlmProviderCapabilities capabilities = FileCapabilities(client, client.GetCapabilities(connection));
        ThrowIfOperationCanceled(cancellationToken);
        return capabilities;
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
        LlmConnection connection = await ResolveConnectionAsync(connectionId, cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        connection = await AttachCatalogAsync(connectionId, connection, client, cancellationToken);
        LlmProviderCapabilities route = client.GetCapabilities(connection);
        ThrowIfOperationCanceled(cancellationToken);
        LlmProviderCapabilities model = client.GetCapabilities(connection, modelId);
        ThrowIfOperationCanceled(cancellationToken);
        LlmToolOutputProjection projection = lane == LlmToolOutputLane.Chat
            ? ToolOutputProjector.Project(output, route.RichContentSupported, model.RichContentSupported).Report
            : ToolOutputProjector.Project(output, route.Agent.RichContentSupported, model.Agent.RichContentSupported).Report;
        ThrowIfOperationCanceled(cancellationToken);
        return projection;
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
        connection = WithContextCache(connection, client);
        LlmProviderCapabilities capabilities = FileCapabilities(client, client.GetCapabilities(connection, modelId));
        ThrowIfOperationCanceled(cancellationToken);
        return capabilities;
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
        return await AttachCatalogCoreAsync(connectionId, connection, client, operation.Token).ConfigureAwait(false);
    }

    private async Task<LlmConnection> AttachCatalogCoreAsync(string connectionId, LlmConnection connection,
        ILlmClient client, CancellationToken cancellationToken)
    {
        ThrowIfOperationCanceled(cancellationToken);
        if (client is not ILlmCatalogCapabilities)
        {
            return connection;
        }
        if (client is ILlmCredentialAdmissionPolicy policy)
        {
            ThrowIfOperationCanceled(cancellationToken);
            connection = EmbeddingRequestData.Snapshot(connection);
            policy.ValidateCredential(connection);
            ThrowIfOperationCanceled(cancellationToken);
        }
        try
        {
            IReadOnlyList<LlmModel> models = await _modelLists.GetAsync(connectionId,
                new(connection.Version, connection.ResolvedContinuationScope),
                client, connection, ModelCatalogFamily.Chat, cancellationToken);
            return connection with { Catalog = models };
        }
        catch (Exception error) when (error is HttpRequestException or LlmProviderException or System.Text.Json.JsonException)
        {
            return connection;
        }
    }

    internal LlmOperationLifetime.Lease EnterManagedOpening(CancellationToken cancellationToken) =>
        _lifetime.Enter(cancellationToken);

    internal void FenceManagedOpening(CancellationToken cancellationToken) => ThrowIfOperationCanceled(cancellationToken);

    // The opener holds EnterManagedOpening through preparation and session factory callbacks.
    internal async Task<LlmConnection> PrepareManagedConnectionAsync(string connectionId, LlmConnection connection,
        ILlmClient client, Action<LlmConnection> preflight, CancellationToken cancellationToken)
    {
        ThrowIfOperationCanceled(cancellationToken);
        connection = EmbeddingRequestData.Snapshot(connection);
        preflight(connection);
        ThrowIfOperationCanceled(cancellationToken);
        connection = await RefreshConnectionAsync(connectionId, connection, client, cancellationToken).ConfigureAwait(false);
        ThrowIfOperationCanceled(cancellationToken);
        if (client is ILlmConnectionPreparation preparation)
            connection = await preparation.PrepareAsync(connection, cancellationToken).ConfigureAwait(false);
        ThrowIfOperationCanceled(cancellationToken);
        connection = await AttachCatalogCoreAsync(connectionId, connection, client, cancellationToken).ConfigureAwait(false);
        ThrowIfOperationCanceled(cancellationToken);
        preflight(connection);
        ThrowIfOperationCanceled(cancellationToken);
        return connection;
    }

    private async Task<LlmConnection> ResolveConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(Connections, connectionId, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfOperationCanceled(cancellationToken);
        RuntimeInputAdmission.Validate(connection);
        return connection;
    }

    private async Task<LlmConnection> ResolveFreshConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        LlmConnection connection = await ResolveConnectionAsync(
            connectionId,
            cancellationToken);
        ILlmClient client = ClientFor(connection.Transport);
        return await RefreshConnectionAsync(connectionId, connection, client, cancellationToken);
    }

    private Task<LlmConnection> RefreshConnectionAsync(string connectionId, LlmConnection connection,
        ILlmClient client, CancellationToken cancellationToken)
    {
        if (client is not ILlmCredentialAdmissionPolicy policy)
            return ConnectionAccess.RefreshAsync(connectionId, connection, cancellationToken);

        return RefreshValidatedAsync();

        async Task<LlmConnection> RefreshValidatedAsync()
        {
            LlmConnection refreshed = await ConnectionAccess.RefreshAsync(connectionId, connection, Validate,
                () => ThrowIfOperationCanceled(cancellationToken), cancellationToken);
            ThrowIfOperationCanceled(cancellationToken);
            return refreshed;
        }

        void Validate(LlmConnection snapshot)
        {
            ThrowIfOperationCanceled(cancellationToken);
            policy.ValidateCredential(snapshot);
            ThrowIfOperationCanceled(cancellationToken);
        }
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

        LlmConnection connection = await ResolveConnectionAsync(
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

    private async Task<LlmCredential> PollXaiAsync(LlmDeviceCode code, CancellationToken token)
    {
        while (true)
        {
            ThrowIfOperationCanceled(token);
            await XaiDevicePolling.WaitAsync(code, token).ConfigureAwait(false);
            ThrowIfOperationCanceled(token);
            try
            {
                LlmCredential credential = await XaiLogin().CompleteAsync(code.DeviceAuthId, code.UserCode, token).ConfigureAwait(false);
                ThrowIfOperationCanceled(token);
                return credential;
            }
            catch (LlmAuthPendingException pending)
            {
                ThrowIfOperationCanceled(token);
                code = XaiDevicePolling.AfterPending(code, pending);
            }
        }
    }

    private void ThrowIfOperationCanceled(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The owner fence is synchronous; linked stop-token callbacks can still be pending.
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error)
        {
            throw new OperationCanceledException("The operation was canceled by facade retirement.", error, token);
        }
    }

    private async Task SaveXaiCredentialAsync(string connectionId, string version, LlmCredential credential, CancellationToken token)
    {
        ThrowIfOperationCanceled(token);
        RuntimeInputAdmission.ValidateCredential(LlmTransport.XaiResponses, credential);
        if (!string.IsNullOrEmpty(credential.ApiKey) || !XaiAuthorization.IsValid(credential))
            throw new InvalidOperationException("Grok sign-in did not return an OAuth credential.");
        string serialized = credential.Serialize();
        ThrowIfOperationCanceled(token);
        await Connections.SaveCredentialAsync(connectionId, serialized, version, token).ConfigureAwait(false);
        ThrowIfOperationCanceled(token);
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

    private IXaiDeviceLogin XaiLogin() =>
        _authenticators.TryGetValue(LlmTransport.XaiResponses, out ILlmAuthenticator? authenticator)
            && authenticator is IXaiDeviceLogin login
                ? login
                : throw new NotSupportedException("Grok login is not registered.");

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
        Task operations = _lifetime.RetireAsync(async () =>
        {
            Task files;
            try { files = _xaiFiles is { } client ? client.DisposeAsync().AsTask() : Task.CompletedTask; }
            catch (Exception error) { files = Task.FromException(error); }
            Task outcomes;
            try { outcomes = _cleanupOutcomes.ReleaseAsync(); }
            catch (Exception error) { outcomes = Task.FromException(error); }
            Task cleanup = Task.WhenAll(files, outcomes);
            try { await cleanup.ConfigureAwait(false); }
            catch
            {
                if (cleanup.Exception is { InnerExceptions.Count: > 1 } failures) throw failures;
                throw;
            }
        });
        Task credentials = _ownsConnectionAccess ? ConnectionAccess.DisposeAsync().AsTask() : Task.CompletedTask;
        Task catalogs = _ownsModelLists ? _modelLists.DisposeAsync().AsTask() : Task.CompletedTask;
        Task retirement = Task.WhenAll(operations, credentials, catalogs);
        try { await retirement.ConfigureAwait(false); }
        catch
        {
            if (retirement.Exception is { InnerExceptions.Count: > 1 } failures) throw failures;
            throw;
        }
        finally
        {
            _cacheBindings.Clear();
            _clients.Clear();
            _authenticators.Clear();
            _accountUsageClients.Clear();
            _setupEnvironments.Clear();
            _connections = null;
            _connectionAccess = null;
            _xaiFiles = null;
            if (_ownsObservations) { _observations.Dispose(); }
        }
    }

    /// <summary>Synchronously retires the facade. Use asynchronous disposal while provider work is active.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
