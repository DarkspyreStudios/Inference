namespace Darkspyre.Inference.Common.Realtime;

/// <summary>A runtime failure with a copied snapshot of actual realtime error observations.</summary>
public sealed class RealtimeOperationException : Exception
{
    /// <summary>Creates the failure from the realtime error observed and the exception that caused it.</summary>
    public RealtimeOperationException(RealtimeErrorEvent error, Exception innerException)
        : base("The realtime operation failed.", innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(innerException);
        Error = error with { AudioArtifacts = error.AudioArtifacts };
    }

    /// <summary>A copy of the realtime error event observed when the operation failed.</summary>
    public RealtimeErrorEvent Error { get; }
}

/// <summary>Cancellation retains the actual cause, token and copied realtime error observations.</summary>
public sealed class RealtimeOperationCanceledException : OperationCanceledException
{
    /// <summary>Creates the cancellation from the realtime error observed and the cancellation that caused it.</summary>
    public RealtimeOperationCanceledException(RealtimeErrorEvent error, OperationCanceledException innerException)
        : base("The realtime operation was canceled.", innerException,
            (innerException ?? throw new ArgumentNullException(nameof(innerException))).CancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error with { AudioArtifacts = error.AudioArtifacts };
    }

    /// <summary>A copy of the realtime error event observed when the operation was canceled.</summary>
    public RealtimeErrorEvent Error { get; }
}
