
namespace Darkspyre.Inference.Connect.HostTools;

public static partial class HostToolsRuntime
{
    // Each build includes one platform's composition; callers depend on the platform seam, not this build's type.
#pragma warning disable CA1859
    private static Darkspyre.LocalServices.IServicePlatform CreatePlatform()
#pragma warning restore CA1859
    {
#pragma warning disable CA1416 // MSBuild includes this composition only for the matching target operating system.
        return new Darkspyre.LocalServices.Platform.Windows.ServicePlatform();
#pragma warning restore CA1416
    }
}
