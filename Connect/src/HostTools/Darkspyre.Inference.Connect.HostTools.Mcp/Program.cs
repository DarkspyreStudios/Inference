using Darkspyre.Inference.Connect.HostTools;
using Darkspyre.LocalServices;
using ModelContextProtocol.Server;

// The program runs from hosttools/mcp/ and uses the service package in the sibling hosttools/service/ folder.
string package = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "service"));
string? folder = Environment.GetEnvironmentVariable(HostToolsProtocol.FolderVariable);
using var interrupt = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, request) => { request.Cancel = true; interrupt.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    // The server connects only when the folder is set, and joins what the app set up there without creating anything.
    await HostToolsMcpServer.RunAsync(new StdioServerTransport(HostToolsProtocol.ServerName),
        Environment.GetEnvironmentVariable(HostToolsProtocol.SessionVariable), folder,
        cancellationToken => HostToolsRuntime.ConnectAsync(package, new ServiceFolders(folder!, null, CreateIfMissing: false), cancellationToken),
        interrupt.Token);
}
catch (OperationCanceledException) when (interrupt.IsCancellationRequested) { }
finally { Console.CancelKeyPress -= cancel; }
