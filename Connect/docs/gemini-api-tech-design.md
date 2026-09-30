# Gemini API connector

The Gemini API connector reaches Google's Gemini models through Google AI Studio with an API key. It
is separate from the Antigravity CLI connection (`gemini-subscription`) and from Vertex AI
(`google-vertex-ai`).

## Identity and registration

| Item | Value |
| --- | --- |
| Provider id | `gemini-api` |
| Display name | Google AI Studio (Gemini API) |
| Transport | `LlmTransport.GoogleGeminiApi` (12) |
| Endpoint | `https://generativelanguage.googleapis.com`, fixed |
| Client | `Providers/GeminiApiLlmClient` |
| Agent client | `Providers/ManagedGeminiApiAgentClient`, on the managed agent loop |
| Continuation owner | `darkspyre.connect.llm.gemini-api` |

`AddLlmConnect()` registers the provider. The client lives in `Darkspyre.Inference.Connect.LLM` because
it needs no Google SDK. Its typed `HttpClient` does not follow redirects.

## Shared Gemini protocol

`Connect.LLM/Internal/Gemini` holds the generateContent code that the Gemini API and the Vertex Gemini
route both use:

- `GeminiRequest.Build` projects a portable request into the generateContent body.
- `GeminiStream.ReadAsync` reads the SSE stream. The caller supplies the HTTP request with its URL and
  authentication, and a `GeminiRoute` with the route name, transport, call-id prefix, status check and
  continuation encoder.
- `GeminiSchemas` holds the output-schema keyword allowlist.
- `GeminiContinuation` validates retained parts and matches them to the visible message.
  `GeminiContinuationCodec` binds them to an owner and the connection's resolved scope.
- `GeminiCapabilities` declares the capabilities the protocol implements and the inline attachment
  rules.

The same portable request produces an identical body on both routes (`GeminiWireParityTests`).

## Authentication and setup

The key is stored in `LlmCredential.ApiKey` by the host's protected credential store. It travels only in
the `x-goog-api-key` header. It never appears in a URL, a request body, an exception or a diagnostic.
`LlmSettings` draws the existing API-key secret field and no endpoint field. A connection rejects an
edited endpoint, additional headers, provider options, a Vertex configuration and any credential other
than an API key.

The connection test lists models with the key. Success proves Google accepts the key. It does not
prove invocation access to a particular model.

## Model discovery

`ListModelsAsync` calls `GET /v1beta/models?pageSize=1000` and follows `nextPageToken`. A repeated page
token or an unparsable page is an error. An HTTP failure is an error with its status code. Discovery
never returns an empty list in place of a failure.

A catalog entry is listed when all of these hold:

- its name starts with `models/gemini-`;
- its `supportedGenerationMethods` include `generateContent`;
- its id does not contain `-tts`, `-image`, `-live`, `native-audio` or `embedding`, since those models
  do not return text chat parts.

Model ids are the bare catalog id, such as `gemini-3-flash-preview`. An id must match
`^[a-z0-9][a-z0-9._-]{0,199}$`, so it cannot change the request path. Discovery maps `inputTokenLimit`
and `outputTokenLimit` to the model's limits. Availability is Unknown.

## Capabilities

Every listed model has the shared Gemini capabilities: streaming with usage, tool use with all choice
modes, text-only rich tool results, prompt images and documents, and managed agent sessions. Strict
tool input is Unsupported. Parallel-call control and argument streaming are not offered.

Reasoning control depends on the family, because the shared request sends `thinkingLevel`:

| Family | Reasoning levels | Provider JSON schema |
| --- | --- | --- |
| `gemini-3*` with `flash` in the id | Minimal, Low, Medium, High | Yes |
| other `gemini-3*` | Low, High | Yes |
| `gemini-2.5*` | none, since it accepts only `thinkingBudget` | Yes |
| any other family | none | No; structured output resolves through the local fallback rules |

Capability overrides cannot enable JSON object mode, account usage or a reasoning level the family
lacks.

## Request validation

These fail before dispatch:

- `Strict=true` tools;
- `AllowParallelToolCalls`;
- provider options;
- temperature outside 0 to 2;
- attachments other than PNG, JPEG or WebP images up to 7 MiB, PDFs up to 50 MiB and plain text up to
  7 MiB;
- an output schema with a keyword outside the allowlist, unless the request sets the local validation
  fallback.

## Stream mapping, usage and errors

The stream mapping is the shared Gemini mapping described in
[the Vertex design](google-vertex-ai-tech-design.md#8-stream-and-terminal-mapping). Synthesized call ids
start with `gemini_`. Output tokens are `candidatesTokenCount` plus `thoughtsTokenCount`.
`ReasoningOutputTokens` is `thoughtsTokenCount` and `CachedInputTokens` is `cachedContentTokenCount`.

An HTTP failure raises `LlmProviderException` with the status code and `Retry-After`. The message
depends only on the status. The response body is not read. Other stream failures raise a generic
`LlmProviderException` with no inner exception.

## Continuation

Retained state keeps Gemini parts in order with `thoughtSignature` on its original part. The scope is
the hash of the transport, endpoint and API key. A different key cannot replay stored state. A
connection pinned to one scope fails before dispatch when its key changes.

## Context estimate

`EstimateInputTokens` is a local text estimate. It returns null for requests with continuation state,
attachments or non-text tool output. The connector does not call `countTokens`.

## Tests and live qualification

`Connect.LLM.Tests/GeminiApiTests` covers the key header, routing, catalog filtering and paging,
failures, terminal reasons, signed tool continuation, key rotation and pre-dispatch rejection.
`Connect.LLM.Vertex.Tests` covers wire parity and setup storage. None of these tests make network
calls.

The ConsoleTester `login --provider gemini` stores the key from `GEMINI_API_KEY`, and `gemini-probe`
runs the live cases. Live qualification must confirm:

- which catalog models accept `thinkingLevel`, and which accept only `thinkingBudget`;
- the inline request size limit for images and PDFs;
- whether the Gemini API enforces schema keywords beyond the allowlist.

## Out of scope

The Interactions API, Files API uploads, context caching, grounding, code execution, the Live API and
Vertex express-mode API keys.
