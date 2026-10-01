using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

internal static class QwenJson
{
    public static Dictionary<string, object?> ReadArguments(string json) =>
        JsonSerializer.Deserialize(json, QwenJsonContext.Default.DictionaryStringObject) ?? [];

    public static string WriteArguments(Dictionary<string, object?> arguments) =>
        JsonSerializer.Serialize(arguments, QwenJsonContext.Default.DictionaryStringObject);

    public static string WriteTools(IReadOnlyList<LlmToolDefinition> tools) => JsonSerializer.Serialize(
        tools.Select(static tool => new QwenToolJson(tool.Name, tool.Description ?? string.Empty, tool.InputSchema)).ToArray(),
        QwenJsonContext.Default.QwenToolJsonArray);
}

internal sealed record QwenToolJson(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters);

// Tool-call dictionaries carry JSON data and the supplier's primitive/string-array values, not arbitrary CLR objects.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(QwenToolJson[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(List<object?>))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
internal sealed partial class QwenJsonContext : JsonSerializerContext;
