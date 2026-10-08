namespace GpuArbiter.Gpu;

public sealed record GpuTarget(string? Model)
{
    public static GpuTarget Comfy { get; } = new((string?)null);
    public static GpuTarget Llm(string model) => new(model);
    public override string ToString() => Model is null ? "comfy" : $"llm:{Model}";
}
public sealed record WaiterView(string Target, string Kind, double WaitingSeconds);
public sealed record GpuView(string Holder, string Phase, string? HandoffStep, int LlmInFlight,
    Backends.ComfyState Comfy, IReadOnlyList<WaiterView> Waiters, int? VramUsedMiB);
public sealed class GpuException(string message, int statusCode = 503) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
public sealed class GpuLease(Action release, CancellationToken routerStopped) : IDisposable
{
    private Action? _release = release;
    public CancellationToken RouterStopped { get; } = routerStopped;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
