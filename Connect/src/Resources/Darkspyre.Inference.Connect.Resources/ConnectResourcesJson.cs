using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.Video;

namespace Darkspyre.Inference.Connect.Resources;

/// <summary>Resources-owned closed metadata. Native receipts grant no runtime authority.</summary>
public static class ConnectResourcesJson
{
    private static JsonTypeInfo<T> Metadata<T>() => CreateOptions().GetTypeInfo<T>();
    public static JsonTypeInfo<LlmResourcePageRequest> LlmResourcePageRequest => Metadata<LlmResourcePageRequest>();
    public static JsonTypeInfo<LlmBatchCounts> LlmBatchCounts => Metadata<LlmBatchCounts>();
    public static JsonTypeInfo<LlmBatch> LlmBatch => Metadata<LlmBatch>();
    public static JsonTypeInfo<LlmBatchOperation> LlmBatchOperation => Metadata<LlmBatchOperation>();
    public static JsonTypeInfo<LlmBatchChatOperation> LlmBatchChatOperation => Metadata<LlmBatchChatOperation>();
    public static JsonTypeInfo<LlmBatchImageOperation> LlmBatchImageOperation => Metadata<LlmBatchImageOperation>();
    public static JsonTypeInfo<LlmBatchVideoGenerationOperation> LlmBatchVideoGenerationOperation => Metadata<LlmBatchVideoGenerationOperation>();
    public static JsonTypeInfo<LlmBatchVideoEditOperation> LlmBatchVideoEditOperation => Metadata<LlmBatchVideoEditOperation>();
    public static JsonTypeInfo<LlmBatchVideoExtensionOperation> LlmBatchVideoExtensionOperation => Metadata<LlmBatchVideoExtensionOperation>();
    public static JsonTypeInfo<LlmBatchRequest> LlmBatchRequest => Metadata<LlmBatchRequest>();
    public static JsonTypeInfo<LlmBatchRequestMetadata> LlmBatchRequestMetadata => Metadata<LlmBatchRequestMetadata>();
    public static JsonTypeInfo<LlmBatchPage> LlmBatchPage => Metadata<LlmBatchPage>();
    public static JsonTypeInfo<LlmBatchRequestPage> LlmBatchRequestPage => Metadata<LlmBatchRequestPage>();
    public static JsonTypeInfo<LlmBatchResult> LlmBatchResult => Metadata<LlmBatchResult>();
    public static JsonTypeInfo<LlmBatchChatResult> LlmBatchChatResult => Metadata<LlmBatchChatResult>();
    public static JsonTypeInfo<LlmBatchImageResult> LlmBatchImageResult => Metadata<LlmBatchImageResult>();
    public static JsonTypeInfo<LlmBatchVideoResult> LlmBatchVideoResult => Metadata<LlmBatchVideoResult>();
    public static JsonTypeInfo<LlmBatchFailure> LlmBatchFailure => Metadata<LlmBatchFailure>();
    public static JsonTypeInfo<LlmBatchResultPage> LlmBatchResultPage => Metadata<LlmBatchResultPage>();
    public static JsonTypeInfo<LlmDeferredCompletion> LlmDeferredCompletion => Metadata<LlmDeferredCompletion>();
    public static JsonTypeInfo<LlmDeferredResult> LlmDeferredResult => Metadata<LlmDeferredResult>();
    public static JsonTypeInfo<LlmDeferredPending> LlmDeferredPending => Metadata<LlmDeferredPending>();
    public static JsonTypeInfo<LlmDeferredCompleted> LlmDeferredCompleted => Metadata<LlmDeferredCompleted>();
    public static JsonTypeInfo<LlmMediaMaterializationOptions> LlmMediaMaterializationOptions => Metadata<LlmMediaMaterializationOptions>();
    public static JsonTypeInfo<MaterializedVideoResult> MaterializedVideoResult => Metadata<MaterializedVideoResult>();
    public static JsonTypeInfo<LlmResourceOperation> LlmResourceOperation => Metadata<LlmResourceOperation>();
    public static JsonTypeInfo<LlmResourceInputUploadObservation> LlmResourceInputUploadObservation => Metadata<LlmResourceInputUploadObservation>();
    public static JsonTypeInfo<LlmResourceOperationObservation> LlmResourceOperationObservation => Metadata<LlmResourceOperationObservation>();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(ConnectResourcesJsonContext.Default.Options) { TypeInfoResolver = new ResourcesResolver() };
        foreach (Type type in new[] { typeof(IProviderAuthorization), typeof(IInferenceResourceAccess),
            typeof(IInferenceResourceReadHandle), typeof(IInferenceResourceWriteHandle),
            typeof(LlmHostedToolKind), typeof(LlmHostedToolPhase), typeof(LlmImageGenerationAction), typeof(VideoJobState), typeof(VideoOperation),
            typeof(InferenceResourceOperationPhase), typeof(InferenceResourceCommitOutcome) })
            options.Converters.Add(InferenceJson.VideoRequest.Options.Converters.Single(converter => converter.CanConvert(type)));
        foreach (Type type in new[] { typeof(LlmAgentCallerServices), typeof(ILlmAgentPermissionSelector),
            typeof(ILlmAgentTextFileReader), typeof(ILlmAgentTextFileWriter), typeof(ILlmAgentTerminalFactory),
            typeof(ILlmAgentTerminal), typeof(ILlmAgentShellExecutor) })
            options.Converters.Add(ConnectLlmJson.ChatRequest.Options.GetConverter(type));
        options.MakeReadOnly();
        return options;
    }

    private sealed class ResourcesResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            JsonTypeInfo? metadata = ((IJsonTypeInfoResolver)ConnectResourcesJsonContext.Default).GetTypeInfo(type, options);
            if ((type == typeof(LlmResourceInputUploadObservation) || type == typeof(LlmResourceOperationObservation)) && metadata is not null)
            {
                metadata.OnSerializing = ResourceObservationData.Validate;
                metadata.OnDeserialized = ResourceObservationData.Validate;
            }
            if (type == typeof(LlmProviderControls) && metadata is not null)
                metadata.PolymorphismOptions = new()
                {
                    TypeDiscriminatorPropertyName = "type",
                    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                    DerivedTypes = { new JsonDerivedType(typeof(XaiMultiAgentControls), "xai-multi-agent") }
                };
            if (type == typeof(LlmHostedTool) && metadata?.PolymorphismOptions is { } hosted)
                hosted.DerivedTypes.Add(new JsonDerivedType(typeof(XaiXSearchTool), "xai-x-search"));
            if (type == typeof(VideoProviderOptions) && metadata is not null)
                metadata.PolymorphismOptions = new()
                {
                    TypeDiscriminatorPropertyName = "type",
                    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                    DerivedTypes = { new JsonDerivedType(typeof(XaiVideoOptions), "xai-video") }
                };
            if (type == typeof(BinaryMediaSource) && metadata is not null)
                metadata.Properties.Single(property => property.Name == "Data").CustomConverter =
                    InferenceJson.BinaryMediaSource.Properties.Single(property => property.Name == "Data").CustomConverter;
            if (type == typeof(LlmHostedImageOutput) && metadata is not null)
                metadata.Properties.Single(property => property.Name == "Data").CustomConverter =
                    InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter;
            return metadata;
        }
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(ResourceRuntimeJsonConverter<ILlmBatchClient>), typeof(ResourceRuntimeJsonConverter<LlmResourceProviderService>),
        typeof(ResourceRuntimeJsonConverter<LlmResourceOperationException>), typeof(ResourceRuntimeJsonConverter<LlmResourceOperationCanceledException>),
        typeof(ResourceOperationJsonConverter)])]
