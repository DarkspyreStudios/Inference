namespace Darkspyre.Inference.Connect.HostTools;

internal static partial class HostToolsBackend
{
    // The service's managed files are the same for every operating system; only its executable is built per runtime.
    // The running operating system picks its platform implementation; callers depend on the platform seam.
    // Connect's in-library operating-system switching is a recorded exception in the repository's AGENTS.md.
    private static Darkspyre.LocalServices.IServicePlatform CreatePlatform() =>
        OperatingSystem.IsMacOS() ? new Darkspyre.LocalServices.Platform.MacOS.ServicePlatform()
        : OperatingSystem.IsLinux() ? new Darkspyre.LocalServices.Platform.Linux.ServicePlatform()
        : OperatingSystem.IsWindows() ? new Darkspyre.LocalServices.Platform.Windows.ServicePlatform()
        : throw new PlatformNotSupportedException("The host-tools service runs on macOS, Linux and Windows.");
}
