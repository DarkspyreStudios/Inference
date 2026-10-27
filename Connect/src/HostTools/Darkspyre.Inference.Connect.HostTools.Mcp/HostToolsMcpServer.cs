using System.Text.Json;
using Darkspyre.LocalServices;
using Darkspyre.LocalServices.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Darkspyre.Inference.Connect.HostTools;

/// <summary>
/// Serves one agent session's host tools over MCP. The session ID and the app's Plugin data folder for host tools come
/// from the environment the agent CLI was started with; a process started without either serves no tools and never
/// connects to the service. It joins the service the app set up in that folder and creates nothing there.
/// </summary>
internal static class HostToolsMcpServer
{
    internal static async Task RunAsync(ITransport transport, string? session, string? folder,
        Func<CancellationToken, Task<HostToolsClient>> connect, CancellationToken token)
    {
        // Without the folder there is no service to join, as without a session.
        if (folder is null) session = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var client = new Lazy<Task<HostToolsClient>>(() => connect(lifetime.Token));
        var options = new McpServerOptions
        {
            ServerInfo = new() { Name = HostToolsProtocol.ServerName, Version = typeof(HostToolsMcpServer).Assembly.GetName().Version!.ToString() },
            Capabilities = new() { Tools = new() },
            Handlers = new()
            {
                ListToolsHandler = async (_, cancellationToken) => session is null ? new ListToolsResult { Tools = [] }
                    : Read<ListToolsResult>(await (await client.Value.WaitAsync(cancellationToken).ConfigureAwait(false))
                        .ListAsync(session, cancellationToken).ConfigureAwait(false)),
                CallToolHandler = async (request, cancellationToken) =>
                {
                    if (session is null) return Failure("This agent session offers no host tools.");
                    try
                    {
                        HostToolsClient connected = await client.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                        JsonElement parameters = JsonSerializer.SerializeToElement(request.Params, McpJsonUtilities.DefaultOptions);
                        return Read<CallToolResult>(await connected.CallAsync(session, parameters, cancellationToken).ConfigureAwait(false));
                    }
                    catch (ServiceProtocolException error) when (error.Code == ServiceErrorCode.NotInstalled)
                    {
                        return Failure($"Host tools are unavailable: no host-tools service is set up in {folder}.");
                    }
                    catch (IOException error)
                    {
                        // The host session is gone or the service closed: the model sees a failed call and the turn continues.
                        return Failure(error.Message);
                    }
                }
            }
        };
        try
        {
            // Server disposal joins its calls before the client releases the service subscription.
            await using (McpServer server = McpServer.Create(transport, options))
                await server.RunAsync(lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (client.IsValueCreated)
            {
                try { await (await client.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }
    }

    private static T Read<T>(JsonElement json) => json.Deserialize<T>(McpJsonUtilities.DefaultOptions)
        ?? throw new InvalidDataException($"The host-tools service returned an empty {typeof(T).Name}.");

    private static CallToolResult Failure(string message) => new() { Content = [new TextContentBlock { Text = message }], IsError = true };
}
