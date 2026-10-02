using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.TTS;

/// <summary>TTS-owned closed portable metadata. Runtime clients and sessions are not data.</summary>
public static class ConnectTtsJson
{
    public static JsonTypeInfo<TtsTransport> TtsTransport => Metadata<TtsTransport>();
    public static JsonTypeInfo<PocketTtsProtocol> PocketTtsProtocol => Metadata<PocketTtsProtocol>();
    public static JsonTypeInfo<TtsAudioFormat> TtsAudioFormat => Metadata<TtsAudioFormat>();
    public static JsonTypeInfo<PocketTtsEndpointOptions> PocketTtsEndpointOptions => Metadata<PocketTtsEndpointOptions>();
    public static JsonTypeInfo<TtsConnection> TtsConnection => Metadata<TtsConnection>();
    public static JsonTypeInfo<TtsRequest> TtsRequest => Metadata<TtsRequest>();
    public static JsonTypeInfo<TtsVoice> TtsVoice => Metadata<TtsVoice>();
    public static JsonTypeInfo<TtsCapabilities> TtsCapabilities => Metadata<TtsCapabilities>();
    public static JsonTypeInfo<TtsResult> TtsResult => Metadata<TtsResult>();
    public static JsonTypeInfo<TtsSetupConfiguration> TtsSetupConfiguration => Metadata<TtsSetupConfiguration>();
    public static JsonTypeInfo<TtsSetupHeaderChanges> TtsSetupHeaderChanges => Metadata<TtsSetupHeaderChanges>();
    public static JsonTypeInfo<TtsSetupChanges> TtsSetupChanges => Metadata<TtsSetupChanges>();
    public static JsonTypeInfo<TtsTimestamp> TtsTimestamp => Metadata<TtsTimestamp>();
    public static JsonTypeInfo<XaiTtsOptions> XaiTtsOptions => Metadata<XaiTtsOptions>();
    public static JsonTypeInfo<TtsStreamOptions> TtsStreamOptions => Metadata<TtsStreamOptions>();
    public static JsonTypeInfo<TtsAudioChunk> TtsAudioChunk => Metadata<TtsAudioChunk>();
    public static JsonTypeInfo<TtsUtteranceCompleted> TtsUtteranceCompleted => Metadata<TtsUtteranceCompleted>();
    public static JsonTypeInfo<TtsStreamError> TtsStreamError => Metadata<TtsStreamError>();

