using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class LlmJson
{
    public static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number)
            ? number
            : null;

    public static JsonElement? GetObject(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
}
