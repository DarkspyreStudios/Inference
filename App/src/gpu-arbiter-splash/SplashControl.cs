using System.Text.Json;

namespace GpuArbiter.Splash;

public sealed record SplashObservation(bool Available, bool Stale, bool Ready, bool MetalHealthy,
    string? MemoryPressure, long? CurrentBytes, long? PeakBytes, bool Idle, int? ServerPid)
{
    public static SplashObservation Unavailable { get; } = new(false, false, false, false, null, null, null, false, null);
}

public interface ISplashControl
{
    Task<bool> ReadyAsync(CancellationToken token);
    Task<SplashObservation> ReadAsync(CancellationToken token);
}

public sealed class SplashControl(IHttpClientFactory clients, SplashOptions options) : ISplashControl
{
    public async Task<bool> ReadyAsync(CancellationToken token)
    {
        try
        {
            using var client = clients.CreateClient("splash-control");
            using var response = await client.GetAsync(options.Url + "/ready", token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    public async Task<SplashObservation> ReadAsync(CancellationToken token)
    {
        try
        {
            using var client = clients.CreateClient("splash-control");
            using var response = await client.GetAsync(options.Url + "/status", token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return SplashObservation.Unavailable;
            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            return Parse(document.RootElement);
        }
        catch (HttpRequestException) { return SplashObservation.Unavailable; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return SplashObservation.Unavailable; }
        catch (JsonException) { return SplashObservation.Unavailable; }
    }

    internal static SplashObservation Parse(JsonElement root)
    {
        if (Number(root, "schema_version") != 5) return SplashObservation.Unavailable;
        var stale = Boolean(root, "transport", "status_stale") != false;
        var healthy = Boolean(root, "metal", "healthy") == true;
        var ready = Boolean(root, "ready") == true && Boolean(root, "transport", "ready") == true &&
            Boolean(root, "transport", "recovering") == false;
        var idle = ready && healthy && !stale;
        foreach (var path in new[]
        {
            new[] { "admission", "waiting" }, ["admission", "waiting_memory"], ["admission", "waiting_concurrency"],
            ["admission", "suspended"], ["scheduler", "queued"], ["scheduler", "waiting_resources"],
            ["scheduler", "waiting_prefix"], ["scheduler", "prefilling"], ["scheduler", "decoding"],
            ["scheduler", "waiting_mask"], ["scheduler", "terminal"], ["transport", "pending"], ["http", "requests", "active"]
        }) idle &= Number(root, path) == 0;
        var pressure = Element(root, "memory_pressure");
        return new(true, stale, ready, healthy, pressure.ValueKind == JsonValueKind.String ? pressure.GetString() : null,
            Number(root, "memory_actual", "current_bytes"), Number(root, "memory_actual", "peak_bytes"), idle,
            Number(root, "instance", "pid") is { } pid && pid is > 0 and <= int.MaxValue ? (int)pid : null);
    }

    private static JsonElement Element(JsonElement root, params string[] path)
    {
        foreach (var name in path)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var next)) return default;
            root = next;
        }
        return root;
    }
    private static long? Number(JsonElement root, params string[] path)
    {
        var value = Element(root, path);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    }
    private static bool? Boolean(JsonElement root, params string[] path) => Element(root, path).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
}
