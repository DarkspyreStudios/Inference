# Darkspyre.Inference.Connect.Anthropic

Conversations and agent sessions through the Anthropic Messages API with an Anthropic API key, with Anthropic's hosted
web search. Its provider appears as "Anthropic API Key".

## Install

```bash
dotnet add package Darkspyre.Inference.Connect.Anthropic --prerelease
```

## Register

```csharp
services.AddLlmConnect();
```

`AddLlmConnect()` from `Darkspyre.Inference.Connect.Common` discovers this package and registers its conversation
client and its agent sessions.

## Example

```csharp
var connection = new LlmConnection
{
    Version = version,
    Transport = LlmTransport.AnthropicMessages,
    Endpoint = "https://api.anthropic.com/v1",
    Credential = protectedApiKeyCredentialJson
};
```

The application returns the connection from its `ILlmConnectionSource`. Conversations open through
`LlmAgentProviderService.StartSessionAsync`. A session that asks for `LlmWebSearchTool` searches the web on a model that
offers it. A request for an unsupported hosted tool is refused.

## Settings

- **Endpoint** is the API's base address. It defaults to `https://api.anthropic.com/v1`.
- **API key** is an Anthropic API key. It is sent only as the `x-api-key` header.
- **Check authentication and model access** loads the model list from the endpoint.

## Limits

- An Anthropic API key comes from an Anthropic API account and is billed separately from a Claude subscription. A Claude
  subscription is not an API key; use `Darkspyre.Inference.Connect.Claude` for one.
- Web search is offered only for a model whose Models API entry reports it. It accepts allowed or excluded domains, not
  both, and has no image search or image understanding.
- Search always enables citations, which cannot combine with native JSON output format. A search/schema request sends
  the original schema as system guidance and validates the returned JSON locally. A request without search keeps the native
  schema-format path when the model/schema supports it.
- Usage includes reported tokens, cache reads/writes and hosted search requests. Unreported units stay unknown. Provider USD
  remains unknown because the response does not report a price. No account quota is reported.
