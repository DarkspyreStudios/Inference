using System.Net;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The observed reason that an explicit cache cannot be used.</summary>
public enum LlmContextCacheUnavailableReason { Missing = 0, Expired = 1 }

/// <summary>The explicitly dispatched cache mutation whose acknowledgement is uncertain.</summary>
public enum LlmContextCacheMutation { UpdateExpiry = 0, Delete = 1 }

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

    public LlmContextCacheUnavailableReason Reason { get; }
    public LlmExplicitCacheReference Reference { get; }
    public HttpStatusCode? StatusCode { get; }
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

    public LlmContextCacheMutation Operation { get; }
    public LlmExplicitCacheReference Reference { get; }
    public string? RequestId { get; }
    public LlmContextCache? ObservedUpdate { get; }
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

    public LlmContextCacheMutation Operation { get; }
    public LlmExplicitCacheReference Reference { get; }
    public string? RequestId { get; }
    public LlmContextCache? ObservedUpdate { get; }
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
            || update.Reference.ModelId != reference.ModelId || update.Reference.ScopeId != reference.ScopeId))
            throw new ArgumentException("Cache mutation evidence does not match the selected reference.");
        if (update?.Reference.ExpiresAt is { } referenceExpiry && referenceExpiry != update.ExpiresAt)
            throw new ArgumentException("Cache mutation evidence contains conflicting observed expiry.");
    }
}

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
