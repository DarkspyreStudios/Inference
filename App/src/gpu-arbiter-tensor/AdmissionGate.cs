using GpuArbiter.Gpu;
using GpuArbiter.Proxy;

namespace GpuArbiter.Tensor;

public sealed class AdmissionGate(GpuCoordinator coordinator, TensorOptions options, TimeProvider clock)
{
    public async Task<GpuLease> AcquireAsync(string model, HttpContext context, bool stream = false)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var admission = coordinator.AcquireAsync(GpuTarget.Llm(model), cancellation.Token);
        try
        {
            if (stream) await AdmissionWait.CommentsAsync(context, admission, coordinator, clock, options.KeepaliveSeconds);
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
