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
