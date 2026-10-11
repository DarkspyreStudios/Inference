namespace Darkspyre.Inference.Common.Resources;

/// <summary>A runtime operation failure with separately portable effect facts.</summary>
public sealed class InferenceResourceOperationException : Exception
{
    private object? _cleanupOwner;
    /// <summary>Creates the exception, and throws <see cref="ArgumentNullException"/> when <paramref name="failure"/> is null.</summary>
    public InferenceResourceOperationException(InferenceFailureCode code, string message,
        InferenceResourceOperationFailure failure, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Code = code;
        Failure = failure;
    }

    /// <summary>The classification of the failure.</summary>
    public InferenceFailureCode Code { get; }
    /// <summary>The observed effects of the failed operation.</summary>
    public InferenceResourceOperationFailure Failure { get; }

    internal void RetainCleanupOwner(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (Failure.Phase != InferenceResourceOperationPhase.Cleanup)
            throw new InvalidOperationException("Only an actual cleanup failure can retain its refused owner.");
        while (true)
        {
            object? previous = Volatile.Read(ref _cleanupOwner);
            if (ReferenceEquals(previous, owner)) return;
            object retained = previous is null ? owner : (previous, owner);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _cleanupOwner, retained, previous), previous)) return;
        }
    }
}

/// <summary>Cancellation retains the actual token and known/unknown effect facts.</summary>
public sealed class InferenceResourceOperationCanceledException : OperationCanceledException
{
    private object? _cleanupOwner;
    /// <summary>Creates the exception with the cancellation token that ended the operation, and throws <see cref="ArgumentNullException"/> when <paramref name="failure"/> is null.</summary>
    public InferenceResourceOperationCanceledException(InferenceResourceOperationFailure failure,
        CancellationToken cancellationToken, Exception? innerException = null)
        : base("The resource operation was canceled.", innerException, cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Failure = failure;
    }

    /// <summary>The observed effects of the canceled operation.</summary>
    public InferenceResourceOperationFailure Failure { get; }

    internal void RetainCleanupOwner(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (Failure.Phase != InferenceResourceOperationPhase.Cleanup)
            throw new InvalidOperationException("Only an actual cleanup failure can retain its refused owner.");
        while (true)
        {
            object? previous = Volatile.Read(ref _cleanupOwner);
            if (ReferenceEquals(previous, owner)) return;
            object retained = previous is null ? owner : (previous, owner);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _cleanupOwner, retained, previous), previous)) return;
        }
    }
}

/// <summary>A refused replacement. No selection mutation occurs on this conflict.</summary>
public sealed class InferenceResourceSelectionConflictException : Exception
{
    /// <summary>Creates the exception, and throws when either revision is negative.</summary>
    public InferenceResourceSelectionConflictException(long expectedRevision, long actualRevision)
        : base("The resource selection revision is stale.")
    {
        ExpectedRevision = ResourceContract.Nonnegative(expectedRevision, nameof(expectedRevision));
        ActualRevision = ResourceContract.Nonnegative(actualRevision, nameof(actualRevision));
    }

    /// <summary>The selection revision the caller expected.</summary>
    public long ExpectedRevision { get; }
    /// <summary>The selection revision currently stored.</summary>
    public long ActualRevision { get; }
}
