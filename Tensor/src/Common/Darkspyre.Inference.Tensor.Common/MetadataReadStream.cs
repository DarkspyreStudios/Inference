namespace Darkspyre.Inference.Tensor.Common;

/// <summary>
/// A read-only view of a seekable source that bounds the total bytes read, including after seeks. Model header parsers
/// read untrusted downloaded bytes through it, so a hostile header cannot make them read past the budget. The source
/// stays open and belongs to the caller.
/// </summary>
public sealed class MetadataReadStream : Stream
{
    private readonly Stream _source;

    /// <summary>Wraps <paramref name="source"/>, rewinds it to the start and allows <paramref name="maximumBytes"/> of reads.
    /// Throws <see cref="ArgumentException"/> when the source is not readable and seekable.</summary>
    public MetadataReadStream(Stream source, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!source.CanRead || !source.CanSeek) throw new ArgumentException("Metadata inspection requires a readable seekable stream.", nameof(source));
        _source = source;
        _source.Position = 0;
        Remaining = maximumBytes;
    }

    /// <summary>The bytes still allowed to be read.</summary>
    public long Remaining { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => true;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => _source.Length;
    /// <inheritdoc />
    public override long Position { get => _source.Position; set => _source.Position = value; }
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <summary>Reads from the source; throws <see cref="InvalidDataException"/> once the budget is spent.</summary>
    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        if (Remaining == 0) throw new InvalidDataException("Model metadata exceeds the inspection byte budget.");
        int read = _source.Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
        Remaining -= read;
        return read;
    }

    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Throws <see cref="InvalidDataException"/> when <paramref name="count"/> items of at least
    /// <paramref name="minimumBytes"/> each need more bytes than the stream, or the budget of a
    /// <see cref="MetadataReadStream"/> under <paramref name="reader"/>, has left. Call it before allocating for a declared
    /// count.</summary>
    public static void RequireElements(BinaryReader reader, ulong count, int minimumBytes)
    {
        ArgumentNullException.ThrowIfNull(reader);
        long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        if (reader.BaseStream is MetadataReadStream bounded) remaining = Math.Min(remaining, bounded.Remaining);
        if (count > int.MaxValue || remaining < 0 || count > (ulong)(remaining / minimumBytes))
            throw new InvalidDataException("Model metadata length exceeds its remaining file or inspection budget.");
    }
}
