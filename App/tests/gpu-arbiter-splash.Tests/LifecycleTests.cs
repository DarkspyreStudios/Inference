using GpuArbiter.Gpu;
using Xunit;

namespace GpuArbiter.Splash.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task FailedStartupRecordsExitCodeAndConfirmsChildCleanup()
    {
        await using var rig = new ApiRig();
        var process = new FakeProcess(); process.Crash(19); rig.Processes.Next = process;
        var error = await Assert.ThrowsAsync<GpuException>(() => rig.Models.LoadAsync("incoai/Qwen3.8-27B-Splash", CancellationToken.None));
        Assert.Contains("19", error.Message);
        var state = (await rig.Models.ModelsAsync(CancellationToken.None))[0];
        Assert.True(state.Failed);
        Assert.Equal(19, state.ExitCode);
        Assert.True(rig.Models.GroupsReleased);
    }

    [Fact]
    public async Task ReadinessTimeoutStopsGroupBeforeReturningFailedLoad()
    {
        await using var rig = new ApiRig();
        rig.Options.ModelLoadTimeoutSeconds = 1;
        rig.Control.Observation = rig.Control.Observation with { Ready = false };
        await Assert.ThrowsAsync<GpuException>(() => rig.Models.LoadAsync("incoai/Qwen3.8-27B-Splash", CancellationToken.None));
        Assert.True(rig.Models.GroupsReleased);
        Assert.True((await rig.Models.ModelsAsync(CancellationToken.None))[0].Failed);
    }

    [Fact]
    public async Task IntendedUnloadKeepsControllerAvailableAndAllowsReload()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        using (var first = await rig.Coordinator.AcquireAsync(GpuTarget.Llm("incoai/Qwen3.8-27B-Splash"), CancellationToken.None)) { }
        await rig.Coordinator.UnloadAsync("incoai/Qwen3.8-27B-Splash", CancellationToken.None);
        using var second = await rig.Coordinator.AcquireAsync(GpuTarget.Llm("incoai/Qwen3.8-27B-Splash"), CancellationToken.None);
        Assert.Equal(2, rig.Processes.Started.Count);
        Assert.False(second.HostStopped.IsCancellationRequested);
    }

    [Fact]
    public async Task CrashCancelsLeasesCleansChildrenAndAllowsSubsequentLoad()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        using var first = await rig.Coordinator.AcquireAsync(GpuTarget.Llm("incoai/Qwen3.8-27B-Splash"), CancellationToken.None);
        rig.Processes.Started[0].Crash();
        await ApiRig.WaitFor(() => first.HostStopped.IsCancellationRequested);
        await ApiRig.WaitFor(() => !rig.Processes.Started[0].GroupAlive);
        using var second = await rig.Coordinator.AcquireAsync(GpuTarget.Llm("incoai/Qwen3.8-27B-Splash"), CancellationToken.None);
        Assert.Equal(2, rig.Processes.Started.Count);
        Assert.False(second.HostStopped.IsCancellationRequested);
    }

    [Fact]
    public async Task WrongPidCannotSatisfyReadinessAndCancelledLoadCleansGroup()
    {
        await using var rig = new ApiRig();
        rig.Control.Observation = rig.Control.Observation with { ServerPid = 999 };
        using var cancellation = new CancellationTokenSource();
        var load = rig.Models.LoadAsync("incoai/Qwen3.8-27B-Splash", cancellation.Token);
        await ApiRig.WaitFor(() => rig.Control.Reads > 0);
        Assert.False(load.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.False(rig.Processes.Started[0].GroupAlive);
        Assert.True((await rig.Models.ModelsAsync(CancellationToken.None))[0].Failed);
    }

    [Fact]
    public async Task SurvivingNativeGroupBlocksUnloadedStateAndRelease()
    {
        await using var rig = new ApiRig();
        var process = new FakeProcess { SurviveStop = true }; rig.Processes.Next = process;
        await rig.Models.LoadAsync("incoai/Qwen3.8-27B-Splash", CancellationToken.None);
        await Assert.ThrowsAsync<GpuException>(() => rig.Models.UnloadAsync("incoai/Qwen3.8-27B-Splash", CancellationToken.None));
        Assert.False(rig.Models.GroupsReleased);
        Assert.Equal("unloading", (await rig.Models.ModelsAsync(CancellationToken.None))[0].Status);
        process.SurviveStop = false;
    }

    [Fact]
    public async Task ShutdownConfirmsAllOwnedGroupsExit()
    {
        await using var rig = new ApiRig();
        await rig.Models.LoadAsync("incoai/Qwen3.8-27B-Splash", CancellationToken.None);
        await rig.Models.StopAsync(CancellationToken.None);
        Assert.True(rig.Models.GroupsReleased);
        Assert.False(rig.Processes.Started[0].GroupAlive);
    }

    [MacFact]
    public async Task MacGroupStopsServerAndTermIgnoringDescendant()
    {
        // No weights or external service: verify real process containment with the installed system Python.
        await using var process = MacProcessGroup.Start("/usr/bin/python3", ["-c",
            "import subprocess,sys,signal,time; signal.signal(signal.SIGTERM,signal.SIG_IGN); subprocess.Popen([sys.executable,'-c','import signal,time; signal.signal(signal.SIGTERM,signal.SIG_IGN); time.sleep(30)']); time.sleep(30)"],
            new Dictionary<string, string> { ["PYTHONDONTWRITEBYTECODE"] = "1" });
        await Task.Delay(150);
        Assert.True(process.GroupAlive);
        await process.StopAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.False(process.GroupAlive);
        Assert.True(process.Exit.IsCompleted);
    }
}

public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute() { if (!OperatingSystem.IsMacOS()) Skip = "Requires Mac process-group APIs; no process containment check runs on other platforms."; }
}
