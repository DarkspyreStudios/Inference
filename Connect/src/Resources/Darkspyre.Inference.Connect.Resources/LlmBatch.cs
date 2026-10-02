using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Resources;

public sealed record LlmResourcePageRequest(int? Limit = null, string? PaginationToken = null);
public sealed record LlmBatchCounts(long? Requests, long? Pending, long? Success, long? Error, long? Cancelled);
public sealed record LlmBatch(string Id, string Name, InferenceResourceScope Scope,
    LlmBatchCounts Counts, DateTimeOffset? CreateTime = null, DateTimeOffset? ExpireTime = null,
    DateTimeOffset? CancelTime = null, string? ProviderCancellationMessage = null, string? CreateCredentialId = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmBatchChatOperation), "chat")]
[JsonDerivedType(typeof(LlmBatchImageOperation), "image")]
[JsonDerivedType(typeof(LlmBatchVideoGenerationOperation), "video-generation")]
[JsonDerivedType(typeof(LlmBatchVideoEditOperation), "video-edit")]
[JsonDerivedType(typeof(LlmBatchVideoExtensionOperation), "video-extension")]
public abstract record LlmBatchOperation
{
    private protected LlmBatchOperation() { }
}
public sealed record LlmBatchChatOperation(LlmChatRequest Request) : LlmBatchOperation;
public sealed record LlmBatchImageOperation(ImageGenerationRequest Request, ImageWorkflow Workflow) : LlmBatchOperation;
public sealed record LlmBatchVideoGenerationOperation(VideoGenerationRequest Request) : LlmBatchOperation;
public sealed record LlmBatchVideoEditOperation(VideoEditRequest Request) : LlmBatchOperation;
public sealed record LlmBatchVideoExtensionOperation(VideoExtensionRequest Request) : LlmBatchOperation;
public sealed record LlmBatchRequest(string CustomId, LlmBatchOperation Operation);
public sealed record LlmBatchRequestMetadata(string Id, string? Endpoint, string? ModelId,
    string? State, DateTimeOffset? CreateTime = null, DateTimeOffset? FinishTime = null);
public sealed record LlmBatchPage(IReadOnlyList<LlmBatch> Items, string? PaginationToken);
public sealed record LlmBatchRequestPage(IReadOnlyList<LlmBatchRequestMetadata> Items, string? PaginationToken);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmBatchChatResult), "chat")]
[JsonDerivedType(typeof(LlmBatchImageResult), "image")]
[JsonDerivedType(typeof(LlmBatchVideoResult), "video")]
[JsonDerivedType(typeof(LlmBatchFailure), "failure")]
public abstract record LlmBatchResult(string CustomId)
{
    private protected LlmBatchResult() : this(string.Empty) { }
}
public sealed record LlmBatchChatResult(string CustomId, LlmChatResult Result) : LlmBatchResult(CustomId);
public sealed record LlmBatchImageResult(string CustomId, ImageGenerationResult Result) : LlmBatchResult(CustomId);
public sealed record LlmBatchVideoResult(string CustomId, VideoJobSnapshot Result,
    MaterializedVideoResult Materialization) : LlmBatchResult(CustomId);
public sealed record LlmBatchFailure(string CustomId, InferenceResourceFailure Error) : LlmBatchResult(CustomId);
public sealed record LlmBatchResultPage(IReadOnlyList<LlmBatchResult> Items, string? PaginationToken);
public sealed record LlmDeferredCompletion(string RequestId, InferenceResourceScope Scope,
    DateTimeOffset? SubmittedAt = null, DateTimeOffset? ExpiresAt = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmDeferredPending), "pending")]
[JsonDerivedType(typeof(LlmDeferredCompleted), "completed")]
public abstract record LlmDeferredResult
{
    private protected LlmDeferredResult() { }
}
public sealed record LlmDeferredPending : LlmDeferredResult;
public sealed record LlmDeferredCompleted(LlmChatResult Result) : LlmDeferredResult;
public sealed record LlmMediaMaterializationOptions(long MaximumInlineImageBytes, long MaximumInlineResultBytes);
public sealed record MaterializedVideoResult(InferenceResourceDescriptor Resource,
    TimeSpan? Duration = null, InferenceProvenance? Provenance = null);

/// <summary>Explicit batch and deferred operations. A declaration does not imply adapter availability.</summary>
public interface ILlmBatchClient
{
    Task<LlmBatch> CreateAsync(string connectionId, string name, CancellationToken cancellationToken = default);
    Task AddRequestsAsync(string connectionId, LlmBatch batch, IReadOnlyList<LlmBatchRequest> requests,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default);
    Task<LlmBatch> GetAsync(string connectionId, LlmBatch batch, CancellationToken cancellationToken = default);
    Task<LlmBatchPage> ListAsync(string connectionId, LlmResourcePageRequest page, CancellationToken cancellationToken = default);
    Task<LlmBatchRequestPage> ListRequestsAsync(string connectionId, LlmBatch batch,
        LlmResourcePageRequest page, CancellationToken cancellationToken = default);
    Task<LlmBatchResultPage> GetResultsAsync(string connectionId, LlmBatch batch,
        LlmResourcePageRequest page, LlmMediaMaterializationOptions materialization,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default);
    Task<LlmBatch> CancelAsync(string connectionId, LlmBatch batch, CancellationToken cancellationToken = default);
    Task<LlmDeferredCompletion> SubmitDeferredAsync(string connectionId, LlmChatRequest request,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default);
    Task<LlmDeferredResult> GetDeferredAsync(string connectionId, LlmDeferredCompletion completion,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default);
    Task<LlmBatch> CreateFromRequestsAsync(string connectionId, string name,
        IReadOnlyList<LlmBatchRequest> requests, IInferenceResourceAccess? resources = null,
        CancellationToken cancellationToken = default);
}
