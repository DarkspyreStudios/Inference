# Darkspyre.Inference.Connect.Grok

Conversations and agent sessions on an eligible Grok subscription. The package talks to xAI's Grok subscription
service directly, with a device sign-in the application owns, and runs host tools through Connect's managed agent
sessions. Its provider appears as "Grok Subscription".

## Install

```bash
dotnet add package Darkspyre.Inference.Connect.Grok
```

## Register

```csharp
services.AddLlmConnect();
```

`AddLlmConnect()` from `Darkspyre.Inference.Connect.Common` discovers this package and registers its client, its agent
sessions and its device sign-in.

## Example

```csharp
LlmProviderService llms = serviceProvider.GetRequiredService<LlmProviderService>();

// Shows the device code through the host's prompt and saves the credential through ILlmConnectionSource.
await llms.SignInAsync(connectionId, authPrompt, cancellationToken);
IReadOnlyList<LlmModel> models = await llms.ListModelsAsync(connectionId, cancellationToken);
```

The application's `ILlmConnectionSource` returns a connection whose `Transport` is `LlmTransport.XaiResponses`, then
opens sessions through `LlmAgentProviderService.StartSessionAsync`.

## Settings

- **Sign in with Grok** shows a device code and an x.ai link. The person approves the code, and the draft receives the
  Grok access token. Save needs a completed sign-in, or a saved credential the edit keeps.
- The settings show no endpoint field: the connection always uses the Grok subscription service.
- **Model** lists the models the subscription offers. The default model is `grok-4.7`.

## Limits

- The connection needs an active Grok subscription. No API key is used.
- Hosted tools are image generation, web search and X search, as each model offers them. Web search accepts at most
  five allowed or excluded domains; X search accepts date bounds. Other hosted tools are refused before the request is
  sent.
- The package offers no embeddings.
