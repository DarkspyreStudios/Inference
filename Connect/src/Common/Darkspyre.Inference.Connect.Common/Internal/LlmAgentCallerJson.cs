using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class LlmAgentCallerJson
{
    internal static JsonTypeInfo? RuntimeMetadata(Type type, JsonSerializerOptions options)
    {
        if (type == typeof(LlmAgentCallerServices)) return JsonMetadataServices.CreateValueInfo<LlmAgentCallerServices>(options, new CallerAuthorityConverter<LlmAgentCallerServices>());
        if (type == typeof(ILlmAgentPermissionSelector)) return JsonMetadataServices.CreateValueInfo<ILlmAgentPermissionSelector>(options, new CallerAuthorityConverter<ILlmAgentPermissionSelector>());
        if (type == typeof(ILlmAgentTextFileReader)) return JsonMetadataServices.CreateValueInfo<ILlmAgentTextFileReader>(options, new CallerAuthorityConverter<ILlmAgentTextFileReader>());
        if (type == typeof(ILlmAgentTextFileWriter)) return JsonMetadataServices.CreateValueInfo<ILlmAgentTextFileWriter>(options, new CallerAuthorityConverter<ILlmAgentTextFileWriter>());
        if (type == typeof(ILlmAgentTerminalFactory)) return JsonMetadataServices.CreateValueInfo<ILlmAgentTerminalFactory>(options, new CallerAuthorityConverter<ILlmAgentTerminalFactory>());
        if (type == typeof(ILlmAgentTerminal)) return JsonMetadataServices.CreateValueInfo<ILlmAgentTerminal>(options, new CallerAuthorityConverter<ILlmAgentTerminal>());
        if (type == typeof(ILlmAgentShellExecutor)) return JsonMetadataServices.CreateValueInfo<ILlmAgentShellExecutor>(options, new CallerAuthorityConverter<ILlmAgentShellExecutor>());
        return null;
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
