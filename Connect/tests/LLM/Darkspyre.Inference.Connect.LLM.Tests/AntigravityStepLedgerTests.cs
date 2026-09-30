using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AntigravityStepLedgerTests
{
    [Fact]
    public void CompletedModelAndCheckpointUsageReplacesOneStepAndExcludesNonBillableWork()
    {
        var ledger = new AntigravityStepLedger();
        var active = Step(1, "agent_response", 100, 10) with { State = "ACTIVE" };
        Assert.True(ledger.Record(active));
        Assert.Null(ledger.Usage);
        Assert.True(ledger.Record(Step(0, "user_input", 999, 999)));
        Assert.True(ledger.Record(Step(2, "tool", 999, 999)));
        Assert.Null(ledger.Usage);
        var done = active with { State = "DONE" };
        Assert.True(ledger.Record(done));
        Assert.Equal(new LlmUsage(100, 10, 110), ledger.Usage);
        Assert.False(ledger.Record(done));
        Assert.False(ledger.Record(done with { Usage = Usage(100, 12) }));
        Assert.Equal(new LlmUsage(100, 12, 112), ledger.Usage);
        Assert.True(ledger.Record(Step(3, "checkpoint", 5, 2)));
        Assert.Equal(new LlmUsage(105, 14, 119), ledger.Usage);
        Assert.Throws<InvalidDataException>(() => ledger.Record(done with { TextDelta = "changed final text" }));
    }

    [Fact]
    public void PreviouslyAccountedStepsAreExcludedOnlyWithinTheirProcessGeneration()
    {
        var accounted = new HashSet<(int Generation, int Index)>();
        var first = new AntigravityStepLedger(1, accounted);
        var step = Step(1, "agent_response", 5, 1);
        Assert.True(first.Record(step));
        var next = new AntigravityStepLedger(1, accounted);
        Assert.False(next.Record(step));
        Assert.Null(next.Usage);
        Assert.True(next.Record(Step(2, "checkpoint", 2, 1)));
        Assert.Equal(new LlmUsage(2, 1, 3), next.Usage);
        var reopened = new AntigravityStepLedger(2, accounted);
        Assert.True(reopened.Record(step));
        Assert.Equal(new LlmUsage(5, 1, 6), reopened.Usage);
    }

    [Fact]
    public void MissingStepUsageNeverAddsAnInventedZero()
    {
        var ledger = new AntigravityStepLedger();
        ledger.Record(Step(1, "agent_response", 5, 1));
        var missing = Step(2, "checkpoint", 0, 0) with { Usage = null };
        ledger.Record(missing);
        Assert.Null(ledger.Usage);
        Assert.False(ledger.Record(missing with { Usage = Usage(0, 0) }));
        Assert.Equal(new LlmUsage(5, 1, 6), ledger.Usage);
    }

    [Fact]
    public void OversizedCountersAndTotalsRemainUnknownWithoutIntegerWraparound()
    {
        Assert.Equal(new LlmUsage(int.MaxValue, 1, null),
            AntigravityStepLedger.Normalize(new(int.MaxValue, 1, null, null, null, false)));
        Assert.Equal(new LlmUsage(null, 1, null),
            AntigravityStepLedger.Normalize(new(long.MaxValue, 1, null, null, long.MaxValue, false)));
        Assert.Null(AntigravityStepLedger.Normalize(Usage(1, 1) with { Invalid = true }));
        var ledger = new AntigravityStepLedger();
        ledger.Record(Step(1, "agent_response", int.MaxValue, 1));
        ledger.Record(Step(2, "checkpoint", 1, 1));
        Assert.Equal(new LlmUsage(null, 2, null), ledger.Usage);
    }

    private static AntigravityStep Step(int index, string type, long input, long output) =>
        new(null, index, "DONE", type, null, null, Usage(input, output));
    private static AntigravityUsage Usage(long input, long output) => new(input, output, null, null, input + output, false);
}
