using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal sealed class CallerTypeInfoResolver : IJsonTypeInfoResolver
{
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        if (type == typeof(LlmAgentCallerServices)) return JsonMetadataServices.CreateValueInfo<LlmAgentCallerServices>(options, new CallerAuthorityConverter<LlmAgentCallerServices>());
        if (type == typeof(ILlmAgentPermissionSelector)) return JsonMetadataServices.CreateValueInfo<ILlmAgentPermissionSelector>(options, new CallerAuthorityConverter<ILlmAgentPermissionSelector>());
        if (type == typeof(ILlmAgentTextFileReader)) return JsonMetadataServices.CreateValueInfo<ILlmAgentTextFileReader>(options, new CallerAuthorityConverter<ILlmAgentTextFileReader>());
        if (type == typeof(ILlmAgentTextFileWriter)) return JsonMetadataServices.CreateValueInfo<ILlmAgentTextFileWriter>(options, new CallerAuthorityConverter<ILlmAgentTextFileWriter>());
        if (type == typeof(ILlmAgentTerminalFactory)) return JsonMetadataServices.CreateValueInfo<ILlmAgentTerminalFactory>(options, new CallerAuthorityConverter<ILlmAgentTerminalFactory>());
        if (type == typeof(ILlmAgentTerminal)) return JsonMetadataServices.CreateValueInfo<ILlmAgentTerminal>(options, new CallerAuthorityConverter<ILlmAgentTerminal>());
        if (type == typeof(ILlmAgentShellExecutor)) return JsonMetadataServices.CreateValueInfo<ILlmAgentShellExecutor>(options, new CallerAuthorityConverter<ILlmAgentShellExecutor>());
        JsonTypeInfo? info = ((IJsonTypeInfoResolver)ConnectLlmJsonContext.Default).GetTypeInfo(type, options);
        if (type == typeof(LlmAgentRuntimePolicy) && info is not null)
        {
            info.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "type",
                DerivedTypes =
                {
                    new JsonDerivedType(typeof(LlmAgentHostRuntimePolicy), "host-controlled"),
                    new JsonDerivedType(typeof(LlmAgentProviderRuntimePolicy), "provider-configured")
                }
            };
        }
        if (info is not null && (type == typeof(LlmAgentPresentedImage)
            || type == typeof(LlmAgentPresentedAudio) || type == typeof(LlmAgentPresentedResourceBytes)))
            info.Properties.Single(property => property.Name == "Data").CustomConverter = new CallerBytesConverter();
        return info;
    }
}

internal sealed class CallerAuthorityConverter<T> : JsonConverter<T> where T : class
{
    public override bool HandleNull => true;
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Live caller authority cannot be deserialized.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is not null) throw new NotSupportedException("Live caller authority cannot be serialized.");
        writer.WriteNullValue();
    }
}

internal sealed class CallerBytesConverter : JsonConverter<ReadOnlyMemory<byte>>
{
    public override ReadOnlyMemory<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String ? reader.GetBytesFromBase64()
            : throw new JsonException("Presented binary data must be a base64 string.");
    public override void Write(Utf8JsonWriter writer, ReadOnlyMemory<byte> value, JsonSerializerOptions options)
        => writer.WriteBase64StringValue(value.Span);
}

internal sealed class CallerEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int number))
            throw new JsonException("Caller enum data must be a defined integer.");
        T value = (T)Enum.ToObject(typeof(T), number);
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown caller enum value.");
        return value;
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown caller enum value.");
        writer.WriteNumberValue(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
    }
}
