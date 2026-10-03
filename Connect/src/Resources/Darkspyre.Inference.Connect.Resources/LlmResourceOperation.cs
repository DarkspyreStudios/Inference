using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Resources;

public enum LlmResourceOperation
{
    CreateBatch = 0, AddBatchRequests = 1, GetBatch = 2,
    ListBatches = 3, ListBatchRequests = 4, GetBatchResults = 5,
    CancelBatch = 6, SubmitDeferred = 7, GetDeferred = 8,
    CreateBatchFromRequests = 9
}

/// <summary>One observed upload attempt. Dispatch alone does not establish upload completion.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmResourceInputUploadObservation(
    InferenceResourceScope Scope,
    bool Dispatched,
    bool IsJsonl)
{
    [JsonRequired]
    public InferenceResourceScope Scope { get; init => field = BatchData.Scope(value); } = BatchData.Scope(Scope);
    [JsonRequired]
    public bool Dispatched
    {
        get;
        init { ResourceObservationData.Upload(value, IsJsonl, CustomId, Source, ObservedFile, ObservedImageReference); field = value; }
    } = Dispatched;
    [JsonRequired]
    public bool IsJsonl
    {
        get;
        init { ResourceObservationData.Upload(Dispatched, value, CustomId, Source, ObservedFile, ObservedImageReference); field = value; }
    } = IsJsonl;
    public string? CustomId
    {
        get;
        init { ResourceObservationData.Upload(Dispatched, IsJsonl, value, Source, ObservedFile, ObservedImageReference); field = value; }
    }
    public InferenceResourceReference? Source
    {
        get;
        init { ResourceObservationData.Upload(Dispatched, IsJsonl, CustomId, value, ObservedFile, ObservedImageReference); field = value; }
    }
    public LlmProviderFile? ObservedFile
    {
        get;
        init { ResourceObservationData.Upload(Dispatched, IsJsonl, CustomId, Source, value, ObservedImageReference); field = value; }
    }
    public ImagePreparedReference? ObservedImageReference
    {
        get;
        init
        {
            ResourceObservationData.Upload(Dispatched, IsJsonl, CustomId, Source, ObservedFile, value);
            field = value is null ? null : value with { ProviderState = value.ProviderState };
        }
    }
    public override string ToString() => "LlmResourceInputUploadObservation { <facts> }";
}

/// <summary>Actual partial effects and results. These facts grant no provider or resource authority.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmResourceOperationObservation(
    LlmResourceOperation Operation,
    bool BatchMutationDispatched,
    bool DeferredSubmissionDispatched,
    bool DeferredRetrievalDispatched,
    bool ResponseObserved)
{
    [JsonRequired]
    public LlmResourceOperation Operation
    {
        get;
        init
        {
            ResourceObservationData.Operation(value, BatchMutationDispatched, DeferredSubmissionDispatched, DeferredRetrievalDispatched);
            ResourceObservationData.UploadOperations(value, InputUploads);
            field = value;
        }
    }
        = ResourceObservationData.Operation(Operation, BatchMutationDispatched, DeferredSubmissionDispatched, DeferredRetrievalDispatched);
    [JsonRequired]
    public bool BatchMutationDispatched
    {
        get;
        init { ResourceObservationData.Operation(Operation, value, DeferredSubmissionDispatched, DeferredRetrievalDispatched); field = value; }
    } = BatchMutationDispatched;
    [JsonRequired]
    public bool DeferredSubmissionDispatched
    {
        get;
        init { ResourceObservationData.Operation(Operation, BatchMutationDispatched, value, DeferredRetrievalDispatched); field = value; }
    } = DeferredSubmissionDispatched;
    [JsonRequired]
    public bool DeferredRetrievalDispatched
    {
        get;
        init { ResourceObservationData.Operation(Operation, BatchMutationDispatched, DeferredSubmissionDispatched, value); field = value; }
    } = DeferredRetrievalDispatched;
    [JsonRequired]
    public bool ResponseObserved
    {
        get;
        init { ResourceObservationData.Response(value, HttpStatusCode, RequestId); field = value; }
    } = ResponseObserved;
    public LlmBatch? SelectedBatch { get; init; }
    public LlmDeferredCompletion? SelectedDeferred { get; init; }
    public LlmBatch? ObservedBatch { get; init; }
    public IReadOnlyList<LlmResourceInputUploadObservation> InputUploads
    {
        get;
        init
        {
            var copy = ResourceObservationData.CopyUploads(value);
            ResourceObservationData.UploadOperations(Operation, copy);
            field = copy;
        }
    } = [];
    public LlmDeferredCompletion? ObservedDeferred { get; init; }
    public LlmDeferredResult? ObservedDeferredResult
    {
        get;
        init => field = value is null ? null : ResourceObservationData.CopyDeferred(value);
    }
    public IReadOnlyList<LlmBatchResult> ObservedResults
    {
        get;
        init => field = ResourceObservationData.CopyResults(value);
    } = [];
    public IReadOnlyList<InferenceResourceCommitResult> ObservedCommits
    {
        get;
        init => field = ResourceObservationData.CopyCommits(value);
    } = [];
    public InferenceResourceOperationFailure? ResourceFailure { get; init; }
    public int? HttpStatusCode
    {
        get;
        init { ResourceObservationData.Response(ResponseObserved, value, RequestId); field = value; }
    }
    public string? RequestId
    {
        get;
        init { ResourceObservationData.Response(ResponseObserved, HttpStatusCode, value); field = value; }
    }
    public override string ToString() => "LlmResourceOperationObservation { <facts> }";
}

