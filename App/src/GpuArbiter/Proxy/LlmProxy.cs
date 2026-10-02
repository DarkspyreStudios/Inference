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
                if (body.Stream) await AdmissionWait.CommentsAsync(context, admission, coordinator, clock, options.KeepaliveSeconds).ConfigureAwait(false);
                lease = await admission.ConfigureAwait(false);
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease?.HostStopped ?? CancellationToken.None);
            await new InferenceForwarder(clients).ForwardAsync(context, options.Llama.Url, body.Bytes, linked.Token, "llama-server").ConfigureAwait(false);
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

    public static Task ErrorAsync(HttpContext context, int status, string message) =>
        InferenceForwarder.ErrorAsync(context, status, message);
}
