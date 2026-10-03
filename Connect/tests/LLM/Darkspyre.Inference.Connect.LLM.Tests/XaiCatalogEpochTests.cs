using System.Collections.Concurrent;
using System.Net;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiCatalogEpochTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ActualCredentialRefreshWithOriginalVersionCannotReusePreviousCatalog()
    {
        var source = new Source(Connection("old-token"));
        var authenticator = new Authenticator();
        using var handler = new CatalogHandler();
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        await using var access = new LlmConnectionAccess(source, [authenticator], [client]);
        await using var cache = new LlmModelListCache();
        await using var service = new LlmProviderService(source, [client], [authenticator], modelLists: cache,
            connectionAccess: access);

        IReadOnlyList<LlmModel> original = await service.ListModelsAsync("selected");
        authenticator.Rotate = true;
        IReadOnlyList<LlmModel> refreshed = await service.ListModelsAsync("selected");

        Assert.Equal(2, source.Reads);
        Assert.Equal(2, authenticator.Calls);
        Assert.Equal("v1", Assert.Single(source.ExpectedVersions));
        Assert.Equal("saved-v2", source.Current.Version);
        Assert.Equal("fresh-token", LlmCredential.Parse(source.Current.Credential)!.OAuth!.AccessToken);
        Assert.Equal("old-model", Assert.Single(original).Id);
        Assert.Equal("fresh-model", Assert.Single(refreshed).Id);
        Assert.Equal(2, handler.Requests.Count(request => request.Authorization == "Bearer old-token"));
        Assert.Equal(2, handler.Requests.Count(request => request.Authorization == "Bearer fresh-token"));
    }

    [Fact]
    public async Task ExplicitEpochReplacementFencesTheOldListingsNextEndpoint()
    {
        var source = new Source(Connection("old-token", "old"));
        using var handler = new CatalogHandler { HoldOldGeneric = true };
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        await using var cache = new LlmModelListCache();
        var service = new LlmProviderService(source, [client], [], modelLists: cache);
        Task<IReadOnlyList<LlmModel>>? original = null;
        Task<IReadOnlyList<LlmModel>>? replacement = null;
        try
        {
            original = service.ListModelsAsync("selected");
            await handler.OldGenericEntered.Task.WaitAsync(Bound);
            source.Current = Connection("old-token", "current");
            replacement = service.RefreshModelsAsync("selected");
            Assert.Equal("current-model", Assert.Single(await replacement.WaitAsync(Bound)).Id);
            handler.ReleaseOldGeneric.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original.WaitAsync(Bound));

            Assert.Equal(1, handler.Requests.Count(request => request.Path == "/v1/models" && request.Phase == "old"));
            Assert.Equal(1, handler.Requests.Count(request => request.Path == "/v1/language-models" && request.Phase == "current"));
            Assert.Equal(0, handler.Requests.Count(request => request.Path == "/v1/language-models" && request.Phase == "old"));
        }
        finally
        {
            Task serviceRetirement = service.DisposeAsync().AsTask();
            Task cacheRetirement = cache.DisposeAsync().AsTask();
            handler.ReleaseOldGeneric.TrySetResult();
            try
            {
                if (original is not null) await DrainAsync(original);
                if (replacement is not null) await DrainAsync(replacement);
            }
            finally { await Task.WhenAll(serviceRetirement, cacheRetirement).WaitAsync(Bound); }
        }
    }

    [Fact]
    public async Task FacadeRetirementDoesNotKillTheInjectedCachesAdmittedXaiProducer()
    {
        var source = new Source(Connection("old-token", "old"));
        using var handler = new CatalogHandler { HoldOldGeneric = true };
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var cache = new LlmModelListCache();
        var originalService = new LlmProviderService(source, [client], [], modelLists: cache);
        LlmProviderService? secondService = null;
        Task<IReadOnlyList<LlmModel>>? original = null;
        Task<IReadOnlyList<LlmModel>>? resumed = null;
        try
        {
            original = originalService.ListModelsAsync("selected");
            await handler.OldGenericEntered.Task.WaitAsync(Bound);
            await originalService.DisposeAsync().AsTask().WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original.WaitAsync(Bound));
            Assert.Single(handler.Requests);
            secondService = new(source, [client], [], modelLists: cache);
            resumed = secondService.ListModelsAsync("selected");
            handler.ReleaseOldGeneric.TrySetResult();
            IReadOnlyList<LlmModel> models = await resumed.WaitAsync(Bound);
            Assert.Equal("old-model", Assert.Single(models).Id);
            Assert.Same(models, await secondService.ListModelsAsync("selected"));
            Assert.Equal(1, handler.Requests.Count(request => request.Path == "/v1/models"));
            Assert.Equal(1, handler.Requests.Count(request => request.Path == "/v1/language-models"));
        }
        finally
        {
            Task first = originalService.DisposeAsync().AsTask();
            Task second = secondService?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            Task retirement = cache.DisposeAsync().AsTask();
            handler.ReleaseOldGeneric.TrySetResult();
            try
            {
                if (original is not null) await DrainAsync(original);
                if (resumed is not null) await DrainAsync(resumed);
            }
            finally { await Task.WhenAll(first, second, retirement).WaitAsync(Bound); }
        }
    }

    private static async Task DrainAsync(Task task)
    {
        try { await task.WaitAsync(Bound); }
        catch (OperationCanceledException) { }
    }

    private static LlmConnection Connection(string token, string? phase = null) => new()
    {
        Version = "v1",
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = token, RefreshToken = "refresh-token" } }.Serialize(),
        AdditionalHeaders = phase is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["X-Catalog-Phase"] = phase }
    };

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public LlmConnection Current { get; set; } = connection;
        public int Reads { get; private set; }
        public List<string> ExpectedVersions { get; } = [];

        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("selected", connectionId);
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult<LlmConnection?>(Current);
        }

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("selected", connectionId);
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Current.Version, expectedVersion);
            ExpectedVersions.Add(expectedVersion);
            Current = Current with { Credential = credentialJson, Version = "saved-v2" };
            return Task.CompletedTask;
        }
    }

    private sealed class Authenticator : ILlmAuthenticator
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
        public bool HoldOldGeneric { get; init; }
        public TaskCompletionSource OldGenericEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOldGeneric { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(string Path, string Authorization, string? Phase)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string authorization = request.Headers.Authorization!.ToString();
            string? phase = request.Headers.TryGetValues("X-Catalog-Phase", out IEnumerable<string>? phases) ? phases.Single() : null;
            Requests.Enqueue((path, authorization, phase));
            if (HoldOldGeneric && path == "/v1/models" && phase == "old")
            {
                OldGenericEntered.TrySetResult();
                await ReleaseOldGeneric.Task;
            }
            string id = phase == "current" ? "current-model" : authorization == "Bearer fresh-token" ? "fresh-model" : "old-model";
            string array = path == "/v1/models" ? "data" : "models";
            Assert.True(path is "/v1/models" or "/v1/language-models", path);
            return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"{array}\":[{{\"id\":\"{id}\"}}]}}") };
        }
    }
}
