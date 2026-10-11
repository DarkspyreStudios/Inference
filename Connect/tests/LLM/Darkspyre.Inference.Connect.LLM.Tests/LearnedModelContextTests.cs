using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LearnedModelContextTests
{
    [Fact]
    public void ReportsCorrectEstimatesAndRefusalsSetLimits()
    {
        var learned = new LearnedModelContext();
        const string codex = "codex-connection";

        // A report under a quarter of the usable window carries framing noise and sets no rate.
        learned.ObserveReport(codex, "model", estimated: 1_000, reported: 1_500, usableWindow: 100_000);
        Assert.Equal(10_000, learned.Correct(codex, "model", 10_000));

        learned.ObserveReport(codex, "model", estimated: 40_000, reported: 60_000, usableWindow: 100_000);
        Assert.Equal(15_000, learned.Correct(codex, "model", 10_000));

        // A refusal's reported size sets the rate and its named limit becomes the learned limit.
        learned.ObserveRefusal(codex, "model", estimated: 50_000, reportedTokens: 80_000, reportedLimit: 70_000, knownLimit: 200_000);
        Assert.Equal(16_000, learned.Correct(codex, "model", 10_000));
        Assert.Equal(70_000, learned.Limit(codex, "model"));

        // With no limit at all, the corrected refused size is the limit.
        learned.ObserveRefusal(codex, "unlisted", estimated: 90_000, reportedTokens: null, reportedLimit: null, knownLimit: null);
        Assert.Equal(90_000, learned.Limit(codex, "unlisted"));

        Assert.Null(learned.Limit("claude-connection", "model"));
    }

    [Fact]
    public void LimitBelongsToTheConnection()
    {
        var learned = new LearnedModelContext();

        learned.ObserveRefusal("local", "qwen3", estimated: 140_000, reportedTokens: 140_068, reportedLimit: 131_072, knownLimit: null);

        Assert.Equal(131_072, learned.Limit("local", "qwen3"));
        Assert.Null(learned.Limit("openrouter", "qwen3"));
        Assert.Equal(10_000, learned.Correct("openrouter", "qwen3", 10_000));
    }
}
