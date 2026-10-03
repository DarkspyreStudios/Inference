using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class SpeechResourceWriter
{
    internal static async Task<InferenceResourceSelectionVersion> ReadScopeAsync(
        IInferenceResourceAccess access, Action fence, CancellationToken token)
    {
        try
        {
            InferenceResourceSelection selection = await LlmResourcePreparation.ReadSelectionAsync(access, fence, token).ConfigureAwait(false);
            return new(selection.WorkspaceId, selection.SessionId, selection.Revision);
        }
        catch (Exception error) when (error is not InferenceResourceOperationException and not InferenceResourceOperationCanceledException)
        {
            var facts = new InferenceResourceOperationFailure(InferenceResourceOperationPhase.ReadSelection, null, InferenceResourceCommitOutcome.NotAttempted);
            throw error is OperationCanceledException canceled
                ? new InferenceResourceOperationCanceledException(facts, canceled.CancellationToken, error)
                : new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed, "The speech resource scope could not be read.", facts, error);
        }
    }

    internal static async Task<InferenceResourceDescriptor> CommitAsync(IInferenceResourceAccess access,
        InferenceResourceSelectionVersion scope, ReadOnlyMemory<byte> audio, string contentType,
        Action fence, Action<Exception> recordCleanup, CancellationToken token)
    {
        Writer writer = await OpenAsync(access, scope, contentType, audio.Length, fence, recordCleanup, token).ConfigureAwait(false);
        Exception? failure = null; InferenceResourceDescriptor? result = null;
        try
        {
            await writer.WriteAsync(audio, token).ConfigureAwait(false);
            result = await writer.CommitAsync(token).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        try { await writer.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException(failure, cleanup); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        writer.Fence(token);
        return result!;
    }

    internal static async Task<Writer> OpenAsync(IInferenceResourceAccess access, InferenceResourceSelectionVersion scope,
        string? contentType, long? expectedBytes, Action fence, Action<Exception> recordCleanup, CancellationToken token)
    {
        var writer = new Writer(access, scope, contentType, fence, recordCleanup);
        try { await writer.OpenAsync(expectedBytes, token).ConfigureAwait(false); return writer; }
        catch (Exception original)
        {
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }

    // REST and streaming share the same scope, effect facts, receipt and cleanup owner.
    internal sealed class Writer(IInferenceResourceAccess access, InferenceResourceSelectionVersion scope,
        string? contentType, Action fence, Action<Exception> recordCleanup) : IAsyncDisposable
    {
        private IInferenceResourceAccess? _access = access;
        private IInferenceResourceWriteHandle? _handle;
        private Stream? _content;
        private InferenceResourceReference? _reference;
        private InferenceResourceSelectionVersion? _version;
        private InferenceResourceOperationPhase _phase = InferenceResourceOperationPhase.OpenWrite;
        private InferenceResourceCommitOutcome _commit = InferenceResourceCommitOutcome.NotAttempted;
        private InferenceResourceCommitOutcome _selection = InferenceResourceCommitOutcome.NotAttempted;
        private long? _bytes = 0;
        private Task? _disposed;

        internal InferenceResourceSelectionVersion? SelectionVersion => _version;
        internal InferenceResourceCommitResult? ObservedCommit { get; private set; }

        internal async Task OpenAsync(long? expectedBytes, CancellationToken token)
        {
            try
            {
                CheckScope(token);
                _handle = await _access!.OpenWriteAsync(new(scope.SessionId, ContentType: contentType, ExpectedBytes: expectedBytes), token).ConfigureAwait(false);
                CheckScope(token); if (_handle is null) throw Invalid();
                _reference = _handle.Reference; CheckScope(token);
                if (_reference is null || _reference.WorkspaceId != scope.WorkspaceId || string.IsNullOrEmpty(_reference.Id)) throw Invalid();
                _content = _handle.Content; CheckScope(token);
                if (_content is null || !_content.CanWrite) throw Invalid(); CheckScope(token);
            }
            catch (Exception error) { throw Failure(error); }
        }
        internal async Task WriteAsync(ReadOnlyMemory<byte> audio, CancellationToken token)
        {
            try
            {
                CheckScope(token); _phase = InferenceResourceOperationPhase.Write;
                long before = _bytes!.Value; _bytes = null;
                await _content!.WriteAsync(audio, token).ConfigureAwait(false);
                _bytes = checked(before + audio.Length); CheckScope(token);
            }
            catch (Exception error) { throw Failure(error); }
        }
        internal async Task<InferenceResourceDescriptor> CommitAsync(CancellationToken token)
        {
            try
            {
                CheckScope(token); _phase = InferenceResourceOperationPhase.Commit;
                _commit = _selection = InferenceResourceCommitOutcome.Unknown;
                InferenceResourceCommitResult receipt = await _handle!.CommitAsync(_bytes!.Value, new() { SelectForSession = true }, token).ConfigureAwait(false);
                // Observe the receipt before a late fence can refuse publication.
                if (receipt is not null && receipt.Resource?.Reference == _reference)
                {
                    _commit = InferenceResourceCommitOutcome.Committed;
                    if (receipt.Selection is { } observed && observed.WorkspaceId == scope.WorkspaceId && observed.SessionId == scope.SessionId
                        && observed.Revision >= 0 && observed.SelectedResources.Contains(_reference))
                    {
                        _selection = InferenceResourceCommitOutcome.Committed;
                        _version = new(observed.WorkspaceId, observed.SessionId, observed.Revision);
                    }
                }
                var descriptor = receipt?.Resource;
                if (descriptor is null || descriptor.Reference != _reference || descriptor.SessionId is { } session && session != scope.SessionId
                    || descriptor.Bytes is { } size && size != _bytes || contentType is not null && descriptor.ContentType is { } type && type != contentType
                    || _selection != InferenceResourceCommitOutcome.Committed) throw Invalid();
                var selection = new InferenceResourceSelection(receipt!.Selection!.WorkspaceId, receipt.Selection.SessionId,
                    receipt.Selection.Revision, receipt.Selection.SelectedResources);
                if (selection.SelectedResources.Any(item => string.IsNullOrEmpty(item.Id))) throw Invalid();
                ObservedCommit = new(descriptor, selection);
                CheckScope(token);
                return descriptor;
            }
            catch (Exception error) { throw Failure(error); }
        }
        internal void Fence(CancellationToken token)
        {
            try { fence(); token.ThrowIfCancellationRequested(); } catch (Exception error) { throw Failure(error); }
        }
        private void CheckScope(CancellationToken token) => LlmResourcePreparation.ValidateScope(_access!, scope, fence, token);
        private Exception Failure(Exception error)
        {
            InferenceResourceOperationFailure? observed = error switch
            {
                InferenceResourceOperationException resource => resource.Failure,
                InferenceResourceOperationCanceledException resource => resource.Failure,
                _ => null
            };
            if (observed is not null)
            {
                _commit = observed.CommitOutcome; _selection = observed.SelectionOutcome;
                _version = observed.SelectionVersion; _bytes = observed.BytesTransferred;
                return error;
            }
            return Wrap(error, _phase);
        }
        public ValueTask DisposeAsync() => new(_disposed ??= DisposeCoreAsync());
        private async Task DisposeCoreAsync()
        {
            IInferenceResourceWriteHandle? handle = _handle;
            _handle = null; _content = null; _access = null;
            if (handle is null) return;
            try { await handle.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                Exception cleanup = LlmResourcePreparation.AsCleanupFailure(LlmResourcePreparation.IsCleanupFailure(error)
                    ? error : Wrap(error, InferenceResourceOperationPhase.Cleanup), _reference, handle);
                recordCleanup(cleanup);
                ExceptionDispatchInfo.Capture(cleanup).Throw();
            }
        }
        private Exception Wrap(Exception error, InferenceResourceOperationPhase actualPhase)
        {
            var facts = new InferenceResourceOperationFailure(actualPhase, _reference, _commit, _bytes)
            { SelectionOutcome = _selection, SelectionVersion = _version };
            return error is OperationCanceledException canceled
                ? new InferenceResourceOperationCanceledException(facts, canceled.CancellationToken, error)
                : new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed, "The speech artifact could not be published.", facts, error);
        }
    }

    private static InferenceException Invalid() => new(InferenceFailureCode.ProtocolFailure, "The speech resource receipt does not match the admitted artifact.");
}
