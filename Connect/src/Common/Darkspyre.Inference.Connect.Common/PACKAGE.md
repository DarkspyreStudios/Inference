# Darkspyre.Inference.Connect.Common

Provider-neutral conversations, agent sessions and embeddings for .NET applications. The package holds the
conversation and embedding contracts, the services that run them, the provider setup model and the discovery that
finds the Connect provider packages an application references. It registers no provider of its own: add one or more
provider packages, such as `Darkspyre.Inference.Connect.OpenAiApi`.

## Install

```bash
dotnet add package Darkspyre.Inference.Connect.Common
```

## Register

```csharp
services.AddSingleton<ILlmConnectionSource, MyConnectionStore>();
services.AddSingleton<ILlmDataFolder, MyDataFolder>();
services.AddLlmConnect();
```

`AddLlmConnect()` registers `LlmProviderService`, `LlmAgentProviderService` and `LlmEmbeddingProviderService`, and
registers every Connect provider package deployed beside this library. Call it once per service collection.

The application supplies two services:

- `ILlmConnectionSource` returns a saved `LlmConnection`, with its credential and version, and saves a refreshed
  credential only when the version still matches.
- `ILlmDataFolder` returns the folder where providers keep their files.

`ConfigureInferenceHttpPipelines`, or the `AddLlmConnect(configureHttp)` overload, sets how the providers' HTTP
clients are created and logged.

## Example

```csharp
LlmAgentProviderService agents = serviceProvider.GetRequiredService<LlmAgentProviderService>();
await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId,
    new LlmAgentSessionOptions { ModelId = modelId }, cancellationToken);

await foreach (LlmAgentEvent agentEvent in session.RunTurnAsync(
    new LlmAgentTurnRequest { Input = [new LlmAgentTextContent("Summarize the attached report.")] },
    cancellationToken))
{
    if (agentEvent is LlmAgentMessageCompletedEvent message)
        Console.WriteLine(message.Text);
}
```

A session runs host tools through `LlmAgentSessionOptions.Tools` and its `ToolHandler`. The host validates and
authorizes every tool call; the model never reaches host resources directly. `LlmProviderService` lists a connection's
models (`ListModelsAsync`), reports its capabilities and its account usage, and signs it in (`SignInAsync`).
`LlmEmbeddingProviderService.EmbedAsync` embeds text or image inputs through a connection whose provider offers
embeddings.

## Settings

`LlmProviderService.SetupDefinitions` lists the discovered providers with their names, icons and setup steps.
`CreateSettingsManager(target)` returns the `LlmSettingsManager` that opens a provider's settings, runs its actions
(check, sign in, test) and saves through the host's `ISetupCommitTarget<LlmSetupChanges>`. Each provider declares the
fields its settings show: an endpoint address, a command-line tool, an engine choice, an API key, a sign-in action and
the model choice. A field edit changes only the settings object; only Save reaches the host. The
`Darkspyre.Inference.UI.Settings` editors render these settings.

## Limits

- Discovery reads provider assemblies from disk beside this library. An application that is not deployed as assembly
  files fails at composition.
- The host owns connection storage, credentials and consent. Connect reads a credential through
  `ILlmConnectionSource` for each operation and writes it back only through `SaveCredentialAsync`.
- A connection's provider package must be referenced; a connection whose provider is not registered is refused.
