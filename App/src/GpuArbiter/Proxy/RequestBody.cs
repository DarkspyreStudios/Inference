using System.Text.Json;

namespace GpuArbiter.Proxy;

public sealed record RequestBody(byte[] Bytes, string? Model, bool Stream)
{
    public static async Task<RequestBody> ReadAsync(HttpRequest request, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, token).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object.");
        string? model = null;
        if (json.RootElement.TryGetProperty("model", out var value))
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model = value.GetString()) ||
                model.Length > 256 || model.Any(char.IsControl)) throw new JsonException("Invalid model identifier.");
        }
        var stream = json.RootElement.TryGetProperty("stream", out var streamValue) && streamValue.ValueKind == JsonValueKind.True;
        // Provider content stays opaque. It is forwarded as bytes, never markup, a path, a command or a log message.
        return new(bytes, model, stream);
    }
}
