using GpuArbiter.Backends;
using GpuArbiter.Gpu;

namespace GpuArbiter.Proxy;

public sealed class ComfyGate(GpuCoordinator coordinator, IComfyClient comfy)
{
    public async Task ForwardAsync(HttpContext context, Func<Task> forward)
    {
        using var lease = await coordinator.AcquireAsync(GpuTarget.Comfy, context.RequestAborted).ConfigureAwait(false);
        try { await forward().ConfigureAwait(false); }
        finally
        {
            // Retain the prompt lease until a post-forward queue sample closes the watcher gap.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { coordinator.ObserveComfy(await comfy.QueueAsync(timeout.Token).ConfigureAwait(false)); }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
        }
    }
}
