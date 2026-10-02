namespace GpuArbiter.Backends;

public sealed record ModelState(string Id, string Status, bool Failed, int? ExitCode);
public sealed record ComfyState(bool Reachable, int Running, int Pending)
{
    public bool Busy => Running + Pending > 0;
}
public interface IModelHost
{
    Task<IReadOnlyList<ModelState>> ModelsAsync(CancellationToken cancellationToken);
    Task LoadAsync(string model, CancellationToken cancellationToken);
    Task UnloadAsync(string model, CancellationToken cancellationToken);
}
public interface IComfyClient
{
    Task<ComfyState> QueueAsync(CancellationToken cancellationToken);
    Task FreeAsync(CancellationToken cancellationToken);
}
public interface IVramProbe
{
    Task<int> ReadAsync(CancellationToken cancellationToken);
}
