using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LearnedModelContextTests
{
    private static readonly LlmConnection Unsaved = new() { Version = "1", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://example.test/v1" };

    /// <summary>A learned limit is saved once through the store, and a new process reads it from the connection until a
    /// refusal in that process teaches another.</summary>
    [Fact]
    public async Task LearnedLimitIsSavedAndReadBackFromTheConnection()
    {
        var store = new Store();
        var learned = new LearnedModelContext(store);

        await learned.ObserveRefusalAsync("codex", "gpt", estimated: 900_000, reportedTokens: null, reportedLimit: 835_000, knownLimit: null,
            CancellationToken.None);
        await learned.ObserveRefusalAsync("codex", "gpt", estimated: 900_000, reportedTokens: null, reportedLimit: 835_000, knownLimit: null,
            CancellationToken.None);
        LlmConnection saved = Unsaved with { LearnedContextLimits = new Dictionary<string, long> { ["gpt"] = 835_000 } };

        Assert.Equal([("codex", "gpt", 835_000L)], store.Saves);
        Assert.Equal(835_000, new LearnedModelContext().Limit("codex", saved, "gpt"));
        Assert.Null(new LearnedModelContext().Limit("codex", saved, "other"));
    }

    private sealed class Store : ILlmLearnedLimitStore
    {
        public List<(string ConnectionId, string ModelId, long Tokens)> Saves { get; } = [];

        public Task SaveContextLimitAsync(string connectionId, string modelId, long tokens, CancellationToken cancellationToken = default)
        {
            Saves.Add((connectionId, modelId, tokens));
            return Task.CompletedTask;
        }
    }
}
