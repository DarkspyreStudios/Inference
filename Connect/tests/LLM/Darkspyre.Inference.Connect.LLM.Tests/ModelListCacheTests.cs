using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ModelListCacheTests
{
    [Fact]
    public async Task RepeatedListingsAskTheProviderOnce()
    {
        var client = new ListingClient(["model-a"]);
        var service = Service(new Source("v1"), client);

        IReadOnlyList<LlmModel> first = await service.ListModelsAsync("connection");
        IReadOnlyList<LlmModel> second = await service.ListModelsAsync("connection");

        Assert.Equal(["model-a"], second.Select(model => model.Id));
        Assert.Same(first, second);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task AConnectionEditListsAgainAndARefreshReplacesTheKeptList()
    {
        var source = new Source("v1");
        var client = new ListingClient(["model-a"]);
        var service = Service(source, client);
        await service.ListModelsAsync("connection");

        source.Version = "v2";
        client.Models = ["model-b"];
        Assert.Equal(["model-b"], (await service.ListModelsAsync("connection")).Select(model => model.Id));
        Assert.Equal(2, client.Calls);

        client.Models = ["model-c"];
        Assert.Equal(["model-b"], (await service.ListModelsAsync("connection")).Select(model => model.Id));
        Assert.Equal(["model-c"], (await service.RefreshModelsAsync("connection")).Select(model => model.Id));
        Assert.Equal(["model-c"], (await service.ListModelsAsync("connection")).Select(model => model.Id));
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public async Task ConcurrentListingsShareOneRequestAndOneCallersCancellationStopsNoOther()
    {
        var client = new ListingClient(["model-a"]) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = Service(new Source("v1"), client);
        using var cancelled = new CancellationTokenSource();

        Task<IReadOnlyList<LlmModel>> abandoned = service.ListModelsAsync("connection", cancelled.Token);
        Task<IReadOnlyList<LlmModel>>[] waiting = [.. Enumerable.Range(0, 4).Select(_ => service.ListModelsAsync("connection"))];
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        client.Gate.SetResult();

        IReadOnlyList<LlmModel>[] lists = await Task.WhenAll(waiting);
        Assert.All(lists, list => Assert.Same(lists[0], list));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task AFailedListingIsNotKept()
    {
        var client = new ListingClient(["model-a"]) { Failures = 1 };
        var service = Service(new Source("v1"), client);

        await Assert.ThrowsAsync<LlmProviderException>(() => service.ListModelsAsync("connection"));
        Assert.Equal(["model-a"], (await service.ListModelsAsync("connection")).Select(model => model.Id));
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task ServicesShareListsOnlyThroughOneCache()
    {
        var client = new ListingClient(["model-a"]);
        var shared = new LlmModelListCache();
        await Service(new Source("v1"), client, shared).ListModelsAsync("connection");
        await Service(new Source("v1"), client, shared).ListModelsAsync("connection");
        Assert.Equal(1, client.Calls);

        // A service built without the application's cache keeps its own.
        await Service(new Source("v1"), client).ListModelsAsync("connection");
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task RequestAccountingReadsTheKeptList()
    {
        var client = new ListingClient(["model-a"]);
        var service = Service(new Source("v1"), client);

        await service.ListModelsAsync("connection");
        LlmRequestAccounting accounting = await service.MeasureRequestAsync("connection", new LlmChatRequest { ModelId = "model-a" });

        Assert.True(accounting.IsSupported);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public void EveryScopeResolvesTheApplicationsOneCache()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(new Source("v1"));
        services.AddLlmConnect();
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();

        Assert.Same(first.ServiceProvider.GetRequiredService<LlmModelListCache>(),
            second.ServiceProvider.GetRequiredService<LlmModelListCache>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<LlmProviderService>(),
            second.ServiceProvider.GetRequiredService<LlmProviderService>());
    }

    private static LlmProviderService Service(Source source, ListingClient client, LlmModelListCache? cache = null) =>
        new(source, [client], [], null, cache);

    private sealed class Source(string version) : ILlmConnectionSource
    {
        public string Version { get; set; } = version;

        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new LlmConnection
            {
                Version = Version,
                Transport = LlmTransport.OpenAiCompatibleChat,
                Endpoint = "https://provider.test/v1"
            });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Counts listings; a gate holds them open and a failure count fails the first ones.</summary>
    private sealed class ListingClient(IReadOnlyList<string> models) : ILlmClient, ILlmContextEstimator
    {
        private int _calls;

        public IReadOnlyList<string> Models { get; set; } = models;

        public TaskCompletionSource? Gate { get; init; }

        public int Failures { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;

        public string? EstimateDiagnostic => null;

        public LlmProviderDefinition ProviderDefinition => new()
        {
            Id = "listing",
            DisplayName = "Listing",
            Description = "Test provider",
            Transport = Transport,
            DefaultEndpoint = "https://provider.test/v1",
            SetupSteps = []
        };

        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) =>
            new() { SupportsStreaming = true, SupportsModelListing = true };

        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request) => null;

        public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (Gate is not null) await Gate.Task;
            if (Failures > 0)
            {
                Failures--;
                throw new LlmProviderException("The listing failed.", Transport);
            }
            return [.. Models.Select(id => new LlmModel(id, id))];
        }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
