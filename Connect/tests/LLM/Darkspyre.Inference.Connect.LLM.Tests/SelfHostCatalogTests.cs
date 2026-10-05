using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class SelfHostCatalogTests
{
    [Fact]
    public async Task ExplicitRefreshReplacesTheKeptEngineCatalogAndTheConnectCatalog()
    {
        var engine = new Engine { Models = [new("first", "First") { MaxContextTokens = 4096 }] };
        await using var service = new LlmProviderService(new Source(), [new SelfHostLlmClient(new Host(engine))]);
        ILlmInference inference = service.Bind("connection");

        Assert.Equal("first", Assert.Single(await inference.ListModelsAsync()).Id);
        engine.Models = [new("second", "Second") { MaxContextTokens = 8192 }, new("unknown", "Unknown")];
        Assert.Equal("first", Assert.Single(await inference.ListModelsAsync()).Id);

        IReadOnlyList<LlmModelInfo> refreshed = await inference.RefreshModelsAsync();
        Assert.Equal(["second", "unknown"], refreshed.Select(model => model.Id));
        Assert.Equal(8192, refreshed[0].MaxContextTokens);
        Assert.Null(refreshed[1].MaxContextTokens);
        Assert.Equal(["second", "unknown"], (await inference.ListModelsAsync()).Select(model => model.Id));
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new() { Version = "1", Transport = LlmTransport.SelfHost, Endpoint = "engine" });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Host(ILlmInference engine) : ILlmSelfHost
    {
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmSelfHostEngine>>([new("engine", "Engine")]);
        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            Task.FromResult(engine);
        public Task<IEmbeddingInference> GetEmbeddingInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) => new() { SupportsModelListing = true };
    }

    private sealed class Engine : ILlmInference
    {
        private IReadOnlyList<LlmModelInfo>? _kept;
        public IReadOnlyList<LlmModelInfo> Models { get; set; } = [];
        public InferenceImplementationDescriptor Descriptor { get; } = new("engine", "Engine",
            InferenceImplementationKind.Tensor, InferenceTask.TextGeneration);
        public Task<LlmCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmCapabilities { SupportsModelListing = true });
        public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_kept ??= Models);
        public Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_kept = Models);
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmInferenceRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
