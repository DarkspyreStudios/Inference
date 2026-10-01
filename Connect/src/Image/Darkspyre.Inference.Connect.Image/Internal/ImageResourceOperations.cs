using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.Image.Internal;

internal static class ImageResourceOperations
{
    internal static async Task<InferenceResourceDescriptor> CommitAsync(IInferenceResourceAccess resources,
        string? fileName, string? contentType, ReadOnlyMemory<byte> bytes, Action<CancellationToken>? fence = null, CancellationToken token = default)
    {
        Fence(fence, token);
        string workspaceId = resources.WorkspaceId;
        string sessionId = resources.SessionId;
        IInferenceResourceWriteHandle? writer = null;
        Exception? failure = null;
        InferenceResourceOperationPhase phase = InferenceResourceOperationPhase.OpenWrite;
        InferenceResourceCommitOutcome outcome = InferenceResourceCommitOutcome.NotAttempted;
        long? written = 0;
        InferenceResourceCommitOutcome selectionOutcome = InferenceResourceCommitOutcome.NotAttempted;
        InferenceResourceSelectionVersion? selectionVersion = null;
        InferenceResourceDescriptor? result = null;
        try
        {
            writer = await resources.OpenWriteAsync(new(sessionId, fileName, contentType, bytes.Length), token).ConfigureAwait(false);
            Fence(fence, token);
            if (writer.Reference.WorkspaceId != workspaceId)
                throw new InferenceException(InferenceFailureCode.ProtocolFailure, "Resource access returned an allocation for a different workspace.");
            phase = InferenceResourceOperationPhase.Write;
            written = null;
            await writer.Content.WriteAsync(bytes, token).ConfigureAwait(false);
            written = bytes.Length;
            Fence(fence, token);
            phase = InferenceResourceOperationPhase.Commit;
            outcome = InferenceResourceCommitOutcome.Unknown;
            selectionOutcome = InferenceResourceCommitOutcome.Unknown;
            InferenceResourceCommitResult committed = await writer.CommitAsync(written.Value,
                new InferenceResourceCommitOptions { SelectForSession = true }, token).ConfigureAwait(false);
            if (committed.Resource.Reference != writer.Reference || committed.Resource.Reference.WorkspaceId != workspaceId
                || committed.Resource.SessionId != sessionId)
                throw new InferenceException(InferenceFailureCode.ProtocolFailure, "Resource commit returned a mismatched content identity.");
            outcome = InferenceResourceCommitOutcome.Committed;
            if (committed.Selection is null || committed.Selection.WorkspaceId != workspaceId
                || committed.Selection.SessionId != sessionId || !committed.Selection.SelectedResources.Contains(writer.Reference))
                throw new InferenceException(InferenceFailureCode.ProtocolFailure, "Resource commit returned mismatched content or session selection.");
            selectionOutcome = InferenceResourceCommitOutcome.Committed;
            selectionVersion = new(committed.Selection.WorkspaceId, committed.Selection.SessionId, committed.Selection.Revision);
            Fence(fence, token);
            result = committed.Resource;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var facts = new InferenceResourceOperationFailure(phase, writer?.Reference, outcome, written)
            { SelectionOutcome = selectionOutcome, SelectionVersion = selectionVersion };
            failure = error switch
            {
                InferenceResourceOperationException or InferenceResourceOperationCanceledException => error,
                OperationCanceledException => new InferenceResourceOperationCanceledException(facts, token, error),
                _ => new InferenceResourceOperationException(error is InferenceException provider ? provider.Code : InferenceFailureCode.ExecutionFailed,
                    "The image resource operation failed.", facts, error)
            };
        }
        if (writer is not null)
        {
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
            {
                if (failure is not null) throw new AggregateException(failure, cleanup);
                throw new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed, "The image resource cleanup failed.",
                    new(phase, writer.Reference, outcome, written) { SelectionOutcome = selectionOutcome, SelectionVersion = selectionVersion }, cleanup);
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        return result!;
    }

    private static void Fence(Action<CancellationToken>? fence, CancellationToken token)
    { token.ThrowIfCancellationRequested(); fence?.Invoke(token); }
}
