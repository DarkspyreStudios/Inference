using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Resources;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmResourcePageRequest(int? Limit = null, string? PaginationToken = null)
{
    public override string ToString() => "LlmResourcePageRequest { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchCounts(long? Requests, long? Pending, long? Success, long? Error, long? Cancelled)
{
    public long? Requests { get; init => field = BatchData.Nonnegative(value); } = BatchData.Nonnegative(Requests);
    public long? Pending { get; init => field = BatchData.Nonnegative(value); } = BatchData.Nonnegative(Pending);
    public long? Success { get; init => field = BatchData.Nonnegative(value); } = BatchData.Nonnegative(Success);
    public long? Error { get; init => field = BatchData.Nonnegative(value); } = BatchData.Nonnegative(Error);
    public long? Cancelled { get; init => field = BatchData.Nonnegative(value); } = BatchData.Nonnegative(Cancelled);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatch(string Id, string Name, InferenceResourceScope Scope,
    LlmBatchCounts Counts, DateTimeOffset? CreateTime = null, DateTimeOffset? ExpireTime = null,
    DateTimeOffset? CancelTime = null, string? ProviderCancellationMessage = null, string? CreateCredentialId = null)
{
    [JsonRequired]
    public string Id { get; init => field = BatchData.Identity(value); } = BatchData.Identity(Id);
    [JsonRequired]
    public string Name { get; init => field = BatchData.Required(value); } = BatchData.Required(Name);
    [JsonRequired]
    public InferenceResourceScope Scope { get; init => field = BatchData.Scope(value); } = BatchData.Scope(Scope);
    [JsonRequired]
    public LlmBatchCounts Counts { get; init => field = BatchData.Required(value); } = BatchData.Required(Counts);
    public override string ToString() => "LlmBatch { <data> }";
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(LlmBatchChatOperation), "chat")]
[JsonDerivedType(typeof(LlmBatchImageOperation), "image")]
[JsonDerivedType(typeof(LlmBatchVideoGenerationOperation), "video-generation")]
[JsonDerivedType(typeof(LlmBatchVideoEditOperation), "video-edit")]
[JsonDerivedType(typeof(LlmBatchVideoExtensionOperation), "video-extension")]
public abstract record LlmBatchOperation
{
    private protected LlmBatchOperation() { }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchChatOperation(LlmChatRequest Request) : LlmBatchOperation
{
    [JsonRequired]
    public LlmChatRequest Request { get; init => field = BatchData.Required(value); } = BatchData.Required(Request);
    public override string ToString() => "LlmBatchChatOperation { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchImageOperation(ImageGenerationRequest Request, ImageWorkflow Workflow) : LlmBatchOperation
{
    [JsonRequired]
    public ImageGenerationRequest Request { get; init => field = BatchData.Required(value); } = BatchData.Required(Request);
    [JsonRequired]
    public ImageWorkflow Workflow { get; init => field = BatchData.Required(value); } = BatchData.Required(Workflow);
    public override string ToString() => "LlmBatchImageOperation { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchVideoGenerationOperation(VideoGenerationRequest Request) : LlmBatchOperation
{
    [JsonRequired]
    public VideoGenerationRequest Request { get; init => field = BatchData.Required(value); } = BatchData.Required(Request);
    public override string ToString() => "LlmBatchVideoGenerationOperation { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchVideoEditOperation(VideoEditRequest Request) : LlmBatchOperation
{
    [JsonRequired]
    public VideoEditRequest Request { get; init => field = BatchData.Required(value); } = BatchData.Required(Request);
    public override string ToString() => "LlmBatchVideoEditOperation { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchVideoExtensionOperation(VideoExtensionRequest Request) : LlmBatchOperation
{
    [JsonRequired]
    public VideoExtensionRequest Request { get; init => field = BatchData.Required(value); } = BatchData.Required(Request);
    public override string ToString() => "LlmBatchVideoExtensionOperation { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchRequest(string CustomId, LlmBatchOperation Operation)
{
    [JsonRequired]
    public string CustomId { get; init => field = BatchData.Identity(value); } = BatchData.Identity(CustomId);
    [JsonRequired]
    public LlmBatchOperation Operation { get; init => field = BatchData.Required(value); } = BatchData.Required(Operation);
    public override string ToString() => "LlmBatchRequest { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchRequestMetadata(string Id, string? Endpoint, string? ModelId,
    string? State, DateTimeOffset? CreateTime = null, DateTimeOffset? FinishTime = null)
{
    [JsonRequired]
    public string Id { get; init => field = BatchData.Identity(value); } = BatchData.Identity(Id);
    public override string ToString() => "LlmBatchRequestMetadata { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchPage(IReadOnlyList<LlmBatch> Items, string? PaginationToken)
{
    [JsonRequired]
    public IReadOnlyList<LlmBatch> Items { get; init => field = BatchData.Copy(value); } = BatchData.Copy(Items);
    public override string ToString() => "LlmBatchPage { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchRequestPage(IReadOnlyList<LlmBatchRequestMetadata> Items, string? PaginationToken)
{
    [JsonRequired]
    public IReadOnlyList<LlmBatchRequestMetadata> Items { get; init => field = BatchData.Copy(value); } = BatchData.Copy(Items);
    public override string ToString() => "LlmBatchRequestPage { <data> }";
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(LlmBatchChatResult), "chat")]
[JsonDerivedType(typeof(LlmBatchImageResult), "image")]
[JsonDerivedType(typeof(LlmBatchVideoResult), "video")]
[JsonDerivedType(typeof(LlmBatchFailure), "failure")]
public abstract record LlmBatchResult
{
    private protected LlmBatchResult(string CustomId) => this.CustomId = CustomId;
    [JsonRequired]
    public string CustomId { get; init => field = BatchData.Identity(value); } = null!;
    public void Deconstruct(out string CustomId) => CustomId = this.CustomId;
    public override string ToString() => "LlmBatchResult { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchChatResult(string CustomId, LlmChatResult Result) : LlmBatchResult(CustomId)
{
    [JsonRequired]
    public LlmChatResult Result { get; init => field = BatchData.Required(value); } = BatchData.Required(Result);
    public override string ToString() => "LlmBatchChatResult { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchImageResult(string CustomId, ImageGenerationResult Result) : LlmBatchResult(CustomId)
{
    [JsonRequired]
    public ImageGenerationResult Result { get; init => field = BatchData.Required(value); } = BatchData.Required(Result);
    public override string ToString() => "LlmBatchImageResult { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchVideoResult(string CustomId, VideoJobSnapshot Result,
    MaterializedVideoResult Materialization) : LlmBatchResult(CustomId)
{
    [JsonRequired]
    public VideoJobSnapshot Result { get; init => field = BatchData.Required(value); } = BatchData.Required(Result);
    [JsonRequired]
    public MaterializedVideoResult Materialization { get; init => field = BatchData.Required(value); } = BatchData.Required(Materialization);
    public override string ToString() => "LlmBatchVideoResult { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchFailure(string CustomId, InferenceResourceFailure Error) : LlmBatchResult(CustomId)
{
    [JsonRequired]
    public InferenceResourceFailure Error { get; init => field = BatchData.Required(value); } = BatchData.Required(Error);
    public override string ToString() => "LlmBatchFailure { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmBatchResultPage(IReadOnlyList<LlmBatchResult> Items, string? PaginationToken)
{
    [JsonRequired]
    public IReadOnlyList<LlmBatchResult> Items { get; init => field = BatchData.Copy(value); } = BatchData.Copy(Items);
    public override string ToString() => "LlmBatchResultPage { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmDeferredCompletion(string RequestId, InferenceResourceScope Scope,
    DateTimeOffset? SubmittedAt = null, DateTimeOffset? ExpiresAt = null)
{
    [JsonRequired]
    public string RequestId { get; init => field = BatchData.Identity(value); } = BatchData.Identity(RequestId);
    [JsonRequired]
    public InferenceResourceScope Scope { get; init => field = BatchData.Scope(value); } = BatchData.Scope(Scope);
    public override string ToString() => "LlmDeferredCompletion { <data> }";
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(LlmDeferredPending), "pending")]
[JsonDerivedType(typeof(LlmDeferredCompleted), "completed")]
public abstract record LlmDeferredResult
{
    private protected LlmDeferredResult() { }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmDeferredPending : LlmDeferredResult
{
    public override string ToString() => "LlmDeferredPending { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmDeferredCompleted(LlmChatResult Result) : LlmDeferredResult
{
    [JsonRequired]
    public LlmChatResult Result { get; init => field = BatchData.Required(value); } = BatchData.Required(Result);
    public override string ToString() => "LlmDeferredCompleted { <data> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmMediaMaterializationOptions(long MaximumInlineImageBytes, long MaximumInlineResultBytes)
{
    [JsonRequired]
    public long MaximumInlineImageBytes { get; init => field = BatchData.Positive(value); } = BatchData.Positive(MaximumInlineImageBytes);
    [JsonRequired]
    public long MaximumInlineResultBytes { get; init => field = BatchData.Positive(value); } = BatchData.Positive(MaximumInlineResultBytes);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MaterializedVideoResult(InferenceResourceDescriptor Resource,
    TimeSpan? Duration = null, InferenceProvenance? Provenance = null)
{
    [JsonRequired]
    public InferenceResourceDescriptor Resource { get; init => field = BatchData.Required(value); } = BatchData.Required(Resource);
    public override string ToString() => "MaterializedVideoResult { <data> }";
}

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

internal static class BatchData
{
    internal static T Required<T>(T? value) where T : class { ArgumentNullException.ThrowIfNull(value); return value; }
    internal static string Identity(string value) { ArgumentException.ThrowIfNullOrEmpty(value); return value; }
    internal static InferenceResourceScope Scope(InferenceResourceScope value)
    {
        Required(value); Required(value.Owner); Required(value.Identity);
        return value;
    }
    internal static long? Nonnegative(long? value)
    {
        if (value is < 0) throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }
    internal static long Positive(long value) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value); return value; }
    internal static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        T[] copy = value.ToArray();
        if (copy.Any(item => item is null)) throw new ArgumentException("Resource pages cannot contain null entries.", nameof(value));
        return Array.AsReadOnly(copy);
    }
}
