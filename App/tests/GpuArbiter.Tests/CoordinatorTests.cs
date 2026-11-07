using GpuArbiter.Backends;
using GpuArbiter.Gpu;
using Xunit;

namespace GpuArbiter.Tests;

public sealed class CoordinatorTests
{
    [Fact]
    public async Task RequestsShareHolderAndDrainBeforeComfyEviction()
    {
        using var rig = new Rig();
        using var first = await rig.Admit("qwen");
        using var second = await rig.Gpu.AcquireAsync(GpuTarget.Llm("qwen"), CancellationToken.None);
        var comfy = rig.Gpu.AcquireAsync(GpuTarget.Comfy, CancellationToken.None);
        await rig.Gpu.StepAsync(CancellationToken.None);
        Assert.False(comfy.IsCompleted);
        Assert.DoesNotContain("unload:qwen", rig.Llama.Calls);
        first.Dispose();
        await rig.Gpu.StepAsync(CancellationToken.None);
        Assert.False(comfy.IsCompleted);
        second.Dispose();
        await rig.Gpu.StepAsync(CancellationToken.None);
        using var admitted = await comfy;
        Assert.Equal("comfy", rig.Gpu.Snapshot().Holder);
        Assert.Contains("unload:qwen", rig.Llama.Calls);
    }
    [Fact]
    public async Task WatcherEvictsIdleLlmForDirectComfyWork()
    {
        using var rig = new Rig();
        using var lease = await rig.Admit("qwen");
        rig.Vram.Used = 20000;
        rig.Gpu.ObserveComfy(new(true, 1, 0));
        await rig.Gpu.StepAsync(CancellationToken.None);
        Assert.Equal("llm:qwen", rig.Gpu.Snapshot().Holder);
        lease.Dispose();
        await rig.Gpu.StepAsync(CancellationToken.None);
        Assert.Equal("comfy", rig.Gpu.Snapshot().Holder);
        Assert.Empty(rig.Gpu.Snapshot().Waiters);
        Assert.Contains("unload:qwen", rig.Llama.Calls);
    }
    [Fact]
    public async Task ExplicitUnloadDrainsRequestsAndBlocksNewAdmissions()
    {
        using var rig = new Rig();
        using var lease = await rig.Admit("qwen");
        var unload = rig.Gpu.UnloadAsync("qwen", CancellationToken.None);
        var later = rig.Gpu.AcquireAsync(GpuTarget.Llm("qwen"), CancellationToken.None);
        await rig.Gpu.StepAsync(CancellationToken.None);
        Assert.False(unload.IsCompleted);
        lease.Dispose();
        await rig.Gpu.StepAsync(CancellationToken.None);
        await unload;
        Assert.Equal("none", rig.Gpu.Snapshot().Holder);
        Assert.False(later.IsCompleted);
        await rig.Gpu.StepAsync(CancellationToken.None);
        using var admitted = await later;
    }
}
