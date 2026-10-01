using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Video;

internal sealed class ConnectVideoInference : IVideoInference, IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private IVideoTransport? _transport;
    private VideoConnection? _connection;
    private XaiFamilyBinding? _xai;
    private IInferenceResourceAccess? _resources;

    internal ConnectVideoInference(IVideoTransport transport, VideoConnection connection,
        XaiFamilyBinding? xai, IInferenceResourceAccess? resources)
    {
        ValidateBinding(connection, xai);
        _transport = transport;
        _connection = connection;
        _xai = xai;
        _resources = resources;
    }

    public async Task<VideoCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        (VideoConnection connection, XaiFamilyBinding binding) = await EnterBindingAsync(lease.Token).ConfigureAwait(false);
        VideoCapabilities capabilities = await _transport!.GetCapabilitiesAsync(connection, modelId, binding, lease.Token).ConfigureAwait(false);
        Fence(lease.Token);
        return capabilities ?? throw ProtocolFailure();
    }

    public async Task<VideoJob> SubmitAsync(VideoRequest request, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        request = CaptureRequest(request);
        ValidateRequest(request, _resources);
        (VideoConnection connection, XaiFamilyBinding binding) = await EnterBindingAsync(lease.Token).ConfigureAwait(false);
        VideoJob result = await _transport!.SubmitAsync(connection, request, binding, _resources, lease.Token).ConfigureAwait(false);
        Fence(lease.Token);
        return result ?? throw ProtocolFailure();
    }

    public async Task<VideoJobSnapshot> GetAsync(VideoJob job, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        ArgumentNullException.ThrowIfNull(job);
        (VideoConnection connection, XaiFamilyBinding binding) = ReceiptBinding();
        VideoJobSnapshot result = await _transport!.GetAsync(connection, job, binding, lease.Token).ConfigureAwait(false);
        Fence(lease.Token);
        if (result is null || result.Job != job) throw ProtocolFailure();
        return result;
    }

    public async Task<Stream> DownloadAsync(VideoOutput output, CancellationToken cancellationToken = default)
    {
        LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        Stream? stream = null;
        try
        {
            ArgumentNullException.ThrowIfNull(output);
            (VideoConnection connection, XaiFamilyBinding binding) = ReceiptBinding();
            stream = await _transport!.DownloadAsync(connection, output, binding, lease.Token).ConfigureAwait(false) ?? throw ProtocolFailure();
            Fence(lease.Token);
            var owned = new VideoDownloadStream(stream, lease, _lifetime);
            stream = owned;
            owned.ObserveRetirement();
            Fence(lease.Token);
            stream = null;
            return owned;
        }
        catch (Exception original)
        {
            try { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            finally { lease.Dispose(); }
            throw;
        }
    }

    public async Task<InferenceResourceDescriptor> MaterializeAsync(VideoOutput output, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        ArgumentNullException.ThrowIfNull(output);
        IInferenceResourceAccess resources = _resources ?? throw new InferenceException(InferenceFailureCode.UnsupportedFeature,
            "Video materialization requires caller-owned resource access.");
        (VideoConnection connection, XaiFamilyBinding binding) = ReceiptBinding();
        string workspace = resources.WorkspaceId;
        string session = resources.SessionId;
        Stream? download = null;
        IInferenceResourceWriteHandle? writer = null;
        InferenceResourceReference? allocation = null;
        InferenceResourceDescriptor? result = null;
        Exception? failure = null;
        InferenceResourceOperationPhase phase = InferenceResourceOperationPhase.Read;
        InferenceResourceCommitOutcome outcome = InferenceResourceCommitOutcome.NotAttempted;
        InferenceResourceCommitOutcome selectionOutcome = InferenceResourceCommitOutcome.NotAttempted;
        InferenceResourceSelectionVersion? selectionVersion = null;
        long? written = 0;
        try
        {
            download = await _transport!.DownloadAsync(connection, output, binding, lease.Token).ConfigureAwait(false) ?? throw ProtocolFailure();
            Fence(lease.Token);
            phase = InferenceResourceOperationPhase.OpenWrite;
            writer = await resources.OpenWriteAsync(new(session, output.FileName, output.ContentType), lease.Token).ConfigureAwait(false);
            allocation = writer.Reference ?? throw ProtocolFailure();
            Fence(lease.Token);
            if (allocation.WorkspaceId != workspace) throw ProtocolFailure();
            byte[] buffer = new byte[65536];
            long completed = 0;
            while (true)
            {
                phase = InferenceResourceOperationPhase.Read;
                int read = await download.ReadAsync(buffer, lease.Token).ConfigureAwait(false);
                Fence(lease.Token);
                if (read == 0) break;
                phase = InferenceResourceOperationPhase.Write;
                written = null;
                await writer.Content.WriteAsync(buffer.AsMemory(0, read), lease.Token).ConfigureAwait(false);
                completed = checked(completed + read);
                written = completed;
                Fence(lease.Token);
            }
            phase = InferenceResourceOperationPhase.Commit;
            outcome = selectionOutcome = InferenceResourceCommitOutcome.Unknown;
            InferenceResourceCommitResult committed = await writer.CommitAsync(completed,
                new InferenceResourceCommitOptions { SelectForSession = true }, lease.Token).ConfigureAwait(false);
            if (committed.Resource.Reference != allocation || committed.Resource.SessionId != session) throw ProtocolFailure();
            outcome = InferenceResourceCommitOutcome.Committed;
            if (committed.Selection is not { } selection || selection.WorkspaceId != workspace || selection.SessionId != session
                || !selection.SelectedResources.Contains(allocation)) throw ProtocolFailure();
            selectionOutcome = InferenceResourceCommitOutcome.Committed;
            selectionVersion = new(selection.WorkspaceId, selection.SessionId, selection.Revision);
            Fence(lease.Token);
            result = committed.Resource;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var facts = new InferenceResourceOperationFailure(phase, allocation, outcome, written)
            { SelectionOutcome = selectionOutcome, SelectionVersion = selectionVersion };
            failure = error switch
            {
                InferenceResourceOperationException or InferenceResourceOperationCanceledException => error,
                OperationCanceledException => new InferenceResourceOperationCanceledException(facts, lease.Token, error),
                _ => new InferenceResourceOperationException(error is InferenceException inference ? inference.Code : InferenceFailureCode.ExecutionFailed,
                    "The video resource operation failed.", facts, error)
            };
        }
        var cleanups = new List<Exception>();
        if (download is not null)
            try { await download.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanups.Add(error); }
        if (writer is not null)
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanups.Add(error); }
        if (cleanups.Count > 0)
        {
            if (failure is not null) throw new AggregateException(new[] { failure }.Concat(cleanups));
            throw new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed, "The video resource cleanup failed.",
                new(phase, allocation, outcome, written) { SelectionOutcome = selectionOutcome, SelectionVersion = selectionVersion },
                cleanups.Count == 1 ? cleanups[0] : new AggregateException(cleanups));
        }
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        try { Fence(lease.Token); }
        catch (OperationCanceledException error)
        {
            throw new InferenceResourceOperationCanceledException(new(phase, allocation, outcome, written)
            { SelectionOutcome = selectionOutcome, SelectionVersion = selectionVersion }, lease.Token, error);
        }
        return result!;
    }

    private async Task<(VideoConnection, XaiFamilyBinding)> EnterBindingAsync(CancellationToken token)
    {
        VideoConnection connection = _connection! with { AdditionalHeaders = _connection!.AdditionalHeaders };
        XaiFamilyBinding? xai = _xai;
        ValidateBinding(connection, xai);
        XaiFamilyBinding entered = await xai!.AcquireBoundAsync(snapshot =>
        {
            if (!string.Equals(connection.Endpoint.TrimEnd('/'), snapshot.Endpoint.TrimEnd('/'), StringComparison.Ordinal))
                throw new ArgumentException("The video endpoint does not match the selected xAI region.");
            using var headers = new HttpRequestMessage();
            ProviderHttp.ApplyHeaders(headers, snapshot, applyApiKeyAsBearer: false);
        }, token).ConfigureAwait(false);
        Fence(token);
        return (connection with { Authorization = entered.Authorization }, entered);
    }

    private (VideoConnection, XaiFamilyBinding) ReceiptBinding()
    {
        VideoConnection connection = _connection! with { AdditionalHeaders = _connection!.AdditionalHeaders };
        XaiFamilyBinding? xai = _xai;
        ValidateBinding(connection, xai);
        return (connection with { Authorization = xai!.Authorization }, xai);
    }

    internal static void ValidateBinding(VideoConnection connection, XaiFamilyBinding? xai)
    {
        VideoContractData.Defined(connection.Transport);
        if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
            || connection.RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Video requires an explicit HTTPS endpoint and positive request timeout.");
        if (connection.Xai is null || xai is null || connection.Xai != xai.Selection
            || connection.Authorization is not null && !ReferenceEquals(connection.Authorization, xai.Authorization))
            throw new ArgumentException("The selected video connection requires its matching borrowed xAI binding.");
        ProviderHttp.ValidateAdditionalHeaders(connection.AdditionalHeaders);
    }

    private static VideoRequest CaptureRequest(VideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        static InferenceMediaSource Copy(InferenceMediaSource source) => source is BinaryMediaSource binary ? binary with { Data = binary.Data.ToArray() } : source;
        return request switch
        {
            VideoGenerationRequest generation => generation with
            {
                FirstFrame = generation.FirstFrame is null ? null : Copy(generation.FirstFrame),
                LastFrame = generation.LastFrame is null ? null : Copy(generation.LastFrame),
                Keyframes = generation.Keyframes?.Select(frame => frame with { Image = Copy(frame.Image) }).ToArray(),
                ReferenceImages = generation.ReferenceImages?.Select(Copy).ToArray(),
                ReferenceAudio = generation.ReferenceAudio?.Select(audio => audio is VideoAudioReference media ? media with { Source = Copy(media.Source) } : audio).ToArray()
            },
            VideoEditRequest edit => edit with { SourceVideo = Copy(edit.SourceVideo) },
            VideoExtensionRequest extension => extension with { SourceVideo = Copy(extension.SourceVideo) },
            _ => throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The video request variant is unsupported.")
        };
    }

    private static void ValidateRequest(VideoRequest request, IInferenceResourceAccess? resources)
    {
        if (request.ProviderOptions is not (null or XaiVideoOptions)) throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The video options variant is unsupported.");
        if (request is VideoExtensionRequest && request.ProviderOptions is XaiVideoOptions { User: not null })
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Video extension does not map User.");
        if (request is VideoGenerationRequest { ReferenceAudio: { } audio } && audio.Any(reference => reference is not (VideoVoiceReference or VideoAudioReference)))
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The video audio-reference variant is unsupported.");
        IEnumerable<InferenceMediaSource?> sources = request switch
        {
            VideoGenerationRequest generation => new[] { generation.FirstFrame, generation.LastFrame }
                .Concat(generation.Keyframes?.Select(frame => frame.Image) ?? [])
                .Concat(generation.ReferenceImages ?? [])
                .Concat(generation.ReferenceAudio?.OfType<VideoAudioReference>().Select(audio => audio.Source) ?? []),
            VideoEditRequest edit => [edit.SourceVideo],
            VideoExtensionRequest extension => [extension.SourceVideo],
            _ => []
        };
        foreach (InferenceMediaSource? source in sources)
        {
            if (source is null) continue;
            if (source is not (BinaryMediaSource or UrlMediaSource or FileMediaSource or ResourceMediaSource))
                throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The video source variant is unsupported.");
            if (source is ResourceMediaSource logical && (resources is null || logical.Resource.WorkspaceId != resources.WorkspaceId))
                throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Logical video sources require matching caller-owned resource access.");
        }
    }

    private static InferenceException ProtocolFailure() => new(InferenceFailureCode.ProtocolFailure, "The video provider returned mismatched or missing facts.");
    private void Fence(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("The video operation owner is retiring.", error, token); }
    }
    public void Dispose() => Retire().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(Retire());
    private Task Retire() => _lifetime.RetireAsync(() =>
    {
        _transport = null; _connection = null; _xai = null; _resources = null;
        return Task.CompletedTask;
    });
}

