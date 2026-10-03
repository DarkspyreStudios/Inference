namespace Darkspyre.Inference.Common.Realtime;

/// <summary>A runtime failure with a copied snapshot of actual realtime error observations.</summary>
public sealed class RealtimeOperationException : Exception
{
    public RealtimeOperationException(RealtimeErrorEvent error, Exception innerException)
        : base("The realtime operation failed.", innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(innerException);
        Error = error with { AudioArtifacts = error.AudioArtifacts };
    }

    public RealtimeErrorEvent Error { get; }
}

/// <summary>Cancellation retains the actual cause, token and copied realtime error observations.</summary>
public sealed class RealtimeOperationCanceledException : OperationCanceledException
{
    public RealtimeOperationCanceledException(RealtimeErrorEvent error, OperationCanceledException innerException)
        : base("The realtime operation was canceled.", innerException,
            (innerException ?? throw new ArgumentNullException(nameof(innerException))).CancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error with { AudioArtifacts = error.AudioArtifacts };
    }

    public RealtimeErrorEvent Error { get; }
}
