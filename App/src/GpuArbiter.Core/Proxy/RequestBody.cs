using System.Text.Json;
using GpuArbiter.Gpu;

namespace GpuArbiter.Proxy;

public sealed record RequestBody(byte[] Bytes, string? Model, bool Stream)
{
    public static async Task<RequestBody> ReadAsync(HttpRequest request, CancellationToken token, long maximumBytes = 128 * 1024 * 1024)
    {
        using var buffer = new MemoryStream();
        if (request.ContentLength > maximumBytes) throw new GpuException("Request body exceeds the configured limit", 413);
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximumBytes) throw new GpuException("Request body exceeds the configured limit", 413);
            await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
        }
        var bytes = buffer.ToArray();
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object.");
        string? model = null;
        if (json.RootElement.TryGetProperty("model", out var value))
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model = value.GetString()) ||
                model.Length > 256 || model.Any(char.IsControl)) throw new JsonException("Invalid model identifier.");
        }
        if (json.RootElement.TryGetProperty("stream", out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException("Stream must be a boolean.");
        var stream = json.RootElement.TryGetProperty("stream", out var streamValue) && streamValue.ValueKind == JsonValueKind.True;
        // Provider content stays opaque. It is forwarded as bytes, never markup, a path, a command or a log message.
        return new(bytes, model, stream);
    }
}
