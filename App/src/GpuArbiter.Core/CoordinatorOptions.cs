namespace GpuArbiter;

public class CoordinatorOptions
{
    public virtual int ModelLoadTimeoutSeconds { get; set; } = 300;
    public ComfyOptions Comfy { get; set; } = new();
    public double ReleaseTimeoutSeconds { get; set; } = 60;
    public VramOptions Vram { get; set; } = new();
}

public sealed class ComfyOptions
{
    public string Url { get; set; } = "http://127.0.0.1:8188";
    public double PollSeconds { get; set; } = 1;
}

public sealed class VramOptions
{
    public int ReleasedMaxUsedMiB { get; set; } = 2500;
}
