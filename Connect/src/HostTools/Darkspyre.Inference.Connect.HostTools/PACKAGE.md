# Darkspyre.Inference.Connect.HostTools

The host-tools local service and its client. An application registers an agent session's tools with the service and
answers their calls; an MCP program that an agent CLI starts lists the session's tools and forwards each call through
the same service. `Darkspyre.Inference.Connect.Antigravity` uses it to give the Antigravity CLI the application's tools.

## Install

```bash
dotnet add package Darkspyre.Inference.Connect.HostTools
```

The package carries the service built for each supported runtime: `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`,
`win-x64` and `win-arm64`. At build time it copies the one for the application's `RuntimeIdentifier`, or for the build
machine's runtime when the application names none, to `hosttools/service/` beside the library.

## Register

The package needs no service registration. `HostToolsRuntime.ConnectAsync` installs the bundled service in the
application's data folder, starts it when no process runs it, and returns a connected `HostToolsClient`.

## Example

```csharp
string package = Path.Combine(AppContext.BaseDirectory, HostToolsRuntime.PackageFolder);
await using HostToolsClient client = await HostToolsRuntime.ConnectAsync(package,
    new ServiceFolders(pluginDataFolder, null, CreateIfMissing: true), cancellationToken);

await using IAsyncDisposable registration = await client.RegisterAsync(sessionId, toolsList,
    (parameters, token) => RunToolAsync(parameters, token), cancellationToken);
```

`toolsList` is the session's MCP `tools/list` result. The handler runs one MCP `tools/call` and returns its
`CallToolResult`. Disposing the registration removes the session from the service.

## Limits

- The client never owns or ends the shared service process. `HostToolsRuntime.StopServiceAsync` stops the service of a
  data folder; every connection to it closes, including the MCP programs' connections.
- An MCP program joins with `CreateIfMissing: false`. Joining a folder where no service is set up fails and creates
  nothing.
- Tool calls run as long as the host tool needs; the agent's own cancellation ends them.
