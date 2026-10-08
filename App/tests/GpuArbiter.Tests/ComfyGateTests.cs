using GpuArbiter.Gpu;
using GpuArbiter.Proxy;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace GpuArbiter.Tests;

public sealed class ComfyGateTests
{
    [Fact]
    public async Task PromptWaitsForLlmDrainAndQueueSampleClosesAdmissionGap()
    {
        using var rig = new Rig();
        using var llm = await rig.Admit("qwen");
        var gate = new ComfyGate(rig.Gpu, rig.Comfy);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = gate.ForwardAsync(new DefaultHttpContext(), async () =>
        {
            received.SetResult();
            await finish.Task;
            rig.Comfy.State = new(true, 0, 1);
        });
        await rig.Gpu.StepAsync(CancellationToken.None);
        Assert.False(received.Task.IsCompleted);
        llm.Dispose();
        await rig.Gpu.StepAsync(CancellationToken.None);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waitingLlm = rig.Gpu.AcquireAsync(GpuTarget.Llm("qwen"), CancellationToken.None);
        await rig.Gpu.StepAsync(CancellationToken.None);
        Assert.False(waitingLlm.IsCompleted);
        finish.SetResult();
        await prompt.WaitAsync(TimeSpan.FromSeconds(5));
        // The prompt lease has ended, but the post-forward queue sample still prevents acquisition.
        await rig.Gpu.StepAsync(CancellationToken.None);
        Assert.False(waitingLlm.IsCompleted);
        Assert.Equal(1, rig.Gpu.Snapshot().Comfy.Pending);
        rig.Comfy.State = new(true, 0, 0);
        rig.Gpu.ObserveComfy(rig.Comfy.State);
        await rig.Gpu.StepAsync(CancellationToken.None);
        using var admitted = await waitingLlm.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
