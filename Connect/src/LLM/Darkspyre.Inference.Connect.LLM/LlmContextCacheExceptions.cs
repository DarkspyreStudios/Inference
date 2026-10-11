using System.Net;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The observed reason that an explicit cache cannot be used.</summary>
public enum LlmContextCacheUnavailableReason
{
    /// <summary>The cache does not exist at its authorized route.</summary>
    Missing = 0,
    /// <summary>The cache's observed expiry has elapsed.</summary>
    Expired = 1
}

/// <summary>The explicitly dispatched cache mutation whose acknowledgement is uncertain.</summary>
public enum LlmContextCacheMutation
{
    /// <summary>An update of the cache's expiry.</summary>
    UpdateExpiry = 0,
    /// <summary>A deletion of the cache.</summary>
    Delete = 1
}

/// <summary>A selected cache is missing at its authorized route or its observed expiry has elapsed.</summary>
public sealed class LlmContextCacheUnavailableException : Exception
{
    /// <summary>Constructs an unavailable fact. Inner failures must already pass the owning adapter's safe diagnostic boundary.</summary>
    public LlmContextCacheUnavailableException(LlmContextCacheUnavailableReason reason,
        LlmExplicitCacheReference reference, HttpStatusCode? statusCode = null,
        string? requestId = null, Exception? innerException = null)
        : base("Explicit cache is unavailable; reconcile explicitly.", innerException)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (reason == LlmContextCacheUnavailableReason.Missing && statusCode is not null && statusCode != HttpStatusCode.NotFound)
            throw new ArgumentException("Missing cache evidence requires an absent status or HTTP not-found.", nameof(statusCode));
        Reason = reason;
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        StatusCode = statusCode;
        RequestId = requestId;
    }

    /// <summary>Whether the cache was missing or expired.</summary>
    public LlmContextCacheUnavailableReason Reason { get; }
    /// <summary>The explicit cache the caller selected.</summary>
    public LlmExplicitCacheReference Reference { get; }
    /// <summary>The HTTP status the provider returned, or null when the fact was observed without a response.</summary>
    public HttpStatusCode? StatusCode { get; }
    /// <summary>The provider request id of the failed call, when the provider supplied one.</summary>
    public string? RequestId { get; }
}

/// <summary>Cancellation after expiry update or deletion may have been dispatched. No rollback or retry is implied.</summary>
public sealed class LlmContextCacheMutationCancelledException : OperationCanceledException
{
    /// <summary>Retains actual evidence and cancellation. Inner failures must already pass the owning adapter's safe diagnostic boundary.</summary>
    public LlmContextCacheMutationCancelledException(LlmContextCacheMutation operation,
        LlmExplicitCacheReference reference, CancellationToken cancellationToken,
        Exception? innerException = null, string? requestId = null,
        LlmContextCache? observedUpdate = null, LlmContextCacheDeleteResult? observedDelete = null)
        : base("Cache mutation may have committed; reconcile explicitly.", innerException, cancellationToken)
    {
        CacheMutationEvidence.Validate(operation, reference, observedUpdate, observedDelete);
        Operation = operation;
        Reference = reference;
        RequestId = requestId;
        ObservedUpdate = observedUpdate;
        ObservedDelete = observedDelete;
    }

    /// <summary>The mutation that was dispatched.</summary>
    public LlmContextCacheMutation Operation { get; }
    /// <summary>The explicit cache the mutation targeted.</summary>
    public LlmExplicitCacheReference Reference { get; }
    /// <summary>The provider request id of the mutation, when the provider supplied one.</summary>
    public string? RequestId { get; }
    /// <summary>The updated cache the provider returned for an expiry update, or null when none was observed.</summary>
    public LlmContextCache? ObservedUpdate { get; }
    /// <summary>The deletion result the provider returned for a delete, or null when none was observed.</summary>
    public LlmContextCacheDeleteResult? ObservedDelete { get; }
}