    private static readonly JsonSerializerOptions OwnedOptions = CreateOptions();
    private static JsonTypeInfo<T> Metadata<T>() => OwnedOptions.GetTypeInfo<T>();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new OwnedResolver() };
        options.Converters.Add(new TtsEnumJsonConverter<TtsTransport>());
        options.Converters.Add(new TtsEnumJsonConverter<PocketTtsProtocol>());
        options.Converters.Add(new TtsEnumJsonConverter<TtsAudioFormat>());
        options.Converters.Add(new TtsRuntimeJsonConverter<ITtsClient>());
        options.Converters.Add(new TtsRuntimeJsonConverter<ITtsProvider>());
        options.Converters.Add(new TtsRuntimeJsonConverter<ITtsStreamingClient>());
        options.Converters.Add(new TtsRuntimeJsonConverter<ITtsStreamingSession>());
        foreach (Type type in new[] { typeof(IProviderAuthorization), typeof(IInferenceResourceAccess), typeof(IInferenceResourceReadHandle), typeof(IInferenceResourceWriteHandle),
            typeof(InferenceResourceOperationPhase), typeof(InferenceResourceCommitOutcome) })
            options.Converters.Add(InferenceJson.InferenceResourceReference.Options.Converters.Single(converter => converter.CanConvert(type)));
        options.Converters.Add(ConnectLlmJson.Connection.Options.Converters.Single(converter => converter.CanConvert(typeof(XaiFamilyBinding))));
        options.Converters.Add(new TtsRuntimeJsonConverter<Func<XaiConnectionSelection, XaiFamilyBinding>>());
        options.Converters.Add(new TtsRuntimeJsonConverter<TtsProviderService>());
        options.Converters.Add(new TtsRuntimeJsonConverter<TtsSettings>());
        options.Converters.Add(new TtsRuntimeJsonConverter<TtsSettingsManager>());
        options.MakeReadOnly();
        return options;
    }

    private sealed class OwnedResolver : IJsonTypeInfoResolver
    {
        // Reuse Common's exact non-null byte policy without changing other ROM properties.
        private readonly JsonConverter audio = InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter!;
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type == typeof(ITtsClient)) return JsonMetadataServices.CreateValueInfo<ITtsClient>(options, new TtsRuntimeJsonConverter<ITtsClient>());
            if (type == typeof(ITtsProvider)) return JsonMetadataServices.CreateValueInfo<ITtsProvider>(options, new TtsRuntimeJsonConverter<ITtsProvider>());
            if (type == typeof(ITtsStreamingClient)) return JsonMetadataServices.CreateValueInfo<ITtsStreamingClient>(options, new TtsRuntimeJsonConverter<ITtsStreamingClient>());
            if (type == typeof(ITtsStreamingSession)) return JsonMetadataServices.CreateValueInfo<ITtsStreamingSession>(options, new TtsRuntimeJsonConverter<ITtsStreamingSession>());
            if (type == typeof(IProviderAuthorization)) return RuntimeValue<IProviderAuthorization>(options);
            if (type == typeof(IInferenceResourceAccess)) return RuntimeValue<IInferenceResourceAccess>(options);
            if (type == typeof(IInferenceResourceReadHandle)) return RuntimeValue<IInferenceResourceReadHandle>(options);
            if (type == typeof(IInferenceResourceWriteHandle)) return RuntimeValue<IInferenceResourceWriteHandle>(options);
            if (type == typeof(XaiFamilyBinding)) return RuntimeValue<XaiFamilyBinding>(options);
            if (type == typeof(Func<XaiConnectionSelection, XaiFamilyBinding>)) return RuntimeValue<Func<XaiConnectionSelection, XaiFamilyBinding>>(options);
            if (type == typeof(TtsProviderService)) return RuntimeValue<TtsProviderService>(options);
            if (type == typeof(TtsSettings)) return RuntimeValue<TtsSettings>(options);
            if (type == typeof(TtsSettingsManager)) return RuntimeValue<TtsSettingsManager>(options);
            JsonTypeInfo? metadata = ((IJsonTypeInfoResolver)ConnectTtsJsonContext.Default).GetTypeInfo(type, options);
            if ((type == typeof(TtsResult) || type == typeof(TtsAudioChunk)) && metadata is not null)
                metadata.Properties.Single(property => property.Name == "Audio").CustomConverter = audio;
            return metadata;
        }
        private static JsonTypeInfo<T> RuntimeValue<T>(JsonSerializerOptions options) where T : class =>
            JsonMetadataServices.CreateValueInfo<T>(options, (JsonConverter<T>)options.Converters.Single(converter => converter.CanConvert(typeof(T))));
    }
}

internal sealed class TtsEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int number)) throw new JsonException("An enum number is required.");
        T value = (T)Enum.ToObject(typeof(T), number);
        return Enum.IsDefined(value) ? value : throw new JsonException("Unknown TTS enum value.");
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown TTS enum value.");
        writer.WriteNumberValue(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
    }
}

internal sealed class TtsRuntimeJsonConverter<T> : JsonConverter<T> where T : class
{
    public override bool HandleNull => true;
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Live TTS clients and sessions cannot be restored from data.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is not null) throw new NotSupportedException("Live TTS clients and sessions cannot be captured as data.");
        writer.WriteNullValue();
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TtsTransport))]
[JsonSerializable(typeof(PocketTtsProtocol))]
[JsonSerializable(typeof(TtsAudioFormat))]
[JsonSerializable(typeof(PocketTtsEndpointOptions))]
[JsonSerializable(typeof(TtsConnection))]
[JsonSerializable(typeof(TtsRequest))]
[JsonSerializable(typeof(TtsVoice))]
[JsonSerializable(typeof(TtsCapabilities))]
[JsonSerializable(typeof(TtsResult))]
[JsonSerializable(typeof(TtsSetupConfiguration))]
[JsonSerializable(typeof(TtsSetupHeaderChanges))]
[JsonSerializable(typeof(TtsSetupChanges))]
[JsonSerializable(typeof(TtsTimestamp))]
[JsonSerializable(typeof(XaiTtsOptions))]
[JsonSerializable(typeof(TtsStreamOptions))]
[JsonSerializable(typeof(TtsAudioChunk))]
[JsonSerializable(typeof(TtsUtteranceCompleted))]
[JsonSerializable(typeof(TtsStreamError))]
internal sealed partial class ConnectTtsJsonContext : JsonSerializerContext;
