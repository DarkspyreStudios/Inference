using System.Collections.Concurrent;
using System.Net;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiCatalogBindingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task BoundFamilyAndChatWaitersUseOneExplicitSelectionAndOneAdmittedSnapshot()
    {
        var source = new Source();
        var auth = new Authenticator();
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        await using var access = new LlmConnectionAccess(source, [auth], [client]);
        await using var cache = new LlmModelListCache();
        await using var service = new LlmProviderService(source, [client], [auth], modelLists: cache, connectionAccess: access);
        var binding = new XaiFamilyBinding(new("selected-key"), access, service);
        XaiFamilyBinding admitted = await binding.AcquireBoundAsync(_ => { });
        LlmConnection snapshot = await admitted.AcquireAsync();
        int callerFences = 0;
        foreach (ModelCatalogFamily family in Enum.GetValues<ModelCatalogFamily>())
            Assert.Single(await admitted.GetModelsAsync(snapshot, family, () => callerFences++, CancellationToken.None));
        IReadOnlyList<LlmModel> chat = await service.ListModelsAsync("selected-key");
        Assert.Same(chat, await admitted.GetModelsAsync(snapshot, ModelCatalogFamily.Chat, () => callerFences++, CancellationToken.None));
        Assert.Equal(2, source.Reads);
        Assert.Equal(2, auth.Calls);
        Assert.Equal(5, handler.Paths.Count);
        Assert.Equal(1, handler.Paths.Count(path => path == "/v1/models"));
        Assert.True(callerFences > 0);

        source.Current = Connection with { Credential = new LlmCredential { OAuth = new() { AccessToken = "replacement" } }.Serialize() };
        Assert.Same(chat, await admitted.GetModelsAsync(snapshot, ModelCatalogFamily.Chat, () => callerFences++, CancellationToken.None));
        Assert.Equal(2, source.Reads);
        Assert.Equal(2, auth.Calls);
        Assert.Equal(5, handler.Paths.Count);
        Assert.All(handler.Authorization, value => Assert.Equal("Bearer admitted", value));
    }

    [Fact]
    public async Task BindingWithoutFacadeRefusesBeforeResolutionOrHttp()
    {
        var source = new Source();
        await using var access = new LlmConnectionAccess(source, []);
        var binding = new XaiFamilyBinding(new("selected-key"), access);
        int fences = 0;
        await Assert.ThrowsAsync<NotSupportedException>(() => binding.GetModelsAsync(Connection, ModelCatalogFamily.Image,
            () => fences++, CancellationToken.None));
        Assert.Equal(0, source.Reads);
        Assert.Equal(0, fences);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task CachedSnapshotBoundaryRejectsInvalidVersionsBeforeCallbacks(string? version)
    {
        var source = new Source();
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], []);
        int fences = 0;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.GetXaiCatalogSnapshotAsync("selected-key",
            Connection with { Version = version! }, ModelCatalogFamily.Image, () => fences++, CancellationToken.None));
        Assert.Equal(0, source.Reads);
        Assert.Equal(0, fences);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task RetiringTheOriginBindingFacadeDoesNotRetireSharedFamilyProduction()
    {
        var source = new Source();
        using var handler = new Handler { HoldGeneric = true };
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        await using var access = new LlmConnectionAccess(source, [], [client]);
        var cache = new LlmModelListCache();
        var first = new LlmProviderService(source, [client], [], modelLists: cache, connectionAccess: access);
        LlmProviderService? second = null;
        Task<IReadOnlyList<LlmModel>>? canceled = null;
        Task<IReadOnlyList<LlmModel>>? survivor = null;
        Task<IReadOnlyList<LlmModel>>? retainedImage = null;
        try
        {
            var binding = new XaiFamilyBinding(new("selected-key"), access, first);
            XaiFamilyBinding admitted = await binding.AcquireBoundAsync(_ => { });
            LlmConnection snapshot = await admitted.AcquireAsync();
            canceled = admitted.GetModelsAsync(snapshot, ModelCatalogFamily.Image, () => { }, CancellationToken.None);
            await handler.Entered.Task.WaitAsync(Bound);
            await first.DisposeAsync().AsTask().WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(Bound));
            Assert.Single(handler.Paths);
            second = new(source, [client], [], modelLists: cache, connectionAccess: access);
            var replacement = new XaiFamilyBinding(new("selected-key"), access, second);
            survivor = replacement.GetModelsAsync(snapshot, ModelCatalogFamily.Video, () => { }, CancellationToken.None);
            retainedImage = replacement.GetModelsAsync(snapshot, ModelCatalogFamily.Image, () => { }, CancellationToken.None);
            handler.Release.TrySetResult();
            Assert.Equal("video", Assert.Single(await survivor.WaitAsync(Bound)).Id);
            Assert.Equal("image", Assert.Single(await retainedImage.WaitAsync(Bound)).Id);
            Assert.Equal(1, source.Reads);
            Assert.Equal(1, handler.Paths.Count(path => path == "/v1/models"));
            Assert.Equal(1, handler.Paths.Count(path => path == "/v1/image-generation-models"));
            Assert.Equal(1, handler.Paths.Count(path => path == "/v1/video-generation-models"));
        }
        finally
        {
            Task firstRetirement = first.DisposeAsync().AsTask();
            Task secondRetirement = second?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            Task retirement = cache.DisposeAsync().AsTask();
            handler.Release.TrySetResult();
            try
            {
                if (canceled is not null) await DrainAsync(canceled);
                if (survivor is not null) await DrainAsync(survivor);
                if (retainedImage is not null) await DrainAsync(retainedImage);
            }
            finally { await Task.WhenAll(firstRetirement, secondRetirement, retirement).WaitAsync(Bound); }
        }
    }

    private static async Task DrainAsync(Task task) { try { await task.WaitAsync(Bound); } catch (OperationCanceledException) { } }
    private static LlmConnection Connection => new()
    {
        Version = "v1",
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "admitted" } }.Serialize()
    };
    private sealed class Source : ILlmConnectionSource
    {
        internal LlmConnection Current { get; set; } = Connection;
        internal int Reads;
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("selected-key", connectionId);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Reads);
            return Task.FromResult<LlmConnection?>(Current);
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This unchanged credential cannot be saved.");
    }
    private sealed class Authenticator : ILlmAuthenticator
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        internal int Calls;
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref Calls); return Task.FromResult(credential); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal bool HoldGeneric { get; init; }
        internal ConcurrentQueue<string> Paths { get; } = new();
        internal ConcurrentQueue<string> Authorization { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Paths.Enqueue(path);
            Authorization.Enqueue(request.Headers.Authorization!.ToString());
            if (HoldGeneric && path == "/v1/models") { Entered.TrySetResult(); await Release.Task; }
            string data = path switch
            {
                "/v1/models" => "\"data\":[{\"id\":\"grok-4.7\"},{\"id\":\"image\"},{\"id\":\"video\"},{\"id\":\"embedding\"}]",
                "/v1/language-models" => "\"models\":[{\"id\":\"grok-4.7\"}]",
                "/v1/image-generation-models" => "\"models\":[{\"id\":\"image\"}]",
                "/v1/video-generation-models" => "\"models\":[{\"id\":\"video\"}]",
                "/v1/embedding-models" => "\"models\":[{\"id\":\"embedding\"}]",
                _ => throw new InvalidOperationException("Unexpected family route.")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent("{" + data + "}") };
        }
    }
}
