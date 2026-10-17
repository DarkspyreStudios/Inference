using System.Diagnostics;
using Darkspyre.LocalServices;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.Inference.Connect.HostTools;

/// <summary>Runs the host-tools service process: ownership, the control host and the router, until the last subscriber leaves.</summary>
internal static partial class HostToolsBackend
{
    internal static Task<int> RunAsync(ServiceStart start, CancellationToken token = default) => RunAsync(start, CreatePlatform(), token);

    internal static async Task<int> RunAsync(ServiceStart start, IServicePlatform platform, CancellationToken token)
    {
        try
        {
            await using IServiceOwner? owner = await platform.TryOwnAsync(start, token).ConfigureAwait(false);
            if (owner is null) return 0;
            var router = new HostToolsRouter();
            await using (var control = new ServiceControlHost(owner.Epoch, HostToolsProtocol.Operations, router.HandleAsync, owner.StagingPath,
                startupTimestamp: TimeProvider.System.GetTimestamp()))
            {
                router.Control = control;
                await owner.PublishAsync(token).ConfigureAwait(false);
                using var peers = new ServicePeerHost(owner, control, router.ReleaseAsync);
                try { await control.Draining.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                ServiceCleanupReport cleanup = await ServiceProcessShutdown.RunAsync([
                    new("accept", _ => peers.StopAcceptingAsync()),
                    new("subscribers", async _ =>
                    {
                        await control.DisposeAsync().ConfigureAwait(false);
                        await peers.JoinPeersAsync().ConfigureAwait(false);
                    })
                ]).ConfigureAwait(false);
                if (!cleanup.Succeeded) Environment.Exit(1); // The framework retains ownership until abnormal process exit.
            }
            await owner.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
#pragma warning disable CA1031 // The process reports any startup or shutdown failure through its exit code.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Trace.TraceError("The host-tools service is unavailable. {0}", error);
            return 1;
        }
    }
}
