using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.Resources.Internal;

/// <summary>One output transfer owns its staging handle, not the borrowed access or Files client.</summary>
internal sealed class XaiGeneratedFileWriter(IInferenceResourceAccess access, InferenceResourceSelectionVersion scope,
    string? fileName, Action fence, XaiResourceHttp.Observation observation,
    Action<InferenceResourceCommitResult>? onCommit = null) : Stream
{
    private IInferenceResourceAccess? _access = access;
    private Action? _fence = fence;
    private Action<InferenceResourceCommitResult>? _onCommit = onCommit;
    private IInferenceResourceWriteHandle? _handle;
    private Stream? _content;
    private InferenceResourceReference? _reference;
    private string? _contentType;
    private long? _bytes = 0;
    private InferenceResourceCommitOutcome _commit = InferenceResourceCommitOutcome.NotAttempted;
    private InferenceResourceCommitOutcome _selection = InferenceResourceCommitOutcome.NotAttempted;
    private InferenceResourceSelectionVersion? _version;

    internal Task<InferenceResourceCommitResult> MaterializeAsync(XaiFileClient files, LlmConnection connection,
        string fileId, CancellationToken token) => MaterializeAsync(async current =>
        {
            observation.BeginStage("GET files/content");
            ResourceDownloadReceipt downloaded = await files.DownloadToAsync(connection, fileId, OpenAsync, () => Check(current),
                () => observation.RequestDispatched = true,
                response => observation.ObserveResponse(response, connection), current).ConfigureAwait(false);
            return downloaded.BytesWritten;
        }, token);

    internal Task<InferenceResourceCommitResult> MaterializeAsync(ReadOnlyMemory<byte> bytes, string? contentType,
        CancellationToken token) => MaterializeAsync(async current =>
        {
            await OpenAsync(contentType, bytes.Length, current).ConfigureAwait(false);
            await WriteAsync(bytes, current).ConfigureAwait(false);
            return bytes.Length;
        }, token);

    internal Task<InferenceResourceCommitResult> MaterializeAsync(XaiMediaDownloadClient media, LlmConnection connection,
        string url, CancellationToken token) => MaterializeAsync(current =>
        {
            observation.BeginStage("GET unsigned media");
            return media.DownloadToAsync(url, ProviderHttp.GenerationTimeout, OpenAsync, () => Check(current),
                () => observation.RequestDispatched = true,
                response => observation.ObserveResponse(response, connection), current);
        }, token);

    [SuppressMessage("Design", "CA1031", Justification = "The transfer preserves operation failure followed by actual owned handle cleanup failure.")]
    private async Task<InferenceResourceCommitResult> MaterializeAsync(Func<CancellationToken, Task<long>> transfer,
        CancellationToken token)
    {
        Action operationFence = _fence!;
        InferenceResourceCommitResult? result = null;
        var errors = new List<Exception>();
        try
        {
            long downloaded;
            try
            {
                downloaded = await transfer(token).ConfigureAwait(false);
            }
            catch (XaiFileClient.ExchangeFailure error)
            {
                throw XaiResourceHttp.CombineFailures(new[] { DownloadFailure(error.InnerExceptions[0]) }
                    .Concat(error.InnerExceptions.Skip(1)));
            }
            catch (AggregateException error)
            {
                throw XaiResourceHttp.CombineFailures(new[] { DownloadFailure(error.InnerExceptions[0]) }
                    .Concat(error.InnerExceptions.Skip(1)));
            }
            catch (Exception error) { throw DownloadFailure(error); }
            Check(token);
            if (_handle is null || _bytes != downloaded) throw Invalid();
            _commit = _selection = InferenceResourceCommitOutcome.Unknown;
            try
            {
                result = await _handle.CommitAsync(downloaded, new() { SelectForSession = true }, token).ConfigureAwait(false);
                // An actual returned receipt precedes cancellation, validation and cleanup publication fences.
                if (result is { Resource: { } observedResource } && observedResource.Reference == _reference)
                {
                    _commit = InferenceResourceCommitOutcome.Committed;
                    observation.ObserveCommit(result);
                    if (result.Selection is { } selected && selected.WorkspaceId == scope.WorkspaceId
                        && selected.SessionId == scope.SessionId && selected.SelectedResources.Contains(_reference))
                    {
                        _selection = InferenceResourceCommitOutcome.Committed;
                        _version = new(selected.WorkspaceId, selected.SessionId, selected.Revision);
                    }
                }
                if (result?.Resource is not { } resource || resource.Reference != _reference
                    || resource.SessionId is { } session && session != scope.SessionId
                    || resource.Bytes is { } size && size != _bytes
                    || fileName is not null && resource.FileName is { } name && name != fileName
                    || _contentType is not null && resource.ContentType is { } type && type != _contentType
                    || _selection != InferenceResourceCommitOutcome.Committed) throw Invalid();
                _onCommit?.Invoke(result);
                Check(token);
            }
            catch (Exception error) { throw Failure(error, InferenceResourceOperationPhase.Commit); }
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            try { await DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { errors.Add(cleanup); }
        }
        if (errors.Count == 1) ExceptionDispatchInfo.Throw(errors[0]);
        if (errors.Count > 1) throw XaiResourceHttp.CombineFailures(errors);
        operationFence(); token.ThrowIfCancellationRequested();
        return result!;
    }

    [SuppressMessage("Design", "CA1031", Justification = "Actual resource callbacks are classified at their owned open-write boundary.")]
    private async Task<Stream> OpenAsync(string? contentType, long? expectedBytes, CancellationToken token)
    {
        try
        {
            Check(token);
            _contentType = contentType;
            _handle = await _access!.OpenWriteAsync(new(scope.SessionId, fileName, contentType, expectedBytes), token).ConfigureAwait(false);
            Check(token);
            if (_handle is null) throw Invalid();
            _reference = _handle.Reference;
            Check(token);
            if (_reference is null || _reference.WorkspaceId != scope.WorkspaceId || string.IsNullOrEmpty(_reference.Id)) throw Invalid();
            _content = _handle.Content;
            Check(token);
            if (_content is null || !_content.CanWrite) throw Invalid();
            Check(token);
            return this;
        }
        catch (Exception error) { throw Failure(error, InferenceResourceOperationPhase.OpenWrite); }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _content is not null;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    [SuppressMessage("Design", "CA1031", Justification = "Successful writes are counted; refused writes preserve unknown byte totals and original causes.")]
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            Check(cancellationToken);
            long before = _bytes!.Value;
            _bytes = null;
            await _content!.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _bytes = checked(before + buffer.Length);
            Check(cancellationToken);
        }
        catch (Exception error) { throw Failure(error, InferenceResourceOperationPhase.Write); }
    }

    [SuppressMessage("Design", "CA1031", Justification = "A refused actual owned handle is retained by the established Cleanup9 carrier; its stream is never separately disposed.")]
    public override async ValueTask DisposeAsync()
    {
        IInferenceResourceWriteHandle? handle = _handle;
        _handle = null; _content = null; _access = null; _fence = null; _onCommit = null;
        try
        {
            if (handle is null) return;
            try { await handle.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                throw LlmResourcePreparation.AsCleanupFailure(Failure(error, InferenceResourceOperationPhase.Cleanup), _reference, handle);
            }
        }
        finally { await base.DisposeAsync().ConfigureAwait(false); }
        GC.SuppressFinalize(this);
    }

    private void Check(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_access is null || _fence is null, this);
        LlmResourcePreparation.ValidateScope(_access, scope, _fence, token);
    }

    private Exception Failure(Exception error, InferenceResourceOperationPhase phase)
    {
        if (phase != InferenceResourceOperationPhase.Cleanup
            && error is InferenceResourceOperationException or InferenceResourceOperationCanceledException)
        {
            InferenceResourceOperationFailure observed = error is InferenceResourceOperationException resource
                ? resource.Failure : ((InferenceResourceOperationCanceledException)error).Failure;
            _commit = observed.CommitOutcome; _selection = observed.SelectionOutcome;
            _version = observed.SelectionVersion; _bytes = observed.BytesTransferred;
            return error;
        }
        var facts = new InferenceResourceOperationFailure(phase, _reference, _commit, _bytes)
        { SelectionOutcome = _selection, SelectionVersion = _version };
        return error is OperationCanceledException canceled
            ? new InferenceResourceOperationCanceledException(facts, canceled.CancellationToken, error)
            : new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed, "The generated file could not be materialized.", facts, error);
    }

    private Exception DownloadFailure(Exception error) => LlmResourcePreparation.IsCleanupFailure(error)
        ? error : Failure(error, InferenceResourceOperationPhase.Read);

    private static InferenceException Invalid() => new(InferenceFailureCode.ProtocolFailure, "The generated file does not match its admitted resource receipt.");
}
