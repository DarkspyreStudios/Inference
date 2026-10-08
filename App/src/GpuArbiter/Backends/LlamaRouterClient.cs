using System.Net.Http.Json;
using System.Text.Json;

namespace GpuArbiter.Backends;

public sealed class LlamaRouterClient(HttpClient http) : ILlamaRouterClient
{
    public async Task<IReadOnlyList<ModelState>> ModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("/models", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return json.RootElement.GetProperty("data").EnumerateArray().Select(model =>
        {
            var status = model.GetProperty("status");
            return new ModelState(model.GetProperty("id").GetString()!, status.GetProperty("value").GetString()!,
                status.TryGetProperty("failed", out var failed) && failed.GetBoolean(),
                status.TryGetProperty("exit_code", out var code) ? code.GetInt32() : null);
        }).ToArray();
    }
    public Task LoadAsync(string model, CancellationToken cancellationToken) => MutateAsync("/models/load", model, cancellationToken);
    public Task UnloadAsync(string model, CancellationToken cancellationToken) => MutateAsync("/models/unload", model, cancellationToken);
    private async Task MutateAsync(string path, string model, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path, new { model }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
            throw new InvalidOperationException("Router rejected model operation.");
    }
}
