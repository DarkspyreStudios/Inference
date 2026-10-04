using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Finite media wire facts; no route, account, upload or receipt authority is established here.</summary>
internal static class XaiMediaData
{
    internal static async Task<string> AudioDataUrlAsync(Stream source, string? contentType, long maximumEncodedBytes,
        Action fence, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumEncodedBytes);
        Fence();
        string prefix = $"data:{contentType ?? "application/octet-stream"};base64,";
        long prefixBytes = Encoding.UTF8.GetByteCount(prefix);
        if (prefixBytes > maximumEncodedBytes) throw TooLarge();
        long maximumRawBytes = (maximumEncodedBytes - prefixBytes) / 4 * 3;
        var encoded = new StringBuilder(prefix);
        var buffer = new byte[12 * 1024];
        long total = 0;
        int retained = 0;
        while (true)
        {
            Fence();
            int requested = (int)Math.Min(buffer.Length - retained, maximumRawBytes - total + 1);
            int read = await source.ReadAsync(buffer.AsMemory(retained, requested), token).ConfigureAwait(false);
            Fence();
            if (read == 0)
            {
                if (retained > 0) encoded.Append(Convert.ToBase64String(buffer, 0, retained));
                break;
            }
            total = checked(total + read);
            if (total > maximumRawBytes) throw TooLarge();
            int available = retained + read;
            int complete = available - available % 3;
            if (complete > 0) encoded.Append(Convert.ToBase64String(buffer, 0, complete));
            retained = available - complete;
            // Preserve only the final incomplete base64 group between borrowed reads.
            if (retained > 0) Buffer.BlockCopy(buffer, complete, buffer, 0, retained);
        }
        Fence();
        return encoded.ToString();

        void Fence() { token.ThrowIfCancellationRequested(); fence(); token.ThrowIfCancellationRequested(); }
        ArgumentException TooLarge() => new("Supplied audio exceeds the remaining encoded request budget.", nameof(maximumEncodedBytes));
    }

    internal static JsonObject Storage(XaiFileStorageOptions value)
    {
        var result = new JsonObject { ["filename"] = value.FileName };
        if (value.ExpiresAfter is { } expiry) result["expires_after"] = expiry.Ticks / TimeSpan.TicksPerSecond;
        if (value.PublicUrl is XaiStoragePublicUrlFlag flag) result["public_url"] = flag.Enabled;
        else if (value.PublicUrl is XaiStoragePublicUrlConfiguration configuration)
        {
            var options = new JsonObject();
            if (configuration.Options.ExpiresAfter is { } publicExpiry)
                options["expires_after"] = publicExpiry.Ticks / TimeSpan.TicksPerSecond;
            result["public_url"] = options;
        }
        else if (value.PublicUrl is not null) throw new NotSupportedException("Unknown media storage control.");
        return result;
    }

    internal static string RequiredString(JsonElement value, string name, Func<string, bool> protectedValue)
    {
        string? result = OptionalString(value, name, protectedValue);
        if (string.IsNullOrWhiteSpace(result)) throw Malformed();
        return result;
    }

    internal static string? OptionalString(JsonElement value, string name, Func<string, bool> protectedValue)
    {
        if (!value.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw Malformed();
        string result = property.GetString()!;
        if (protectedValue(result)) throw new JsonException("Grok media response metadata cannot be published safely.");
        return result;
    }

    internal static JsonElement? Usage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind == JsonValueKind.Null) return null;
        if (usage.ValueKind != JsonValueKind.Object) throw Malformed();
        var result = new JsonObject();
        if (usage.TryGetProperty("cost_in_usd_ticks", out JsonElement cost))
        {
            if (!cost.TryGetInt64(out long ticks)) throw Malformed();
            result["cost_in_usd_ticks"] = ticks;
        }
        CopyCounts(usage, result, ["input_tokens", "output_tokens", "total_tokens"]);
        CopyDetails(usage, result, "input_tokens_details", ["text_tokens", "image_tokens", "cached_tokens"]);
        CopyDetails(usage, result, "output_tokens_details", ["text_tokens", "image_tokens", "reasoning_tokens"]);
        return Element(result);
    }

    private static void CopyDetails(JsonElement source, JsonObject result, string name, string[] fields)
    {
        if (!source.TryGetProperty(name, out JsonElement details) || details.ValueKind == JsonValueKind.Null) return;
        if (details.ValueKind != JsonValueKind.Object) throw Malformed();
        var copied = new JsonObject();
        CopyCounts(details, copied, fields);
        result[name] = copied;
    }

    private static void CopyCounts(JsonElement source, JsonObject result, string[] fields)
    {
        foreach (string name in fields)
            if (source.TryGetProperty(name, out JsonElement count) && count.ValueKind == JsonValueKind.Number
                && count.TryGetInt32(out int observed) && observed >= 0) result[name] = observed;
    }

    internal static JsonObject? FileOutput(JsonElement source, Func<string, bool> protectedValue)
    {
        if (!source.TryGetProperty("file_output", out JsonElement file) || file.ValueKind == JsonValueKind.Null) return null;
        if (file.ValueKind != JsonValueKind.Object) throw Malformed();
        var result = new JsonObject
        {
            ["file_id"] = RequiredString(file, "file_id", protectedValue),
            ["filename"] = RequiredString(file, "filename", protectedValue)
        };
        CopyTimestamp(file, result, "expires_at");
        CopyTimestamp(file, result, "public_url_expires_at");
        if (OptionalString(file, "public_url", protectedValue) is { } url)
        {
            RequireHttpUrl(url);
            result["public_url"] = url;
        }
        // Error bodies are diagnostics, not public content or authority. Preserve their observed presence only.
        if (OptionalString(file, "public_url_error", protectedValue) is not null) result["public_url_failed"] = true;
        return result;
    }

    private static void CopyTimestamp(JsonElement source, JsonObject result, string name)
    {
        if (!source.TryGetProperty(name, out JsonElement timestamp) || timestamp.ValueKind == JsonValueKind.Null) return;
        if (!timestamp.TryGetInt64(out long seconds)) throw Malformed();
        try { _ = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { throw Malformed(); }
        result[name] = seconds;
    }

    internal static void RequireHttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)) throw Malformed();
    }

    internal static JsonElement Element(JsonNode value)
    {
        using JsonDocument document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    internal static JsonException Malformed() => new("Grok media returned an invalid response shape.");
}
