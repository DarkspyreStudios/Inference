using Darkspyre.Inference.Connect.HostTools;
using ModelContextProtocol.Server;

// The program runs from hosttools/mcp/ and uses the service package in the sibling hosttools/service/ folder.
string package = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "service"));
using var interrupt = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, request) => { request.Cancel = true; interrupt.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    await HostToolsMcpServer.RunAsync(new StdioServerTransport(HostToolsProtocol.ServerName),
        Environment.GetEnvironmentVariable(HostToolsProtocol.SessionVariable), cancellationToken => HostToolsRuntime.ConnectAsync(package, cancellationToken), interrupt.Token);
}
catch (OperationCanceledException) when (interrupt.IsCancellationRequested) { }
finally { Console.CancelKeyPress -= cancel; }
