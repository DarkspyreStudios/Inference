using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Image;

/// <summary>Image-owned metadata for portable contracts. Native receipt data grants no authority.</summary>
public static class ConnectImageJson
{
    private static readonly JsonSerializerOptions OwnedOptions = CreateOptions();
    private static JsonTypeInfo<T> Metadata<T>() => OwnedOptions.GetTypeInfo<T>();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(ConnectImageJsonContext.Default.Options) { TypeInfoResolver = new ImageTypeInfoResolver() };
        options.Converters.Add(new ImageWorkflowOriginJsonConverter());
        foreach (Type type in new[] { typeof(IProviderAuthorization), typeof(IInferenceResourceAccess), typeof(IInferenceResourceReadHandle), typeof(IInferenceResourceWriteHandle),
            typeof(InferenceResourceOperationPhase), typeof(InferenceResourceCommitOutcome) })
            options.Converters.Add(InferenceJson.InferenceResourceReference.Options.Converters.Single(converter => converter.CanConvert(type)));
        options.MakeReadOnly();
        return options;
    }

    private sealed class ImageTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            JsonTypeInfo? metadata = ((IJsonTypeInfoResolver)ConnectImageJsonContext.Default).GetTypeInfo(type, options);
            if ((type == typeof(ImageTransportDownload) || type == typeof(BinaryMediaSource)) && metadata is not null)
                metadata.Properties.Single(property => property.Name == "Data").CustomConverter =
                    (type == typeof(BinaryMediaSource) ? InferenceJson.BinaryMediaSource : (JsonTypeInfo)InferenceJson.LlmHostedImageOutput)
                    .Properties.Single(property => property.Name == "Data").CustomConverter;
            return metadata;
        }
    }

    public static JsonTypeInfo<ImageConnection> ImageConnection => Metadata<ImageConnection>();
    public static JsonTypeInfo<ImageTransport> ImageTransport => Metadata<ImageTransport>();
    public static JsonTypeInfo<ImageTransportCapabilities> ImageTransportCapabilities => Metadata<ImageTransportCapabilities>();
    public static JsonTypeInfo<ImageTransportDescriptor> ImageTransportDescriptor => Metadata<ImageTransportDescriptor>();
    public static JsonTypeInfo<ImageWorkflow> ImageWorkflow => Metadata<ImageWorkflow>();
    public static JsonTypeInfo<ImageReference> ImageReference => Metadata<ImageReference>();
    public static JsonTypeInfo<ImageWorkflowOrigin> ImageWorkflowOrigin => Metadata<ImageWorkflowOrigin>();
    public static JsonTypeInfo<ImageWorkflowSelection> ImageWorkflowSelection => Metadata<ImageWorkflowSelection>();
    public static JsonTypeInfo<ImageSetupConfiguration> ImageSetupConfiguration => Metadata<ImageSetupConfiguration>();
    public static JsonTypeInfo<ImageSetupHeaderChanges> ImageSetupHeaderChanges => Metadata<ImageSetupHeaderChanges>();
    public static JsonTypeInfo<ImageSetupChanges> ImageSetupChanges => Metadata<ImageSetupChanges>();
    public static JsonTypeInfo<ImageTransportPreparationRequest> ImageTransportPreparationRequest => Metadata<ImageTransportPreparationRequest>();
    public static JsonTypeInfo<ImagePreparedReference> ImagePreparedReference => Metadata<ImagePreparedReference>();
    public static JsonTypeInfo<PreparedImageTransportRequest> PreparedImageTransportRequest => Metadata<PreparedImageTransportRequest>();
    public static JsonTypeInfo<ImageTransportOutput> ImageTransportOutput => Metadata<ImageTransportOutput>();
    public static JsonTypeInfo<ImageTransportDownload> ImageTransportDownload => Metadata<ImageTransportDownload>();
    public static JsonTypeInfo<ImageTransportJobState> ImageTransportJobState => Metadata<ImageTransportJobState>();
    public static JsonTypeInfo<ImageTransportJobSnapshot> ImageTransportJobSnapshot => Metadata<ImageTransportJobSnapshot>();
    public static JsonTypeInfo<ImageTransportProgressKind> ImageTransportProgressKind => Metadata<ImageTransportProgressKind>();
    public static JsonTypeInfo<ImageTransportProgress> ImageTransportProgress => Metadata<ImageTransportProgress>();
    public static JsonTypeInfo<ImageProviderHealth> ImageProviderHealth => Metadata<ImageProviderHealth>();
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(ImageBindingJsonConverter), typeof(ImageBindingFactoryJsonConverter)])]
[JsonSerializable(typeof(ImageConnection))]
[JsonSerializable(typeof(ImageGenerationRequest))]
[JsonSerializable(typeof(ImageTransport))]
[JsonSerializable(typeof(ImageTransportCapabilities))]
[JsonSerializable(typeof(ImageTransportDescriptor))]
[JsonSerializable(typeof(ImageWorkflow))]
[JsonSerializable(typeof(ImageReference))]
[JsonSerializable(typeof(ImageWorkflowOrigin))]
[JsonSerializable(typeof(ImageWorkflowSelection))]
[JsonSerializable(typeof(ImageSetupConfiguration))]
[JsonSerializable(typeof(ImageSetupHeaderChanges))]
[JsonSerializable(typeof(ImageSetupChanges))]
[JsonSerializable(typeof(ImageTransportPreparationRequest))]
[JsonSerializable(typeof(ImagePreparedReference))]
[JsonSerializable(typeof(PreparedImageTransportRequest))]
[JsonSerializable(typeof(ImageTransportOutput))]
[JsonSerializable(typeof(ImageTransportDownload))]
[JsonSerializable(typeof(ImageTransportJobState))]
[JsonSerializable(typeof(ImageTransportJobSnapshot))]
[JsonSerializable(typeof(ImageTransportProgressKind))]
[JsonSerializable(typeof(ImageTransportProgress))]
[JsonSerializable(typeof(ImageProviderHealth))]
internal sealed partial class ConnectImageJsonContext : JsonSerializerContext;

internal sealed class ImageWorkflowOriginJsonConverter : JsonConverter<ImageWorkflowOrigin>
{
    public override ImageWorkflowOrigin Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value))
            throw new JsonException("Image workflow origin must be a defined integer.");
        return value switch
        {
            (int)ImageWorkflowOrigin.Host => ImageWorkflowOrigin.Host,
            (int)ImageWorkflowOrigin.BuiltIn => ImageWorkflowOrigin.BuiltIn,
            _ => throw new JsonException("The Image workflow origin is undefined.")
        };
    }

    public override void Write(Utf8JsonWriter writer, ImageWorkflowOrigin value, JsonSerializerOptions options)
    {
        if (value is not (ImageWorkflowOrigin.Host or ImageWorkflowOrigin.BuiltIn))
            throw new JsonException("The Image workflow origin is undefined.");
        writer.WriteNumberValue((int)value);
    }
}

internal abstract class ImageRuntimeJsonConverter<T> : JsonConverter<T> where T : class
{
    public override bool HandleNull => true;
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Live image collaborators are not portable data.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else throw new NotSupportedException("Live image collaborators are not portable data.");
    }
}
internal sealed class ImageBindingJsonConverter : ImageRuntimeJsonConverter<XaiFamilyBinding>;
internal sealed class ImageBindingFactoryJsonConverter : ImageRuntimeJsonConverter<Func<XaiConnectionSelection, XaiFamilyBinding>>;
