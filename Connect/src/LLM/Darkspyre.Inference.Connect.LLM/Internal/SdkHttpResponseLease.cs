using System.Net;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal sealed class SdkHttpResponseLease : HttpContent
{
    private readonly Lock _gate = new();
    private readonly SdkHttpGenerationPool _owner;
    private readonly SdkHttpPipelineEntry _entry;
    private HttpContent? _content;
    private LlmOperationLifetime.Lease? _operation;
    private bool _disposing;
    private bool _disposed;
    private int _cleanup;
    internal SdkHttpResponseLease(SdkHttpGenerationPool owner, SdkHttpPipelineEntry entry, HttpContent content, LlmOperationLifetime.Lease operation)
    {
        _owner = owner; _entry = entry; _content = content; _operation = operation;
        foreach (var header in content.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }
    private HttpContent Content { get { lock (_gate) { ObjectDisposedException.ThrowIf(_disposing, this); return _content!; } } }
    protected override bool TryComputeLength(out long length)
    {
        long? actual = Content.Headers.ContentLength; length = actual ?? 0; return actual.HasValue;
    }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, default);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using BodyOperation operation = Enter(cancellationToken);
        await Content.CopyToAsync(stream, context, operation.Token).ConfigureAwait(false); Fence(operation.Token);
    }
    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using BodyOperation operation = Enter(cancellationToken);
        Content.CopyTo(stream, context, operation.Token); Fence(operation.Token);
    }
    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
    {
        using BodyOperation operation = Enter(cancellationToken);
        Stream stream = Content.ReadAsStream(operation.Token); Fence(operation.Token);
        return new SdkHttpBodyStream(this, stream);
    }
    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(default);
    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        using BodyOperation operation = Enter(cancellationToken);
        Stream stream = await Content.ReadAsStreamAsync(operation.Token).ConfigureAwait(false); Fence(operation.Token);
        return new SdkHttpBodyStream(this, stream);
    }
    internal BodyOperation Enter(CancellationToken cancellationToken)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposing, this);
        return new(_owner, _entry, _owner.EnterBody(_entry, cancellationToken));
    }
    internal void Fence(CancellationToken token)
    {
        _owner.Fence(token);
        lock (_gate) ObjectDisposedException.ThrowIf(_disposing, this);
    }
    internal bool BeginStreamCleanup()
    {
        lock (_gate) { if (_disposed) return false; _cleanup++; return true; }
    }
    internal void EndStreamCleanup()
    { lock (_gate) { _cleanup--; ReleaseIfComplete(); } }
    internal void Failed(object resource, Exception error) => _owner.RecordFailure(_entry, resource, error);
    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        HttpContent? content;
        lock (_gate) { if (_disposing) return; _disposing = true; content = _content; }
        try { content!.Dispose(); }
        catch (Exception error) { Failed(content!, error); throw; }
        finally
        {
            try { base.Dispose(true); }
            finally { lock (_gate) { _disposed = true; _content = null; ReleaseIfComplete(); } }
        }
    }
    private void ReleaseIfComplete()
    {
        if (!_disposed || _cleanup != 0 || _operation is null) return;
        LlmOperationLifetime.Lease operation = _operation; _operation = null; _owner.ReleaseUse(_entry, operation);
    }
    internal sealed class BodyOperation(SdkHttpGenerationPool owner, SdkHttpPipelineEntry entry, LlmOperationLifetime.Lease operation) : IDisposable
    {
        internal CancellationToken Token => operation.Token;
        public void Dispose() => owner.ReleaseUse(entry, operation);
    }
}

internal sealed class SdkHttpBodyStream(SdkHttpResponseLease owner, Stream stream) : Stream
{
    private int _disposed;
    public override bool CanRead => stream.CanRead;
    public override bool CanSeek => stream.CanSeek;
    public override bool CanWrite => stream.CanWrite;
    public override long Length => stream.Length;
    public override long Position { get => stream.Position; set => stream.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        using var operation = owner.Enter(default); int read = stream.Read(buffer); owner.Fence(operation.Token); return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var operation = owner.Enter(cancellationToken);
        int read = await stream.ReadAsync(buffer, operation.Token).ConfigureAwait(false); owner.Fence(operation.Token); return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        using var operation = owner.Enter(default); stream.Write(buffer); owner.Fence(operation.Token);
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var operation = owner.Enter(cancellationToken); await stream.WriteAsync(buffer, operation.Token).ConfigureAwait(false); owner.Fence(operation.Token);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() { using var operation = owner.Enter(default); stream.Flush(); owner.Fence(operation.Token); }
    public override async Task FlushAsync(CancellationToken cancellationToken)
    { using var operation = owner.Enter(cancellationToken); await stream.FlushAsync(operation.Token).ConfigureAwait(false); owner.Fence(operation.Token); }
    public override long Seek(long offset, SeekOrigin origin) { using var operation = owner.Enter(default); long result = stream.Seek(offset, origin); owner.Fence(operation.Token); return result; }
    public override void SetLength(long value) { using var operation = owner.Enter(default); stream.SetLength(value); owner.Fence(operation.Token); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) owner.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || !owner.BeginStreamCleanup())
        {
            await base.DisposeAsync().ConfigureAwait(false);
            return;
        }
        var errors = new List<Exception>();
        try
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); owner.Failed(stream, error); }
            try { owner.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        }
        finally
        {
            owner.EndStreamCleanup();
            await base.DisposeAsync().ConfigureAwait(false);
        }
        SdkHttpGenerationPool.ThrowFailures(errors);
        GC.SuppressFinalize(this);
    }
}
