using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LlmLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CacheRetirementCancelsReplacedAndRefreshedListings()
    {
        await using var cache = new LlmModelListCache();
        var started = new TaskCompletionSource[3];
        var tokens = new CancellationToken[3];
        var requests = new Task<IReadOnlyList<LlmModel>>[3];
        for (int index = 0; index < 3; index++)
        {
            int slot = index;
            started[slot] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<IReadOnlyList<LlmModel>> List(CancellationToken token)
            {
                tokens[slot] = token;
                started[slot].SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [];
            }
            requests[slot] = slot == 2
                ? cache.RefreshAsync("connection", new("v2", null), List, CancellationToken.None)
                : cache.GetAsync("connection", new($"v{slot + 1}", null), List, CancellationToken.None);
            await started[slot].Task.WaitAsync(Bound);
        }

        await cache.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
        foreach (Task<IReadOnlyList<LlmModel>> request in requests)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.GetAsync("connection", new("v3", null), _ => Task.FromResult<IReadOnlyList<LlmModel>>([]), CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.RefreshAsync("connection", new("v3", null), _ => Task.FromResult<IReadOnlyList<LlmModel>>([]), CancellationToken.None));
    }

    [Fact]
    public async Task CacheRetirementWaitsForAnIgnoringProviderAndDoesNotPublishItsLateResult()
    {
        await using var cache = new LlmModelListCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<LlmModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<LlmModel>> request = cache.GetAsync("connection", new("v1", null), async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() => stopped.TrySetResult());
            started.SetResult();
            return await release.Task;
        }, CancellationToken.None);
        await started.Task.WaitAsync(Bound);
        Task retirement = cache.DisposeAsync().AsTask();
        try
        {
            await stopped.Task.WaitAsync(Bound);
            Assert.False(retirement.IsCompleted);
            release.SetResult([new("late", "Late")]);
            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        finally { release.TrySetResult([]); }
    }

    [Fact]
    public async Task AbandonedProviderFailureDoesNotFailOwnerRetirement()
    {
        await using var cache = new LlmModelListCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        Task<IReadOnlyList<LlmModel>> waiting = cache.GetAsync("connection", new("v1", null), async _ =>
        {
            started.SetResult();
            await release.Task;
            throw new LlmProviderException("controlled failure", LlmTransport.OpenAiCompatibleChat);
        }, caller.Token);
        await started.Task.WaitAsync(Bound);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Task retirement = cache.DisposeAsync().AsTask();
        release.SetResult();

        await retirement.WaitAsync(Bound);
        Assert.True(retirement.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FacadeRetiresItsPrivateCacheButDoesNotDisposeTheBorrowedClient()
    {
        using var client = new BlockingClient();
        await using var facade = new LlmProviderService(new Source(), [client]);
        Task<IReadOnlyList<LlmModel>> request = facade.ListModelsAsync("connection");
        await client.Started.Task.WaitAsync(Bound);

        await facade.DisposeAsync().AsTask().WaitAsync(Bound);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(client.ModelToken.IsCancellationRequested);
        Assert.False(client.Disposed);
        Assert.Throws<ObjectDisposedException>(() => facade.Bind("connection"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.GetCapabilitiesAsync("connection"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.IsAuthenticatedAsync("connection"));
    }

    [Fact]
    public async Task DisposingOneFacadeDoesNotCancelAnInjectedSharedCacheListing()
    {
        await using var cache = new LlmModelListCache();
        using var client = new BlockingClient { IgnoreModelCancellation = true };
        await using var first = new LlmProviderService(new Source(), [client], [], modelLists: cache);
        await using var second = new LlmProviderService(new Source(), [client], [], modelLists: cache);
        Task<IReadOnlyList<LlmModel>> abandoned = first.ListModelsAsync("connection");
        await client.Started.Task.WaitAsync(Bound);
        Task<IReadOnlyList<LlmModel>> waiting = second.ListModelsAsync("connection");
        try
        {
            await first.DisposeAsync().AsTask().WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
            Assert.False(client.ModelToken.IsCancellationRequested);
            Assert.False(client.Disposed);
            client.Release.TrySetResult();
            Assert.Single(await waiting.WaitAsync(Bound));
            Assert.Equal(1, client.Calls);
        }
        finally { client.Release.TrySetResult(); }
    }

    [Fact]
    public async Task FacadeRetirementWaitsForActiveStreamsAndIsSharedByConcurrentDisposers()
    {
        using var client = new BlockingClient();
        await using var facade = new LlmProviderService(new Source(), [client]);
        Task request = ReadAsync(facade.StreamAsync("connection", new() { ModelId = "model", Messages = [] }));
        await client.StreamStarted.Task.WaitAsync(Bound);
        Task retirement = facade.DisposeAsync().AsTask();
        try
        {
            await client.StreamStopped.Task.WaitAsync(Bound);
            Assert.Same(retirement, facade.DisposeAsync().AsTask());
            Assert.False(retirement.IsCompleted);
            client.Release.TrySetResult();
            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.False(client.Disposed);
        }
        finally { client.Release.TrySetResult(); }
    }

    [Fact]
    public async Task RetiredFacadeRejectsAnEnumerableCreatedBeforeRetirement()
    {
        using var client = new BlockingClient();
        await using var facade = new LlmProviderService(new Source(), [client]);
        IAsyncEnumerable<LlmStreamEvent> stream = facade.StreamAsync("connection", new() { ModelId = "model", Messages = [] });

        await facade.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => ReadAsync(stream));
        Assert.False(client.StreamStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task CancellationCallbackFailureStillWaitsForOwnedOperations()
    {
        var lifetime = new LlmOperationLifetime();
        using LlmOperationLifetime.Lease lease = lifetime.Enter(CancellationToken.None);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = lease.Token.Register(() =>
        {
            stopped.TrySetResult();
            throw new InvalidOperationException("controlled cancellation callback failure");
        });
        Task retirement = lifetime.RetireAsync();
        await stopped.Task.WaitAsync(Bound);
        Assert.False(retirement.IsCompleted);

        lease.Dispose();

        await Assert.ThrowsAsync<AggregateException>(() => retirement.WaitAsync(Bound));
        Assert.Throws<ObjectDisposedException>(() => lifetime.Enter(CancellationToken.None));
    }

    [Fact]
    public async Task RetirementPreservesCancellationAndReleaseFailuresForEveryDisposer()
    {
        var lifetime = new LlmOperationLifetime();
        using LlmOperationLifetime.Lease lease = lifetime.Enter(CancellationToken.None);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationFailure = new InvalidOperationException("controlled cancellation failure");
        var releaseFailure = new IOException("controlled release failure");
        using CancellationTokenRegistration registration = lease.Token.Register(() =>
        {
            stopped.TrySetResult();
            throw cancellationFailure;
        });
        Task retirement = lifetime.RetireAsync(() => Task.FromException(releaseFailure));
        await stopped.Task.WaitAsync(Bound);
        Assert.False(retirement.IsCompleted);
        Assert.Same(retirement, lifetime.RetireAsync());
        lease.Dispose();

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => retirement.WaitAsync(Bound));
        Assert.Contains(cancellationFailure, failure.Flatten().InnerExceptions);
        Assert.Contains(releaseFailure, failure.Flatten().InnerExceptions);
        Assert.Same(retirement, lifetime.RetireAsync());
    }

    private static async Task ReadAsync(IAsyncEnumerable<LlmStreamEvent> stream)
    {
        await foreach (LlmStreamEvent _ in stream) { }
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new() { Version = "v1", Transport = LlmTransport.OpenAiCompatibleChat, Endpoint = "https://provider.test/v1" });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingClient : ILlmClient, IDisposable
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StreamStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken ModelToken { get; private set; }
        internal bool IgnoreModelCancellation { get; init; }
        internal bool Disposed { get; private set; }
        internal int Calls { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiCompatible;

        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) =>
            new() { SupportsStreaming = true, SupportsModelListing = true };

        public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        {
            Calls++;
            ModelToken = cancellationToken;
            Started.TrySetResult();
            if (IgnoreModelCancellation) await Release.Task;
            else await Release.Task.WaitAsync(cancellationToken);
            return [new("model", "Model")];
        }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() => StreamStopped.TrySetResult());
            StreamStarted.TrySetResult();
            await Release.Task;
            yield break;
        }

        public void Dispose() => Disposed = true;
    }
}
