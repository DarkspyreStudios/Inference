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

Both routes share the portable request body except their native `responseFormat` shapes.
Gemini API uses an object; Vertex uses a single-item array (`GeminiWireParityTests`).
Vertex streaming additionally requests its preview path-based function arguments for supported
Gemini 3 profiles; Gemini API does not send that control.

## Authentication and setup

The key is stored in `LlmCredential.ApiKey` by the host's protected credential store. It travels only in
the `x-goog-api-key` header. It never appears in a URL, a request body, an exception or a diagnostic.
`LlmSettings` draws the existing API-key secret field and no endpoint field. A connection rejects an
edited endpoint, additional headers, a Vertex configuration and any credential other
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
modes, prompt images and documents, and managed agent sessions. Gemini 3 tool results carry PNG,
JPEG, WebP and PDF parts natively up to 7 MiB per binary part. Earlier models omit these unsupported
binary results through the projection report. Text documents remain text-carried. Strict
tool input is Unsupported. Parallel-call control and argument streaming are not offered.

Reasoning control comes from `GeminiModelProfiles`, which the Vertex Gemini route shares. Gemini 3 takes
`thinkingLevel`; Gemini 2.5 takes `thinkingBudget`. The request never sends both.

| Prefix | Reasoning levels | Wire form |
| --- | --- | --- |
| `gemini-3.8-flash`, `gemini-3.7-flash` | Low, Medium, High | `thinkingLevel` |
| `gemini-3.6-flash`, `gemini-3.5-flash-lite`, `gemini-3.5-flash`, `gemini-3.1-flash-lite`, `gemini-3-flash` | Minimal, Low, Medium, High | `thinkingLevel` |
| `gemini-3.1-pro` | Low, Medium, High | `thinkingLevel` |
| `gemini-3-pro` | Low, High | `thinkingLevel` |
| `gemini-2.5-flash-lite`, `gemini-2.5-flash` | None, Low, Medium, High | `thinkingBudget` 0 / 1,024 / 8,192 / 24,576 |
| `gemini-2.5-pro` | Low, Medium, High | `thinkingBudget` 1,024 / 8,192 / 32,768 |
| any other model | none | none |

The service keeps the catalog listing and attaches it to generation and capability queries. A catalog
entry whose `thinking` flag is false offers no level, because `thinkingConfig` on a non-thinking model
is an error. `gemini-3*` and `gemini-2.5*` models use provider JSON schema; any other family resolves
structured output through the local fallback rules.

Capability overrides cannot enable JSON object mode, account usage or a reasoning level the family
lacks. The shared profile owns sampling support independently of catalog thinking support.
Flash 3.6 and later reject explicit temperature, top-p, top-k and penalties. Supported profiles
map top-p, top-k, seed, presence/frequency penalties and up to five stop strings into
`generationConfig`. Top-k additionally requires a positive `topK` default in the retained catalog
entry. An override cannot invent that evidence. Seeds are non-negative; top-p is finite and in
(0, 1]; top-k is positive; penalties are finite.

## Request validation

These fail before dispatch:

- `Strict=true` tools;
- `AllowParallelToolCalls`;
- temperature outside 0 to 2;
- attachments other than PNG, JPEG or WebP images up to 7 MiB, PDFs up to 50 MiB and plain text up to
  7 MiB;
- an output schema with a keyword outside the allowlist, unless the request sets the local validation
  fallback.

Connection and request `ProviderOptions` map into `generationConfig`, not the top-level request.
Request options override connection options. An option cannot replace a standard field that the
builder writes. The adapter rejects Anthropic's declared tool configuration on either scope.
Native output schemas use `responseFormat.text` with `mimeType = APPLICATION_JSON` and the original
schema. The adapter does not send deprecated `responseJsonSchema` or `responseMimeType` fields.

Gemini API accepts `$id`, `$anchor`, internal `$ref`, `$defs`, `prefixItems`, `oneOf` and string-array
types alongside the shared keyword set. A `$ref` schema permits only `$`-prefixed siblings.
Definitions and composition children use the same recursive keyword validation. The adapter does
not rewrite schemas or expand references. Vertex retains its narrower keyword set; unsupported
keywords on either route require explicit local validation.

## Stream mapping, usage and errors

The stream mapping is the shared Gemini mapping described in
[the Vertex design](google-vertex-ai-tech-design.md#8-stream-and-terminal-mapping). Synthesized call ids
start with `gemini_`. Output tokens are `candidatesTokenCount` plus `thoughtsTokenCount`.
`ReasoningOutputTokens` is `thoughtsTokenCount` and `CachedInputTokens` is `cachedContentTokenCount`.
Input tokens add `toolUsePromptTokenCount` to a known `promptTokenCount`; a missing prompt count
remains unknown. Running observations replace prior counts and final usage remains authoritative.

`MALFORMED_FUNCTION_CALL`, `UNEXPECTED_TOOL_CALL` and `TOO_MANY_TOOL_CALLS` complete with Failed,
preserving emitted text but no completed tool calls or continuation state. `IMAGE_SAFETY` and prompt
blocks, including `JAILBREAK`, complete with Refused. `MISSING_THOUGHT_SIGNATURE` is a protocol error.

An HTTP failure raises `LlmProviderException` with the status code and `Retry-After`. The message
depends only on the status. The response body is not read. Other stream failures raise a generic
`LlmProviderException` with no inner exception.

## Continuation

Retained state keeps Gemini parts in order with `thoughtSignature` on its original part. The scope is
the hash of the transport, endpoint and API key. A different key cannot replay stored state. A
connection pinned to one scope fails before dispatch when its key changes.

Explicit `UnboundContinuationState.Omit` imports visible foreign tool calls without native state.
Gemini 3 adds the documented `skip_thought_signature_validator` value to the first call in each
imported assistant group. Earlier models need no dummy signature. Default Reject still requires
native continuation. Parallel results follow their original call order, not host arrival order.
Native signatures are never replaced by a dummy signature.

Gemini 3 function responses carry images and PDFs inside `functionResponse.parts.inlineData`.
Their response object references each binary through one `$ref` in its `attachments` array.
Display names have unique ordinal prefixes, contain only ASCII letters/digits and `._-`, and are
limited to 128 characters. Text and binary channels do not preserve the original cross-kind order.

## Context estimate

`EstimateInputTokens` is a local text estimate. It returns null for requests with continuation state,
attachments or non-text tool output. The estimate never calls `countTokens`.
`CountInputTokensAsync` uses the canonical generation request in `generateContentRequest`, including
its model, to call the provider count endpoint. Provider counts remain separate from local estimates
and never fabricate generation output usage.

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
- whether the selected API revision accepts nested function-response `displayName` references;
- whether native `responseFormat` accepts the preserved schema shapes.

## Out of scope

The Interactions API, Files API uploads, context caching, grounding, code execution, the Live API and
Vertex express-mode API keys.
