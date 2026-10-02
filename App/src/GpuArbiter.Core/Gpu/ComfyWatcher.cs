using GpuArbiter.Backends;

namespace GpuArbiter.Gpu;

public sealed class ComfyWatcher(IComfyClient client, GpuCoordinator coordinator, CoordinatorOptions options,
    TimeProvider clock, ILogger<ComfyWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { coordinator.ObserveComfy(await client.QueueAsync(stoppingToken).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("ComfyUI queue response could not be read; retaining last known state"); }
            await Task.Delay(TimeSpan.FromSeconds(options.Comfy.PollSeconds), clock, stoppingToken).ConfigureAwait(false);
        }
    }
}
