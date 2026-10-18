namespace Darkspyre.Inference.Tensor.LLM.Qwen;

/// <summary>Withholds the part of a streamed text that can still become a stop sequence, and ends the text before the
/// first stop sequence. Ported from TSF:TensorSharp.Runtime/StreamingStopSequenceFilter.cs.</summary>
internal sealed class StopSequenceFilter
{
    private readonly string[] _sequences;
    private string _pending = string.Empty;

    /// <summary>Throws <see cref="ArgumentException"/> when a sequence is empty.</summary>
    internal StopSequenceFilter(IReadOnlyList<string> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        _sequences = [.. sequences];
        if (_sequences.Any(string.IsNullOrEmpty))
            throw new ArgumentException("Stop sequences must contain non-empty strings.", nameof(sequences));
    }

    /// <summary>True once a stop sequence matched; every later Append returns empty text.</summary>
    internal bool Stopped { get; private set; }

    /// <summary>The text that can be shown now: everything before a match, or everything except a suffix that is a
    /// prefix of a stop sequence.</summary>
    internal string Append(string piece)
    {
        if (Stopped) return string.Empty;
        string text = _pending + piece;
        int match = -1;
        foreach (string stop in _sequences)
        {
            int index = text.IndexOf(stop, StringComparison.Ordinal);
            if (index >= 0 && (match < 0 || index < match)) match = index;
        }
        if (match >= 0)
        {
            Stopped = true;
            _pending = string.Empty;
            return text[..match];
        }

        int retain = 0;
        foreach (string stop in _sequences)
        {
            for (int length = Math.Min(stop.Length - 1, text.Length); length > retain; length--)
            {
                if (text.AsSpan(text.Length - length).SequenceEqual(stop.AsSpan(0, length)))
                {
                    retain = length;
                    break;
                }
            }
        }
        _pending = text[(text.Length - retain)..];
        return text[..(text.Length - retain)];
    }

    /// <summary>The withheld suffix, at the end of a stream that never matched.</summary>
    internal string Complete()
    {
        string tail = _pending;
        _pending = string.Empty;
        return tail;
    }
}
