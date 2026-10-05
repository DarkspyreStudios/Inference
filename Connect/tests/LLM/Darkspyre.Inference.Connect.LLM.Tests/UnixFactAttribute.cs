namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>A fact that runs only where POSIX shells and signals exist.</summary>
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "Requires macOS or Linux."; }
}
