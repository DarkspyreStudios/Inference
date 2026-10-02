using System.Text.Json;
using System.Text.RegularExpressions;
using GpuArbiter.Gpu;
using GpuArbiter.Proxy;

namespace GpuArbiter.Splash;

public sealed partial class SplashApi(SplashOptions options, SplashModelHost host,
    GpuCoordinator coordinator, InferenceForwarder forwarder, TimeProvider clock)
{
    private static readonly HashSet<string> InferenceRoutes = new(StringComparer.Ordinal)
    {
        "/v1/chat/completions", "/v1/responses", "/v1/messages", "/v1/messages/count_tokens",
        "/tokenize", "/apply-template", "/v1/judgments", "/v1/systemone"
    };

    public async Task HandleAsync(HttpContext context)
    {
        var protocol = Protocol(context.Request.Path.Value!);
        GpuLease? lease = null;
        Task<GpuLease>? admission = null;
        var sent = false;
        using var admissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            var path = context.Request.Path.Value!;
            if (HttpMethods.IsGet(context.Request.Method) && path == "/gpu")
            {
                var view = await coordinator.SnapshotAsync(context.RequestAborted).ConfigureAwait(false);
                var native = await host.ObservationAsync(context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsJsonAsync(new
                {
                    view.Holder, view.Phase, view.HandoffStep, view.LlmInFlight, view.Comfy, view.Waiters,
                    view.VramUsedMiB, view.ReleaseBasis, view.ReleaseObservationAvailable, splash = native
                }, context.RequestAborted).ConfigureAwait(false);
                return;
            }
            if (HttpMethods.IsGet(context.Request.Method) && (path == "/v1/models" || path.StartsWith("/v1/models/", StringComparison.Ordinal)))
            {
                object Describe(SplashModelOptions m) => new { id = m.Id, @object = "model", created = 0, owned_by = "splash", context_length = options.MaximumContext };
                if (path == "/v1/models")
                    await context.Response.WriteAsJsonAsync(new { @object = "list", data = options.Models.Select(Describe) }, context.RequestAborted).ConfigureAwait(false);
                else
                {
                    var id = Uri.UnescapeDataString(path["/v1/models/".Length..]);
                    var model = options.Models.FirstOrDefault(m => m.Id == id) ?? throw new GpuException("Unknown model", 404);
                    await context.Response.WriteAsJsonAsync(Describe(model), context.RequestAborted).ConfigureAwait(false);
                }
                return;
            }
            if (HttpMethods.IsPost(context.Request.Method) && path is "/models/load" or "/models/unload")
            {
                var body = await ReadBodyAsync(context).ConfigureAwait(false);
                if (path == "/models/unload") await coordinator.UnloadAsync(body.Model!, context.RequestAborted).ConfigureAwait(false);
                else lease = await coordinator.AcquireAsync(GpuTarget.Llm(body.Model!), context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsJsonAsync(new { success = true }, context.RequestAborted).ConfigureAwait(false);
                return;
            }
            byte[]? bytes = null;
            if (HttpMethods.IsPost(context.Request.Method) && InferenceRoutes.Contains(path))
            {
                var body = await ReadBodyAsync(context).ConfigureAwait(false);
                if (body.Stream && path is not ("/v1/chat/completions" or "/v1/responses" or "/v1/messages"))
                    throw new GpuException("This Splash operation does not support streaming", 400);
                bytes = body.Bytes;
                admission = coordinator.AcquireAsync(GpuTarget.Llm(body.Model!), admissionCancellation.Token);
                if (body.Stream) await AdmissionWait.CommentsAsync(context, admission, coordinator, clock, options.KeepaliveSeconds).ConfigureAwait(false);
                lease = await admission.ConfigureAwait(false);
            }
            else if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)) && StoredResponsePath().IsMatch(path))
                lease = coordinator.AcquireResident();
            else throw new GpuException("This operation is not supported by Splash", 404);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.HostStopped);
            sent = true;
            await forwarder.ForwardAsync(context, options.Url, bytes, linked.Token, "Splash", protocol, stripCredentials: true).ConfigureAwait(false);
        }
        catch (JsonException) { await InferenceForwarder.ErrorAsync(context, 400, "Invalid JSON request, model or stream flag", protocol).ConfigureAwait(false); }
        catch (GpuException error) { await InferenceForwarder.ErrorAsync(context, error.StatusCode, error.Message, protocol).ConfigureAwait(false); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (OperationCanceledException) { await InferenceForwarder.ErrorAsync(context, 502, "Splash instance stopped", protocol).ConfigureAwait(false); }
        catch (HttpRequestException) { await InferenceForwarder.ErrorAsync(context, 502, "Splash server is unavailable", protocol).ConfigureAwait(false); }
        finally
        {
            try
            {
                if (lease is not null && sent) await host.ConfirmCompletionAsync(lease.HostStopped).ConfigureAwait(false);
            }
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
    }

    private async Task<RequestBody> ReadBodyAsync(HttpContext context)
    {
        var body = await RequestBody.ReadAsync(context.Request, context.RequestAborted, options.MaximumRequestBytes).ConfigureAwait(false);
        if (body.Model is null) throw new JsonException("Model is required.");
        if (!options.Models.Any(m => m.Id == body.Model)) throw new GpuException("Unknown model", 404);
        return body;
    }

    private static WireProtocol Protocol(string path) => path.StartsWith("/v1/messages", StringComparison.Ordinal) ? WireProtocol.Anthropic :
        path.StartsWith("/v1/responses", StringComparison.Ordinal) ? WireProtocol.Responses :
        path == "/v1/systemone" ? WireProtocol.SystemOne : WireProtocol.OpenAi;

    [GeneratedRegex("^/v1/responses/resp_[A-Za-z0-9_]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex StoredResponsePath();
}
