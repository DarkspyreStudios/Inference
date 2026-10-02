using System.Net.Http.Headers;
using System.Text.Json;
using GpuArbiter.Gpu;

namespace GpuArbiter.Proxy;

public sealed class LlmProxy(GpuCoordinator coordinator, IHttpClientFactory clients, ArbiterOptions options, TimeProvider clock)
{
    public static IReadOnlySet<string> Routes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "/v1/chat/completions", "/v1/completions", "/v1/embeddings", "/v1/responses", "/v1/messages",
        "/completion", "/completions", "/embeddings", "/infill", "/tokenize", "/detokenize", "/apply-template"
    };
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host" };

    public async Task HandleAsync(HttpContext context)
    {
        using var admissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        GpuLease? lease = null;
        Task<GpuLease>? admission = null;
        try
        {
            var body = await RequestBody.ReadAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
            if (body.Model is not null)
            {
                admission = coordinator.AcquireAsync(GpuTarget.Llm(body.Model), admissionCancellation.Token);
                if (body.Stream) await KeepAliveAsync(context, admission).ConfigureAwait(false);
                lease = await admission.ConfigureAwait(false);
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease?.HostStopped ?? CancellationToken.None);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                options.Llama.Url + context.Request.Path + context.Request.QueryString)
            { Content = new ByteArrayContent(body.Bytes) };
            var connectionHeaders = context.Request.Headers.Connection.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var header in context.Request.Headers)
            {
                if (HopHeaders.Contains(header.Key) || connectionHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            request.Content.Headers.ContentType ??= new MediaTypeHeaderValue("application/json");
            using var client = clients.CreateClient("llm");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (context.Response.HasStarted && !response.IsSuccessStatusCode)
            {
                await ErrorAsync(context, (int)response.StatusCode, "llama-server rejected the request").ConfigureAwait(false);
                return;
            }
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                var responseConnection = response.Headers.TryGetValues("Connection", out var connection) ?
                    string.Join(',', connection).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                    if (!HopHeaders.Contains(header.Key) && !responseConnection.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                        context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (JsonException) { await ErrorAsync(context, 400, "Invalid JSON request or model identifier").ConfigureAwait(false); }
        catch (GpuException error) { await ErrorAsync(context, error.StatusCode, error.Message).ConfigureAwait(false); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (OperationCanceledException) { await ErrorAsync(context, 502, "llama-server restarted").ConfigureAwait(false); }
        catch (HttpRequestException) { await ErrorAsync(context, 502, "llama-server is unavailable").ConfigureAwait(false); }
        finally
        {
            lease?.Dispose();
            if (lease is null && admission is not null)
            {
                await admissionCancellation.CancelAsync().ConfigureAwait(false);
                try { (await admission.ConfigureAwait(false)).Dispose(); }
                catch (OperationCanceledException) { }
                catch (GpuException) { }
            }
        }
    }

    private async Task KeepAliveAsync(HttpContext context, Task<GpuLease> admission)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            var delay = Task.Delay(TimeSpan.FromSeconds(2), clock, delayCancellation.Token);
            if (await Task.WhenAny(admission, delay).ConfigureAwait(false) == admission) return;
            await delay.ConfigureAwait(false);
            if (admission.IsCompleted) return;
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            while (!admission.IsCompleted)
            {
                await context.Response.WriteAsync($": waiting for GPU: {coordinator.WaitingReason()}\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                delay = Task.Delay(TimeSpan.FromSeconds(options.KeepaliveSeconds), clock, delayCancellation.Token);
                if (await Task.WhenAny(admission, delay).ConfigureAwait(false) == admission) break;
                await delay.ConfigureAwait(false);
            }
        }
        finally { await delayCancellation.CancelAsync().ConfigureAwait(false); }
    }

    public static async Task ErrorAsync(HttpContext context, int status, string message)
    {
        if (context.RequestAborted.IsCancellationRequested) return;
        var error = new { error = new { message, type = "gpu_arbiter_error", code = status } };
        if (context.Response.HasStarted)
        {
            if (context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            {
                await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(error)}\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
            else context.Abort();
        }
        else
        {
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(error, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
