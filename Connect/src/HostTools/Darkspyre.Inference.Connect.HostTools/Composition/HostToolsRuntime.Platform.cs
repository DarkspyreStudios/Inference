namespace Darkspyre.Inference.Connect.HostTools;

public static partial class HostToolsRuntime
{
    // One build serves every operating system, so a published package works wherever it is restored. The running
    // operating system picks its platform implementation; callers depend on the platform seam, not the chosen type.
    // Connect's in-library operating-system switching is a recorded exception in the repository's AGENTS.md.
    private static Darkspyre.LocalServices.IServicePlatform CreatePlatform() =>
        OperatingSystem.IsMacOS() ? new Darkspyre.LocalServices.Platform.MacOS.ServicePlatform()
        : OperatingSystem.IsLinux() ? new Darkspyre.LocalServices.Platform.Linux.ServicePlatform()
        : OperatingSystem.IsWindows() ? new Darkspyre.LocalServices.Platform.Windows.ServicePlatform()
        : throw new PlatformNotSupportedException("The host-tools service runs on macOS, Linux and Windows.");
}
