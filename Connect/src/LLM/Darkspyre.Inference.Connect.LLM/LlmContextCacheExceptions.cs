namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Cancellation after cache creation may have been dispatched. Reconciliation is caller-directed.</summary>
public sealed class LlmContextCacheCreateCancelledException : OperationCanceledException
{
    public LlmContextCacheCreateCancelledException(LlmContextCacheCreateCorrelation correlation,
        CancellationToken cancellationToken, Exception? innerException = null,
        string? requestId = null, LlmContextCache? observedReceipt = null)
        : base("Cache creation may have committed; reconcile explicitly.", innerException, cancellationToken)
    {
        Correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        RequestId = requestId;
        ObservedReceipt = observedReceipt;
    }

    public LlmContextCacheCreateCorrelation Correlation { get; }
    public string? RequestId { get; }
    public LlmContextCache? ObservedReceipt { get; }
}

/// <summary>Cache creation may have committed despite a failed acknowledgement. No retry is implied.</summary>
public sealed class LlmContextCacheCreateUncertainException : Exception
{
    public LlmContextCacheCreateUncertainException(LlmContextCacheCreateCorrelation correlation,
        Exception innerException, string? requestId = null, LlmContextCache? observedReceipt = null)
        : base("Cache creation may have committed; reconcile explicitly.", innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        Correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        RequestId = requestId;
        ObservedReceipt = observedReceipt;
    }

    public LlmContextCacheCreateCorrelation Correlation { get; }
    public string? RequestId { get; }
    public LlmContextCache? ObservedReceipt { get; }
}
