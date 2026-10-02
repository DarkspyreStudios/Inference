using System.Net;
using System.Net.Http.Headers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

// Content owns no source stream; the admitted resource reader owns the opened handle.
internal sealed class GoogleCacheMediaContent : HttpContent
{
    private readonly Stream _source;
    private readonly long? _length;
    private readonly long _maximum;
    private readonly Action<CancellationToken> _fence;
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
        byte[] buffer = new byte[32 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested(); _fence(token);
            int read = await _source.ReadAsync(buffer, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); _fence(token);
            if (read == 0) break;
            long total = checked(BytesWritten + read);
            if (total > _maximum || _length is { } expected && total > expected)
                throw new InvalidOperationException("The staged content exceeds its permitted byte count.");
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            BytesWritten = total;
            token.ThrowIfCancellationRequested(); _fence(token);
        }
        if (_length is { } actual && BytesWritten != actual)
            throw new InvalidOperationException("The staged content does not match its observed byte count.");
    }
}
