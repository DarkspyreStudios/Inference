# Darkspyre.Inference.Connect.Antigravity

Conversations and native agent sessions on a Google subscription, through the official Antigravity CLI (`agy`). The
CLI keeps its own Google sign-in; Connect never reads, copies or stores its credentials. Its provider appears as
"Gemini Subscription (Antigravity)".

## Install

```bash
dotnet add package Darkspyre.Inference.Connect.Antigravity
```

The package brings `Darkspyre.Inference.Connect.HostTools`. Both copy a program built for the application's runtime
beside their library at build time: the host-tools service and the `darkspyre-host-tools-mcp` program the CLI starts to
reach the application's tools.

## Register

```csharp
services.AddLlmConnect();
```

`AddLlmConnect()` from `Darkspyre.Inference.Connect.Common` discovers this package and registers its client, its agent
sessions and its account usage. The application's `ILlmDataFolder` holds the host-tools service and each call's
scratch folder. Before the application moves that folder, it awaits `ReleaseAsync` on every registered
`ILlmDataFolderUser`.

## Example

```csharp
var connection = new LlmConnection
{
    Version = version,
    Transport = LlmTransport.GoogleAntigravityCli,
    Endpoint = "agy"
};
```

The connection has no credential. `Endpoint` is `agy` or the CLI's absolute path. The application opens sessions
through `LlmAgentProviderService.StartSessionAsync`, and `ResumeSessionAsync` reopens one by its thread id.

## Settings

- **Check CLI and authentication** confirms the CLI is installed, signed in and able to list the account's models.
- The CLI executable is under More options. A blank value uses `agy`.
- **Model** lists the models the account offers, plus `auto`, which uses the CLI's default.

## Limits

- Install the official Antigravity CLI and complete its Google sign-in once by running `agy` in a terminal. On macOS
  and Linux a bare name also resolves from `~/.local/bin`.
- Temperature, maximum output tokens, custom headers and provider options are refused.
- Each request runs in an empty folder under the application's data folder, deleted when the call ends.
- A request that turns streaming off passes its prompt to the CLI as a command-line argument, which the local process
  list shows. Such a prompt is limited to 131,071 UTF-8 bytes on macOS and Linux and 30,000 characters on Windows.
