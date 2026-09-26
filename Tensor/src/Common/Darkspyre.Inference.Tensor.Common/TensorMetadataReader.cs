namespace Darkspyre.Inference.Tensor.Common;

/// <summary>
/// Runs synchronous format-header parsers over bounded source ranges. Ranges are downloaded
/// asynchronously; the parser only ever reads downloaded bytes. No local files, model installation or
/// native model allocation. One reader bounds an entire resolution's downloads.
/// </summary>
public sealed class TensorMetadataReader
{
    private const int PageSize = 64 * 1024;
    private const int ReadAhead = 1024 * 1024;
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

    /// <summary>
    /// The callback must inspect metadata only. It must not retain the stream or request model tensor data.
    /// The callback runs on a background thread over downloaded pages. When it reads beyond them, the
    /// attempt is discarded, the missing range is downloaded asynchronously, and the callback runs again.
    /// A miss fetches one page; a miss directly after the previous fetch doubles the window, up to 1 MiB,
    /// so sequential headers need few attempts. A window never takes more than the remaining budget.
    /// </summary>
    public async Task<T> ReadAsync<T>(TensorSourceArtifact artifact, Func<Stream, T> inspect, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(inspect);
        if (artifact.Length is not > 0) throw new TensorSourceException(TensorSourceState.Unverified, "metadata-size", "The artifact size is unknown or empty.");
        long length = artifact.Length.Value;
        var pages = new Dictionary<long, byte[]>();
        long previousEnd = -1;
        int window = PageSize;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new PageStream(length, pages);
            T result;
            try
            {
                // Format parsers are synchronous CPU work.
                result = await Task.Run(() =>
                {
                    using (stream) { return inspect(stream); }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (stream.MissingOffset is not null)
            {
                result = default!;
            }

            // A parser that caught the miss itself still read incomplete data; its result is discarded.
            if (stream.MissingOffset is not long missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }

            long start = missing / PageSize * PageSize;
            window = start == previousEnd ? Math.Min(window * 2, ReadAhead) : PageSize;
            previousEnd = await FetchAsync(artifact, length, pages, start, window, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <returns>The end of the fetched range.</returns>
    private async Task<long> FetchAsync(TensorSourceArtifact artifact, long length, Dictionary<long, byte[]> pages,
        long start, int window, CancellationToken cancellationToken)
    {
        long end = Math.Min(length, start + window);
        for (long page = start + PageSize; page < end; page += PageSize)
        {
            if (pages.ContainsKey(page)) { end = page; break; }
        }

        int needed = (int)Math.Min(PageSize, length - start);
        int count = Math.Max(needed, Math.Min(checked((int)(end - start)), Available() / PageSize * PageSize));
        Reserve(count);
        TensorSourceResult<TensorMetadataContent> response = await _source.ReadMetadataAsync(artifact, new(start, count), cancellationToken)
            .ConfigureAwait(false);
        if (response.State != TensorSourceState.Available || response.Value is null)
        {
            TensorSourceIssue? issue = response.Issues is { Count: > 0 } ? response.Issues[0] : null;
            throw new TensorSourceException(response.State, issue?.Code ?? "metadata-unavailable", issue?.Message ?? "The source could not provide metadata.");
        }

        TensorMetadataContent content = response.Value;
        if (content.Offset != start || content.TotalLength != length || content.Bytes.Length != count)
            throw new TensorSourceException(TensorSourceState.Unverified, "metadata-range", "The source returned an inconsistent metadata range.");
        for (int position = 0; position < count; position += PageSize)
        {
            int size = Math.Min(PageSize, count - position);
            pages[start + position] = content.Bytes.Slice(position, size).ToArray();
        }

        return start + count;
    }

    private int Available()
    {
        lock (_gate) { return _remaining; }
    }

    private void Reserve(int count)
    {
        lock (_gate)
        {
            if (count > _remaining) throw new TensorSourceException(TensorSourceState.Unverified, "metadata-budget", "Metadata exceeds the resolution inspection budget.");
            _remaining -= count;
        }
    }

    /// <summary>A seekable view of the downloaded pages. A read outside them records the missing offset.</summary>
    private sealed class PageStream(long length, IReadOnlyDictionary<long, byte[]> pages) : Stream
    {
        private long _position;
        private bool _disposed;

        public long? MissingOffset { get; private set; }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (value < 0 || value > length) throw new IOException("Metadata seek is outside the artifact.");
                _position = value;
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.IsEmpty || _position == length) return 0;
            long start = _position / PageSize * PageSize;
            if (!pages.TryGetValue(start, out byte[]? page))
            {
                MissingOffset ??= _position;
                throw new IOException("The metadata range has not been downloaded yet.");
            }

            int offsetInPage = (int)(_position - start);
            int copied = Math.Min(buffer.Length, page.Length - offsetInPage);
            page.AsSpan(offsetInPage, copied).CopyTo(buffer);
            _position += copied;
            return copied;
        }
        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
