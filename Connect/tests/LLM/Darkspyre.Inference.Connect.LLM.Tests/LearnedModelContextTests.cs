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

    /// <summary>A text inside a request carries the rate but not the framing, which a whole request carries once.</summary>
    [Fact]
    public void TextEstimatesLeaveOutTheFraming()
    {
        var learned = new LearnedModelContext();
        learned.ObserveReport("claude", "opus", estimated: 40, reported: 30_040, usableWindow: 872_000);
        learned.ObserveReport("claude", "opus", estimated: 300_000, reported: 360_000, usableWindow: 872_000);

        Assert.Equal(30_000, learned.Correct("claude", "opus", 1_000) - learned.CorrectText("claude", "opus", 1_000));
        Assert.Equal(1_100, learned.CorrectText("claude", "opus", 1_000));
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

    /// <summary>Read lists each learned value of the connection's models with the time it changed, and only that
    /// connection's.</summary>
    [Fact]
    public async Task ReadReturnsEachLearnedValueWithItsTime()
    {
        var clock = new Clock(new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        var learned = new LearnedModelContext(time: clock);

        learned.ObserveReport("local", "qwen3", estimated: 1_000, reported: 1_500, usableWindow: 100_000);
        DateTimeOffset framingAt = clock.Now;
        clock.Now = framingAt.AddMinutes(5);
        await learned.ObserveRefusalAsync("local", "qwen3", estimated: 50_000, reportedTokens: 80_500, reportedLimit: 70_000, knownLimit: null,
            CancellationToken.None);
        DateTimeOffset refusedAt = clock.Now;
        clock.Now = refusedAt.AddMinutes(5);
        learned.ObserveRefusedSettings("local", "qwen3", LlmRefusedSettings.Temperature);
        DateTimeOffset temperatureAt = clock.Now;
        clock.Now = temperatureAt.AddMinutes(5);
        learned.ObserveRefusedSettings("local", "qwen3", LlmRefusedSettings.Temperature | LlmRefusedSettings.Reasoning);
        learned.ObserveReport("other", "model", estimated: 1_000, reported: 1_500, usableWindow: 100_000);

        Assert.Equal(
        [
            new LlmLearnedModelFact("qwen3", "Estimated to reported token ratio", "1.6", refusedAt),
            new LlmLearnedModelFact("qwen3", "Framing tokens", "500", framingAt),
            new LlmLearnedModelFact("qwen3", "Learned context limit", "70000", refusedAt),
            new LlmLearnedModelFact("qwen3", "Refused setting", "Temperature", temperatureAt),
            new LlmLearnedModelFact("qwen3", "Refused setting", "Reasoning", clock.Now)
        ], learned.Read("local"));
        Assert.Empty(learned.Read("unknown"));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
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
