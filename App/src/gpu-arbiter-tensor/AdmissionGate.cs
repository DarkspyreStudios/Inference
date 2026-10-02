using GpuArbiter.Gpu;

namespace GpuArbiter.Tensor;

public sealed class AdmissionGate(GpuCoordinator coordinator, TensorOptions options, TimeProvider clock)
{
    public async Task<GpuLease> AcquireAsync(string model, HttpContext context, bool stream = false)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var admission = coordinator.AcquireAsync(GpuTarget.Llm(model), cancellation.Token);
        try
        {
            if (stream)
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(2), clock, cancellation.Token);
                if (await Task.WhenAny(admission, delay) != admission)
                {
                    await delay;
                    while (!admission.IsCompleted)
                    {
                        OpenAiOutput.BeginStream(context);
                        await context.Response.WriteAsync($": waiting for GPU: {coordinator.WaitingReason()}\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        delay = Task.Delay(TimeSpan.FromSeconds(options.KeepaliveSeconds), clock, cancellation.Token);
                        if (await Task.WhenAny(admission, delay) == admission) break;
                        await delay;
                    }
                }
            }
            return await admission;
        }
        catch
        {
            await cancellation.CancelAsync();
            try { (await admission).Dispose(); }
            catch (OperationCanceledException) { }
            catch (GpuException) { }
            throw;
        }
        finally { await cancellation.CancelAsync(); }
    }
}
