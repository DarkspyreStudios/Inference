using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.TTS;

internal static class TtsResourceWriter
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
        IInferenceResourceWriteHandle? writer = null;
        InferenceResourceReference? reference = null;
        InferenceResourceDescriptor? descriptor = null;
        InferenceResourceSelectionVersion? version = null;
        InferenceResourceOperationPhase phase = InferenceResourceOperationPhase.OpenWrite;
        InferenceResourceCommitOutcome commit = InferenceResourceCommitOutcome.NotAttempted;
        InferenceResourceCommitOutcome selection = InferenceResourceCommitOutcome.NotAttempted;
        long? bytes = 0;
        Exception? failure = null;
        try
        {
            CheckScope();
            writer = await access.OpenWriteAsync(new(scope.SessionId, ContentType: contentType, ExpectedBytes: audio.Length), token).ConfigureAwait(false);
            CheckScope();
            if (writer is null) throw Invalid();
            reference = writer.Reference;
            CheckScope();
            if (reference is null || reference.WorkspaceId != scope.WorkspaceId || string.IsNullOrEmpty(reference.Id)) throw Invalid();
            Stream content = writer.Content;
            CheckScope();
            if (content is null || !content.CanWrite) throw Invalid();
            CheckScope();
            phase = InferenceResourceOperationPhase.Write;
            bytes = null;
            await content.WriteAsync(audio, token).ConfigureAwait(false);
            bytes = audio.Length;
            CheckScope();
            phase = InferenceResourceOperationPhase.Commit;
            commit = selection = InferenceResourceCommitOutcome.Unknown;
            InferenceResourceCommitResult receipt = await writer.CommitAsync(bytes.Value, new() { SelectForSession = true }, token).ConfigureAwait(false);
            // A returned receipt records effects even if the following owner fence refuses publication.
            if (receipt?.Resource?.Reference == reference)
            {
                commit = InferenceResourceCommitOutcome.Committed;
                if (receipt.Selection is { } observed && observed.WorkspaceId == scope.WorkspaceId && observed.SessionId == scope.SessionId
                    && observed.Revision >= 0 && observed.SelectedResources.Contains(reference))
                {
                    selection = InferenceResourceCommitOutcome.Committed;
                    version = new(observed.WorkspaceId, observed.SessionId, observed.Revision);
                }
            }
            CheckScope();
            descriptor = receipt?.Resource;
            if (descriptor is null || descriptor.Reference != reference || descriptor.SessionId != scope.SessionId
                || descriptor.Bytes is { } size && size != bytes
                || descriptor.ContentType is { } type && type != contentType
                || selection != InferenceResourceCommitOutcome.Committed) throw Invalid();
            // Validate the complete returned selection, including caller-created with-values.
            _ = new InferenceResourceSelection(scope.WorkspaceId, scope.SessionId, receipt!.Selection!.Revision, receipt.Selection.SelectedResources);
            if (receipt.Selection.SelectedResources.Any(item => string.IsNullOrEmpty(item.Id))) throw Invalid();
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
                commit = observed.CommitOutcome; selection = observed.SelectionOutcome;
                version = observed.SelectionVersion; bytes = observed.BytesTransferred;
            }
        }
        if (writer is not null)
        {
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                Exception cleanup = LlmResourcePreparation.AsCleanupFailure(LlmResourcePreparation.IsCleanupFailure(error)
                    ? error : Wrap(error, InferenceResourceOperationPhase.Cleanup), reference, writer);
                recordCleanup(cleanup);
                failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        try { CheckScope(); }
        catch (Exception error) { ExceptionDispatchInfo.Capture(Wrap(error, phase)).Throw(); }
        return descriptor!;

        void CheckScope() => LlmResourcePreparation.ValidateScope(access, scope, fence, token);
        Exception Wrap(Exception error, InferenceResourceOperationPhase actualPhase)
        {
            var facts = new InferenceResourceOperationFailure(actualPhase, reference, commit, bytes)
            { SelectionOutcome = selection, SelectionVersion = version };
            return error is OperationCanceledException canceled
                ? new InferenceResourceOperationCanceledException(facts, canceled.CancellationToken, error)
                : new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed, "The speech artifact could not be published.", facts, error);
        }
    }

    private static InferenceException Invalid() => new(InferenceFailureCode.ProtocolFailure, "The speech resource receipt does not match the admitted artifact.");
}
