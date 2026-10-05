using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.STT;

/// <summary>STT-owned closed data metadata. Borrowed runtime authorities are never portable data.</summary>
public static class ConnectSttJson
{
    private static JsonTypeInfo<T> Metadata<T>() => CreateOptions().GetTypeInfo<T>();
    public static JsonTypeInfo<SttTransport> SttTransport => Metadata<SttTransport>();
    public static JsonTypeInfo<SttConnection> SttConnection => Metadata<SttConnection>();
    public static JsonTypeInfo<SttSetupConfiguration> SttSetupConfiguration => Metadata<SttSetupConfiguration>();
    public static JsonTypeInfo<SttSetupHeaderChanges> SttSetupHeaderChanges => Metadata<SttSetupHeaderChanges>();
    public static JsonTypeInfo<SttSetupChanges> SttSetupChanges => Metadata<SttSetupChanges>();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(ConnectSttJsonContext.Default.Options) { TypeInfoResolver = new SttTypeInfoResolver() };
        foreach (Type type in new[] { typeof(IProviderAuthorization), typeof(IInferenceResourceAccess), typeof(IInferenceResourceReadHandle), typeof(IInferenceResourceWriteHandle), typeof(ISpeechTranscriptionClient), typeof(ISpeechTranscriptionSession) })
            options.Converters.Add(InferenceJson.SpeechTranscriptionOptions.Options.Converters.Single(converter => converter.CanConvert(type)));
        options.MakeReadOnly();
        return options;
    }

    private sealed class SttTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type == typeof(XaiFamilyBinding)) return JsonMetadataServices.CreateValueInfo<XaiFamilyBinding>(options, new SttBindingJsonConverter());
            if (type == typeof(Func<XaiConnectionSelection, XaiFamilyBinding>)) return JsonMetadataServices.CreateValueInfo<Func<XaiConnectionSelection, XaiFamilyBinding>>(options, new SttBindingFactoryJsonConverter());
            if (type == typeof(IInferenceResourceAccess)) return JsonMetadataServices.CreateValueInfo<IInferenceResourceAccess>(options, (JsonConverter<IInferenceResourceAccess>)options.Converters.Single(converter => converter.CanConvert(type)));
            if (type == typeof(IInferenceResourceReadHandle)) return JsonMetadataServices.CreateValueInfo<IInferenceResourceReadHandle>(options, (JsonConverter<IInferenceResourceReadHandle>)options.Converters.Single(converter => converter.CanConvert(type)));
            if (type == typeof(IInferenceResourceWriteHandle)) return JsonMetadataServices.CreateValueInfo<IInferenceResourceWriteHandle>(options, (JsonConverter<IInferenceResourceWriteHandle>)options.Converters.Single(converter => converter.CanConvert(type)));
            if (type == typeof(ISpeechTranscriptionClient)) return JsonMetadataServices.CreateValueInfo<ISpeechTranscriptionClient>(options, (JsonConverter<ISpeechTranscriptionClient>)options.Converters.Single(converter => converter.CanConvert(type)));
            if (type == typeof(ISpeechTranscriptionSession)) return JsonMetadataServices.CreateValueInfo<ISpeechTranscriptionSession>(options, (JsonConverter<ISpeechTranscriptionSession>)options.Converters.Single(converter => converter.CanConvert(type)));
            if (type == typeof(ISttProvider)) return JsonMetadataServices.CreateValueInfo<ISttProvider>(options, new SttProviderJsonConverter());
            if (type == typeof(SttProviderService)) return JsonMetadataServices.CreateValueInfo<SttProviderService>(options, new SttServiceJsonConverter());
            if (type == typeof(SttSettingsManager)) return JsonMetadataServices.CreateValueInfo<SttSettingsManager>(options, new SttManagerJsonConverter());
            return ((IJsonTypeInfoResolver)ConnectSttJsonContext.Default).GetTypeInfo(type, options);
        }
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(SttTransportJsonConverter), typeof(SttBindingJsonConverter), typeof(SttBindingFactoryJsonConverter)])]
[JsonSerializable(typeof(SttTransport))]
[JsonSerializable(typeof(SttConnection))]
[JsonSerializable(typeof(SttSetupConfiguration))]
[JsonSerializable(typeof(SttSetupHeaderChanges))]
[JsonSerializable(typeof(SttSetupChanges))]
internal sealed partial class ConnectSttJsonContext : JsonSerializerContext;

internal sealed class SttTransportJsonConverter : JsonConverter<SttTransport>
{
    public override SttTransport Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value) && Enum.IsDefined((SttTransport)value)
            ? (SttTransport)value : throw new JsonException("Unknown hosted-STT transport.");
    public override void Write(Utf8JsonWriter writer, SttTransport value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown hosted-STT transport.");
        writer.WriteNumberValue((int)value);
    }
}
internal abstract class SttRuntimeJsonConverter<T> : JsonConverter<T> where T : class
{
    public override bool HandleNull => true;
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Runtime STT collaborators are not portable data.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else throw new NotSupportedException("Runtime STT collaborators are not portable data.");
    }
}
internal sealed class SttBindingJsonConverter : SttRuntimeJsonConverter<XaiFamilyBinding>;
internal sealed class SttBindingFactoryJsonConverter : SttRuntimeJsonConverter<Func<XaiConnectionSelection, XaiFamilyBinding>>;
internal sealed class SttProviderJsonConverter : SttRuntimeJsonConverter<ISttProvider>;
internal sealed class SttServiceJsonConverter : SttRuntimeJsonConverter<SttProviderService>;
internal sealed class SttManagerJsonConverter : SttRuntimeJsonConverter<SttSettingsManager>;