[JsonSerializable(typeof(LlmResourceOperation))]
[JsonSerializable(typeof(LlmResourceInputUploadObservation))]
[JsonSerializable(typeof(LlmResourceOperationObservation))]
[JsonSerializable(typeof(LlmResourcePageRequest))]
[JsonSerializable(typeof(LlmBatchCounts))]
[JsonSerializable(typeof(LlmBatch))]
[JsonSerializable(typeof(LlmBatchOperation))]
[JsonSerializable(typeof(LlmBatchChatOperation))]
[JsonSerializable(typeof(LlmBatchImageOperation))]
[JsonSerializable(typeof(LlmBatchVideoGenerationOperation))]
[JsonSerializable(typeof(LlmBatchVideoEditOperation))]
[JsonSerializable(typeof(LlmBatchVideoExtensionOperation))]
[JsonSerializable(typeof(LlmBatchRequest))]
[JsonSerializable(typeof(LlmBatchRequestMetadata))]
[JsonSerializable(typeof(LlmBatchPage))]
[JsonSerializable(typeof(LlmBatchRequestPage))]
[JsonSerializable(typeof(LlmBatchResult))]
[JsonSerializable(typeof(LlmBatchChatResult))]
[JsonSerializable(typeof(LlmBatchImageResult))]
[JsonSerializable(typeof(LlmBatchVideoResult))]
[JsonSerializable(typeof(LlmBatchFailure))]
[JsonSerializable(typeof(LlmBatchResultPage))]
[JsonSerializable(typeof(LlmDeferredCompletion))]
[JsonSerializable(typeof(LlmDeferredResult))]
[JsonSerializable(typeof(LlmDeferredPending))]
[JsonSerializable(typeof(LlmDeferredCompleted))]
[JsonSerializable(typeof(LlmMediaMaterializationOptions))]
[JsonSerializable(typeof(MaterializedVideoResult))]
[JsonSerializable(typeof(XaiXSearchTool))]
[JsonSerializable(typeof(XaiMultiAgentControls))]
[JsonSerializable(typeof(XaiVideoOptions))]
[JsonSerializable(typeof(XaiFileStorageOptions))]
[JsonSerializable(typeof(XaiStoragePublicUrlRequest))]
[JsonSerializable(typeof(XaiStoragePublicUrlFlag))]
[JsonSerializable(typeof(XaiStoragePublicUrlConfiguration))]
internal sealed partial class ConnectResourcesJsonContext : JsonSerializerContext;

internal sealed class ResourceRuntimeJsonConverter<T> : JsonConverter<T> where T : class
{
    public override bool HandleNull => true;
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Resource runtime objects are not portable data.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else throw new NotSupportedException("Resource runtime objects are not portable data.");
    }
}

internal sealed class ResourceOperationJsonConverter : JsonConverter<LlmResourceOperation>
{
    public override LlmResourceOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value)
            && Enum.IsDefined((LlmResourceOperation)value) ? (LlmResourceOperation)value
            : throw new JsonException("Unknown resource operation.");

    public override void Write(Utf8JsonWriter writer, LlmResourceOperation value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown resource operation.");
        writer.WriteNumberValue((int)value);
    }
}
