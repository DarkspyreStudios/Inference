namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Runs synchronous format-header parsers over bounded source ranges on a worker thread.
/// No local files, model installation or native model allocation. One reader bounds an entire resolution's downloads.</summary>
public sealed class TensorMetadataReader
{
    private readonly ITensorArtifactReader _source;
    private readonly Lock _gate = new();
    private int _remaining;

    public TensorMetadataReader(ITensorArtifactReader source, int maximumBytes = 64 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _source = source;
        _remaining = maximumBytes;
    }

    /// <summary>The callback must inspect metadata only. It must not retain the stream or request model tensor data.
    /// Task.Run keeps synchronous library parsers and the range bridge off a host's UI synchronization context.</summary>
    public Task<T> ReadAsync<T>(TensorSourceArtifact artifact, Func<Stream, T> inspect, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(inspect);
        return Task.Run(() =>
        {
            using var stream = new RangeStream(this, artifact, cancellationToken);
            T result = inspect(stream);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);
    }

    private void Reserve(int count)
    {
        lock (_gate)
        {
            if (count > _remaining) throw new TensorSourceException(TensorSourceState.Unverified, "metadata-budget", "Metadata exceeds the resolution inspection budget.");
            _remaining -= count;
        }
    }

    private sealed class RangeStream : Stream
    {
        private const int PageSize = 64 * 1024;
        private readonly TensorMetadataReader _reader;
        private readonly TensorSourceArtifact _artifact;
        private readonly CancellationToken _token;
        private readonly Dictionary<long, byte[]> _pages = [];
        private readonly long _length;
        private long _position;
        private bool _disposed;

        public RangeStream(TensorMetadataReader reader, TensorSourceArtifact artifact, CancellationToken token)
        {
            if (artifact.Length is not > 0) throw new TensorSourceException(TensorSourceState.Unverified, "metadata-size", "The artifact size is unknown or empty.");
            _reader = reader; _artifact = artifact; _token = token; _length = artifact.Length.Value;
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (value < 0 || value > _length) throw new IOException("Metadata seek is outside the artifact.");
                _position = value;
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _token.ThrowIfCancellationRequested();
            if (buffer.IsEmpty || _position == _length) return 0;
            long start = _position / PageSize * PageSize;
            if (!_pages.TryGetValue(start, out byte[]? page))
            {
                int count = (int)Math.Min(PageSize, _length - start);
                _reader.Reserve(count);
                TensorSourceResult<TensorMetadataContent> response = _reader._source.ReadMetadataAsync(_artifact, new(start, count), _token).GetAwaiter().GetResult();
                if (response.State != TensorSourceState.Available || response.Value is null)
                {
                    TensorSourceIssue? issue = response.Issues is { Count: > 0 } ? response.Issues[0] : null;
                    throw new TensorSourceException(response.State, issue?.Code ?? "metadata-unavailable", issue?.Message ?? "The source could not provide metadata.");
                }
                TensorMetadataContent content = response.Value;
                if (content.Offset != start || content.TotalLength != _length || content.Bytes.Length != count)
                    throw new TensorSourceException(TensorSourceState.Unverified, "metadata-range", "The source returned an inconsistent metadata range.");
                _pages.Add(start, page = content.Bytes.ToArray());
            }
            int offsetInPage = (int)(_position - start);
            int copied = Math.Min(buffer.Length, page.Length - offsetInPage);
            page.AsSpan(offsetInPage, copied).CopyTo(buffer);
            _position += copied;
            return copied;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _pages.Clear();
            _disposed = true;
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
