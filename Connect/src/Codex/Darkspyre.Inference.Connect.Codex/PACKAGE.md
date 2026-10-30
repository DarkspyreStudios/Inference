# Darkspyre.Inference.Connect.Codex

Conversations and agent sessions on an eligible ChatGPT subscription. The package talks to the ChatGPT subscription
service directly, with an OAuth sign-in the application owns, and runs host tools through Connect's managed agent
sessions. Its provider appears as "ChatGPT Subscription".

## Install

```bash
dotnet add package Darkspyre.Inference.Connect.Codex
```

## Register

```csharp
services.AddLlmConnect();
```

`AddLlmConnect()` from `Darkspyre.Inference.Connect.Common` discovers this package and registers its client, its
agent sessions and its device sign-in.

## Example

```csharp
var connection = new LlmConnection
{
    Version = version,
    Transport = LlmTransport.OpenAiCodex,
    Endpoint = "https://chatgpt.com/backend-api/codex",
    Credential = protectedOAuthCredentialJson
};

// Shows the device code through the host's prompt and saves the credential through ILlmConnectionSource.
LlmProviderService llms = serviceProvider.GetRequiredService<LlmProviderService>();
await llms.SignInAsync(connectionId, authPrompt, cancellationToken);
```

The application returns the connection from its `ILlmConnectionSource`, then opens sessions through
`LlmAgentProviderService.StartSessionAsync`.

## Settings

- **Sign in with ChatGPT** shows a device code and a link. The person enters the code at the link, and the draft
  receives the ChatGPT access token. Save needs a completed sign-in, or a saved credential the edit keeps.
- The settings show no endpoint field: the connection always uses the ChatGPT subscription service.
- **Model** lists the models the subscription offers. A model offers the reasoning levels its catalog entry lists.

## Limits

- The connection needs an eligible ChatGPT subscription. An OpenAI API key does not work here; use
  `Darkspyre.Inference.Connect.OpenAiApi` for one.
- An existing Codex CLI login is separate and is never read, copied or used.
- Each request asks the service to store nothing. Connect sends each turn with the state saved in the
  conversation's own messages.
- JSON-object output mode is not offered.
