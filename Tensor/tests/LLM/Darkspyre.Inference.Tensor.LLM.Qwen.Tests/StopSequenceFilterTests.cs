namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class StopSequenceFilterTests
{
    [Fact]
    public void HidesAStopSequenceSplitAcrossPiecesAndNeverEmitsIt()
    {
        var filter = new StopSequenceFilter(["</done>"]);

        string shown = filter.Append("answer </") + filter.Append("do") + filter.Append("ne> trailing") + filter.Append("more");

        Assert.Equal("answer ", shown);
        Assert.True(filter.Stopped);
        Assert.Equal(string.Empty, filter.Complete());
    }

    [Fact]
    public void ReturnsAHeldPrefixWhenTheStreamEndsWithoutAMatch()
    {
        var filter = new StopSequenceFilter(["STOP"]);

        Assert.Equal("tail ", filter.Append("tail ST"));
        Assert.False(filter.Stopped);
        Assert.Equal("ST", filter.Complete());
    }

    [Fact]
    public void RefusesAnEmptySequence() => Assert.Throws<ArgumentException>(() => new StopSequenceFilter([""]));
}
