using Darkspyre.Inference.Connect.HostTools;
using ModelContextProtocol.Server;

using var interrupt = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, request) => { request.Cancel = true; interrupt.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    await HostToolsMcpServer.RunAsync(new StdioServerTransport(HostToolsProtocol.ServerName),
        Environment.GetEnvironmentVariable(HostToolsProtocol.SessionVariable), HostToolsRuntime.ConnectAsync, interrupt.Token);
}
catch (OperationCanceledException) when (interrupt.IsCancellationRequested) { }
finally { Console.CancelKeyPress -= cancel; }
