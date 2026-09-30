using System.Net.Http.Json;
using System.Text.Json;

namespace GpuArbiter.Backends;

public sealed class ComfyClient(HttpClient http) : IComfyClient
{
    public async Task<ComfyState> QueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("/queue", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new(true, json.RootElement.GetProperty("queue_running").GetArrayLength(),
                json.RootElement.GetProperty("queue_pending").GetArrayLength());
        }
        catch (HttpRequestException) { return new(false, 0, 0); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, 0, 0); }
    }
    public async Task FreeAsync(CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("/free", new { unload_models = true, free_memory = true }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
