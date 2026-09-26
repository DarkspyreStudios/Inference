namespace Darkspyre.Inference.Tensor.Common.Tests;

/// <summary>A fact that relies on Unix directory permissions; it is reported as skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Requires Unix directory permissions.";
        }
    }
}
