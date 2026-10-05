using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.Audio;

/// <summary>Audio-owned metadata for closed connection, generation and saved setup data.</summary>
public static class ConnectAudioJson
{
    public static JsonTypeInfo<AudioTransport> AudioTransport => Metadata<AudioTransport>();
    public static JsonTypeInfo<TangoFluxEndpointOptions> TangoFluxEndpointOptions => Metadata<TangoFluxEndpointOptions>();
    public static JsonTypeInfo<AudioConnection> AudioConnection => Metadata<AudioConnection>();
    public static JsonTypeInfo<AudioGenerationRequest> AudioGenerationRequest => Metadata<AudioGenerationRequest>();
    public static JsonTypeInfo<AudioGenerationCapabilities> AudioGenerationCapabilities => Metadata<AudioGenerationCapabilities>();
    public static JsonTypeInfo<AudioGenerationResult> AudioGenerationResult => Metadata<AudioGenerationResult>();
    public static JsonTypeInfo<AudioSetupConfiguration> AudioSetupConfiguration => Metadata<AudioSetupConfiguration>();
    public static JsonTypeInfo<AudioSetupHeaderChanges> AudioSetupHeaderChanges => Metadata<AudioSetupHeaderChanges>();
    public static JsonTypeInfo<AudioSetupChanges> AudioSetupChanges => Metadata<AudioSetupChanges>();

    private static JsonTypeInfo<T> Metadata<T>() => CreateOptions().GetTypeInfo<T>();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new OwnedResolver() };
        options.Converters.Add(new AudioTransportJsonConverter());
        options.Converters.Add(new AudioRuntimeJsonConverter<IAudioGenerationClient>());
        options.Converters.Add(new AudioRuntimeJsonConverter<IAudioProvider>());
        options.Converters.Add(new AudioRuntimeJsonConverter<AudioProviderService>());
        options.Converters.Add(new AudioRuntimeJsonConverter<AudioSettings>());
        options.Converters.Add(new AudioRuntimeJsonConverter<AudioSettingsManager>());
        options.Converters.Add(new AudioRuntimeJsonConverter<ISetupCommitTarget<AudioSetupChanges>>());
        options.Converters.Add(new AudioRuntimeJsonConverter<Stream>());
        foreach (Type type in new[] { typeof(IProviderAuthorization), typeof(IInferenceResourceAccess),
            typeof(IInferenceResourceReadHandle), typeof(IInferenceResourceWriteHandle) })
            options.Converters.Add(InferenceJson.InferenceResourceReference.Options.Converters.Single(converter => converter.CanConvert(type)));
        options.MakeReadOnly();
        return options;
    }

    private sealed class OwnedResolver : IJsonTypeInfoResolver
    {
        private readonly JsonConverter audio = InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter!;

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type == typeof(IAudioGenerationClient)) return Runtime<IAudioGenerationClient>(options);
            if (type == typeof(IAudioProvider)) return Runtime<IAudioProvider>(options);
            if (type == typeof(AudioProviderService)) return Runtime<AudioProviderService>(options);
            if (type == typeof(AudioSettings)) return Runtime<AudioSettings>(options);
            if (type == typeof(AudioSettingsManager)) return Runtime<AudioSettingsManager>(options);
            if (type == typeof(ISetupCommitTarget<AudioSetupChanges>)) return Runtime<ISetupCommitTarget<AudioSetupChanges>>(options);
            if (type == typeof(Stream)) return Runtime<Stream>(options);
            if (type == typeof(IProviderAuthorization)) return Runtime<IProviderAuthorization>(options);
            if (type == typeof(IInferenceResourceAccess)) return Runtime<IInferenceResourceAccess>(options);
            if (type == typeof(IInferenceResourceReadHandle)) return Runtime<IInferenceResourceReadHandle>(options);
            if (type == typeof(IInferenceResourceWriteHandle)) return Runtime<IInferenceResourceWriteHandle>(options);
            JsonTypeInfo? metadata = ((IJsonTypeInfoResolver)ConnectAudioJsonContext.Default).GetTypeInfo(type, options);
            if (metadata is null || metadata.Kind != JsonTypeInfoKind.Object) return metadata;
            metadata.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            if (type == typeof(AudioGenerationCapabilities))
                foreach (JsonPropertyInfo property in metadata.Properties) property.IsRequired = true;
            if (type == typeof(AudioGenerationResult))
            {
                Require(metadata, "ContentType", "Audio", "Provenance");
                metadata.Properties.Single(property => property.Name == "Audio").CustomConverter = audio;
            }
            if (type == typeof(AudioSetupConfiguration)) Require(metadata, "Connection");
            if (type == typeof(AudioSetupHeaderChanges)) Require(metadata, "Set", "Remove");
            if (type == typeof(AudioSetupChanges)) Require(metadata, "Identity", "Configuration", "Headers");
            metadata.OnSerializing = Validate;
            metadata.OnDeserialized = Validate;
            return metadata;
        }

        private static JsonTypeInfo<T> Runtime<T>(JsonSerializerOptions options) where T : class =>
            JsonMetadataServices.CreateValueInfo<T>(options, (JsonConverter<T>)options.Converters.Single(converter => converter.CanConvert(typeof(T))));

        private static void Require(JsonTypeInfo metadata, params string[] names)
        {
            foreach (string name in names) metadata.Properties.Single(property => property.Name == name).IsRequired = true;
        }

        private static void Validate(object value)
        {
            switch (value)
            {
                case AudioConnection connection:
                    NonNull(connection.Endpoint); NonNull(connection.Headers); NonNull(connection.TangoFlux);
                    if (connection.Headers.Values.Any(item => item is null)) throw new JsonException("Audio header values cannot be null.");
                    break;
                case TangoFluxEndpointOptions endpoint:
                    NonNull(endpoint.GeneratePath);
                    break;
                case AudioGenerationRequest request:
                    NonNull(request.Prompt);
                    break;
                case AudioGenerationCapabilities capabilities:
                    NonNull(capabilities.ContentTypes);
                    if (capabilities.ContentTypes.Any(item => item is null)) throw new JsonException("Audio content types cannot contain null.");
                    break;
                case AudioGenerationResult result:
                    NonNull(result.ContentType); NonNull(result.Provenance);
                    break;
                case AudioSetupConfiguration configuration:
                    NonNull(configuration.Connection);
                    break;
                case AudioSetupHeaderChanges changes:
                    NonNull(changes.Set); NonNull(changes.Remove);
                    if (changes.Set.Values.Any(item => item is null)) throw new JsonException("Audio header values cannot be null.");
                    if (changes.Remove.Any(item => item is null)) throw new JsonException("Audio header removals cannot contain null.");
                    break;
                case AudioSetupChanges changes:
                    NonNull(changes.Identity); NonNull(changes.Configuration); NonNull(changes.Headers);
                    break;
            }
        }

        private static void NonNull(object? value)
        {
            if (value is null) throw new JsonException("A required Audio data value cannot be null.");
        }
    }
}

