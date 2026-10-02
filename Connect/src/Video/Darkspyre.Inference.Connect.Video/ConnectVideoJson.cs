using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Video;

/// <summary>Finite Video-owned metadata. Serialized native receipts do not confer authority.</summary>
public static class ConnectVideoJson
{
    private static readonly JsonSerializerOptions OwnedOptions = CreateOptions();
    private static JsonTypeInfo<T> Metadata<T>() => OwnedOptions.GetTypeInfo<T>();
    public static JsonTypeInfo<VideoTransport> VideoTransport => Metadata<VideoTransport>();
    public static JsonTypeInfo<VideoConnection> VideoConnection => Metadata<VideoConnection>();
    public static JsonTypeInfo<XaiVideoOptions> XaiVideoOptions => Metadata<XaiVideoOptions>();
    public static JsonTypeInfo<XaiVideoUploadDestination> XaiVideoUploadDestination => Metadata<XaiVideoUploadDestination>();
    public static JsonTypeInfo<VideoSetupConfiguration> VideoSetupConfiguration => Metadata<VideoSetupConfiguration>();
    public static JsonTypeInfo<VideoSetupHeaderChanges> VideoSetupHeaderChanges => Metadata<VideoSetupHeaderChanges>();
    public static JsonTypeInfo<VideoSetupChanges> VideoSetupChanges => Metadata<VideoSetupChanges>();
    public static JsonTypeInfo<VideoProviderOptions> VideoProviderOptions => Metadata<VideoProviderOptions>();
    public static JsonTypeInfo<VideoRequest> VideoRequest => Metadata<VideoRequest>();
    public static JsonTypeInfo<VideoGenerationRequest> VideoGenerationRequest => Metadata<VideoGenerationRequest>();
    public static JsonTypeInfo<VideoEditRequest> VideoEditRequest => Metadata<VideoEditRequest>();
    public static JsonTypeInfo<VideoExtensionRequest> VideoExtensionRequest => Metadata<VideoExtensionRequest>();
    public static JsonTypeInfo<VideoCapabilities> VideoCapabilities => Metadata<VideoCapabilities>();
    public static JsonTypeInfo<VideoJob> VideoJob => Metadata<VideoJob>();
    public static JsonTypeInfo<VideoJobState> VideoJobState => Metadata<VideoJobState>();
    public static JsonTypeInfo<VideoJobSnapshot> VideoJobSnapshot => Metadata<VideoJobSnapshot>();
    public static JsonTypeInfo<VideoOutput> VideoOutput => Metadata<VideoOutput>();
    public static JsonTypeInfo<VideoOperation> VideoOperation => Metadata<VideoOperation>();
    public static JsonTypeInfo<VideoKeyframe> VideoKeyframe => Metadata<VideoKeyframe>();
    public static JsonTypeInfo<VideoReferenceAudio> VideoReferenceAudio => Metadata<VideoReferenceAudio>();
    public static JsonTypeInfo<VideoVoiceReference> VideoVoiceReference => Metadata<VideoVoiceReference>();
    public static JsonTypeInfo<VideoAudioReference> VideoAudioReference => Metadata<VideoAudioReference>();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(ConnectVideoJsonContext.Default.Options) { TypeInfoResolver = new VideoResolver() };
        foreach (Type type in new[] { typeof(IProviderAuthorization), typeof(IInferenceResourceAccess), typeof(IInferenceResourceReadHandle), typeof(IInferenceResourceWriteHandle),
            typeof(IVideoInference), typeof(VideoJobState), typeof(VideoOperation), typeof(InferenceMediaSourceKind),
            typeof(InferenceResourceOperationPhase), typeof(InferenceResourceCommitOutcome) })
            options.Converters.Add(InferenceJson.VideoRequest.Options.Converters.Single(converter => converter.CanConvert(type)));
        options.MakeReadOnly();
        return options;
    }

    private sealed class VideoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            JsonTypeInfo? metadata = ((IJsonTypeInfoResolver)ConnectVideoJsonContext.Default).GetTypeInfo(type, options);
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
            return metadata;
        }
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(VideoTransportJsonConverter), typeof(VideoBindingJsonConverter), typeof(VideoBindingFactoryJsonConverter)])]
[JsonSerializable(typeof(VideoTransport))]
[JsonSerializable(typeof(VideoConnection))]
[JsonSerializable(typeof(XaiVideoOptions))]
[JsonSerializable(typeof(XaiVideoUploadDestination))]
[JsonSerializable(typeof(XaiFileStorageOptions))]
[JsonSerializable(typeof(XaiStoragePublicUrlRequest))]
[JsonSerializable(typeof(XaiStoragePublicUrlFlag))]
[JsonSerializable(typeof(XaiStoragePublicUrlConfiguration))]
[JsonSerializable(typeof(VideoSetupConfiguration))]
[JsonSerializable(typeof(VideoSetupHeaderChanges))]
[JsonSerializable(typeof(VideoSetupChanges))]
[JsonSerializable(typeof(VideoProviderOptions))]
[JsonSerializable(typeof(VideoRequest))]
[JsonSerializable(typeof(VideoGenerationRequest))]
[JsonSerializable(typeof(VideoEditRequest))]
[JsonSerializable(typeof(VideoExtensionRequest))]
[JsonSerializable(typeof(VideoCapabilities))]
[JsonSerializable(typeof(VideoJob))]
[JsonSerializable(typeof(VideoJobState))]
[JsonSerializable(typeof(VideoJobSnapshot))]
[JsonSerializable(typeof(VideoOutput))]
[JsonSerializable(typeof(VideoOperation))]
[JsonSerializable(typeof(VideoKeyframe))]
[JsonSerializable(typeof(VideoReferenceAudio))]
[JsonSerializable(typeof(VideoVoiceReference))]
[JsonSerializable(typeof(VideoAudioReference))]
internal sealed partial class ConnectVideoJsonContext : JsonSerializerContext;

internal sealed class VideoTransportJsonConverter : JsonConverter<VideoTransport>
{
    public override VideoTransport Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value) && Enum.IsDefined((VideoTransport)value)
            ? (VideoTransport)value : throw new JsonException("A defined video transport number is required.");
    public override void Write(Utf8JsonWriter writer, VideoTransport value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("The video transport is undefined.");
        writer.WriteNumberValue((int)value);
    }
}

internal abstract class VideoRuntimeJsonConverter<T> : JsonConverter<T> where T : class
{
    public override bool HandleNull => true;
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Live video collaborators are not portable data.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else throw new NotSupportedException("Live video collaborators are not portable data.");
    }
}
internal sealed class VideoBindingJsonConverter : VideoRuntimeJsonConverter<XaiFamilyBinding>;
internal sealed class VideoBindingFactoryJsonConverter : VideoRuntimeJsonConverter<Func<XaiConnectionSelection, XaiFamilyBinding>>;
