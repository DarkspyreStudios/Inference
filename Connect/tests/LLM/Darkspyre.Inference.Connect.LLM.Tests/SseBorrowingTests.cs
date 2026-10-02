using System.Text;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class SseBorrowingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParserNeverClosesTheCallerOwnedStream(bool abandon)
    {
        using var stream = new TrackedStream();
        await using (var parser = Sse.ReadDataPayloadsAsync(stream).GetAsyncEnumerator())
        {
            Assert.True(await parser.MoveNextAsync());
            Assert.Equal("first", parser.Current);
            if (!abandon)
            {
                Assert.True(await parser.MoveNextAsync());
                Assert.Equal("second", parser.Current);
                Assert.False(await parser.MoveNextAsync());
            }
        }
        Assert.Equal(0, stream.DisposalAttempts);
        Assert.True(stream.CanRead);
        stream.Dispose();
        Assert.Equal(1, stream.DisposalAttempts);
    }

    private sealed class TrackedStream() : MemoryStream(Encoding.UTF8.GetBytes("data: first\n\ndata: second\n\n"))
    {
        public int DisposalAttempts { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposalAttempts++;
            base.Dispose(disposing);
        }
    }
}
