using System.Diagnostics;
using System.Reflection;
using Darkspyre.LocalServices;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.Inference.Connect.HostTools;

/// <summary>Installs the bundled host-tools service and connects to it, starting it when no process owns it.</summary>
public static partial class HostToolsRuntime
{
    /// <summary>The folder beside this library that holds the service's manifest.json and payload/.</summary>
    internal const string PackageFolder = "hosttools/service";

    /// <summary>Installs the bundled service, connects to it and returns a client that owns the subscription.</summary>
    /// <gotcha>The caller disposes the client. It never owns or terminates the shared service process.</gotcha>
    public static async Task<HostToolsClient> ConnectAsync(CancellationToken cancellationToken = default)
    {
        IServicePlatform platform = CreatePlatform();
        Assembly assembly = typeof(HostToolsRuntime).Assembly;
        string directory = Path.GetDirectoryName(assembly.Location)
            ?? throw new InvalidDataException("The host-tools client has no physical location.");
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        string package = Path.Combine(directory, PackageFolder);
        var manifest = ServicePackageManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(package, "manifest.json"), cancellationToken).ConfigureAwait(false));
        ServiceRegistration registration = await platform.InstallAsync(new("HostTools", ServiceFamily.HostTools,
            Required("HostTools.LayoutId"), Required("HostTools.ClientBuildDigest"), Path.Combine(package, "payload"), manifest,
            manifest.BuildDigest()), cancellationToken).ConfigureAwait(false);
        IServiceSubscription subscription = await new ServiceConnector(platform, registration, ObserveLaunch)
            .ConnectAsync(cancellationToken).ConfigureAwait(false);
        return new HostToolsClient(subscription);

        string Required(string key) => metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidDataException($"The host-tools client is missing {key}.");
    }

    private static void ObserveLaunch(ServiceLaunchObservation launch) => _ = ObserveExitAsync(launch);

#pragma warning disable CA1031 // Exit observation only records diagnostics; it never fails the caller.
    private static async Task ObserveExitAsync(ServiceLaunchObservation launch)
    {
        try
        {
            ServiceProcessExit exit = await launch.Completion.ConfigureAwait(false);
            Trace.TraceInformation("Host-tools service attempt {0} exited with code {1}.", launch.AttemptId, exit.ExitCode);
        }
        catch (Exception error) { Trace.TraceError("Host-tools service attempt {0} exit observation failed: {1}", launch.AttemptId, error); }
    }
#pragma warning restore CA1031
}
