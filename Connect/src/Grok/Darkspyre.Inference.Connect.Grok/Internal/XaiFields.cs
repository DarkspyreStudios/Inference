using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Reads the proxy's snake-case or camel-case spelling of a field, snake first.</summary>
internal static class XaiFields
{
    internal static bool TryGet(JsonElement item, string snake, string camel, out JsonElement value)
    {
        value = default;
        return item.ValueKind == JsonValueKind.Object
            && (item.TryGetProperty(snake, out value) || item.TryGetProperty(camel, out value))
            && value.ValueKind != JsonValueKind.Null;
    }
}
