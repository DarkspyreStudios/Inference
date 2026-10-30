# Darkspyre.Inference.Connect.Claude

Conversations and agent sessions on a Claude subscription, through the official Claude CLI. Each connection is signed
in to one Claude account and runs only as that account. Its provider appears as "Claude Subscription".

## Install

```bash
dotnet add package Darkspyre.Inference.Connect.Claude
```

## Register

```csharp
services.AddLlmConnect();
```

`AddLlmConnect()` from `Darkspyre.Inference.Connect.Common` discovers this package and registers its client, its agent
sessions and its Claude account sign-in.

## Example

```csharp
var connection = new LlmConnection
{
    Version = version,
    Transport = LlmTransport.AnthropicClaudeCli,
    Endpoint = "claude",
    Credential = new LlmCredential { OAuth = accountTokens }.Serialize()
};
```

`Endpoint` is the CLI's name on `PATH` or its absolute path. `Credential` holds the Claude account token the settings'
sign-in saves. The application opens sessions through `LlmAgentProviderService.StartSessionAsync` and runs host tools
through the session's tool handler.

## Settings

- **Sign in to a Claude account** is required. It shows a Claude sign-in link; the person signs in and pastes back the
  code the page shows. Connect saves a one-year token for that account as the connection's credential.
- Until the connection is signed in, the model field reports that the account must sign in first, Save is
  unavailable, and the Check action reports signed out.
- **Check CLI and authentication** confirms the Claude CLI is installed and runs as the connection's account.
- The CLI executable is under More options. A blank value uses `claude`.
- **Model** lists the models the account offers.

## Limits

- Install the official Claude CLI and make `claude` available on `PATH`, or enter its absolute path.
- The connection runs only on its own token. The computer's own Claude CLI login is never used, read or changed, so
  connections on different accounts run at the same time.
- A connection without its own token starts no CLI process: its requests fail as not signed in.
- The token is never refreshed. Signing in again replaces it.
- Each prompt runs in an empty temporary folder with the CLI's own tools, slash commands and session history turned
  off. Host tools reach the model only through the agent session.
- Account usage is the quota the connection's own turns report; it is unavailable until a turn has reported one.
