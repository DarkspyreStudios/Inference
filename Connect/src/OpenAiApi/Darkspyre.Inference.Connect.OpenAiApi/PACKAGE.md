# Darkspyre.Inference.Connect.OpenAiApi

Conversations, agent sessions and text embeddings through the OpenAI Responses API, at OpenAI or at another service
that serves the same API at its own endpoint, such as a local model server. Its provider appears as "OpenAI API Key".

## Install

```bash
dotnet add package Darkspyre.Inference.Connect.OpenAiApi
```

## Register

```csharp
services.AddLlmConnect();
```

`AddLlmConnect()` from `Darkspyre.Inference.Connect.Common` discovers this package and registers its conversation
client, its agent sessions and its embedding client.

## Example

```csharp
var connection = new LlmConnection
{
    Version = version,
    Transport = LlmTransport.OpenAiResponses,
    Endpoint = "https://api.openai.com/v1",
    Credential = protectedApiKeyCredentialJson
};

LlmEmbeddingProviderService embeddings = serviceProvider.GetRequiredService<LlmEmbeddingProviderService>();
LlmEmbeddingResult result = await embeddings.EmbedAsync(connectionId, new LlmEmbeddingRequest
{
    ModelId = "text-embedding-3-small",
    Inputs = [new TextEmbeddingInput("The meeting moved to Thursday.")]
}, cancellationToken);
```

The application returns the connection from its `ILlmConnectionSource`. Conversations open through
`LlmAgentProviderService.StartSessionAsync`.

## Settings

- **Endpoint** is the API's base address. It defaults to `https://api.openai.com/v1`.
- **API key** is optional. Leave it blank for a service that takes no key.
- **Service tiers** shows the service-tier choice in the chat panel and sends the chosen tier. It starts checked on
  `api.openai.com` and unchecked on any other endpoint.
- **Context lengths** rows each set one model's context length.
- **Check authentication and model access** loads the model list from the endpoint.

## Limits

- An OpenAI API key comes from an OpenAI platform account and is billed separately from a ChatGPT subscription. A
  ChatGPT subscription alone is not an API key; use `Darkspyre.Inference.Connect.Codex` for one.
- When the endpoint refuses temperature or reasoning settings for a model, the failure names the refused setting, so
  the application can send the request again without it.
