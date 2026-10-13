using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class Jwt
{
    public static JsonElement? DecodePayload(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        string[] parts = token.Split('.');
        if (parts.Length < 2 || string.IsNullOrEmpty(parts[1]))
        {
            return null;
        }

        try
        {
            string normalized = parts[1].Replace('-', '+').Replace('_', '/');
            string padded = normalized.PadRight(
                normalized.Length + (4 - normalized.Length % 4) % 4,
                '=');
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(padded));
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    public static DateTimeOffset? ReadExpiration(string? token)
    {
        JsonElement? payload = DecodePayload(token);
        if (payload is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("exp", out JsonElement expiration)
            && expiration.ValueKind == JsonValueKind.Number
            && expiration.TryGetInt64(out long seconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }
}
