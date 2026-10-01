# Google Vertex AI connector: technical mapping

**Ticket:** [DSA-61](https://darkspyre.atlassian.net/browse/DSA-61), under DSA-57.  
**Scope:** current Vertex routing, configuration and protocol mapping. Live qualification is outstanding.

### Implementation checkpoint

The source implementation includes the Common continuation envelope, all existing connector
projections, typed Google setup, Vertex authentication/routing, Gemini/Claude protocols and the
ConsoleTester probe. Deterministic tests cover protocol and migration behavior. Live Vertex
qualification has not been run; catalog visibility does not establish invocation access.
The nullable express-mode configuration is a breaking contract in the synchronized package family.
Publication evidence for published versions is recorded in `.roles/project.md`.

Verification (2026-09-28): Connect 553 passed / 3 existing conditional skips (27 Vertex fixtures);
Tensor 346 passed; UI 65 passed. Shared Common tests occur in each solution. All three solutions
passed restore, zero-warning build, format verification and transitive dependency vulnerability audit.
No paid/live inference calls were used.

Gemini text and Claude model IDs use the shared Gemini and Anthropic profiles. The adapter has no
two-model or regional allowlist. Gemini TTS, image, live, native-audio and embedding models are excluded
from this text-chat adapter. Gemini 3 Flash's documented limits are 1,048,576 input and 65,536 output
tokens. Other unavailable limits remain null. Claude's catalog-free strict-schema evidence remains
Unknown; ordinary non-strict tools work independently, and explicit schema capability overrides are
supported. Discovery uses `v1beta1/publishers/{publisher}/models`, paginates both publishers and filters
to text-chat families. Returned availability stays Unknown. Google.Apis.Auth 1.76.0 owns token
acquisition and refresh; deterministic tests cover credential-owner caching, concurrent resolution,
file replacement and cancellation, not Google's live token exchange.

**Approved strict capability amendment:** `LlmToolCapabilities.StrictInputSchema` uses
`LlmStrictInputSchemaSupport` with Supported / Unsupported / Unknown (Unknown is the default).
Unknown permits an explicit strict attempt through Connect, unchanged; it does not promise success.
The host decides its policy, and AgentDS defaults to "try and see if it works". Known Unsupported
rejects explicit strict requests. Ordinary tools remain non-strict and are never gated by strict
support. Existing compatible-chat, Anthropic and Bedrock opt-in wire behavior is preserved.

**Approved persistence policy:** capture/envelope format 2 replaces format 1. Unversioned captures
and messages containing the former `ResponseState` member are rejected, not silently accepted with
lost state. DSA-68 owns migration before host package adoption and preservation of original records.
No library dual-reader or compatibility shim is planned.

## 1. Scope and evidence

Implement one selectable Google Vertex AI provider for Gemini and Claude, with one setup and
two internal protocol routes. Parity means that the existing neutral chat, tool, structured-output
and managed-agent semantics survive each supported route, including through `ILlmInference`.
Required information must not disappear at a public boundary. A feature that the selected model
cannot honor is rejected explicitly; a narrower model is not advertised as fully capable.

This mapping combines repository inspection with official documentation. No live Google Cloud
call or credential acquisition was performed. Model-specific combinations still need the
qualification work in section 11. Google currently redirects some Vertex documentation to
Gemini Enterprise Agent Platform; the API routing and ticket identity remain explicit here.

Embeddings, image/audio generation, Google-hosted agent deployment, billing integration and
provider-hosted search/code tools are separate capabilities, not implied by this LLM ticket.
This boundary does not remove any of the existing host-executed tool requirements.

## 2. Components and ownership

| Component | Responsibility |
| --- | --- |
| Common LLM contracts | Portable continuation state and capability/usage additions shared by Connect and Tensor |
| `Darkspyre.Inference.Connect.LLM` | Existing services, setup integration, managed session runtime and neutral contracts |
| New `Darkspyre.Inference.Connect.LLM.Vertex` assembly | Google authentication, endpoint validation, model catalog, Gemini and Claude wire adapters |
| `VertexLlmClient : ILlmClient` | One provider definition, model routing, capabilities, listing and streaming |
| `ManagedVertexAgentClient : ILlmAgentClient` | Compose the existing managed runtime with the Vertex client |
| Internal Claude protocol components | Request projection, stream parsing, continuation codec and error translation |
| Shared Gemini protocol in `Connect.LLM/Internal/Gemini` | Request body, SSE stream reading, schema allowlist, continuation part validation and capabilities, shared with the Gemini API adapter |
| Existing ConsoleTester | Setup/discovery and explicit live qualification for both routes |

Provider ID is `google-vertex-ai`; add `LlmTransport.GoogleVertexAi` without renumbering
existing values. Keep four NuGets. Include the new assembly in Connect and place any Google auth
SDK dependency only in that assembly, with its version in `Directory.Packages.props`.
Use owned HTTP protocol adapters and Google's credential library; no Google SDK types cross the seam.
The dependency participates in the repository vulnerability audit.

Reuse the managed-runtime access pattern already used by the Bedrock assembly. Extract genuinely
shared Anthropic message/block handling into one internal implementation where behavior matches;
retain separate authentication, URL construction, version fields and capability profiles. Do not
route through the direct Anthropic client by fabricating an API-key connection.

## 3. Connection, credentials and setup

Use a typed optional `LlmConnection.Vertex` configuration containing nullable `ProjectId`, `Location`
and `AuthenticationMode` (`ApplicationDefault`, `ServiceAccount` or `ApiKey`). ADC and service accounts
require a validated project and explicit location. `ApiKey` requires null project/location and an
API-key-only `LlmCredential`. These belong to the owning
connector contract, not Common. Add a protected Google service-account credential shape to
`LlmCredential`; do not place a private key in `ProviderOptions` or `AdditionalHeaders`.
The host remains the credential-store owner. `Version` continues to cover configuration and secrets.

ADC delegates discovery and refresh to Google's library. Explicit service-account mode accepts
only the declared service-account credential format through a vetted parser. It must not accept
arbitrary credential JSON that redirects token exchange to a caller-supplied endpoint. Google
documents ADC's environment/local-file/attached-account discovery sequence [S1].

The adapter acquires a bearer token immediately before an operation. Token refresh is coordinated
per resolved credential owner and never saved as a replacement for the host's service-account key.
Refreshing an access token must not invalidate conversation state. Switching the actual principal,
project, location or credential source must invalidate its binding. Ambient ADC has no host `Version`
change when its file/account changes: resolving and detecting that change is a required auth-boundary
test, not something the existing version token solves. If stable principal identity cannot be proven,
use a conservative credential-instance scope; never infer equivalence from project alone.

Build HTTPS endpoints from validated project/location/model identifiers. Gemini uses the Google
publisher generation route; Claude uses the Anthropic publisher prediction route. Regional/global
host selection belongs to the adapter. Reject an arbitrary endpoint override or authorization header;
disable redirects that could forward credentials. The provider verifies location eligibility.
No automatic regional fallback, model substitution, impersonation or Cloud resource creation.

Provider-owned setup steps:

1. Select ADC, explicit service-account authentication or Gemini-only express API-key mode.
2. Enter project and location for ADC/service accounts. Express mode hides both fields and requires an API key.
3. Resolve credentials and validate configuration without running inference.
4. Discover/select a model, showing catalog versus verified-access status.
5. Offer an explicit metered connection test, with its side effect declared in setup metadata.
6. Save through the existing typed commit target and compare-and-swap contract.

Extend `LlmSettings`, its draft/change projection and manager for these fields. The provider supplies
its `SetupDefinition`; ConsoleTester and UI.Settings consume it. Do not create a second setup flow.
Authentication success, catalog access and inference permission are separate reported results.

Express mode uses `https://aiplatform.googleapis.com/v1/publishers/google/models/{model}` with
`generateContent`, `streamGenerateContent?alt=sse` or `countTokens`. Authentication uses only the
`x-goog-api-key` header. The API key never enters the URL, payload, diagnostics or continuation state.
No Google SDK/ADC discovery runs in this mode. Claude is unsupported. Model listing returns an empty
list without HTTP and reports `SupportsModelListing = false`; the host permits manual model entry.
The API-key hash scopes continuation state. Changing keys invalidates that state; same-key replay
preserves the existing format and model binding. Setup does not create a project or run inference.

## 4. Model identity and discovery

Use canonical request IDs `google/<native-model-id>` and `anthropic/<native-model-id>`. Preserve
native version suffixes. The parser accepts exactly those publisher namespaces and validated model
segments; callers cannot inject project/location/operation paths through a model ID.

Each adapter-owned model profile records native ID, protocol, endpoint eligibility, context/output
limits, capabilities, supported MIME types, schema rules, reasoning mapping and known parameter
combination restrictions. Shared family profiles provide known controls without treating every future
Gemini/Claude model as equivalent. Missing schema evidence and limits remain unknown.

Propose optional neutral model metadata on `LlmModel` and `LlmModelInfo`:

- `Availability`: `Unknown`, `Available` or `Unavailable` for the current connection.
- `AvailabilityCheckedAt`: timestamp for that evidence, if any.
- `AvailabilityReason`: sanitized explanation, never a raw provider response.

`Unknown` remains selectable; only an operation or authoritative access check can verify access.
An inaccessible catalog must not be returned as an empty successful list. Cache identity includes
project, location and auth scope, with explicit refresh through the existing service.

Discovery uses the paged `v1beta1/publishers/{publisher}/models` endpoint for Google and Anthropic.
It retains each native resource name, `versionId` and `launchStage`. A catalog revision does not alter
the invocation model ID. Launch stage appears in availability diagnostics only for recognized enum
values. The adapter filters to Gemini text and Claude families. An Anthropic publisher-list 403 or
404 contributes no entries; other catalog failures remain errors. Manual Claude IDs remain usable.
Actual endpoint permissions and regional applicability remain qualification items.
Claude's native Models endpoint is not available on this platform [S2]. Do not substitute the
project's custom-trained-model registry. If an owned catalog is necessary, label its provenance and
unknown access accurately; do not claim a live authorized-model inventory.

## 5. Request and protocol mapping

The following is the adapter mapping specification, subject to model-profile qualification.
Google's native generation and function APIs and Claude's Vertex Messages variation are the
protocol references [S2–S4]. Use a single candidate; our result contract represents one answer.

| Neutral input | Gemini route | Claude route |
| --- | --- | --- |
| Model ID | URL publisher/model resource | URL publisher/model resource; omit body `model` |
| Operation | `:streamGenerateContent?alt=sse` | `:streamRawPredict`, body `stream: true` |
| Protocol version | Explicit Vertex API version in path | Path version plus body `anthropic_version: vertex-2023-10-16` |
| Instructions | `systemInstruction` text parts | Top-level `system` blocks |
| User/assistant history | `contents`, roles `user`/`model` | `messages`, roles `user`/`assistant` |
| Text | Ordered text parts | Ordered text blocks |
| Temperature | `generationConfig.temperature` | `temperature` |
| Sampling | `generationConfig.topP`, `topK`, `seed`, `presencePenalty`, `frequencyPenalty`; penalties in [-2, 2) | `top_p`, `top_k`; thinking permits top-p >= 0.95 and no top-k |
| Stop strings | `generationConfig.stopSequences`, maximum 5 | `stop_sequences`, no adapter-imposed limit |
| Maximum output | `generationConfig.maxOutputTokens` | `max_tokens`; resolve a valid model-specific value when omitted |
| Reasoning level | `GeminiModelProfiles`, shared with the Gemini API, supplies the profile's levels and maps a level to `thinkingLevel` or `thinkingBudget` | `AnthropicModelProfiles` family table maps to thinking mode/budget and applicable effort |
| Tool declarations (default non-strict) | `tools[].functionDeclarations`; profile selects supported `parametersJsonSchema` or lossless `parameters` representation | `tools` with `input_schema`; omit `strict` when false |
| Tool choice Auto/None | `AUTO`/`NONE` | `auto`/`none`, subject to model support |
| Tool choice Required/Specific | `ANY`; restrict allowed function names for Specific | `any`/`tool` plus name |
| Parallel-call control | Map only where the selected protocol can enforce it | Map to supported `disable_parallel_tool_use` setting |
| Previous calls | `functionCall` parts in assistant content | `tool_use` blocks |
| Tool results | `functionResponse`, with original call association | `tool_result`, preserving `tool_use_id` and `is_error` |
| Schema output | JSON MIME type plus supported schema field | Supported `output_config.format` JSON-schema configuration |
| Image/document bytes | Supported inline data parts | Supported base64 image/document blocks |
| Conversation key | Host affinity only | Host affinity only |

Vertex Claude accepts the shared Anthropic top-level `ProviderOptions`. Request options override
connection options. The shared request builder owns tool, thinking, sampling and schema controls;
the Vertex adapter owns the body protocol version and URL model. Count requests reuse the shared
projection and field preflight. A top-level field that the count API cannot represent fails before
credential resolution and HTTP. Generation does not silently drop that field.

Do not flatten signed assistant output into visible text/calls. Preserve sequence and per-part
association through the continuation envelope in section 6. Keep original wire call IDs; if Gemini
does not supply one, allocate a stable Connect call ID and retain its wire association in state.
Repeated calls with the same function name remain distinct. Reject ambiguous imported tool histories.

### Tool schema acceptance and optional strict enforcement

Review of the existing implementation and its fixes is authoritative for this proposal:

| Existing behavior/evidence | Vertex requirement |
| --- | --- |
| `ea4245a`: managed tools inherited the chat default of true; OpenAI rejected valid host schemas | Preserve `LlmAgentToolDefinition.Strict = false` and pass the host's value unchanged into chat requests |
| `201e84c`: all tool definitions moved to the non-strict default | Preserve `LlmToolDefinition.Strict = false`, including through the portable bridge |
| `8285e6d`: Nova rejected even `strict: false` | False does not imply serializing a field named `strict` |
| `27a8053`: Anthropic/compatible chat omit the flag unless true | Claude on Vertex follows that omission rule for ordinary tools |
| Responses/Codex serialize the host's true/false value | Keep adapter-specific wire behavior; no universal strict-field serializer |
| `ManagedAgentSession` validates returned arguments against `InputSchema` before invoking the handler | Keep this local validation regardless of Strict; invalid arguments produce an error tool result without running the handler |

Four independent concepts govern a tool request:

1. **Host input schema:** what the tool accepts. Optional properties stay optional. Preserve the
   original `required` set and `additionalProperties` semantics.
2. **Provider schema acceptance:** the schema dialect/shape the endpoint accepts in its declaration.
   Validate actual wire requirements for the chosen route. A non-strict declaration must not be
   rejected merely because it fails another provider's strict-generation subset.
3. **Tool choice:** whether a call is permitted or required, and which tool can be called. This does
   not opt a host into `Strict = true`.
4. **Strict generation enforcement:** the host explicitly requests provider-enforced argument
   conformance. Qualify this independently of ordinary tool support and local validation.

**Default lane:** send tools non-strict. For Claude omit `strict`. Gemini has no equivalent per-tool
`strict` property in `FunctionDeclaration`; use the ordinary native declaration and requested choice
mode [S9]. `Auto` stays `AUTO`; do not promote it to `VALIDATED` or `ANY` because a schema exists.
Gemini modes can intrinsically constrain arguments: `Strict = false` requests no extra strict
guarantee, not that the provider disable its inherent validation. Required still maps to `ANY`.
Use `parametersJsonSchema` where qualified; a typed `parameters` conversion must preserve meaning.
Do not make every property required, close objects, or add nullable alternatives to manufacture an
OpenAI strict schema. Unsupported wire constructs get a specific schema-compatibility error.

**Explicit strict lane:** only a tool with `Strict = true` requests the additional guarantee.
Claude may send `strict: true` on a qualified model. Gemini requires a proven schema/mode mapping;
there is no flag to copy. Until that mapping is qualified, advertise strict enforcement unsupported
while continuing to support ordinary tools. Reject only requests that actually ask for an unsupported
guarantee or combination. Do not silently downgrade true, force Auto to call a tool, rewrite schemas,
or enable stricter requirements for unrelated non-strict tools in a mixed request. Gemini's
request-wide modes versus per-tool Strict need an explicit mixed-request qualification [S4, S10].

`HostToolsAreSentNonStrictUnlessTheHostRequestsStrictness` is the existing regression anchor: its
`command` is required, `timeout` optional, and `additionalProperties` absent. Its schema is preserved.
`UnknownToolsAndSchemaFailuresReturnErrorsWithoutExecuting` anchors local execution validation.
Chat callers still execute/validate their own tools; a chat adapter never executes a handler.

Executing tools sequentially in our host also does not enforce a request that the model emit only
one call. No silent removal of `AllowParallelToolCalls = false` [S4].

### Structured reply output is a separate request

Keep the current `LlmStructuredOutputRequest.Strict = true` default. It does not change the tool
definitions' Strict values, and a structured reply does not make all tools strict. Qualify the
provider's output-schema support separately from its tool-schema acceptance and strict tool support.
Neither local validation nor a non-strict schema hint is evidence of provider-enforced conformance.

Validate output and tool schemas against their respective route requirements without deleting
constraints. JSON-object fallback must actually
enforce the promised shape; JSON MIME type alone is not sufficient. Respect the existing explicit
fallback policy and report the actual enforcement. Tools-plus-schema and thinking-plus-forced-tools
must be qualified as combinations, not inferred from individual capability booleans [S3, S5, S6].

For rich results, preserve text order, structured content and error semantics. Use the existing
`LlmRichContentSupported` projection rules for unsupported parts, including omission reporting.
Advertise native image/document tool results only after qualification. Resource URIs are references;
Connect does not fetch them. Claude document support on Google Cloud requires base64 sources [S7].
MIME types, payload limits and attachment completeness are validated before dispatch.

The Gemini 3 profile carries PNG, JPEG, WebP and PDF tool results as nested
`functionResponse.parts.inlineData`, with one matching `$ref` per binary in the response's
`attachments` array. Each binary is bounded at 7 MiB. Display names use a unique ordinal prefix,
ASCII letters/digits and `._-`, capped at 128 characters. Text documents remain text-carried;
cross-kind order is not preserved. Projection reports retain omitted-part information.
Explicit Omit imports foreign visible call groups with the documented dummy thought signature on
their first call; default Reject still requires original native state. Native signed state is replayed
unchanged, and parallel responses follow original call order.

Gemini input usage includes tool-use prompt tokens when the provider reports a prompt count.
Malformed, unexpected and excessive tool-call finish reasons yield Failed with preserved text,
no completed calls and no continuation. Missing thought signatures remain protocol errors.
Image safety and prompt blocks yield Refused. Unknown finish reasons stay Unknown.

Gemini connection and request provider options go into `generationConfig`. Request options override
connection options. Options cannot replace standard fields written by the canonical builder.
Anthropic tool configuration is rejected on Gemini. Vertex Gemini retains its existing output-schema
keyword allowlist. Unsupported constraints require explicit local validation without schema rewriting.
Native structured output uses a single-item `generationConfig.responseFormat` array containing
`text: { mimeType: "APPLICATION_JSON", schema: ... }`. Gemini API uses the corresponding object.
Neither route writes the deprecated response schema or MIME fields. Counting uses this same builder.

Vertex Gemini streaming requests preview `streamFunctionCallArguments` for Gemini 3 profiles other
than 3.1 Flash-Lite when tools are present. Non-streaming and counting do not enable the control.
The stream emits the call name at its first fragment. It reconstructs `partialArgs` by root,
property and array-index paths, appends string fragments, and retains one complete call with its
original thought signature. Unsupported paths fail with a fixed protocol diagnostic. The parser
accepts only single-target paths, not filters, slices or wildcards. String end markers and an empty
function-call terminator close pending fragments. Sparse array expansion is bounded by the existing
continuation wire-size limit and reconstruction depth matches the JSON serializer's 64-level limit.
Successful terminal replies require all opened calls to be complete; truncation, refusal and failed
model outcomes never expose unfinished calls as executable tools or continuation state.
`SupportsArgumentStreaming` remains false because typed path fragments are not JSON-text deltas.
Preview availability and the reconstructed wire replay require real-provider release qualification.

## 6. Required continuation contract change

Before DSA-61, `LlmChatMessage`/`LlmChatResult` carried the Connect-only `LlmProviderResponseState`; Common's `LlmMessage`
and `LlmInferenceResult` did not. The former portable bridge consequently could not round-trip it. Gemini
thought signatures make this a correctness requirement for applicable tool continuations [S8].

**C1:** replace the Connect-only carrier with one Common semantic carrier,
`LlmContinuationState`, used by both portable and Connect messages/results. Proposed fields:

| Field | Meaning |
| --- | --- |
| `FormatVersion` | Version of the neutral envelope |
| `ImplementationId` | Stable owning implementation identity; no Connect transport enum in Common |
| `ModelId` | Exact canonical model binding |
| `ScopeId` | Opaque compatibility binding issued by the owning adapter |
| `Payload` | Opaque allowlisted continuation content, not a full provider response |
| `InputTokens`, `OutputTokens` | Optional provider reports used by existing context accounting |

Add `ContinuationState` to Common and Connect assistant messages/results, carry it unchanged through
the portable bridge, and migrate the current producers and consumers together. Move the explicit
reject/omit policy into Common as `LlmUnboundContinuationState`; expose omission count on both result
surfaces. Preserve default Reject semantics. Tensor implementations that cannot consume supplied state
must reject or honor explicit Omit, never silently ignore it. Providers without state return null.

The envelope is a pass-through value, not a public native-protocol API. Hosts must not parse or log
its payload. Imported bytes remain untrusted and sensitive. Owning codecs validate version, size,
binding, content shape, visible text and tool-call correspondence. Replay replaces the assistant's
wire content exactly once; visible fields are not also serialized. State may be essential when
visible text is empty. Never include credentials, headers or arbitrary diagnostic fields.

Gemini's codec retains ordered parts and signatures attached to their original parts. Claude's codec
retains ordered text/tool/thinking/redacted-thinking blocks and signatures. Neither normalizes signed
content or reconstructs it from displayed reasoning. Compaction preserves complete retained groups;
discarding summarized groups is explicit. Missing required state in a tool continuation is an error.

Refactor the current central `ProviderContinuationStateCodec`, which assumes Responses/Anthropic output
shapes and derives scope from generic connection fields. Each owning route validates its own payload
and supplies its binding; shared runtime code works on the neutral envelope. Google project/location
and principal must participate in binding. Do not add Gemini JSON parsing to host/session code.

This is a public contract migration. Inventory exported types, serialized context captures and external
consumer handoffs before implementation. No second internal state mechanism or implicit conversation
cache. Decide the published-contract compatibility policy explicitly; a breaking coordinated release
and capture-format revision may be required. The user subsequently authorized the coordinated major release for host testing.

## 7. Other contract deltas

**C2 — Tool controls:** add a shared `LlmToolCapabilities` descriptor to Common and Connect model
capabilities. Members: supported `ChoiceModes`, `StrictInputSchema`,
`SupportsParallelCalls`, `SupportsParallelCallControl`, and `SupportsArgumentStreaming`.
Carry this descriptor through `ConnectLlmInference`. `SupportsToolUse` continues to mean that calls
and results can round-trip with ordinary non-strict tools; it does not promise every control.
`StrictInputSchema = Unsupported` must never disable ordinary tools or trigger a strict-schema
preflight for them. It describes an optional generation guarantee, not schema parsing or local
argument validation. These additions do not change either tool definition's default or add a
connection-wide strict toggle. Profiles additionally validate
combinations with reasoning and structured output. Existing adapters receive accurate declarations
in the same migration. Overrides cannot enable a control the adapter has not implemented.

**C3 — Usage details:** preserve the existing input/output/total counters and add optional neutral
`CachedInputTokens`, `CacheWriteInputTokens` and `ReasoningOutputTokens` details. Define normalized
input as all consumed input, including cached input; output includes reasoning when counted by the
provider. Cache and reasoning fields are subsets, not additional totals. Normalize Claude's separate
cache counters and Gemini's usage metadata once. Unknown breakdowns remain null; do not derive cost
or quota from token totals. Exact field formulas require provider response fixtures before approval.

**C4 — Model access evidence:** add the optional metadata from section 4 and preserve it in the bridge.
Known model support and permission to invoke it are separate properties.

**C5 — Google setup:** add the typed connection/credential shapes and setup projection from section 3.
These are connector-only. No Google configuration or SDK dependency enters Common or UI.Settings.

**C6 — Context measurement:** the existing managed runtime's estimator is synchronous even though
public measurement methods return tasks. A remote token-count endpoint cannot be called under that
lock or hidden in the estimator. Initially retain truthful Estimated/Mixed/Unknown accounting using
provider reports and existing estimation. If exact remote measurement is included in parity acceptance,
replace the internal seam with asynchronous measurement: capture revision under lock, await outside
it, reject stale results, honor cancellation, and distinguish provider-measured from estimated source.
Do not present a token-count estimate as a provider admission guarantee. The implementation uses local estimates and provider reports; remote preflight measurement is not included.

## 8. Stream and terminal mapping

| Provider observation | Neutral event/result |
| --- | --- |
| Visible text delta | `LlmTextDeltaEvent` |
| Public thinking/summary text | `LlmReasoningDeltaEvent` with truthful summary flag |
| Signature/private reasoning bytes | State accumulator only; never a reasoning delta |
| Tool starts / JSON argument fragments / completed call | Existing tool start, arguments delta and completed events |
| Complete structured value | Existing final result plus local schema validation |
| Natural stop (`STOP`, `end_turn`, applicable `stop_sequence`) | Completed, unless valid tool calls require host action |
| Complete tool request / `tool_use` | RequiresAction with validated calls |
| Output limit (`MAX_TOKENS`, `max_tokens`) | Incomplete; do not execute a truncated call |
| Explicit safety refusal | Refused with sanitized category |
| Malformed function call, stream error or premature EOF | Failure; partial output is not success |
| Unknown terminal reason | Unknown, retaining a safe reason; no invented natural completion |

Some Gemini argument streams carry structured partial updates rather than appendable JSON text.
Accumulate those internally; emit a valid complete argument string at completion when incremental
translation would misrepresent the public delta contract. Set `SupportsArgumentStreaming` truthfully.
Claude's `input_json_delta` can map directly after call-index association is established.

Emit exactly one terminal result on normal completion. Wait for the protocol's completed message
boundary and usage trailers. Never sum repeated cumulative usage snapshots. Cancellation propagates
as cancellation through the existing service/runtime contract, disposes the response and stops reads.
It cannot promise reversal of cloud computation already accepted. No automatic inference replay after
partial output, tool execution, credential refresh failure or ambiguous transport failure.

Map authentication/permission/model-unavailable/invalid-request/rate-limit/provider-failure separately
where existing error contracts permit. Retain safe HTTP status/request ID diagnostics. Do not expose
raw provider error bodies or tokens. Unknown finish reasons and HTTP 200 stream errors have fixtures.

## 9. Agent capability declaration

Reuse `ManagedAgentSession`: host context ownership, structured import, capture/replacement/status,
host compaction handoff, safe-boundary steering, interrupt and host tool execution. Per-turn
instructions reach the selected route. Rich-result support is the same as the selected chat profile.
Context capture preserves continuation envelopes and follows existing attachment inclusion rules.

Declare native resume/fork, provider-owned compaction and runtime resource observation unsupported.
Do not advertise native server sessions because a Cloud agent product exists. Session continuation
uses the settled host context and retained model state. Pin identity for a run; changing auth scope or
project/location fails before another model step. Host tools already executed are never rerun to
recover an ambiguous provider failure. Account usage remains unsupported until separately designed.

## 10. Affected files and migration units

Paths below are relative to the repository root. These are planned edits, not new source stubs.

| Unit | Source areas | Required acceptance |
| --- | --- | --- |
| 1. Shared contracts | `Common/src/Darkspyre.Inference.Common/LLM/`, Common tests | Portable state, tool metadata, usage and model evidence round-trip; defaults and rejection rules tested |
| 2. Existing consumers | Connect `LlmChat.cs`, `LlmAgentContext.cs`, `ConnectLlmInference.cs`, codecs, managed runtime, existing adapters; Tensor LLM; tests/examples/docs | One state carrier; existing Responses/Anthropic/Bedrock behavior retained; Tensor handles foreign state explicitly |
| 3. Google setup/auth | New Vertex assembly/tests; Connect connection/credential/settings; central dependency file; solution and packaging membership | ADC and service-account doubles, token refresh/scope changes, safe endpoint construction, setup Save conflicts |
| 4. Protocol routes | Vertex Gemini/Claude codecs and clients, owned profiles/catalog | Both routes independently pass request/stream/state/error fixtures |
| 5. Managed integration | Vertex agent composition and existing runtime tests | Multi-step tools, capture/import, compaction, steering, cancellation and credential changes |
| 6. Harness and handoff | `Connect/tools/Darkspyre.Inference.Connect.ConsoleTester`, README and consumer handoff docs | One provider setup; both publishers selectable; explicit live tests report pass/fail/skip |

Units 1–2 form one complete contract migration for all in-repository consumers. Do not merge a
half-migrated contract. Confirm affected external consumers and capture compatibility before release.
Implementation verification covers all affected product solutions, not only the Vertex project.
No pack/publish action is part of technical design.

### Mandatory migration of every connector

The user explicitly requires every connector to utilize the revised contracts. DSA-61 owns this
library-wide migration; it is not satisfied by updating Vertex or by making other adapters compile.
Every LLM transport must populate supported fields, validate supplied state and controls, and
declare unsupported behavior truthfully. An implementation with no opaque continuation returns null
and applies the explicit reject/omit policy to foreign state; it must not manufacture state or
silently drop it. Optional usage details remain null when unavailable.

| Existing transport / proposed route | Migration and regression obligation |
| --- | --- |
| OpenAI Responses | Move ordered assistant/reasoning/call state to the shared envelope; preserve replay, explicit tool Strict values, capability and usage semantics |
| OpenAI-compatible Chat | Use the shared message/result/capability shapes; preserve non-strict omission; explicitly handle unsupported continuation rather than dropping it |
| Anthropic Messages | Move signed thinking/redacted thinking and tool history to the shared envelope; preserve non-strict omission and existing authentication |
| Direct Codex | Migrate Responses state plus managed-agent capture/import, accounting, scope binding and model metadata |
| Bedrock Converse | Migrate current counts-only state and managed-agent accounting without falsely declaring native replay; preserve ordinary Nova tool requests |
| Antigravity CLI | Update projections/capability/usage reporting and explicit unsupported-state handling; retain external CLI authentication and actual runtime limits |
| Claude CLI | Cover chat and native agent paths, opening-context conversion and retained history; keep native resume/fork/resources separate from per-message state |
| Bedrock AgentCore | Cover chat and native agent projections, opening context, usage and capability reporting; preserve its native session ownership |
| SelfHost | Update `SelfHostLlmClient.ToCommon` and `ToConnect` in both directions, including messages, results, omission policy/count, capabilities, usage and model evidence |
| Apple Foundation Models | Update managed process result/projection and agent paths; retain structured snapshots, Unknown completion and cancellation behavior; no fabricated opaque replay |
| Vertex Gemini and Claude | Each route implements the shared state/control/usage contract and independently passes the mapping acceptance cases |

Also migrate `ConnectLlmInference`, provider/session services, state projection and codecs, context
capture/serialization, caches, console harness, examples and test doubles. Trace the SelfHost route
into Tensor's Common LLM implementations, including Qwen. Engine-produced continuation state must
retain its owning implementation identity across the SelfHost bridge; a transport wrapper must not
claim ownership of the engine's opaque bytes. Foreign state is rejected or explicitly omitted there.

Use a parameterized contract suite against the registry, with a coverage assertion that every
registered chat and agent adapter is represented. Include optional registrations (Bedrock/AgentCore
and SelfHost), not only the default `AddLlmConnect` composition. For each route, verify supported
round trips or explicit unsupported handling, accurate metadata, unchanged non-strict defaults,
usage totals, failure/cancellation and existing native-versus-managed ownership. Keep route-specific
wire fixtures; a shared mock implementation is not evidence that every adapter was migrated.

### Host and shell responsibility

[DSA-68: Adopt Connect contract changes across the host and shell](https://darkspyre.atlassian.net/browse/DSA-68)
tracks the consumer work separately. DSA-61's finalized contract and implementation block completion
of that migration. Host design/inventory can proceed while this design is open.

The host ticket owns package/plugin adoption, transcript and capture persistence, serialization
compatibility, compaction/provider-switch handling, capability-driven tool controls, normalized
usage display, provider/model discovery, and Google setup/secret-store commit projection. Its
acceptance cases cover retained state across save/reload and tool turns, explicit omission, ordinary
non-strict tools, unknown usage/access, native sessions and credential-store concurrency.
Consumer-specific source changes do not belong inside the Connect adapter. Both tickets must record
their consumer inventory and release sequencing before a breaking package reaches an incompatible
host. No package version is selected by this mapping.

### Additional provider features after migration

[DSA-69](https://darkspyre.atlassian.net/browse/DSA-69) tracks additional provider features enabled by
the revised contract and adapter-owned codecs. DSA-61 blocks its implementation completion;
DSA-68 owns any host/shell integration. Mandatory migration behavior stays in DSA-61.

The first concrete feature is **Bedrock Converse reasoning with signed continuation**. The current
adapter forces `ReasoningLevels` empty, maps text/tool-use blocks only, and retains counts-only state.
AWS exposes reasoning content and requires signed reasoning/history to be retained for subsequent
turns ([content blocks](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_ContentBlock.html),
[conversation protocol](https://docs.aws.amazon.com/bedrock/latest/userguide/conversation-inference.html)).
The owning-codec change removes the central codec's counts-only restriction and the portable envelope
can carry the resulting state. The feature still requires model-specific request mapping, signed
block replay, visible reasoning projection and tool-turn qualification. Signed-history constraints
must be reconciled explicitly with compaction; changing prior messages can invalidate signatures.

For each further feature, DSA-69 must identify the provider/model, missing behavior, previous contract
limitation, exact enabling change, official protocol evidence, adapter work, host impact and tests.
Do not equate a new capability field with implemented functionality. Preserve ordinary non-strict
tools throughout. Features already representable by current contracts are separate adapter gaps.

| Finding | Owner/disposition |
| --- | --- |
| Existing Responses/Codex/Anthropic state lost at portable boundaries | Required DSA-61 migration |
| Existing providers exposing reported cache/reasoning usage details | Required DSA-61 mapping; DSA-68 host display |
| Bedrock reasoning and signed assistant-history replay | Additional feature design in DSA-69 |
| Direct Anthropic native structured output currently disabled | Existing contract already represents it; not classified as unlocked by this change |
| Host streaming transcript for plain API connections | Existing DSA-17; no duplicate scope |
| Explicit cache placement/TTL, native cloud runtimes and billing | Not implied by continuation or usage reporting; require separate scoped design |

## 11. Qualification requirements

| Area | Qualification evidence |
| --- | --- |
| Model catalog | Exact endpoint/version, auth requirements, pagination, regional applicability, and catalog/access distinction |
| Shared model families | Gemini text and Claude IDs, truthful shared capabilities/limits, native version suffixes and provider regional errors |
| Reasoning mapping | Supported neutral levels mapped to valid native values; temperature/output/tool-choice combinations verified |
| Ordinary tool schemas | Actual provider declaration requirements; optional properties/open objects preserved; omission of unsupported strict fields |
| Opt-in strict tools and structured replies | Separate supported subsets/guarantees, mixed strict/non-strict tools, Auto semantics and schema-plus-tools combinations |
| Parallel control | Whether Gemini can enforce model-call cardinality on the selected API; capability false and explicit rejection if it cannot |
| Rich results | Images/PDFs/text/structured/error outputs through each native tool-result representation |
| Scope identity | Stable refresh behavior and detectable ADC principal changes without retaining or logging tokens |
| Accounting | Normalized usage formulas and whether remote preflight measurement belongs in this implementation |
| Public migration | Final continuation names/shape, serialized capture version, external consumer impact and release classification |

Deterministic fixtures must cover: both routing paths; malformed IDs; wrong project/location/scope;
expired credentials; refresh races; cancellation during refresh; concurrent configuration edits;
signed ordinary turns and signed tool turns; same-name parallel calls; invalid/duplicate tool IDs;
altered visible state; unsupported schemas; partial/truncated arguments; missing finish; usage trailers;
HTTP 200 errors; refusal versus token limit; no leaked secrets; and portable-interface round trips.

The strictness regression matrix must cover both publishers through chat, portable chat and managed
sessions: default false; optional `timeout` with required `command`; absent `additionalProperties`;
unchanged schema meaning; Claude's absent false flag; Gemini's absence of an invented strict field;
Auto remaining Auto; Required without explicit Strict; supported explicit true and its unsupported
failure; mixed strict/non-strict tools; structured output leaving tools non-strict; and invalid returned
arguments causing no handler execution. Ordinary tool support must pass independently of strict support.

Live qualification is explicit and bounded: selected shared families and express API-key mode, text, schema, image/PDF,
two-step tool continuation, reasoning state, cancellation and managed capture/import. Normal tests use
doubles and no paid calls. Missing credentials produce a visible skip, never a passing live test.
Use the existing ConsoleTester; a separate prototype is justified only for an unresolved protocol
behavior it cannot isolate. Live qualification remains a release-readiness follow-up; deterministic fixtures do not certify provider access or live protocol behavior.

## 12. Sources and local anchors

Official documentation inspected for this mapping (2026-09-28):

- [S1: Google Application Default Credentials](https://docs.cloud.google.com/docs/authentication/application-default-credentials).
- [S2: Claude on Google Cloud](https://platform.claude.com/docs/en/build-with-claude/claude-on-vertex-ai).
- [S3: Gemini generation reference](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/model-reference/inference).
- [S4: Gemini function calling](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/multimodal/function-calling).
- [S5: Gemini structured output](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/multimodal/control-generated-output).
- [S6: Claude structured output](https://platform.claude.com/docs/en/build-with-claude/structured-outputs).
- [S7: Claude PDF support](https://platform.claude.com/docs/en/build-with-claude/pdf-support).
- [S8: Gemini thought signatures](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/thought-signatures).
- [S9: Gemini FunctionDeclaration fields](https://docs.cloud.google.com/gemini-enterprise-agent-platform/reference/rest/Shared.Types/FunctionDeclaration).
- [S11: Publisher model catalog](https://docs.cloud.google.com/gemini-enterprise-agent-platform/reference/rest/v1beta1/publishers.models/list).
- [S12: Gemini 3 Flash profile](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/models/gemini/3-flash).
- [S13: Claude Sonnet 4.6 profile](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/partner-models/claude/sonnet-4-6).
- [S14: Gemini generation schema contract](https://docs.cloud.google.com/dotnet/docs/reference/Google.Cloud.AIPlatform.V1/latest/Google.Cloud.AIPlatform.V1.GenerationConfig).
- [S15: Bedrock cache usage accounting](https://docs.aws.amazon.com/us_en/bedrock/latest/userguide/prompt-caching.html).
- [S10: Claude optional tool definition properties](https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-reference).
- [S16: Vertex express mode](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/start/express-mode/overview).
- [S17: Publisher catalog fields](https://docs.cloud.google.com/gemini-enterprise-agent-platform/reference/rest/v1beta1/publishers.models).

Repository baseline: [managed-agent design](enhancements.md),
[Connect integration contract](../README.md),
[portable LLM contracts](../../Common/src/Darkspyre.Inference.Common/LLM/LlmContracts.cs),
[portable bridge](../src/LLM/Darkspyre.Inference.Connect.LLM/ConnectLlmInference.cs),
[current state codec](../src/LLM/Darkspyre.Inference.Connect.LLM/Internal/ProviderContinuationStateCodec.cs),
and [capability declarations](../src/LLM/Darkspyre.Inference.Connect.LLM/LlmProviderCapabilities.cs).
The existing [non-strict tool decision](enhancements.md#one-non-strict-default-for-tool-definitions)
and [managed tool regression tests](../tests/LLM/Darkspyre.Inference.Connect.LLM.Tests/ManagedAgentTests.cs)
govern the tool-schema mapping above.
