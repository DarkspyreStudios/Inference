using System.Net;
using System.Net.Http.Headers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

// Content owns no source stream; the admitted resource reader owns the opened handle.
internal sealed class GoogleCacheMediaContent : HttpContent
{
    private Stream? _source;
    private readonly long? _length;
    private readonly long _maximum;
    private Action<CancellationToken>? _fence;
    internal long BytesWritten { get; private set; }

    internal GoogleCacheMediaContent(Stream source, string mime, long? length, long maximum, Action<CancellationToken> fence)
    {
        _source = source; _length = length; _maximum = maximum; _fence = fence;
        Headers.ContentType = MediaTypeHeaderValue.Parse(mime);
    }

    protected override bool TryComputeLength(out long computed) { computed = _length ?? 0; return _length is not null; }
    protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => WriteAsync(destination, CancellationToken.None);
    protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context, CancellationToken token) => WriteAsync(destination, token);
    private async Task WriteAsync(Stream destination, CancellationToken token)
    {
        Stream source = _source ?? throw new ObjectDisposedException(nameof(GoogleCacheMediaContent));
        Action<CancellationToken> fence = _fence ?? throw new ObjectDisposedException(nameof(GoogleCacheMediaContent));
        byte[] buffer = new byte[32 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested(); fence(token);
            int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); fence(token);
            if (read == 0) break;
            long total = checked(BytesWritten + read);
            if (total > _maximum || _length is { } expected && total > expected)
                throw new InvalidOperationException("The staged content exceeds its permitted byte count.");
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            BytesWritten = total;
            token.ThrowIfCancellationRequested(); fence(token);
        }
        if (_length is { } actual && BytesWritten != actual)
            throw new InvalidOperationException("The staged content does not match its observed byte count.");
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally { if (disposing) DetachBorrowed(); }
    }
    internal void DetachBorrowed() { _source = null; _fence = null; }
}