internal sealed class AudioTransportJsonConverter : JsonConverter<AudioTransport>
{
    public override AudioTransport Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number) && number == (int)AudioTransport.TangoFluxHttp
            ? AudioTransport.TangoFluxHttp : throw new JsonException("Unknown Audio transport.");

    public override void Write(Utf8JsonWriter writer, AudioTransport value, JsonSerializerOptions options)
    {
        if (value != AudioTransport.TangoFluxHttp) throw new JsonException("Unknown Audio transport.");
        writer.WriteNumberValue((int)value);
    }
}

internal sealed class AudioRuntimeJsonConverter<T> : JsonConverter<T> where T : class
{
    public override bool HandleNull => true;
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Live Audio runtime objects cannot be restored from data.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is not null) throw new NotSupportedException("Live Audio runtime objects cannot be captured as data.");
        writer.WriteNullValue();
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AudioTransport))]
[JsonSerializable(typeof(TangoFluxEndpointOptions))]
[JsonSerializable(typeof(AudioConnection))]
[JsonSerializable(typeof(AudioGenerationRequest))]
[JsonSerializable(typeof(AudioGenerationCapabilities))]
[JsonSerializable(typeof(AudioGenerationResult))]
[JsonSerializable(typeof(AudioSetupConfiguration))]
[JsonSerializable(typeof(AudioSetupHeaderChanges))]
[JsonSerializable(typeof(AudioSetupChanges))]
internal sealed partial class ConnectAudioJsonContext : JsonSerializerContext;
