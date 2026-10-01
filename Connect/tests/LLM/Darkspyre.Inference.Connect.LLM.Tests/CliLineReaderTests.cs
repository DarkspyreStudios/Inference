using System.Text;
using System.Threading.Channels;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CliLineReaderTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public async Task DelimitersEmptyLinesAndFinalUnterminatedTextMatchTextReader(string delimiter)
    {
        using var source = new StringReader("first" + delimiter + delimiter + "last");
        var reader = new CliLineReader(source, 5, "limit");
        Assert.Equal("first", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal(string.Empty, await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("last", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public async Task CompleteInteractiveLineDoesNotWaitForMoreOutput(string delimiter)
    {
        using var source = new ControlledReader();
        source.Feed("ready" + delimiter);
        var reader = new CliLineReader(source, 5, "limit");
        Assert.Equal("ready", await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public async Task ExistingStreamReaderOwnsBomDecodingAndUnicodeCharacterCounts(string kind)
    {
        Encoding encoding = kind switch
        {
            "utf16le" => new UnicodeEncoding(false, true),
            "utf16be" => new UnicodeEncoding(true, true),
            "utf32le" => new UTF32Encoding(false, true),
            "utf32be" => new UTF32Encoding(true, true),
            _ => new UTF8Encoding(true)
        };
        const string text = "\u03bb\uD83D\uDE00";
        using var stream = new MemoryStream([.. encoding.GetPreamble(), .. encoding.GetBytes(text + "\n")]);
        using var source = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var reader = new CliLineReader(source, text.Length, "limit");
        Assert.Equal(text, await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExistingDecoderFallbackIsNotReplacedWithAStrictUtf8Policy()
    {
        using var stream = new MemoryStream([0xff, 0x0a]);
        using var source = new StreamReader(stream, new UTF8Encoding(false, false));
        var reader = new CliLineReader(source, 1, "limit");
        Assert.Equal("\uFFFD", await reader.ReadLineAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("1234\n")]
    [InlineData("1234\r")]
    [InlineData("1234\r\n")]
    public async Task ExactCharacterLimitIsAllowedAtEofOrAnyDelimiter(string text)
    {
        using var source = new StringReader(text);
        var reader = new CliLineReader(source, 4, "limit");
        Assert.Equal("1234", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PartialTextSurvivesCallerCancellation()
    {
        using var source = new ControlledReader();
        source.Feed("first");
        var reader = new CliLineReader(source, 11, "limit");
        using var cancellation = new CancellationTokenSource();
        Task<string?> pending = reader.ReadLineAsync(cancellation.Token).AsTask();
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        source.Feed("second\n");
        source.End();
        Assert.Equal("firstsecond", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationBetweenCrAndLfKeepsTheDelimiterState()
    {
        using var source = new ControlledReader();
        source.Feed("first\r");
        var reader = new CliLineReader(source, 5, "limit");
        Assert.Equal("first", await reader.ReadLineAsync(CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        Task<string?> pending = reader.ReadLineAsync(cancellation.Token).AsTask();
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        source.Feed("\nnext\n");
        source.End();
        Assert.Equal("next", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("12345\nvalid\n")]
    public async Task OversizedLineFaultsBeforeDelimiterAndCannotResumeOrTruncate(string text)
    {
        using var source = new ControlledReader();
        source.Feed(text);
        var reader = new CliLineReader(source, 4, "owned limit");
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadLineAsync(CancellationToken.None).AsTask());
        Assert.Equal("owned limit", failure.Message);
        Assert.Equal(5, source.Reads);
        reader.ReleaseBufferedText();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadLineAsync(CancellationToken.None).AsTask()));
        Assert.Equal(5, source.Reads);
    }

    private sealed class ControlledReader : TextReader
    {
        private readonly Channel<char> _input = Channel.CreateUnbounded<char>();
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads { get; private set; }
        internal void Feed(string text)
        {
            foreach (char value in text) Assert.True(_input.Writer.TryWrite(value));
        }
        internal void End() => _input.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (!_input.Reader.TryRead(out char value))
            {
                Waiting.TrySetResult();
                try { value = await _input.Reader.ReadAsync(cancellationToken); }
                catch (ChannelClosedException) { return 0; }
            }
            buffer.Span[0] = value;
            Reads++;
            return 1;
        }
        protected override void Dispose(bool disposing)
        {
            _input.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
