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
    private static JsonTypeInfo<T> Metadata<T>() => CreateOptions().GetTypeInfo<T>();

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

    /// <summary>JSON metadata for <see cref="ImageConnection"/>.</summary>
    public static JsonTypeInfo<ImageConnection> ImageConnection => Metadata<ImageConnection>();
    /// <summary>JSON metadata for <see cref="ImageTransport"/>.</summary>
    public static JsonTypeInfo<ImageTransport> ImageTransport => Metadata<ImageTransport>();
    /// <summary>JSON metadata for <see cref="ImageTransportCapabilities"/>.</summary>
    public static JsonTypeInfo<ImageTransportCapabilities> ImageTransportCapabilities => Metadata<ImageTransportCapabilities>();
    /// <summary>JSON metadata for <see cref="ImageTransportDescriptor"/>.</summary>
    public static JsonTypeInfo<ImageTransportDescriptor> ImageTransportDescriptor => Metadata<ImageTransportDescriptor>();
    /// <summary>JSON metadata for <see cref="ImageWorkflow"/>.</summary>
    public static JsonTypeInfo<ImageWorkflow> ImageWorkflow => Metadata<ImageWorkflow>();
    /// <summary>JSON metadata for <see cref="ImageReference"/>.</summary>
    public static JsonTypeInfo<ImageReference> ImageReference => Metadata<ImageReference>();
    /// <summary>JSON metadata for <see cref="ImageWorkflowOrigin"/>.</summary>
    public static JsonTypeInfo<ImageWorkflowOrigin> ImageWorkflowOrigin => Metadata<ImageWorkflowOrigin>();
    /// <summary>JSON metadata for <see cref="ImageWorkflowSelection"/>.</summary>
    public static JsonTypeInfo<ImageWorkflowSelection> ImageWorkflowSelection => Metadata<ImageWorkflowSelection>();
    /// <summary>JSON metadata for <see cref="ImageSetupConfiguration"/>.</summary>
    public static JsonTypeInfo<ImageSetupConfiguration> ImageSetupConfiguration => Metadata<ImageSetupConfiguration>();
    /// <summary>JSON metadata for <see cref="ImageSetupHeaderChanges"/>.</summary>
    public static JsonTypeInfo<ImageSetupHeaderChanges> ImageSetupHeaderChanges => Metadata<ImageSetupHeaderChanges>();
    /// <summary>JSON metadata for <see cref="ImageSetupChanges"/>.</summary>
    public static JsonTypeInfo<ImageSetupChanges> ImageSetupChanges => Metadata<ImageSetupChanges>();
    /// <summary>JSON metadata for <see cref="ImageTransportPreparationRequest"/>.</summary>
    public static JsonTypeInfo<ImageTransportPreparationRequest> ImageTransportPreparationRequest => Metadata<ImageTransportPreparationRequest>();
    /// <summary>JSON metadata for <see cref="ImagePreparedReference"/>.</summary>
    public static JsonTypeInfo<ImagePreparedReference> ImagePreparedReference => Metadata<ImagePreparedReference>();
    /// <summary>JSON metadata for <see cref="PreparedImageTransportRequest"/>.</summary>
    public static JsonTypeInfo<PreparedImageTransportRequest> PreparedImageTransportRequest => Metadata<PreparedImageTransportRequest>();
    /// <summary>JSON metadata for <see cref="ImageTransportOutput"/>.</summary>
    public static JsonTypeInfo<ImageTransportOutput> ImageTransportOutput => Metadata<ImageTransportOutput>();
    /// <summary>JSON metadata for <see cref="ImageTransportDownload"/>.</summary>
    public static JsonTypeInfo<ImageTransportDownload> ImageTransportDownload => Metadata<ImageTransportDownload>();
    /// <summary>JSON metadata for <see cref="ImageTransportJobState"/>.</summary>
    public static JsonTypeInfo<ImageTransportJobState> ImageTransportJobState => Metadata<ImageTransportJobState>();
    /// <summary>JSON metadata for <see cref="ImageTransportJobSnapshot"/>.</summary>
    public static JsonTypeInfo<ImageTransportJobSnapshot> ImageTransportJobSnapshot => Metadata<ImageTransportJobSnapshot>();
    /// <summary>JSON metadata for <see cref="ImageTransportProgressKind"/>.</summary>
    public static JsonTypeInfo<ImageTransportProgressKind> ImageTransportProgressKind => Metadata<ImageTransportProgressKind>();
    /// <summary>JSON metadata for <see cref="ImageTransportProgress"/>.</summary>
    public static JsonTypeInfo<ImageTransportProgress> ImageTransportProgress => Metadata<ImageTransportProgress>();
    /// <summary>JSON metadata for <see cref="ImageProviderHealth"/>.</summary>
    public static JsonTypeInfo<ImageProviderHealth> ImageProviderHealth => Metadata<ImageProviderHealth>();
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
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

