using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class ImageResourceOperations
{
    internal static async Task<InferenceResourceCommitResult> CommitAsync(IInferenceResourceAccess resources,
        string? fileName, string? contentType, ReadOnlyMemory<byte> bytes, Action<CancellationToken>? fence = null,
        Action<InferenceResourceCommitResult>? onCommitted = null, CancellationToken token = default)
    {
        string? workspaceId = null;
        string? sessionId = null;
        IInferenceResourceWriteHandle? writer = null;
        InferenceResourceReference? reference = null;
        Exception? failure = null;
        InferenceResourceOperationPhase phase = InferenceResourceOperationPhase.OpenWrite;
        InferenceResourceCommitOutcome outcome = InferenceResourceCommitOutcome.NotAttempted;
        long? written = 0;
        InferenceResourceCommitOutcome selectionOutcome = InferenceResourceCommitOutcome.NotAttempted;
        InferenceResourceSelectionVersion? selectionVersion = null;
        InferenceResourceCommitResult? result = null;
        IReadOnlyList<InferenceResourceCommitResult> retained = [];
        try
        {
            Fence(fence, token);
            workspaceId = resources.WorkspaceId;
            Fence(fence, token);
            sessionId = resources.SessionId;
            CheckScope();
            writer = await resources.OpenWriteAsync(new(sessionId, fileName, contentType, bytes.Length), token).ConfigureAwait(false);
            CheckScope();
            if (writer is null) throw Invalid();
            reference = writer.Reference;
            CheckScope();
            if (reference is null || reference.WorkspaceId != workspaceId || string.IsNullOrEmpty(reference.Id))
                throw new InferenceException(InferenceFailureCode.ProtocolFailure, "Resource access returned an allocation for a different workspace.");
            Stream content = writer.Content;
            CheckScope();
            if (content is null || !content.CanWrite) throw Invalid();
            CheckScope();
            phase = InferenceResourceOperationPhase.Write;
            written = null;
            await content.WriteAsync(bytes, token).ConfigureAwait(false);
            written = bytes.Length;
            CheckScope();
            phase = InferenceResourceOperationPhase.Commit;
            outcome = InferenceResourceCommitOutcome.Unknown;
            selectionOutcome = InferenceResourceCommitOutcome.Unknown;
            InferenceResourceCommitResult committed = await writer.CommitAsync(written.Value,
                new InferenceResourceCommitOptions { SelectForSession = true }, token).ConfigureAwait(false);
            // Returned receipts retain actual effects even when a later fence refuses publication.
            if (committed?.Resource?.Reference == reference)
            {
                outcome = InferenceResourceCommitOutcome.Committed;
                if (committed.Selection is { } observed && observed.WorkspaceId == workspaceId
                    && observed.SessionId == sessionId && observed.Revision >= 0 && observed.SelectedResources.Contains(reference))
                {
                    selectionOutcome = InferenceResourceCommitOutcome.Committed;
                    selectionVersion = new(observed.WorkspaceId, observed.SessionId, observed.Revision);
                }
            }
            InferenceResourceDescriptor? resource = committed?.Resource;
            if (resource is null || resource.Reference != reference || resource.SessionId is { } reportedSession && reportedSession != sessionId
                || resource.Bytes is { } size && size != written
                || ImageContentTypes.ContentTypesConflict(resource.ContentType, contentType)
                || selectionOutcome != InferenceResourceCommitOutcome.Committed) throw Invalid();
            var selection = new InferenceResourceSelection(workspaceId!, sessionId!, committed!.Selection!.Revision, committed.Selection.SelectedResources);
            if (selection.SelectedResources.Any(item => string.IsNullOrEmpty(item.Id))) throw Invalid();
            result = new InferenceResourceCommitResult(resource, selection);
            onCommitted?.Invoke(result);
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
                if (result is null)
                {
                    outcome = observed.CommitOutcome; selectionOutcome = observed.SelectionOutcome;
                    selectionVersion = observed.SelectionVersion; written = observed.BytesTransferred;
                }
            }
        }
        if (failure is not null) failure = WithReceipts(failure);
        if (writer is not null)
        {
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                Exception refused = WithReceipts(LlmResourcePreparation.AsCleanupFailure(LlmResourcePreparation.IsCleanupFailure(cleanup)
                    ? cleanup : Wrap(cleanup, InferenceResourceOperationPhase.Cleanup), reference, writer));
                failure = failure is null ? refused : new AggregateException(failure, refused);
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        try { CheckScope(); }
        catch (Exception error) { ExceptionDispatchInfo.Throw(Wrap(error, phase)); }
        return result!;

        void CheckScope()
        {
            Fence(fence, token);
            if (string.IsNullOrEmpty(workspaceId) || string.IsNullOrEmpty(sessionId)) throw Invalid();
            string currentWorkspace = resources.WorkspaceId;
            Fence(fence, token);
            if (currentWorkspace != workspaceId) throw Invalid();
            string currentSession = resources.SessionId;
            Fence(fence, token);
            if (currentSession != sessionId) throw Invalid();
        }
        Exception Wrap(Exception error, InferenceResourceOperationPhase actualPhase)
        {
            var facts = new InferenceResourceOperationFailure(actualPhase, reference, outcome, written)
            {
                SelectionOutcome = selectionOutcome,
                SelectionVersion = selectionVersion,
                GeneratedResources = result is null ? retained : [result]
            };
            return error is OperationCanceledException canceled
                ? new InferenceResourceOperationCanceledException(facts, canceled.CancellationToken, new FailureEvidence(error))
                : new InferenceResourceOperationException(error is InferenceException provider ? provider.Code : InferenceFailureCode.ExecutionFailed,
                    "The image resource operation failed.", facts, new FailureEvidence(error));
        }
        Exception WithReceipts(Exception error) => LlmResourcePreparation.WithGeneratedResources(error,
            result is null ? retained : [result]);
    }

    internal static Exception ActualCleanup(Exception error)
    {
        while (true)
        {
            error = LlmResourcePreparation.CleanupCause(error);
            Exception? original = error.InnerException switch
            {
                FailureEvidence evidence => evidence.Original,
                _ => error.InnerException
            };
            if (original is InferenceResourceOperationException resource && resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup
                || original is InferenceResourceOperationCanceledException canceled && canceled.Failure.Phase == InferenceResourceOperationPhase.Cleanup)
                error = original;
            else return error;
        }
    }

    internal sealed class FailureEvidence(Exception original, Exception? safeCause = null)
        : Exception("The image resource boundary reported a failure.", safeCause)
    { internal Exception Original { get; } = original; }

    private static void Fence(Action<CancellationToken>? fence, CancellationToken token)
    { token.ThrowIfCancellationRequested(); fence?.Invoke(token); }

    private static InferenceException Invalid() => new(InferenceFailureCode.ProtocolFailure, "The image resource scope or receipt does not match the admitted artifact.");
}
