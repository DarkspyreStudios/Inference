using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.Video.Internal;

namespace Darkspyre.Inference.Connect.Video;

internal sealed class ConnectVideoInference : IVideoInference, IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private IVideoTransport? _transport;
    private VideoConnection? _connection;
    private XaiFamilyBinding? _xai;
    private IInferenceResourceAccess? _resources;
    private readonly List<(Exception Original, Exception Reported)> _retirementFailures = [];

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
        try
        {
            VideoCapabilities capabilities = await _transport!.GetCapabilitiesAsync(connection, modelId, binding, lease.Token).ConfigureAwait(false);
            Fence(lease.Token);
            return capabilities ?? throw ProtocolFailure();
        }
        finally { binding.DetachOperationFence(); }
    }

    public async Task<VideoJob> SubmitAsync(VideoRequest request, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        request = CaptureRequest(request);
        ValidateRequest(request, _resources, lease.Token);
        (VideoConnection connection, XaiFamilyBinding binding) = await EnterBindingAsync(lease.Token).ConfigureAwait(false);
        try
        {
            VideoJob result = await _transport!.SubmitAsync(connection, request, binding, _resources, lease.Token).ConfigureAwait(false);
            Fence(lease.Token);
            return result ?? throw ProtocolFailure();
        }
        finally { binding.DetachOperationFence(); }
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
            var owned = new VideoDownloadStream(stream, lease, _lifetime, error => RecordFailure(error, ownedRetirement: true));
            stream = owned;
            owned.ObserveRetirement();
            Fence(lease.Token);
            stream = null;
            return owned;
        }
        catch (Exception original)
        {
            RecordFailure(original);
            try { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                Exception refused = LlmResourcePreparation.AsCleanupFailure(cleanup, refusedOwner: stream);
                RecordFailure(refused);
                throw new AggregateException(original, refused);
            }
            finally { lease.Dispose(); }
            throw;
        }
    }

    public async Task<InferenceResourceDescriptor> MaterializeAsync(VideoOutput output, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        try { return await MaterializeAdmittedAsync(output, lease).ConfigureAwait(false); }
        catch (Exception error) { RecordFailure(error); throw; }
    }

    private async Task<InferenceResourceDescriptor> MaterializeAdmittedAsync(VideoOutput output, LlmOperationLifetime.Lease lease)
    {
        ArgumentNullException.ThrowIfNull(output);
        IInferenceResourceAccess resources = _resources ?? throw new InferenceException(InferenceFailureCode.UnsupportedFeature,
            "Video materialization requires caller-owned resource access.");
        (VideoConnection connection, XaiFamilyBinding binding) = ReceiptBinding();
        string? workspace = null;
        string? session = null;
        Stream? download = null;
        IInferenceResourceWriteHandle? writer = null;
        InferenceResourceReference? allocation = null;
        InferenceResourceDescriptor? result = null;
        InferenceResourceCommitResult? receipt = null;
        IReadOnlyList<InferenceResourceCommitResult> retained = [];
        Exception? failure = null;
        InferenceResourceOperationPhase phase = InferenceResourceOperationPhase.Read;
        InferenceResourceCommitOutcome outcome = InferenceResourceCommitOutcome.NotAttempted;
        InferenceResourceCommitOutcome selectionOutcome = InferenceResourceCommitOutcome.NotAttempted;
        InferenceResourceSelectionVersion? selectionVersion = null;
        long? written = 0;
        try
        {
            Fence(lease.Token);
            workspace = resources.WorkspaceId;
            Fence(lease.Token);
            session = resources.SessionId;
            CheckScope();
            download = await _transport!.DownloadAsync(connection, output, binding, lease.Token).ConfigureAwait(false) ?? throw ProtocolFailure();
            CheckScope();
            if (!download.CanRead) throw ProtocolFailure();
            CheckScope();
            phase = InferenceResourceOperationPhase.OpenWrite;
            writer = await resources.OpenWriteAsync(new(session, output.FileName, output.ContentType), lease.Token).ConfigureAwait(false);
            CheckScope();
            if (writer is null) throw ProtocolFailure();
            allocation = writer.Reference ?? throw ProtocolFailure();
            CheckScope();
            if (allocation.WorkspaceId != workspace || string.IsNullOrEmpty(allocation.Id)) throw ProtocolFailure();
            Stream content = writer.Content;
            CheckScope();
            if (content is null || !content.CanWrite) throw ProtocolFailure();
            CheckScope();
            byte[] buffer = new byte[65536];
            long completed = 0;
            while (true)
            {
                phase = InferenceResourceOperationPhase.Read;
                int read = await download.ReadAsync(buffer, lease.Token).ConfigureAwait(false);
                CheckScope();
                if (read < 0 || read > buffer.Length) throw ProtocolFailure();
                if (read == 0) break;
                phase = InferenceResourceOperationPhase.Write;
                written = null;
                await content.WriteAsync(buffer.AsMemory(0, read), lease.Token).ConfigureAwait(false);
                completed = checked(completed + read);
                written = completed;
                CheckScope();
            }
            phase = InferenceResourceOperationPhase.Commit;
            outcome = selectionOutcome = InferenceResourceCommitOutcome.Unknown;
            InferenceResourceCommitResult committed = await writer.CommitAsync(completed,
                new InferenceResourceCommitOptions { SelectForSession = true }, lease.Token).ConfigureAwait(false);
            // Keep actual effects even if a later fence refuses publication.
            if (committed?.Resource?.Reference == allocation)
            {
                outcome = InferenceResourceCommitOutcome.Committed;
                if (committed.Selection is { } selected && selected.WorkspaceId == workspace && selected.SessionId == session
                    && selected.Revision >= 0 && selected.SelectedResources.Contains(allocation))
                {
                    selectionOutcome = InferenceResourceCommitOutcome.Committed;
                    selectionVersion = new(selected.WorkspaceId, selected.SessionId, selected.Revision);
                }
            }
            result = committed?.Resource;
            if (result is null || result.Reference != allocation || result.SessionId is { } reportedSession && reportedSession != session
                || result.Bytes is { } bytes && bytes != completed
                || XaiMediaData.ContentTypesConflict(result.ContentType, output.ContentType)
                || selectionOutcome != InferenceResourceCommitOutcome.Committed) throw ProtocolFailure();
            var selectedReceipt = new InferenceResourceSelection(workspace!, session!, committed!.Selection!.Revision, committed.Selection.SelectedResources);
            if (committed.Selection.SelectedResources.Any(reference => string.IsNullOrEmpty(reference.Id))) throw ProtocolFailure();
            receipt = new(result, selectedReceipt);
            CheckScope();
        }
        catch (Exception error)
        {
            failure = error is InferenceResourceOperationException or InferenceResourceOperationCanceledException ? error : Wrap(error, phase);
            InferenceResourceOperationFailure? observed = error switch
            {
                InferenceResourceOperationException resource => resource.Failure,
                InferenceResourceOperationCanceledException resource => resource.Failure,
                _ => null
            };
            if (observed is not null)
            {
                retained = observed.GeneratedResources;
                if (receipt is null)
                {
                    outcome = observed.CommitOutcome; selectionOutcome = observed.SelectionOutcome;
                    selectionVersion = observed.SelectionVersion; written = observed.BytesTransferred;
                }
            }
        }
        var failures = new List<Exception>();
        if (failure is not null) failures.Add(WithReceipts(failure));
        if (download is not null)
            try { await download.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(WithReceipts(Cleanup(error, download))); }
        if (writer is not null)
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(WithReceipts(Cleanup(error, writer))); }
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException(failures);
        try { CheckScope(); }
        catch (Exception error) { ExceptionDispatchInfo.Throw(Wrap(error, phase)); }
        return result!;

        void CheckScope()
        {
            Fence(lease.Token);
            if (string.IsNullOrEmpty(workspace) || string.IsNullOrEmpty(session)) throw ProtocolFailure();
            string currentWorkspace = resources.WorkspaceId;
            Fence(lease.Token);
            if (currentWorkspace != workspace) throw ProtocolFailure();
            string currentSession = resources.SessionId;
            Fence(lease.Token);
            if (currentSession != session) throw ProtocolFailure();
        }
        Exception Wrap(Exception error, InferenceResourceOperationPhase actualPhase)
        {
            var facts = new InferenceResourceOperationFailure(actualPhase, allocation, outcome, written)
            {
                SelectionOutcome = selectionOutcome,
                SelectionVersion = selectionVersion,
                GeneratedResources = receipt is null ? retained : [receipt]
            };
            return error is OperationCanceledException canceled
                ? new InferenceResourceOperationCanceledException(facts, canceled.CancellationToken, error)
                : new InferenceResourceOperationException(error is InferenceException inference ? inference.Code : InferenceFailureCode.ExecutionFailed,
                    "The video resource operation failed.", facts, error);
        }
        Exception Cleanup(Exception error, object actualOwner) => LlmResourcePreparation.AsCleanupFailure(
            LlmResourcePreparation.IsCleanupFailure(error) ? error : Wrap(error, InferenceResourceOperationPhase.Cleanup), allocation, actualOwner);
        Exception WithReceipts(Exception error) => LlmResourcePreparation.WithGeneratedResources(error,
            receipt is null ? retained : [receipt]);
    }

    private async Task<(VideoConnection, XaiFamilyBinding)> EnterBindingAsync(CancellationToken token)
    {
        VideoConnection connection = _connection! with { AdditionalHeaders = _connection!.AdditionalHeaders };
        XaiFamilyBinding? xai = _xai;
        ValidateBinding(connection, xai);
        XaiFamilyBinding entered = await xai!.AcquireBoundAsync(snapshot => ValidateSelected(connection, snapshot),
            () => Fence(token), token).ConfigureAwait(false);
        try
        {
            Fence(token);
            return (connection with { Authorization = entered.Authorization }, entered);
        }
        catch { entered.DetachOperationFence(); throw; }
    }

    internal static void ValidateSelected(VideoConnection connection, LlmConnection snapshot)
    {
        if (!string.Equals(connection.Endpoint.TrimEnd('/'), snapshot.Endpoint.TrimEnd('/'), StringComparison.Ordinal))
            throw new ArgumentException("The video endpoint does not match the selected xAI region.");
        using var headers = new HttpRequestMessage();
        ApplyHeaders(headers, connection, snapshot);
    }

    internal static void ApplyHeaders(HttpRequestMessage request, VideoConnection connection, LlmConnection snapshot)
    {
        XaiRequestHeaders.Apply(request, snapshot, false);
        LlmCredential? credential = LlmCredential.Parse(snapshot.Credential);
        foreach ((string name, string value) in connection.AdditionalHeaders)
        {
            if (credential?.SecretHeaders.Keys.Contains(name, StringComparer.OrdinalIgnoreCase) is true)
                throw new ArgumentException("A video additional header conflicts with a selected secret header.");
            if (!request.Headers.TryAddWithoutValidation(name, value)) throw new ArgumentException("A video header cannot be applied.");
        }
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
            || connection.RequestTimeout <= TimeSpan.Zero || connection.RequestTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentException("Video requires an explicit HTTPS endpoint and positive request timeout.");
        if (connection.MaximumEncodedReferenceAudioBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(connection), "The encoded reference audio limit must be positive when supplied.");
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

    private void ValidateRequest(VideoRequest request, IInferenceResourceAccess? resources, CancellationToken token)
    {
        try { XaiVideoProtocol.Validate(request); }
        catch (NotSupportedException error)
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The video request uses an unsupported native control or source.", error);
        }
        if (request is VideoGenerationRequest { ReferenceAudio: { } audio }
            && audio.OfType<VideoAudioReference>().Any(reference => reference.Source is BinaryMediaSource or ResourceMediaSource)
            && _connection!.MaximumEncodedReferenceAudioBytes is not > 0)
            throw new ArgumentException("Supplied binary or workspace reference audio requires a positive encoded byte limit on the video connection.", nameof(request));
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
            Fence(token);
            if (source is null) continue;
            if (source is BinaryMediaSource binary)
            {
                if (binary.FileName is { } name) XaiFileRequestData.FileName(name);
                if (binary.ContentType is { } type && !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(type, out _))
                    throw new ArgumentException("The supplied video media MIME type is invalid.", nameof(request));
            }
            if (source is FileMediaSource file) XaiFileRequestData.Identity(file.File.FileId);
            if (source is ResourceMediaSource logical)
            {
                if (resources is null)
                    throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Logical video sources require matching caller-owned resource access.");
                LlmResourcePreparation.ValidateNamedReference(logical.Resource, resources);
                string workspace = resources.WorkspaceId;
                Fence(token);
                if (logical.Resource.WorkspaceId != workspace)
                    throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Logical video sources require matching caller-owned resource access.");
            }
        }
        Fence(token);
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
        if (_retirementFailures.Count == 1) return Task.FromException(_retirementFailures[0].Reported);
        if (_retirementFailures.Count > 1) return Task.FromException(new AggregateException(_retirementFailures.Select(item => item.Reported)));
        return Task.CompletedTask;
    });

    private void RecordFailure(Exception error, bool ownedRetirement = false)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        lock (_retirementFailures)
        {
            while (pending.TryPop(out Exception? current))
            {
                if (!visited.Add(current)) continue;
                bool cleanup = current is InferenceResourceOperationException resource && resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup
                    || current is InferenceResourceOperationCanceledException canceled && canceled.Failure.Phase == InferenceResourceOperationPhase.Cleanup;
                if (cleanup || ownedRetirement && current is not AggregateException)
                {
                    Exception actual = cleanup ? LlmResourcePreparation.CleanupCause(current) : current;
                    if (!_retirementFailures.Any(existing => ReferenceEquals(existing.Original, actual))) _retirementFailures.Add((actual, current));
                }
                else if (current is AggregateException aggregate)
                    for (int index = aggregate.InnerExceptions.Count - 1; index >= 0; index--) pending.Push(aggregate.InnerExceptions[index]);
                else if (current.InnerException is { } inner) pending.Push(inner);
            }
        }
    }
}

