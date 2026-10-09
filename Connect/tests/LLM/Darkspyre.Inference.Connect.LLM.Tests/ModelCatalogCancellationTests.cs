using Darkspyre.Inference.Common;
using System.Collections.Concurrent;
using System.Net;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ModelCatalogCancellationTests
{
    [Fact]
    public async Task CredentialRefreshWithOriginalVersionReplacesThePreviousCatalog()
    {
        var options = new XaiConnectionOptions();
        var source = new RefreshSource(new()
        {
            Version = "version-1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = options.GetEndpoint(),
            Xai = options,
            Credential = new LlmCredential { OAuth = new() { AccessToken = "old-token", RefreshToken = "refresh-token" } }.Serialize()
        });
        var authenticator = new RefreshAuthenticator();
        using var handler = new CatalogHandler();
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        await using var access = new LlmConnectionAccess(source, [authenticator], [client]);
        await using var cache = new LlmModelListCache();
        await using var service = new LlmProviderService(source, [client], [authenticator], modelLists: cache,
            connectionAccess: access);

        IReadOnlyList<LlmModel> original = await service.ListModelsAsync("connection");
        authenticator.Rotate = true;
        IReadOnlyList<LlmModel> refreshed = await service.ListModelsAsync("connection");

        Assert.Equal("old-model", Assert.Single(original).Id);
        Assert.Equal("fresh-model", Assert.Single(refreshed).Id);
        Assert.Equal("version-1", Assert.Single(source.ExpectedVersions));
        Assert.Equal("version-2", source.Current.Version);
        Assert.Equal("fresh-token", LlmCredential.Parse(source.Current.Credential)!.OAuth!.AccessToken);
        Assert.Equal(2, source.Reads);
        Assert.Equal(2, authenticator.Calls);
        Assert.Equal(2, handler.Authorizations.Count(value => value == "Bearer old-token"));
        Assert.Equal(2, handler.Authorizations.Count(value => value == "Bearer fresh-token"));
    }

    [Fact]
    public async Task CancelingOneWaiterDoesNotCancelTheSharedListingOrDiscardItsFacts()
    {
        await using var cache = new LlmModelListCache();
        var provider = new ListingProvider();
        var connection = new LlmConnection
        {
            Version = "version-1",
            Transport = provider.Transport,
            Endpoint = "https://provider.test/v1"
        };
        var scope = new LlmModelListCache.Scope(connection.Version, null);
        using var canceledWaiter = new CancellationTokenSource();

        Task<IReadOnlyList<LlmModel>> first = cache.GetAsync("connection", scope, provider, connection,
            ModelCatalogFamily.Chat, canceledWaiter.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<IReadOnlyList<LlmModel>> second = cache.GetAsync("connection", scope, provider, connection,
            ModelCatalogFamily.Chat, CancellationToken.None);

        canceledWaiter.Cancel();
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(cancellation.CancellationToken.IsCancellationRequested);
        provider.Result.SetResult([new("first", "First") { MaxContextTokens = 8192 }, new("second", "Second")]);

        IReadOnlyList<LlmModel> models = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["first", "second"], models.Select(model => model.Id));
        Assert.Equal(8192, models[0].MaxContextTokens);
        Assert.Null(models[1].MaxContextTokens);
        IReadOnlyList<LlmModel> kept = await cache.GetAsync("connection", scope, provider, connection,
            ModelCatalogFamily.Chat, CancellationToken.None);
        Assert.Same(models, kept);
        Assert.Equal(1, provider.Listings);
    }

    private sealed class RefreshSource(LlmConnection connection) : ILlmConnectionSource
    {
        public LlmConnection Current { get; private set; } = connection;
        public int Reads { get; private set; }
        public List<string> ExpectedVersions { get; } = [];

        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("connection", connectionId);
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult<LlmConnection?>(Current);
        }

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("connection", connectionId);
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Current.Version, expectedVersion);
            ExpectedVersions.Add(expectedVersion);
            Current = Current with { Credential = credentialJson, Version = "version-2" };
            return Task.CompletedTask;
        }
    }

    private sealed class RefreshAuthenticator : ILlmAuthenticator
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public bool Rotate { get; set; }
        public int Calls { get; private set; }

        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Rotate ? credential with { OAuth = credential.OAuth! with { AccessToken = "fresh-token" } } : credential);
        }
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Authorizations { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string authorization = request.Headers.Authorization!.ToString();
            Authorizations.Enqueue(authorization);
            string path = request.RequestUri!.AbsolutePath;
            Assert.True(path is "/v1/models" or "/v1/language-models", path);
            string array = path == "/v1/models" ? "data" : "models";
            string id = authorization == "Bearer fresh-token" ? "fresh-model" : "old-model";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"{array}\":[{{\"id\":\"{id}\"}}]}}")
            });
        }
    }

    private sealed class ListingProvider : ILlmClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<LlmModel>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Listings { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;
        public LlmProviderDefinition ProviderDefinition => throw new NotSupportedException();
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) =>
            new() { SupportsModelListing = InferenceSupport.Supported };
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection,
            CancellationToken cancellationToken = default)
        {
            Listings++;
            Entered.SetResult();
            return await Result.Task.WaitAsync(cancellationToken);
        }
    }
}