/// <summary>Expiry update or deletion may have committed despite a failed acknowledgement. Reconciliation is explicit.</summary>
public sealed class LlmContextCacheMutationUncertainException : Exception
{
    /// <summary>Retains actual evidence. The required inner failure must already pass the owning adapter's safe diagnostic boundary.</summary>
    public LlmContextCacheMutationUncertainException(LlmContextCacheMutation operation,
        LlmExplicitCacheReference reference, Exception innerException, string? requestId = null,
        LlmContextCache? observedUpdate = null, LlmContextCacheDeleteResult? observedDelete = null)
        : base("Cache mutation may have committed; reconcile explicitly.", innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        CacheMutationEvidence.Validate(operation, reference, observedUpdate, observedDelete);
        Operation = operation;
        Reference = reference;
        RequestId = requestId;
        ObservedUpdate = observedUpdate;
        ObservedDelete = observedDelete;
    }

    /// <summary>The mutation that was dispatched.</summary>
    public LlmContextCacheMutation Operation { get; }
    /// <summary>The explicit cache the mutation targeted.</summary>
    public LlmExplicitCacheReference Reference { get; }
    /// <summary>The provider request id of the mutation, when the provider supplied one.</summary>
    public string? RequestId { get; }
    /// <summary>The updated cache the provider returned for an expiry update, or null when none was observed.</summary>
    public LlmContextCache? ObservedUpdate { get; }
    /// <summary>The deletion result the provider returned for a delete, or null when none was observed.</summary>
    public LlmContextCacheDeleteResult? ObservedDelete { get; }
}

internal static class CacheMutationEvidence
{
    internal static void Validate(LlmContextCacheMutation operation, LlmExplicitCacheReference reference,
        LlmContextCache? update, LlmContextCacheDeleteResult? delete)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        ArgumentNullException.ThrowIfNull(reference);
        if ((operation == LlmContextCacheMutation.UpdateExpiry && delete is not null)
            || (operation == LlmContextCacheMutation.Delete && update is not null))
            throw new ArgumentException("Cache mutation evidence does not match the operation.");
        if (update is not null && (update.Reference.ResourceName != reference.ResourceName
            || update.Reference.ModelId != reference.ModelId))
            throw new ArgumentException("Cache mutation evidence does not match the selected reference.");
        if (update?.Reference.ExpiresAt is { } referenceExpiry && referenceExpiry != update.ExpiresAt)
            throw new ArgumentException("Cache mutation evidence contains conflicting observed expiry.");
    }
}

/// <summary>Cancellation after cache creation may have been dispatched. Reconciliation is caller-directed.</summary>
public sealed class LlmContextCacheCreateCancelledException : OperationCanceledException
{
    /// <summary>Retains the create correlation, cancellation and any observed receipt. Inner failures must already pass the owning adapter's safe diagnostic boundary.</summary>
    public LlmContextCacheCreateCancelledException(LlmContextCacheCreateCorrelation correlation,
        CancellationToken cancellationToken, Exception? innerException = null,
        string? requestId = null, LlmContextCache? observedReceipt = null)
        : base("Cache creation may have committed; reconcile explicitly.", innerException, cancellationToken)
    {
        Correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        RequestId = requestId;
        ObservedReceipt = observedReceipt;
    }

    /// <summary>The operation id and display name that identify the dispatched create for reconciliation.</summary>
    public LlmContextCacheCreateCorrelation Correlation { get; }
    /// <summary>The provider request id of the create, when the provider supplied one.</summary>
    public string? RequestId { get; }
    /// <summary>The created cache the provider returned, or null when no receipt was observed.</summary>
    public LlmContextCache? ObservedReceipt { get; }
}

/// <summary>Cache creation may have committed despite a failed acknowledgement. No retry is implied.</summary>
public sealed class LlmContextCacheCreateUncertainException : Exception
{
    /// <summary>Retains the create correlation and any observed receipt. The required inner failure must already pass the owning adapter's safe diagnostic boundary.</summary>
    public LlmContextCacheCreateUncertainException(LlmContextCacheCreateCorrelation correlation,
        Exception innerException, string? requestId = null, LlmContextCache? observedReceipt = null)
        : base("Cache creation may have committed; reconcile explicitly.", innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        Correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        RequestId = requestId;
        ObservedReceipt = observedReceipt;
    }

    /// <summary>The operation id and display name that identify the dispatched create for reconciliation.</summary>
    public LlmContextCacheCreateCorrelation Correlation { get; }
    /// <summary>The provider request id of the create, when the provider supplied one.</summary>
    public string? RequestId { get; }
    /// <summary>The created cache the provider returned, or null when no receipt was observed.</summary>
    public LlmContextCache? ObservedReceipt { get; }
}
