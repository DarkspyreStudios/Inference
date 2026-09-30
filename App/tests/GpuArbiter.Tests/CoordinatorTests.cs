using GpuArbiter.Backends;
using GpuArbiter.Gpu;
using Xunit;

namespace GpuArbiter.Tests;

public sealed class CoordinatorTests
{
    [Fact]
    public async Task AcquisitionFromNoneFreesCachedComfyAndLoadsModel()
    {
        using var rig = new Rig();
        using var lease = await rig.Admit("qwen");
        Assert.Equal(1, rig.Comfy.Frees);
        Assert.Equal(["load:qwen"], rig.Llama.Calls);
        Assert.Equal("llm:qwen", rig.Gpu.Snapshot().Holder);
        Assert.Equal(1, rig.Gpu.Snapshot().LlmInFlight);
    }
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
    public async Task AnotherTargetBlocksNewHolderRequestsAndFifoPreventsStarvation()
    {
        using var rig = new Rig();
        using var first = await rig.Admit("qwen");
        var comfy = rig.Gpu.AcquireAsync(GpuTarget.Comfy, CancellationToken.None);
        var laterQwen = rig.Gpu.AcquireAsync(GpuTarget.Llm("qwen"), CancellationToken.None);
        var laterComfy = rig.Gpu.AcquireAsync(GpuTarget.Comfy, CancellationToken.None);
        Assert.False(laterQwen.IsCompleted);
        first.Dispose();
        await rig.Gpu.StepAsync(CancellationToken.None);
        using var comfyLease = await comfy;
        Assert.False(laterQwen.IsCompleted);
        using var lastLease = await laterComfy.WaitAsync(TimeSpan.FromSeconds(5));
        lastLease.Dispose();
        comfyLease.Dispose();
        await rig.Gpu.StepAsync(CancellationToken.None);
        using var qwenLease = await laterQwen;
        qwenLease.Dispose();
    }
    [Fact]
    public async Task BusyComfyQueueAndPromptLeaseBothBlockLlmAcquisition()
    {
        using var rig = new Rig();
        using var comfy = await rig.Admit(null);
        var qwen = rig.Gpu.AcquireAsync(GpuTarget.Llm("qwen"), CancellationToken.None);
        await rig.Gpu.StepAsync(CancellationToken.None);
        Assert.False(qwen.IsCompleted);
        rig.Comfy.State = new(true, 1, 2);
        rig.Gpu.ObserveComfy(rig.Comfy.State);
        comfy.Dispose();
        await rig.Gpu.StepAsync(CancellationToken.None);
        Assert.False(qwen.IsCompleted);
        rig.Comfy.State = new(false, 0, 0);
        rig.Gpu.ObserveComfy(rig.Comfy.State);
        await rig.Gpu.StepAsync(CancellationToken.None);
        using var admitted = await qwen;
        Assert.Equal(0, rig.Comfy.Frees);
    }
    [Fact]
    public async Task CanceledHeadIsRemovedBeforeSwitchStarts()
    {
        using var rig = new Rig();
        using var lease = await rig.Admit("qwen");
        using var cancellation = new CancellationTokenSource();
        var pending = rig.Gpu.AcquireAsync(GpuTarget.Comfy, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(rig.Gpu.Snapshot().Waiters);
        lease.Dispose();
        await rig.Gpu.StepAsync(CancellationToken.None);
        Assert.Equal("llm:qwen", rig.Gpu.Snapshot().Holder);
        Assert.DoesNotContain("unload:qwen", rig.Llama.Calls);
    }
    [Fact]
    public async Task ReleaseTimeoutFailsTargetWaitersAndRetainsHolder()
    {
        using var rig = new Rig();
        using var lease = await rig.Admit("qwen");
        lease.Dispose();
        rig.Options.Vram.ReleaseTimeoutSeconds = 1;
        rig.Vram.Used = 20000;
        var pending = rig.Gpu.AcquireAsync(GpuTarget.Comfy, CancellationToken.None);
        var step = rig.Gpu.StepAsync(CancellationToken.None);
        for (int i = 0; i < 100 && !step.IsCompleted; i++) { rig.Clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }
        await step.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<GpuException>(() => pending);
        Assert.Contains("20000 MiB", error.Message);
        Assert.Equal("llm:qwen", rig.Gpu.Snapshot().Holder);
        rig.Vram.Used = 700;
        using var reloaded = await rig.Admit("qwen");
        Assert.Equal(2, rig.Llama.Calls.Count(call => call == "load:qwen"));
    }
    [Fact]
    public async Task LoadFailureReportsExitCodeAndClearsHolder()
    {
        using var rig = new Rig();
        rig.Llama.FailLoad = true;
        var pending = rig.Gpu.AcquireAsync(GpuTarget.Llm("qwen"), CancellationToken.None);
        await rig.Gpu.StepAsync(CancellationToken.None);
        var failure = await Assert.ThrowsAsync<GpuException>(() => pending);
        Assert.Contains("17", failure.Message);
        Assert.Equal("none", rig.Gpu.Snapshot().Holder);
    }
    [Fact]
    public async Task WatcherEvictsIdleLlmForDirectComfyWork()
    {
        using var rig = new Rig();
        using var lease = await rig.Admit("qwen");
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
    public async Task RouterExitCancelsLeasesAndWaitsForReadinessBeforeLoading()
    {
        using var rig = new Rig();
        using var lease = await rig.Admit("qwen");
        rig.Gpu.RouterExited(11);
        Assert.True(lease.RouterStopped.IsCancellationRequested);
        Assert.Equal("none", rig.Gpu.Snapshot().Holder);
        var pending = rig.Gpu.AcquireAsync(GpuTarget.Llm("other"), CancellationToken.None);
        await rig.Gpu.StepAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
        rig.Llama.Models["qwen"] = new("qwen", "unloaded", false, null);
        rig.Gpu.RouterReady();
        await rig.Gpu.StepAsync(CancellationToken.None);
        using var next = await pending;
        lease.Dispose();
        Assert.Equal(1, rig.Gpu.Snapshot().LlmInFlight);
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
