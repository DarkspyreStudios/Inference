using System.Diagnostics;
using System.Reflection;
using Darkspyre.LocalServices;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.Inference.Connect.HostTools;

/// <summary>Installs the bundled host-tools service and connects to it, starting it when no process owns it.</summary>
public static partial class HostToolsRuntime
{
    /// <summary>
    /// The folder, beside the library that bundles the service, that holds the service's manifest.json and payload/. The
    /// MCP program in the sibling hosttools/mcp/ folder uses this same package and carries no copy of its own.
    /// </summary>
    public const string PackageFolder = "hosttools/service";

    /// <summary>
    /// Installs the service package in <paramref name="package"/> with <paramref name="folders"/>, connects to it and
    /// returns a client that owns the subscription.
    /// </summary>
    /// <param name="package">The folder holding the service's manifest.json and payload/, built with this library.</param>
    /// <param name="folders">The Plugin data folder of the app copy that owns the service, with CreateIfMissing true for
    /// that owner and false for the MCP program that joins it. Host tools load no models, so ModelFiles is null.</param>
    /// <param name="cancellationToken">Cancels the installation and the connection.</param>
    /// <gotcha>The caller disposes the client. It never owns or terminates the shared service process. A joiner at a folder
    /// with no registration fails with <see cref="ServiceErrorCode.NotInstalled"/> and creates nothing.</gotcha>
    public static async Task<HostToolsClient> ConnectAsync(string package, ServiceFolders folders, CancellationToken cancellationToken = default)
    {
        IServicePlatform platform = CreatePlatform();
        ServiceRegistration registration = await InstallAsync(platform, package, folders, cancellationToken).ConfigureAwait(false);
        IServiceSubscription subscription = await new ServiceConnector(platform, registration, ObserveLaunch)
            .ConnectAsync(cancellationToken).ConfigureAwait(false);
        return new HostToolsClient(subscription);
    }

    /// <summary>
    /// Stops the host-tools service whose owner folder is <paramref name="pluginData"/> and returns once it has exited.
    /// Returns at once when no service is set up there; it creates nothing.
    /// </summary>
    /// <param name="package">The folder holding the service's manifest.json and payload/, built with this library.</param>
    /// <param name="pluginData">The Plugin data folder of the app copy that owns the service.</param>
    /// <param name="cancellationToken">Bounds the stop and the wait for the exit.</param>
    /// <gotcha>Every connection closes, the MCP programs' too; their calls in flight end with the closed subscription.</gotcha>
    public static async Task StopServiceAsync(string package, string pluginData, CancellationToken cancellationToken = default)
    {
        IServicePlatform platform = CreatePlatform();
        ServiceRegistration registration;
        try
        {
            registration = await InstallAsync(platform, package, new ServiceFolders(pluginData, null, CreateIfMissing: false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceProtocolException error) when (error.Code == ServiceErrorCode.NotInstalled)
        {
            return;
        }
        await new ServiceConnector(platform, registration, ObserveLaunch).StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ServiceRegistration> InstallAsync(IServicePlatform platform, string package, ServiceFolders folders,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        ArgumentNullException.ThrowIfNull(folders);
        Assembly assembly = typeof(HostToolsRuntime).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        var manifest = ServicePackageManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(package, "manifest.json"), cancellationToken).ConfigureAwait(false));
        return await platform.InstallAsync(new(ServiceFamily.HostTools, Required("HostTools.LayoutId"),
            Required("HostTools.ClientBuildDigest"), Path.Combine(package, "payload"), manifest, manifest.BuildDigest(), folders),
            cancellationToken).ConfigureAwait(false);

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
