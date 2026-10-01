using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Image;

/// <summary>Image-owned metadata for portable contracts. Native receipt data grants no authority.</summary>
public static class ConnectImageJson
{
    public static JsonTypeInfo<ImageConnection> ImageConnection => ConnectImageJsonContext.Default.ImageConnection;
    public static JsonTypeInfo<ImageTransport> ImageTransport => ConnectImageJsonContext.Default.ImageTransport;
    public static JsonTypeInfo<ImageTransportCapabilities> ImageTransportCapabilities => ConnectImageJsonContext.Default.ImageTransportCapabilities;
    public static JsonTypeInfo<ImageTransportDescriptor> ImageTransportDescriptor => ConnectImageJsonContext.Default.ImageTransportDescriptor;
    public static JsonTypeInfo<ImageWorkflow> ImageWorkflow => ConnectImageJsonContext.Default.ImageWorkflow;
    public static JsonTypeInfo<ImageReference> ImageReference => ConnectImageJsonContext.Default.ImageReference;
    public static JsonTypeInfo<ImageWorkflowOrigin> ImageWorkflowOrigin => ConnectImageJsonContext.Default.ImageWorkflowOrigin;
    public static JsonTypeInfo<ImageWorkflowSelection> ImageWorkflowSelection => ConnectImageJsonContext.Default.ImageWorkflowSelection;
    public static JsonTypeInfo<ImageSetupConfiguration> ImageSetupConfiguration => ConnectImageJsonContext.Default.ImageSetupConfiguration;
    public static JsonTypeInfo<ImageSetupHeaderChanges> ImageSetupHeaderChanges => ConnectImageJsonContext.Default.ImageSetupHeaderChanges;
    public static JsonTypeInfo<ImageSetupChanges> ImageSetupChanges => ConnectImageJsonContext.Default.ImageSetupChanges;
    public static JsonTypeInfo<ImageTransportPreparationRequest> ImageTransportPreparationRequest => ConnectImageJsonContext.Default.ImageTransportPreparationRequest;
    public static JsonTypeInfo<ImagePreparedReference> ImagePreparedReference => ConnectImageJsonContext.Default.ImagePreparedReference;
    public static JsonTypeInfo<PreparedImageTransportRequest> PreparedImageTransportRequest => ConnectImageJsonContext.Default.PreparedImageTransportRequest;
    public static JsonTypeInfo<ImageTransportOutput> ImageTransportOutput => ConnectImageJsonContext.Default.ImageTransportOutput;
    public static JsonTypeInfo<ImageTransportDownload> ImageTransportDownload => ConnectImageJsonContext.Default.ImageTransportDownload;
    public static JsonTypeInfo<ImageTransportJobState> ImageTransportJobState => ConnectImageJsonContext.Default.ImageTransportJobState;
    public static JsonTypeInfo<ImageTransportJobSnapshot> ImageTransportJobSnapshot => ConnectImageJsonContext.Default.ImageTransportJobSnapshot;
    public static JsonTypeInfo<ImageTransportProgressKind> ImageTransportProgressKind => ConnectImageJsonContext.Default.ImageTransportProgressKind;
    public static JsonTypeInfo<ImageTransportProgress> ImageTransportProgress => ConnectImageJsonContext.Default.ImageTransportProgress;
    public static JsonTypeInfo<ImageProviderHealth> ImageProviderHealth => ConnectImageJsonContext.Default.ImageProviderHealth;
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(ImageAuthorizationJsonConverter), typeof(ImageBindingJsonConverter),
        typeof(ImageResourceAccessJsonConverter), typeof(ImageResourceReadJsonConverter),
        typeof(ImageResourceWriteJsonConverter), typeof(ImageBindingFactoryJsonConverter)])]
[JsonSerializable(typeof(ImageConnection))]
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
internal sealed class ImageAuthorizationJsonConverter : ImageRuntimeJsonConverter<IProviderAuthorization>;
internal sealed class ImageBindingJsonConverter : ImageRuntimeJsonConverter<XaiFamilyBinding>;
internal sealed class ImageResourceAccessJsonConverter : ImageRuntimeJsonConverter<IInferenceResourceAccess>;
internal sealed class ImageResourceReadJsonConverter : ImageRuntimeJsonConverter<IInferenceResourceReadHandle>;
internal sealed class ImageResourceWriteJsonConverter : ImageRuntimeJsonConverter<IInferenceResourceWriteHandle>;
internal sealed class ImageBindingFactoryJsonConverter : ImageRuntimeJsonConverter<Func<XaiConnectionSelection, XaiFamilyBinding>>;
