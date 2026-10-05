using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class SelfHostEmbeddingTests
{
    [Theory]
    [InlineData("headers", "HTTP headers")]
    [InlineData("connection", "capability overrides")]
    [InlineData("model", "capability overrides")]
    public async Task UnsupportedConnectionOptionsRefuseBeforeEmbeddingEngineAcquisition(string option, string message)
    {
        var connection = new LlmConnection { Version = "1", Transport = LlmTransport.SelfHost, Endpoint = "engine" };
        connection = option switch
        {
            "headers" => connection with { AdditionalHeaders = new Dictionary<string, string> { ["X-Custom"] = "value" } },
            "connection" => connection with { CapabilityOverrides = new() },
            "model" => connection with { ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides> { ["model"] = new() } },
            _ => throw new ArgumentException("Unknown fixture option.", nameof(option))
        };
        await using var service = new LlmEmbeddingProviderService(new Source(connection), [new SelfHostEmbeddingClient(new Host())]);

        NotSupportedException refusal = await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.EmbedAsync("connection", new() { ModelId = "model", Inputs = ["reference"] }));

        Assert.Contains(message, refusal.Message, StringComparison.Ordinal);
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(connection);

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("This refusal must not change credentials.");
    }

    private sealed class Host : ILlmSelfHost
    {
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This refusal must not enumerate engines.");

        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This refusal must not acquire a chat engine.");

        public Task<IEmbeddingInference> GetEmbeddingInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unsupported connection options must refuse before engine acquisition.");

        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) =>
            throw new InvalidOperationException("This refusal must not inspect a chat engine.");
    }
}
