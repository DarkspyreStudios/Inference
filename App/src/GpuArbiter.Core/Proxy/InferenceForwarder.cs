using System.Net.Http.Headers;
using System.Text.Json;
using GpuArbiter.Gpu;

namespace GpuArbiter.Proxy;

public enum WireProtocol { OpenAi, Responses, Anthropic, SystemOne }

public sealed class InferenceForwarder(IHttpClientFactory clients)
{
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host" };

    public async Task ForwardAsync(HttpContext context, string upstream, byte[]? body, CancellationToken token,
        string backend, WireProtocol protocol = WireProtocol.OpenAi, bool stripCredentials = false)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
            upstream + context.Request.Path + context.Request.QueryString);
        if (body is not null) request.Content = new ByteArrayContent(body);
        var connectionHeaders = ConnectionHeaders(context.Request.Headers.Connection.ToString());
        foreach (var header in context.Request.Headers)
        {
            if (HopHeaders.Contains(header.Key) || connectionHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase) ||
                (stripCredentials && (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || header.Key.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Contains("token", StringComparison.OrdinalIgnoreCase) || header.Key.Contains("secret", StringComparison.OrdinalIgnoreCase)))) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && request.Content is not null)
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        if (request.Content is not null) request.Content.Headers.ContentType ??= new MediaTypeHeaderValue("application/json");
        using var client = clients.CreateClient("llm");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (context.Response.HasStarted && !response.IsSuccessStatusCode)
        {
            await ErrorAsync(context, (int)response.StatusCode, $"{backend} rejected the request", protocol).ConfigureAwait(false);
            return;
        }
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            var responseConnection = response.Headers.TryGetValues("Connection", out var connection) ? ConnectionHeaders(string.Join(',', connection)) : [];
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!HopHeaders.Contains(header.Key) && !responseConnection.Contains(header.Key, StringComparer.OrdinalIgnoreCase) &&
                    !(stripCredentials && header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
        }
    }

    private static string[] ConnectionHeaders(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static async Task ErrorAsync(HttpContext context, int status, string message, WireProtocol protocol = WireProtocol.OpenAi)
    {
        if (context.RequestAborted.IsCancellationRequested) return;
        object error = protocol == WireProtocol.Anthropic
            ? new { type = "error", error = new { type = "api_error", message } }
            : new { error = new { message, type = "gpu_arbiter_error", code = status } };
        if (context.Response.HasStarted)
        {
            if (context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            {
                var prefix = protocol is WireProtocol.Anthropic or WireProtocol.Responses ? "event: error\n" : "";
                if (protocol == WireProtocol.Responses) error = new { type = "error", code = "gpu_arbiter_error", message };
                await context.Response.WriteAsync($"{prefix}data: {JsonSerializer.Serialize(error)}\n\n", context.RequestAborted).ConfigureAwait(false);
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
