using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ModelListPublicationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(null, "")]
    [InlineData("", null)]
    [InlineData("scope", "Scope")]
    public async Task ScopeComponentsUseExactNullableOrdinalEquality(string? originalScope, string? replacementScope)
    {
        await using var cache = new LlmModelListCache();
        var firstScope = new LlmModelListCache.Scope("version", originalScope);
        var currentScope = new LlmModelListCache.Scope("version", replacementScope);
        IReadOnlyList<LlmModel> original = await cache.GetAsync("connection", firstScope,
            new CacheListingClient(_ => Task.FromResult<IReadOnlyList<LlmModel>>([new("original", "Original")])),
            CacheListingClient.Connection("version"), ModelCatalogFamily.Chat, CancellationToken.None);
        IReadOnlyList<LlmModel> current = await cache.GetAsync("connection", currentScope,
            new CacheListingClient(_ => Task.FromResult<IReadOnlyList<LlmModel>>([new("current", "Current")])),
            CacheListingClient.Connection("version"), ModelCatalogFamily.Chat, CancellationToken.None);

        Assert.NotSame(original, current);
        Assert.Equal("current", Assert.Single(current).Id);
        Assert.Same(current, await cache.GetAsync("connection", new("version", replacementScope),
            new CacheListingClient(_ => throw new InvalidOperationException("An equal tuple must reuse the published snapshot.")),
            CacheListingClient.Connection("version"), ModelCatalogFamily.Chat, CancellationToken.None));
    }

    [Fact]
    public async Task PublicationOwnsTheListAndAllMutableCapabilitySets()
    {
        await using var cache = new LlmModelListCache();
        var levels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.Low };
        var choices = new HashSet<LlmToolChoiceMode> { LlmToolChoiceMode.Auto };
        var policies = new HashSet<LlmAgentRuntimePolicyKind> { LlmAgentRuntimePolicyKind.HostControlled };
        var retentions = new HashSet<LlmPromptCacheRetention> { LlmPromptCacheRetention.Default };
        var source = new List<LlmModel>
        {
            new("model", "Model", Capabilities: new()
            {
                ReasoningLevels = levels,
                Tools = new() { ChoiceModes = choices },
                Agent = new() { SupportedRuntimePolicies = policies },
                PromptCache = new() { Retentions = retentions }
            })
        };

        IReadOnlyList<LlmModel> published = await cache.GetAsync("connection", new("v1", null),
            new CacheListingClient(_ => Task.FromResult<IReadOnlyList<LlmModel>>(source)),
            CacheListingClient.Connection("v1"), ModelCatalogFamily.Chat, CancellationToken.None);
        source.Clear();
        levels.Clear();
        choices.Clear();
        policies.Clear();
        retentions.Clear();

        LlmModel model = Assert.Single(published);
        Assert.Equal(LlmReasoningLevel.Low, Assert.Single(model.Capabilities!.ReasoningLevels));
        Assert.Equal(LlmToolChoiceMode.Auto, Assert.Single(model.Capabilities.Tools.ChoiceModes));
        Assert.Equal(LlmAgentRuntimePolicyKind.HostControlled, Assert.Single(model.Capabilities.Agent.SupportedRuntimePolicies));
        Assert.Equal(LlmPromptCacheRetention.Default, Assert.Single(model.Capabilities.PromptCache.Retentions));
        Assert.True(Assert.IsAssignableFrom<IList<LlmModel>>(published).IsReadOnly);
        Assert.Same(published, await cache.GetAsync("connection", new("v1", null),
            new CacheListingClient(_ => throw new InvalidOperationException("The published snapshot must remain cached.")),
            CacheListingClient.Connection("v1"), ModelCatalogFamily.Chat, CancellationToken.None));
    }

    [Fact]
    public async Task PublicationOwnsTheProviderCatalogJson()
    {
        await using var cache = new LlmModelListCache();
        JsonDocument document = JsonDocument.Parse("{\"aliases\":[\"actual-alias\"]}");
        IReadOnlyList<LlmModel> published;
        try
        {
            published = await cache.GetAsync("connection", new("v1", null),
                new CacheListingClient(_ => Task.FromResult<IReadOnlyList<LlmModel>>([new("model", "Model") { CatalogEntry = document.RootElement }])),
                CacheListingClient.Connection("v1"), ModelCatalogFamily.Chat, CancellationToken.None);
        }
        finally { document.Dispose(); }

        Assert.Equal("actual-alias", Assert.Single(published).CatalogEntry!.Value.GetProperty("aliases")[0].GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementRefusesLatePublicationAndKeepsTheCurrentListing(bool refresh)
    {
        await using var cache = new LlmModelListCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<LlmModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken providerToken = default;
        Task<IReadOnlyList<LlmModel>> old = cache.GetAsync("connection", new("v1", null), new CacheListingClient(token =>
        {
            providerToken = token;
            entered.SetResult();
            return release.Task;
        }), CacheListingClient.Connection("v1"), ModelCatalogFamily.Chat, CancellationToken.None);
        await entered.Task.WaitAsync(Bound);
        string version = refresh ? "v1" : "v2";
        Task<IReadOnlyList<LlmModel>> current = refresh
            ? cache.RefreshAsync("connection", new(version, null), new CacheListingClient(_ => Task.FromResult<IReadOnlyList<LlmModel>>([new("current", "Current")])), CacheListingClient.Connection(version), ModelCatalogFamily.Chat, CancellationToken.None)
            : cache.GetAsync("connection", new(version, null), new CacheListingClient(_ => Task.FromResult<IReadOnlyList<LlmModel>>([new("current", "Current")])), CacheListingClient.Connection(version), ModelCatalogFamily.Chat, CancellationToken.None);
        try
        {
            IReadOnlyList<LlmModel> models = await current.WaitAsync(Bound);
            Assert.Equal("current", Assert.Single(models).Id);
            Assert.False(providerToken.IsCancellationRequested);
            release.SetResult([new("stale", "Stale")]);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.WaitAsync(Bound));
            Assert.Same(models, await cache.GetAsync("connection", new(version, null),
                new CacheListingClient(_ => throw new InvalidOperationException("The current listing must remain cached.")),
                CacheListingClient.Connection(version), ModelCatalogFamily.Chat, CancellationToken.None));
        }
        finally { release.TrySetResult([]); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledAdmissionDoesNotStartOrReplaceAListing(bool refresh)
    {
        await using var cache = new LlmModelListCache();
        IReadOnlyList<LlmModel> current = await cache.GetAsync("connection", new("v1", null),
            new CacheListingClient(_ => Task.FromResult<IReadOnlyList<LlmModel>>([new("current", "Current")])),
            CacheListingClient.Connection("v1"), ModelCatalogFamily.Chat, CancellationToken.None);
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
            if (refresh) await cache.RefreshAsync("connection", new("v1", null), new CacheListingClient(List), CacheListingClient.Connection("v1"), ModelCatalogFamily.Chat, caller.Token);
            else await cache.GetAsync("connection", new("v2", null), new CacheListingClient(List), CacheListingClient.Connection("v2"), ModelCatalogFamily.Chat, caller.Token);
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
        Task<IReadOnlyList<LlmModel>> old = cache.GetAsync("connection", new("v1", null), new CacheListingClient(_ =>
        {
            entered.SetResult();
            return release.Task;
        }), CacheListingClient.Connection("v1"), ModelCatalogFamily.Chat, CancellationToken.None);
        await entered.Task.WaitAsync(Bound);
        await cache.GetAsync("connection", new("v2", null), new CacheListingClient(_ => Task.FromResult<IReadOnlyList<LlmModel>>([])), CacheListingClient.Connection("v2"), ModelCatalogFamily.Chat, CancellationToken.None);
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