/// <summary>A resource operation failure with separately portable observed effects.</summary>
public sealed class LlmResourceOperationException : Exception
{
    public LlmResourceOperationException(LlmResourceOperationObservation observation, Exception? innerException = null)
        : base("The resource operation failed.", innerException)
        => Observation = ResourceObservationData.Snapshot(observation);

    public LlmResourceOperationObservation Observation { get; }
    public override string ToString() => "LlmResourceOperationException: The resource operation failed.";
}

/// <summary>Cancellation preserves the actual cause's token and separately portable observed effects.</summary>
public sealed class LlmResourceOperationCanceledException : OperationCanceledException
{
    public LlmResourceOperationCanceledException(LlmResourceOperationObservation observation, OperationCanceledException cancellation)
        : base("The resource operation was canceled.", cancellation,
            (cancellation ?? throw new ArgumentNullException(nameof(cancellation))).CancellationToken)
        => Observation = ResourceObservationData.Snapshot(observation);

    public LlmResourceOperationObservation Observation { get; }
    public override string ToString() => "LlmResourceOperationCanceledException: The resource operation was canceled.";
}

internal static class ResourceObservationData
{
    internal static LlmResourceOperation Defined(LlmResourceOperation value)
        => Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));

    internal static LlmResourceOperation Operation(LlmResourceOperation operation, bool batchMutation,
        bool deferredSubmission, bool deferredRetrieval)
    {
        Defined(operation);
        if ((batchMutation && operation is not (LlmResourceOperation.CreateBatch or LlmResourceOperation.AddBatchRequests
                or LlmResourceOperation.CancelBatch or LlmResourceOperation.CreateBatchFromRequests))
            || (deferredSubmission && operation != LlmResourceOperation.SubmitDeferred)
            || (deferredRetrieval && operation != LlmResourceOperation.GetDeferred))
            throw new ArgumentException("The dispatch facts contradict the resource operation.");
        return operation;
    }

    internal static void Validate(object value)
    {
        if (value is LlmResourceInputUploadObservation upload)
        {
            BatchData.Scope(upload.Scope);
            Upload(upload.Dispatched, upload.IsJsonl, upload.CustomId, upload.Source, upload.ObservedFile, upload.ObservedImageReference);
            return;
        }
        var observation = (LlmResourceOperationObservation)value;
        Operation(observation.Operation, observation.BatchMutationDispatched,
            observation.DeferredSubmissionDispatched, observation.DeferredRetrievalDispatched);
        Response(observation.ResponseObserved, observation.HttpStatusCode, observation.RequestId);
        UploadOperations(observation.Operation, observation.InputUploads);
        foreach (LlmResourceInputUploadObservation item in observation.InputUploads)
            Validate(item);
    }

    internal static void Upload(bool dispatched, bool isJsonl, string? customId, InferenceResourceReference? source,
        LlmProviderFile? file, ImagePreparedReference? image)
    {
        if ((file is not null && image is not null)
            || (!dispatched && (file is not null || image is not null))
            || (isJsonl && (source is not null || customId is not null || image is not null))
            || (source is not null && image?.Resource is { } resource && source != resource))
            throw new ArgumentException("The input upload observation contains contradictory facts.");
    }

    internal static void Response(bool observed, int? status, string? requestId)
    {
        if (!observed && (status is not null || requestId is not null))
            throw new ArgumentException("Response facts require an observed response.");
    }

    internal static void UploadOperations(LlmResourceOperation operation, IReadOnlyList<LlmResourceInputUploadObservation> uploads)
    {
        if (operation != LlmResourceOperation.CreateBatchFromRequests && uploads.Any(item => item.IsJsonl))
            throw new ArgumentException("Operation-created JSONL belongs only to batch creation from requests.");
    }

    internal static LlmResourceOperationObservation Snapshot(LlmResourceOperationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Validate(observation);
        return observation with
        {
            InputUploads = observation.InputUploads,
            ObservedDeferredResult = observation.ObservedDeferredResult,
            ObservedResults = observation.ObservedResults,
            ObservedCommits = observation.ObservedCommits
        };
    }

    internal static IReadOnlyList<LlmResourceInputUploadObservation> CopyUploads(IReadOnlyList<LlmResourceInputUploadObservation> value)
        => Array.AsReadOnly(BatchData.Copy(value).Select(item =>
        {
            Validate(item);
            return item with { ObservedImageReference = item.ObservedImageReference };
        }).ToArray());

    internal static IReadOnlyList<LlmBatchResult> CopyResults(IReadOnlyList<LlmBatchResult> value)
        => Array.AsReadOnly(BatchData.Copy(value).Select(item => item switch
        {
            LlmBatchChatResult chat => (LlmBatchResult)(chat with { Result = CopyChat(chat.Result) }),
            LlmBatchImageResult image => image with
            {
                Result = image.Result with
                {
                    Images = Array.AsReadOnly(BatchData.Copy(image.Result.Images).Select(output => output with
                    {
                        Data = output.Data.ToArray(),
                        Selection = CopySelection(output.Selection)
                    }).ToArray())
                }
            },
            LlmBatchVideoResult video => video with
            {
                Result = video.Result with { ProviderState = video.Result.ProviderState },
                Materialization = video.Materialization with { Selection = CopySelection(video.Materialization.Selection) },
                JobSnapshot = video.JobSnapshot is { } job ? job with
                {
                    Output = job.Output is { } output ? output with { ProviderState = output.ProviderState } : null
                } : null
            },
            LlmBatchFailure failure => failure,
            _ => throw new ArgumentException("Unknown batch result.", nameof(value))
        }).ToArray());

    internal static LlmDeferredResult CopyDeferred(LlmDeferredResult value) => value switch
    {
        LlmDeferredPending pending => pending,
        LlmDeferredCompleted completed => completed with { Result = CopyChat(completed.Result) },
        _ => throw new ArgumentException("Unknown deferred result.", nameof(value))
    };

    internal static IReadOnlyList<InferenceResourceCommitResult> CopyCommits(IReadOnlyList<InferenceResourceCommitResult> value)
        => Array.AsReadOnly(BatchData.Copy(value).Select(item => item with { Selection = CopySelection(item.Selection) }).ToArray());

    private static InferenceResourceSelection? CopySelection(InferenceResourceSelection? selection)
        => selection is null ? null : selection with { SelectedResources = selection.SelectedResources };

    private static LlmChatResult CopyChat(LlmChatResult result) => BatchData.Required(result) with
    {
        ToolCalls = Array.AsReadOnly(BatchData.Copy(result.ToolCalls).Select(ClientTools.Snapshot).ToArray()),
        Citations = BatchData.Copy(result.Citations),
        GeneratedResources = result.GeneratedResources,
        StructuredOutput = result.StructuredOutput is { } structured ? structured with { Errors = BatchData.Copy(structured.Errors) } : null
    };
}
