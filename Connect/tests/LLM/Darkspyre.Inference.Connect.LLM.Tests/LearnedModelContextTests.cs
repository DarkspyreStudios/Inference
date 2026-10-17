using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LearnedModelContextTests
{
    private static readonly LlmConnection Unsaved = new() { Version = "1", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://example.test/v1" };

    [Fact]
    public async Task ReportsCorrectEstimatesAndRefusalsSetLimits()
    {
        var learned = new LearnedModelContext();
        const string codex = "codex-connection";

        // A report under a quarter of the usable window sets no rate; its surplus is the framing, added to later estimates.
        learned.ObserveReport(codex, "model", estimated: 1_000, reported: 1_500, usableWindow: 100_000);
        Assert.Equal(10_500, learned.Correct(codex, "model", 10_000));

        // The rate is taken with the framing removed, and the framing is added back.
        learned.ObserveReport(codex, "model", estimated: 40_000, reported: 60_500, usableWindow: 100_000);
        Assert.Equal(15_500, learned.Correct(codex, "model", 10_000));

        // A refusal's reported size sets the rate and its named limit becomes the learned limit.
        await learned.ObserveRefusalAsync(codex, "model", estimated: 50_000, reportedTokens: 80_500, reportedLimit: 70_000, knownLimit: 200_000,
            CancellationToken.None);
        Assert.Equal(16_500, learned.Correct(codex, "model", 10_000));
        Assert.Equal(70_000, learned.Limit(codex, Unsaved, "model"));

        // With no limit at all, the corrected refused size is the limit.
        await learned.ObserveRefusalAsync(codex, "unlisted", estimated: 90_000, reportedTokens: null, reportedLimit: null, knownLimit: null,
            CancellationToken.None);
        Assert.Equal(90_000, learned.Limit(codex, Unsaved, "unlisted"));

        Assert.Null(learned.Limit("claude-connection", Unsaved, "model"));
    }

    /// <summary>A small request's reported surplus is the framing; the latest one replaces it, and a local count that
    /// ran high teaches none.</summary>
    [Fact]
    public void SmallReportsTeachTheFraming()
    {
        var learned = new LearnedModelContext();

        learned.ObserveReport("grok", "grok-4.7", estimated: 40, reported: 1_403, usableWindow: 240_000);
        Assert.Equal(6_363, learned.Correct("grok", "grok-4.7", 5_000));
        // A rate learned on a small capped window keeps the framing out of the rate.
        learned.ObserveReport("grok", "grok-4.7", estimated: 12_000, reported: 12_563, usableWindow: 26_000);
        Assert.Equal(6_030, learned.Correct("grok", "grok-4.7", 5_000));
        learned.ObserveReport("grok", "grok-4.7", estimated: 6_000, reported: 5_900, usableWindow: 240_000);

        Assert.Equal(5_235, learned.Correct("grok", "grok-4.7", 5_000));
        Assert.Equal(5_000, learned.Correct("grok", "grok-4.5", 5_000));
    }

    [Fact]
    public async Task LimitBelongsToTheConnection()
    {
        var learned = new LearnedModelContext();

        await learned.ObserveRefusalAsync("local", "qwen3", estimated: 140_000, reportedTokens: 140_068, reportedLimit: 131_072, knownLimit: null,
            CancellationToken.None);

        Assert.Equal(131_072, learned.Limit("local", Unsaved, "qwen3"));
        Assert.Null(learned.Limit("openrouter", Unsaved, "qwen3"));
        Assert.Equal(10_000, learned.Correct("openrouter", "qwen3", 10_000));
    }

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
