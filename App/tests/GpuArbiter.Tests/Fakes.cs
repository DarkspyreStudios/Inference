using GpuArbiter.Backends;
using GpuArbiter.Gpu;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GpuArbiter.Tests;

internal sealed class Rig : IDisposable
{
    public TestClock Clock { get; } = new();
    public FakeLlama Llama { get; } = new();
    public FakeComfy Comfy { get; } = new();
    public FakeVram Vram { get; } = new();
    public ArbiterOptions Options { get; } = new();
    public GpuCoordinator Gpu { get; }
    public Rig(bool ready = true)
    {
        Gpu = new(Llama, Comfy, Vram, Options, Clock, NullLogger<GpuCoordinator>.Instance);
        if (ready) Gpu.RouterReady();
    }
    public async Task<GpuLease> Admit(string? model)
    {
        var pending = Gpu.AcquireAsync(new(model), CancellationToken.None);
        await Gpu.StepAsync(CancellationToken.None);
        return await pending;
    }
    public void Dispose() => Gpu.Dispose();
}
internal sealed class FakeLlama : ILlamaRouterClient
{
    public Dictionary<string, ModelState> Models { get; } = new()
    { ["qwen"] = new("qwen", "unloaded", false, null), ["other"] = new("other", "unloaded", false, null) };
    public List<string> Calls { get; } = [];
    public bool FailLoad { get; set; }
    public bool HangLoad { get; set; }
    public Task<IReadOnlyList<ModelState>> ModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ModelState>>(Models.Values.ToArray());
    public Task LoadAsync(string model, CancellationToken token)
    {
        Calls.Add("load:" + model);
        if (Models.ContainsKey(model)) Models[model] = new(model, FailLoad ? "unloaded" : HangLoad ? "loading" : "loaded", FailLoad, FailLoad ? 17 : null);
        return Task.CompletedTask;
    }
    public Task UnloadAsync(string model, CancellationToken token)
    {
        Calls.Add("unload:" + model);
        Models[model] = new(model, "unloaded", false, null);
        return Task.CompletedTask;
    }
}
internal sealed class FakeComfy : IComfyClient
{
    public ComfyState State { get; set; } = new(true, 0, 0);
    public int Frees { get; private set; }
    public Task<ComfyState> QueueAsync(CancellationToken token) => Task.FromResult(State);
    public Task FreeAsync(CancellationToken token) { Frees++; return Task.CompletedTask; }
}
internal sealed class FakeVram : IVramProbe
{
    public int Used { get; set; } = 700;
    public Task<int> ReadAsync(CancellationToken token) => Task.FromResult(Used);
}

internal sealed class TestClock : FakeTimeProvider
{
    private int _timers;
    public int TimersCreated => Volatile.Read(ref _timers);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        Interlocked.Increment(ref _timers);
        return timer;
    }
}