/// <summary>Owns the returned provider stream and keeps its operation admitted until closure.</summary>
internal sealed class VideoDownloadStream : Stream
{
    private readonly Lock _gate = new();
    private readonly LlmOperationLifetime _reads = new();
    private readonly LlmOperationLifetime.Lease _lease;
    private readonly LlmOperationLifetime _owner;
    private Stream? _stream;
    private Task? _disposal;
    private CancellationTokenRegistration _registration;

    internal VideoDownloadStream(Stream stream, LlmOperationLifetime.Lease lease, LlmOperationLifetime owner)
    {
        _stream = stream; _lease = lease; _owner = owner;
    }
    internal void ObserveRetirement() => _registration = _lease.Token.UnsafeRegister(static owner => ((VideoDownloadStream)owner!).Dispose(), this);
    private Stream Inner { get { Fence(default); return _stream ?? throw new ObjectDisposedException(nameof(VideoDownloadStream)); } }
    private void Fence(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); _lease.Token.ThrowIfCancellationRequested();
        try { _owner.ThrowIfStopping(); _reads.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("The video download owner is retiring.", error, _lease.Token); }
    }
    public override bool CanRead => _stream?.CanRead == true;
    public override bool CanSeek => _stream?.CanSeek == true;
    public override bool CanWrite => false;
    public override long Length { get { using LlmOperationLifetime.Lease read = _reads.Enter(default); long result = Inner.Length; Fence(read.Token); return result; } }
    public override long Position
    {
        get { using LlmOperationLifetime.Lease read = _reads.Enter(default); long result = Inner.Position; Fence(read.Token); return result; }
        set { using LlmOperationLifetime.Lease read = _reads.Enter(default); Inner.Position = value; Fence(read.Token); }
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        using LlmOperationLifetime.Lease read = _reads.Enter(default);
        int result = Inner.Read(buffer, offset, count);
        Fence(read.Token);
        return result;
    }
    public override int Read(Span<byte> buffer)
    {
        using LlmOperationLifetime.Lease read = _reads.Enter(default);
        int result = Inner.Read(buffer);
        Fence(read.Token);
        return result;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease read = _reads.Enter(cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lease.Token, read.Token);
        int count = await Inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        Fence(linked.Token);
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin)
    {
        using LlmOperationLifetime.Lease read = _reads.Enter(default);
        long result = Inner.Seek(offset, origin);
        Fence(read.Token);
        return result;
    }
    public override void Flush() => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) Retire().GetAwaiter().GetResult(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync()
    {
        await Retire().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
    private Task Retire()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposal is not null) return _disposal;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
        }
        _ = ReleaseAsync(completion);
        return completion.Task;
    }
    private async Task ReleaseAsync(TaskCompletionSource completion)
    {
        Task drained = _reads.RetireAsync();
        Stream? stream = Interlocked.Exchange(ref _stream, null);
        _registration.Unregister();
        Task close;
        try { close = stream?.DisposeAsync().AsTask() ?? Task.CompletedTask; }
        catch (Exception failure) { close = Task.FromException(failure); }
        Task both = Task.WhenAll(close, drained);
        try
        {
            await both.ConfigureAwait(false);
            _lease.Dispose();
            completion.TrySetResult();
        }
        catch (Exception failure)
        {
            _lease.Dispose();
            completion.TrySetException(both.Exception is { InnerExceptions.Count: > 1 } errors ? errors : failure);
        }
    }
}
