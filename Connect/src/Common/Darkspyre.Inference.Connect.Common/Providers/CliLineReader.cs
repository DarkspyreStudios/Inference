using System.Runtime.ExceptionServices;
using System.Text;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Bounds decoded stdout lines without replacing the pipe's encoding policy.</summary>
internal sealed class CliLineReader
{
    private readonly TextReader _reader;
    private readonly int _maximumCharacters;
    private readonly string _limitMessage;
    private readonly char[] _character = new char[1];
    private StringBuilder _partial = new();
    private bool _skipLineFeed;
    private ExceptionDispatchInfo? _failure;

    internal CliLineReader(TextReader reader, int maximumCharacters, string limitMessage)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        _reader = reader;
        _maximumCharacters = maximumCharacters;
        _limitMessage = limitMessage;
    }

    internal async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        _failure?.Throw();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // StreamReader buffers bytes internally. A one-character request never waits for
            // another byte-buffer refill after a complete interactive line is available.
            if (await _reader.ReadAsync(_character.AsMemory(), cancellationToken).ConfigureAwait(false) == 0)
                return _partial.Length == 0 ? null : CompleteLine();
            char value = _character[0];
            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                if (value == '\n') continue;
            }
            if (value is '\r' or '\n')
            {
                _skipLineFeed = value == '\r';
                return CompleteLine();
            }
            if (_partial.Length == _maximumCharacters)
            {
                _partial = new();
                _failure = ExceptionDispatchInfo.Capture(new InvalidOperationException(_limitMessage));
                _failure.Throw();
            }
            _partial.Append(value);
        }
    }

    private string CompleteLine()
    {
        string line = _partial.ToString();
        _partial = new();
        return line;
    }

    internal void ReleaseBufferedText()
    {
        _partial = new();
        _character[0] = '\0';
    }
}
