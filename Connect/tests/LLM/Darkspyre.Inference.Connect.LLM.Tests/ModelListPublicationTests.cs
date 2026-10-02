namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ModelListPublicationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementRefusesLatePublicationAndKeepsTheCurrentListing(bool refresh)
    {
        await using var cache = new LlmModelListCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<LlmModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken providerToken = default;
        Task<IReadOnlyList<LlmModel>> old = cache.GetAsync("connection", "v1", token =>
        {
            providerToken = token;
            entered.SetResult();
            return release.Task;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(Bound);
        string version = refresh ? "v1" : "v2";
        Task<IReadOnlyList<LlmModel>> current = refresh
            ? cache.RefreshAsync("connection", version, _ => Task.FromResult<IReadOnlyList<LlmModel>>([new("current", "Current")]), CancellationToken.None)
            : cache.GetAsync("connection", version, _ => Task.FromResult<IReadOnlyList<LlmModel>>([new("current", "Current")]), CancellationToken.None);
        try
        {
            IReadOnlyList<LlmModel> models = await current.WaitAsync(Bound);
            Assert.Equal("current", Assert.Single(models).Id);
            Assert.False(providerToken.IsCancellationRequested);
            release.SetResult([new("stale", "Stale")]);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.WaitAsync(Bound));
            Assert.Same(models, await cache.GetAsync("connection", version,
                _ => throw new InvalidOperationException("The current listing must remain cached."), CancellationToken.None));
        }
        finally { release.TrySetResult([]); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledAdmissionDoesNotStartOrReplaceAListing(bool refresh)
    {
        await using var cache = new LlmModelListCache();
        IReadOnlyList<LlmModel> current = await cache.GetAsync("connection", "v1",
            _ => Task.FromResult<IReadOnlyList<LlmModel>>([new("current", "Current")]), CancellationToken.None);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        int calls = 0;
        Task<IReadOnlyList<LlmModel>> List(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<LlmModel>>([new("unexpected", "Unexpected")]);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (refresh) await cache.RefreshAsync("connection", "v1", List, caller.Token);
            else await cache.GetAsync("connection", "v2", List, caller.Token);
        });
        await cache.DisposeAsync();
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal("current", Assert.Single(current).Id);
    }

    [Fact]
    public async Task ReplacementStillDrainsAnIgnoringProviderDuringRetirement()
    {
        await using var cache = new LlmModelListCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<LlmModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<LlmModel>> old = cache.GetAsync("connection", "v1", _ =>
        {
            entered.SetResult();
            return release.Task;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(Bound);
        await cache.GetAsync("connection", "v2", _ => Task.FromResult<IReadOnlyList<LlmModel>>([]), CancellationToken.None);
        Task retirement = cache.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            release.SetResult([new("stale", "Stale")]);
            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.WaitAsync(Bound));
        }
        finally { release.TrySetResult([]); }
    }
}
