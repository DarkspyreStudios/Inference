using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Effect-free Google cache data conversion. Callers supply independently validated routing and scope.</summary>
internal static partial class GoogleCacheData
{
    internal static LlmContextCacheCreate Snapshot(LlmContextCacheCreate request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        ArgumentNullException.ThrowIfNull(request.Correlation);
        if ((request.TimeToLive is null) == (request.ExpiresAt is null))
            throw new ArgumentException("Cache creation requires exactly one explicit TTL or expiry.");
        if (request.TimeToLive is { } ttl && ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "Cache TTL must be positive.");
        if (request.ExpiresAt is { } expiry && expiry <= now)
            throw new ArgumentOutOfRangeException(nameof(request), "Cache expiry must be in the future.");
        if (request.DisplayName is { } display && display != request.Correlation.ReconciliationDisplayName)
            throw new ArgumentException("Cache display name conflicts with the reconciliation display name.");
        var prefix = ClientTools.Snapshot(new LlmChatRequest
        {
            ModelId = request.ModelId,
            Instructions = request.Instructions,
            Messages = request.Messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Resources = request.Resources
        });
        ClientTools.Validate(prefix, supportsShellTools: false);
        return request with { Messages = prefix.Messages, Tools = prefix.Tools };
    }

    internal static void AddExpiration(JsonObject payload, LlmContextCacheCreate request)
    {
        if (request.TimeToLive is { } ttl) payload["ttl"] = Duration(ttl);
        else payload["expireTime"] = Timestamp(request.ExpiresAt!.Value);
    }

    internal static string Duration(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
        return ((decimal)value.Ticks / TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture) + "s";
    }

    internal static string Timestamp(DateTimeOffset value) => value.ToUniversalTime()
        .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    internal static LlmContextCache Receipt(JsonElement root, string scopeId,
        Action<string> validateResource, Func<string, string> resolveModel, bool vertex)
    {
        RequireObject(root);
        string name = RequiredString(root, "name");
        validateResource(name);
        string model = resolveModel(RequiredString(root, "model"));
        DateTimeOffset expiry = ReadTimestamp(RequiredString(root, "expireTime"));
        LlmContextCacheUsage? usage = null;
        if (root.TryGetProperty("usageMetadata", out JsonElement observed) && observed.ValueKind != JsonValueKind.Null)
        {
            RequireObject(observed);
            usage = new(Count(observed, "totalTokenCount"),
                vertex ? Count(observed, "textCount") : null,
                vertex ? Count(observed, "imageCount") : null,
                vertex ? Seconds(observed, "audioDurationSeconds") : null,
                vertex ? Seconds(observed, "videoDurationSeconds") : null);
        }
        return new(new(name, model, scopeId, expiry), OptionalString(root, "displayName"),
            OptionalTimestamp(root, "createTime"), OptionalTimestamp(root, "updateTime"), expiry, usage);
    }

    internal static void ValidatePage(LlmContextCacheList request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.PageSize, 1000);
    }

    internal static LlmContextCachePage Page(JsonElement root, Func<JsonElement, LlmContextCache> receipt)
    {
        RequireObject(root);
        var items = new List<LlmContextCache>();
        // ProtoJSON omits empty repeated fields; null has the same unset semantics.
        if (root.TryGetProperty("cachedContents", out JsonElement entries) && entries.ValueKind != JsonValueKind.Null)
        {
            if (entries.ValueKind != JsonValueKind.Array) throw InvalidReceipt();
            foreach (JsonElement entry in entries.EnumerateArray()) items.Add(receipt(entry));
        }
        return new(items, OptionalString(root, "nextPageToken"));
    }

    internal static DateTimeOffset ReadTimestamp(string value)
    {
        Match match = TimestampSyntax().Match(value);
        if (!match.Success) throw InvalidReceipt();
        Group fraction = match.Groups[1];
        // Protobuf allows nanoseconds; DateTimeOffset cannot represent sub-tick observations exactly.
        if (fraction.Length > 7)
        {
            if (fraction.ValueSpan[7..].ContainsAnyExcept('0')) throw InvalidReceipt();
            value = value.Remove(fraction.Index + 7, fraction.Length - 7);
        }
        string format = value.Contains('.', StringComparison.Ordinal)
            ? "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK" : "yyyy-MM-dd'T'HH:mm:ssK";
        if (!DateTimeOffset.TryParseExact(value, format, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTimeOffset result)) throw InvalidReceipt();
        return result.ToUniversalTime();
    }

    private static DateTimeOffset? OptionalTimestamp(JsonElement root, string field) =>
        OptionalString(root, field) is { } value ? ReadTimestamp(value) : null;

    private static long? Count(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        string? text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
        if (text is null) throw InvalidReceipt();
        // ProtoJSON accepts quoted integers and exponent notation. Exact integer arithmetic
        // avoids decimal/double rounding turning a fractional observation into a valid count.
        using JsonDocument number = JsonDocument.Parse(text);
        if (number.RootElement.ValueKind != JsonValueKind.Number) throw InvalidReceipt();
        int exponentAt = text.IndexOfAny(['e', 'E']);
        string mantissa = exponentAt < 0 ? text : text[..exponentAt];
        BigInteger exponent = exponentAt < 0 ? BigInteger.Zero
            : BigInteger.Parse(text.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int point = mantissa.IndexOf('.');
        int fraction = point < 0 ? 0 : mantissa.Length - point - 1;
        BigInteger count = BigInteger.Parse(point < 0 ? mantissa : mantissa.Remove(point, 1), CultureInfo.InvariantCulture);
        if (count.Sign < 0) throw InvalidReceipt();
        if (count.IsZero) return 0;
        BigInteger scale = exponent - fraction;
        if (scale >= 0)
        {
            if (scale > 18) throw InvalidReceipt();
            count *= BigInteger.Pow(10, (int)scale);
        }
        else
        {
            if (-scale > mantissa.Length) throw InvalidReceipt();
            count = BigInteger.DivRem(count, BigInteger.Pow(10, (int)-scale), out BigInteger remainder);
            if (!remainder.IsZero) throw InvalidReceipt();
        }
        if (count > long.MaxValue) throw InvalidReceipt();
        return (long)count;
    }

    private static TimeSpan? Seconds(JsonElement root, string field)
    {
        long? value = Count(root, field);
        if (value is null) return null;
        if (value > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond) throw InvalidReceipt();
        return TimeSpan.FromTicks(value.Value * TimeSpan.TicksPerSecond);
    }

    private static string RequiredString(JsonElement root, string field) =>
        OptionalString(root, field) is { Length: > 0 } value ? value : throw InvalidReceipt();

    private static string? OptionalString(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw InvalidReceipt();
        return value.GetString();
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidReceipt();
    }

    private static JsonException InvalidReceipt() => new("Google returned invalid or unrepresentable cache facts.");

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.([0-9]{1,9}))?(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampSyntax();
}
