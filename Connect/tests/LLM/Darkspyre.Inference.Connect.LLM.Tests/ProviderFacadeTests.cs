using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderFacadeTests
{
    [Fact]
    public async Task AllProviderFacadesRequireTheSameConnectionVersionContract()
    {
        var source = new RecordingConnectionSource(new LlmConnection
        {
            Version = string.Empty,
            Transport = LlmTransport.OpenAiResponses,
            Endpoint = "https://provider.test/v1"
        });
        var chat = new LlmProviderService(source, []);
        var agents = new LlmAgentProviderService(source, []);
        var embeddings = new LlmEmbeddingProviderService(source, []);

        InvalidOperationException chatFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => chat.GetCapabilitiesAsync("connection"));
        InvalidOperationException agentFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agents.StartSessionAsync("connection", AgentOptions()));
        InvalidOperationException embeddingFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => embeddings.EmbedAsync("connection", EmbeddingRequest()));

        Assert.Equal(
            "LLM connection 'connection' did not supply a concurrency version.",
            chatFailure.Message);
        Assert.Equal(chatFailure.Message, agentFailure.Message);
        Assert.Equal(chatFailure.Message, embeddingFailure.Message);
        Assert.Equal(3, source.Reads);
    }

    [Fact]
    public async Task AllProviderFacadesRejectBlankIdsBeforeReadingTheConnectionSource()
    {
        var source = new RecordingConnectionSource(null);
        var chat = new LlmProviderService(source, []);
        var agents = new LlmAgentProviderService(source, []);
        var embeddings = new LlmEmbeddingProviderService(source, []);

        await Assert.ThrowsAsync<ArgumentException>(() => chat.GetCapabilitiesAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => agents.StartSessionAsync(" ", AgentOptions()));
        await Assert.ThrowsAsync<ArgumentException>(() => embeddings.EmbedAsync(" ", EmbeddingRequest()));

        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void CliInfrastructureOwnsProviderExecutableResolution()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() ? "claude.exe" : "claude",
            ClaudeCliConfiguration.ResolveExecutable("local://claude-cli"));
        Assert.Throws<ArgumentException>(() =>
            ClaudeCliConfiguration.ResolveExecutable("relative/claude"));
    }

    private static LlmAgentSessionOptions AgentOptions() => new()
    {
        ModelId = "model",
        Runtime = new() { WorkingDirectory = Environment.CurrentDirectory },
    };

    private static LlmEmbeddingRequest EmbeddingRequest() => new()
    {
        ModelId = "embedding-model",
        Inputs = ["input"]
    };

    private sealed class RecordingConnectionSource(LlmConnection? connection) : ILlmConnectionSource
    {
        public int Reads { get; private set; }

        public Task<LlmConnection?> GetConnectionAsync(
            string connectionId,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(connection);
        }

        public Task SaveCredentialAsync(
            string connectionId,
            string credentialJson,
            string expectedVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