/// <summary>Owns the returned provider stream and keeps its operation admitted until closure.</summary>
internal sealed class VideoDownloadStream : Stream
{
    private readonly Lock _gate = new();
    private readonly LlmOperationLifetime _reads = new();
    private readonly LlmOperationLifetime.Lease _lease;
    private readonly LlmOperationLifetime _owner;
    private readonly Action<Exception> _recordFailure;
    private Stream? _stream;
    private Task? _disposal;
    private CancellationTokenRegistration _registration;

    internal VideoDownloadStream(Stream stream, LlmOperationLifetime.Lease lease, LlmOperationLifetime owner, Action<Exception> recordFailure)
    {
        _stream = stream; _lease = lease; _owner = owner; _recordFailure = recordFailure;
    }
    internal void ObserveRetirement() => _registration = _lease.Token.UnsafeRegister(static owner => _ = ((VideoDownloadStream)owner!).RetireFromOwnerAsync(), this);
    // The facade records the owned retirement outcome before this stream releases its lease.
    private async Task RetireFromOwnerAsync() => await Retire().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    private Stream Inner { get { Fence(default); return _stream ?? throw new ObjectDisposedException(nameof(VideoDownloadStream)); } }
    private void Fence(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); _lease.Token.ThrowIfCancellationRequested();
        try { _owner.ThrowIfStopping(); _reads.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("The video download owner is retiring.", error, _lease.Token); }
    }
    public override bool CanRead
    {
        get
        {
            if (_stream is null) return false;
            using LlmOperationLifetime.Lease read = _reads.Enter(default);
            bool result = Inner.CanRead;
            Fence(read.Token);
            return result;
        }
    }
    public override bool CanSeek
    {
        get
        {
            if (_stream is null) return false;
            using LlmOperationLifetime.Lease read = _reads.Enter(default);
            bool result = Inner.CanSeek;
            Fence(read.Token);
            return result;
        }
    }
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
        close = stream is null ? Task.CompletedTask : CloseAsync(stream);
        Task both = Task.WhenAll(close, drained);
        try
        {
            await both.ConfigureAwait(false);
            _lease.Dispose();
            completion.TrySetResult();
        }
        catch (Exception failure)
        {
            Exception actual = both.Exception is { InnerExceptions.Count: > 1 } errors ? errors : failure;
            _recordFailure(actual);
            _lease.Dispose();
            completion.TrySetException(actual);
        }
    }

    private static async Task CloseAsync(Stream stream)
    {
        try { await stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { ExceptionDispatchInfo.Throw(LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: stream)); }
    }
}
