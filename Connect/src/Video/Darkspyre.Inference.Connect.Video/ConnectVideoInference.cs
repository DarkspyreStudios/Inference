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
        ValidateRequest(_connection!, request, _resources, () => Fence(lease.Token), lease.Token);
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
        (VideoConnection connection, XaiFamilyBinding binding) = ReceiptBinding(lease.Token);
        try
        {
            VideoJobSnapshot result = await _transport!.GetAsync(connection, job, binding, lease.Token).ConfigureAwait(false);
            Fence(lease.Token);
            if (result is null || result.Job != job) throw ProtocolFailure();
            return result;
        }
        finally { binding.DetachOperationFence(); }
    }

    public async Task<Stream> DownloadAsync(VideoOutput output, CancellationToken cancellationToken = default)
    {
        LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        Stream? stream = null;
        XaiFamilyBinding? binding = null;
        try
        {
            ArgumentNullException.ThrowIfNull(output);
            VideoConnection connection;
            (connection, binding) = ReceiptBinding(lease.Token);
            stream = await OpenDownloadAsync(connection, output, binding, lease.Token).ConfigureAwait(false);
            Fence(lease.Token);
            var owned = new VideoDownloadStream(stream, lease, _lifetime, error => RecordFailure(error, ownedRetirement: true), binding.DetachOperationFence);
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
            finally { binding?.DetachOperationFence(); lease.Dispose(); }
            throw;
        }
    }

    public async Task<InferenceResourceDescriptor> MaterializeAsync(VideoOutput output, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        (VideoConnection connection, XaiFamilyBinding binding) = ReceiptBinding(lease.Token);
        try { return await MaterializeAdmittedAsync(output, connection, binding, lease).ConfigureAwait(false); }
        catch (Exception error) { RecordFailure(error); throw; }
        finally { binding.DetachOperationFence(); }
    }

    private async Task<InferenceResourceDescriptor> MaterializeAdmittedAsync(VideoOutput output, VideoConnection connection,
        XaiFamilyBinding binding, LlmOperationLifetime.Lease lease)
    {
        ArgumentNullException.ThrowIfNull(output);
        IInferenceResourceAccess resources = _resources ?? throw new InferenceException(InferenceFailureCode.UnsupportedFeature,
            "Video materialization requires caller-owned resource access.");
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
            download = await OpenDownloadAsync(connection, output, binding, lease.Token).ConfigureAwait(false);
            CheckScope();
            if (!download.CanRead) throw ProtocolFailure();
            CheckScope();
            string? contentType = output.ContentType ?? (download as VideoDownloadStream)?.ObservedContentType;
            string? fileName = output.FileName ?? (download as VideoDownloadStream)?.ObservedFileName;
            phase = InferenceResourceOperationPhase.OpenWrite;
            writer = await resources.OpenWriteAsync(new(session, fileName, contentType), lease.Token).ConfigureAwait(false);
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
            if ((download as VideoDownloadStream)?.ObservedLength is { } sourceLength && sourceLength != completed)
                throw ProtocolFailure();
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
                || XaiMediaData.ContentTypesConflict(result.ContentType, contentType)
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
        ApplyAdditionalHeaders(request, connection, snapshot);
    }

    internal static void ApplyAdditionalHeaders(HttpRequestMessage request, VideoConnection connection, LlmConnection snapshot)
    {
        LlmCredential? credential = LlmCredential.Parse(snapshot.Credential);
        foreach ((string name, string value) in connection.AdditionalHeaders)
        {
            if (credential?.SecretHeaders.Keys.Contains(name, StringComparer.OrdinalIgnoreCase) is true)
                throw new ArgumentException("A video additional header conflicts with a selected secret header.");
            if (!request.Headers.TryAddWithoutValidation(name, value)) throw new ArgumentException("A video header cannot be applied.");
        }
    }

    private (VideoConnection, XaiFamilyBinding) ReceiptBinding(CancellationToken token)
    {
        VideoConnection connection = _connection! with { AdditionalHeaders = _connection!.AdditionalHeaders };
        XaiFamilyBinding? xai = _xai;
        ValidateBinding(connection, xai);
        XaiFamilyBinding entered = xai!.EnterOperation(() => Fence(token));
        return (connection with { Authorization = entered.Authorization }, entered);
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

    internal static VideoRequest CaptureRequest(VideoRequest request)
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

    internal static void ValidateRequest(VideoConnection connection, VideoRequest request,
        IInferenceResourceAccess? resources, Action fence, CancellationToken token)
    {
        try { XaiVideoProtocol.Validate(request); }
        catch (NotSupportedException error)
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The video request uses an unsupported native control or source.", error);
        }
        if (request is VideoGenerationRequest { ReferenceAudio: { } audio }
            && audio.OfType<VideoAudioReference>().Any(reference => reference.Source is BinaryMediaSource or ResourceMediaSource)
            && connection.MaximumEncodedReferenceAudioBytes is not > 0)
            throw new ArgumentException("Supplied binary or workspace reference audio requires a positive encoded byte limit on the video connection.", nameof(request));
        if (request is VideoGenerationRequest { ReferenceAudio: { } references }
            && connection.MaximumEncodedReferenceAudioBytes is { } maximum)
        {
            long encoded = 0;
            foreach (BinaryMediaSource binary in references.OfType<VideoAudioReference>().Select(reference => reference.Source).OfType<BinaryMediaSource>())
            {
                encoded = checked(encoded + System.Text.Encoding.UTF8.GetByteCount($"data:{binary.ContentType ?? "application/octet-stream"};base64,")
                    + ((long)binary.Data.Length + 2) / 3 * 4);
                if (encoded > maximum) throw new ArgumentException("Supplied audio exceeds its encoded reference byte limit.", nameof(request));
            }
        }
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
            Check();
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
                Check();
                if (logical.Resource.WorkspaceId != workspace)
                    throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Logical video sources require matching caller-owned resource access.");
            }
        }
        Check();
        void Check() { token.ThrowIfCancellationRequested(); fence(); token.ThrowIfCancellationRequested(); }
    }

    private async Task<Stream> OpenDownloadAsync(VideoConnection connection, VideoOutput output, XaiFamilyBinding binding, CancellationToken token)
        => await (output.Resource is null ? _transport!.DownloadAsync(connection, output, binding, token)
            : OpenResourceDownloadAsync(output, token)).ConfigureAwait(false) ?? throw ProtocolFailure();

    private async Task<Stream> OpenResourceDownloadAsync(VideoOutput output, CancellationToken token)
    {
        IInferenceResourceAccess access = _resources ?? throw new InferenceException(InferenceFailureCode.UnsupportedFeature,
            "Portable video output requires caller-owned resource access.");
        var reads = LlmResourcePreparation.ForNamedReads();
        try
        {
            var opened = await reads.OpenNamedAsync(access, output.Resource!.Reference, descriptor =>
            {
                if (XaiMediaData.ContentTypesConflict(descriptor.ContentType, output.ContentType)
                    || XaiMediaData.ContentTypesConflict(descriptor.ContentType, output.Resource.ContentType)
                    || output.Resource.SessionId is { } session && descriptor.SessionId is { } actualSession && session != actualSession
                    || output.Resource.Bytes is { } bytes && descriptor.Bytes is { } actualBytes && bytes != actualBytes)
                    throw ProtocolFailure();
            }, () => Fence(token), token).ConfigureAwait(false);
            Fence(token);
            return new VideoDownloadStream(opened.Content, reads, () => Fence(token),
                opened.Descriptor.ContentType, opened.Descriptor.FileName, opened.Descriptor.Bytes, token);
        }
        catch (Exception original)
        {
            try { await reads.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
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
    private readonly LlmOperationLifetime.Lease? _lease;
    private readonly LlmOperationLifetime? _owner;
    private readonly CancellationToken _token;
    private Action<Exception>? _recordFailure;
    private Action? _onDrained;
    private Action? _externalFence;
    private XaiHttpDownload? _httpDownload;
    private LlmResourcePreparation? _namedReads;
    private Stream? _stream;
    private Task? _disposal;
    private CancellationTokenRegistration _registration;

    internal VideoDownloadStream(Stream stream, LlmOperationLifetime.Lease lease, LlmOperationLifetime owner,
        Action<Exception> recordFailure, Action onDrained)
    {
        _stream = stream; _lease = lease; _owner = owner; _token = lease.Token;
        _recordFailure = recordFailure; _onDrained = onDrained;
        if (stream is VideoDownloadStream owned)
        { ObservedContentType = owned.ObservedContentType; ObservedFileName = owned.ObservedFileName; ObservedLength = owned.ObservedLength; }
    }
    internal VideoDownloadStream(Stream stream, LlmResourcePreparation namedReads, Action fence,
        string? contentType, string? fileName, long? length, CancellationToken token)
    {
        _stream = stream; _namedReads = namedReads; _externalFence = fence; _token = token;
        ObservedContentType = contentType; ObservedFileName = fileName; ObservedLength = length;
        ObserveRetirement();
    }
    internal VideoDownloadStream(Stream stream, XaiHttpDownload download, Action<Exception> recordFailure,
        Action onDrained, string? fileName)
    {
        _stream = stream; _httpDownload = download; _token = download.Token;
        _recordFailure = recordFailure; _onDrained = onDrained;
        ObservedContentType = download.ContentType; ObservedLength = download.DeclaredLength; ObservedFileName = fileName;
        ObserveRetirement();
    }
    internal string? ObservedContentType { get; }
    internal string? ObservedFileName { get; }
    internal long? ObservedLength { get; }
    internal void ObserveRetirement()
    {
        CancellationTokenRegistration registration = _token.UnsafeRegister(static owner => _ = ((VideoDownloadStream)owner!).RetireFromOwnerAsync(), this);
        lock (_gate)
        {
            if (_disposal is null) _registration = registration;
            else registration.Unregister();
        }
    }
    // The facade records the owned retirement outcome before this stream releases its lease.
    private async Task RetireFromOwnerAsync() => await Retire().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    private Stream Inner { get { Fence(default); return _stream ?? throw new ObjectDisposedException(nameof(VideoDownloadStream)); } }
    private void Fence(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); _token.ThrowIfCancellationRequested();
        try { _owner?.ThrowIfStopping(); _reads.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("The video download owner is retiring.", error, _token); }
        _externalFence?.Invoke();
        _httpDownload?.Fence();
        token.ThrowIfCancellationRequested(); _token.ThrowIfCancellationRequested();
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, read.Token);
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
        close = _httpDownload is { } http ? http.CloseAsync()
            : _namedReads is { } named ? named.DisposeAsync().AsTask()
            : stream is null ? Task.CompletedTask : CloseAsync(stream);
        Task both = Task.WhenAll(close, drained);
        try
        {
            await both.ConfigureAwait(false);
            ReleaseOwners();
            completion.TrySetResult();
        }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            if (close.Exception is { } closing) errors.AddRange(closing.InnerExceptions);
            if (drained.Exception is { } stopping)
                foreach (Exception error in stopping.InnerExceptions)
                    errors.Add(_httpDownload is { } download ? download.RecordReadCleanup(error, _reads)
                        : LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: _reads));
            Exception actual = errors.Count > 1 ? new AggregateException(errors) : errors.Count == 1 ? errors[0] : failure;
            _recordFailure?.Invoke(actual);
            ReleaseOwners();
            completion.TrySetException(actual);
        }
    }

    private void ReleaseOwners()
    {
        Action? onDrained = _onDrained;
        _onDrained = null; _externalFence = null; _recordFailure = null; _namedReads = null;
        XaiHttpDownload? download = _httpDownload;
        _httpDownload = null;
        try { download?.ReleaseAfterDrain(); }
        finally
        {
            try { onDrained?.Invoke(); }
            finally { _lease?.Dispose(); }
        }
    }

    private static async Task CloseAsync(Stream stream)
    {
        try { await stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { ExceptionDispatchInfo.Throw(LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: stream)); }
    }
}
