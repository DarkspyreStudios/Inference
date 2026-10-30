namespace Darkspyre.Inference.Connect.HostTools;

internal static partial class HostToolsBackend
{
    // The service's managed files are the same for every operating system; only its executable is built per runtime.
    // The running operating system picks its platform implementation; callers depend on the platform seam.
    // Exception to the operating-system boundaries rule: one HostTools package must work on every operating system it
    // is restored on, so this picks the LocalServices platform from the running OS. Brandon approved this on 2026-10-24.
    private static Darkspyre.LocalServices.IServicePlatform CreatePlatform() =>
        OperatingSystem.IsMacOS() ? new Darkspyre.LocalServices.Platform.MacOS.ServicePlatform()
        : OperatingSystem.IsLinux() ? new Darkspyre.LocalServices.Platform.Linux.ServicePlatform()
        : OperatingSystem.IsWindows() ? new Darkspyre.LocalServices.Platform.Windows.ServicePlatform()
        : throw new PlatformNotSupportedException("The host-tools service runs on macOS, Linux and Windows.");
}
