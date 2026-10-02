using GpuArbiter.Gpu;

namespace GpuArbiter.Proxy;

public static class AdmissionWait
{
    public static async Task CommentsAsync(HttpContext context, Task<GpuLease> admission,
        GpuCoordinator coordinator, TimeProvider clock, int keepaliveSeconds)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            var delay = Task.Delay(TimeSpan.FromSeconds(2), clock, cancellation.Token);
            if (await Task.WhenAny(admission, delay).ConfigureAwait(false) == admission) return;
            await delay.ConfigureAwait(false);
            while (!admission.IsCompleted)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/event-stream";
                    context.Response.Headers.CacheControl = "no-cache";
                }
                await context.Response.WriteAsync($": waiting for GPU: {coordinator.WaitingReason()}\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                delay = Task.Delay(TimeSpan.FromSeconds(keepaliveSeconds), clock, cancellation.Token);
                if (await Task.WhenAny(admission, delay).ConfigureAwait(false) == admission) break;
                await delay.ConfigureAwait(false);
            }
        }
        finally { await cancellation.CancelAsync().ConfigureAwait(false); }
    }
}
