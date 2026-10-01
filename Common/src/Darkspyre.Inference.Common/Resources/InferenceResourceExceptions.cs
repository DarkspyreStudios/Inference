namespace Darkspyre.Inference.Common.Resources;

/// <summary>A runtime operation failure with separately portable primitive effect facts.</summary>
public sealed class InferenceResourceOperationException : Exception
{
    public InferenceResourceOperationException(InferenceFailureCode code, string message,
        InferenceResourceOperationFailure failure, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Code = code;
        Failure = failure;
    }

    public InferenceFailureCode Code { get; }
    public InferenceResourceOperationFailure Failure { get; }
}

/// <summary>Cancellation retains the actual token and known/unknown effect facts.</summary>
public sealed class InferenceResourceOperationCanceledException : OperationCanceledException
{
    public InferenceResourceOperationCanceledException(InferenceResourceOperationFailure failure,
        CancellationToken cancellationToken, Exception? innerException = null)
        : base("The resource operation was canceled.", innerException, cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Failure = failure;
    }

    public InferenceResourceOperationFailure Failure { get; }
}

/// <summary>A refused replacement. No selection mutation occurs on this conflict.</summary>
public sealed class InferenceResourceSelectionConflictException : Exception
{
    public InferenceResourceSelectionConflictException(long expectedRevision, long actualRevision)
        : base("The resource selection revision is stale.")
    {
        ExpectedRevision = ResourceContract.Nonnegative(expectedRevision, nameof(expectedRevision));
        ActualRevision = ResourceContract.Nonnegative(actualRevision, nameof(actualRevision));
    }

    public long ExpectedRevision { get; }
    public long ActualRevision { get; }
}
