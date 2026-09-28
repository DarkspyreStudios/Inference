# Google Vertex AI connector: technical mapping

**Ticket:** [DSA-61](https://darkspyre.atlassian.net/browse/DSA-61), under DSA-57.  
**Date:** 2026-09-28. **Phase:** technical design, first mapping draft.  
**Authorization:** contract changes required for parity are in scope. Implementation has not
been authorized. Names and signatures below are proposals, not shipped APIs.

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

| Component | Proposed responsibility |
| --- | --- |
| Common LLM contracts | Portable continuation state and capability/usage additions shared by Connect and Tensor |
| `Darkspyre.Inference.Connect.LLM` | Existing services, setup integration, managed session runtime and neutral contracts |
| New `Darkspyre.Inference.Connect.LLM.Vertex` assembly | Google authentication, endpoint validation, model catalog, Gemini and Claude wire adapters |
| `VertexLlmClient : ILlmClient` | One provider definition, model routing, capabilities, listing and streaming |
| `ManagedVertexAgentClient : ILlmAgentClient` | Compose the existing managed runtime with the Vertex client |
| Internal Gemini/Claude protocol components | Request projection, stream parsing, continuation codecs and error translation |
| Existing ConsoleTester | Setup/discovery and explicit live qualification for both routes |

Proposed provider ID is `google-vertex-ai`; add `LlmTransport.GoogleVertexAi` without renumbering
existing values. Keep four NuGets. Include the new assembly in Connect and place any Google auth
SDK dependency only in that assembly, with its version in `Directory.Packages.props`.
Use owned HTTP protocol adapters and Google's credential library; no Google SDK types cross the seam.
The exact dependency version is an implementation prerequisite, subject to the vulnerability audit.

Reuse the managed-runtime access pattern already used by the Bedrock assembly. Extract genuinely
shared Anthropic message/block handling into one internal implementation where behavior matches;
retain separate authentication, URL construction, version fields and capability profiles. Do not
route through the direct Anthropic client by fabricating an API-key connection.

## 3. Connection, credentials and setup

Propose a typed optional `LlmConnection.Vertex` configuration containing `ProjectId`, `Location`
and `AuthenticationMode` (`ApplicationDefault` or `ServiceAccount`). These belong to the owning
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
disable redirects that could forward credentials. Location eligibility belongs to each model profile.
No automatic regional fallback, model substitution, impersonation or Cloud resource creation.

Provider-owned setup steps:

1. Select ADC or explicit service-account authentication; explain the external prerequisites.
2. Enter project and location. No permission or residency assumptions from a default location.
3. Resolve credentials and validate configuration without running inference.
4. Discover/select a model, showing catalog versus verified-access status.
5. Offer an explicit metered connection test, with its side effect declared in setup metadata.
6. Save through the existing typed commit target and compare-and-swap contract.

Extend `LlmSettings`, its draft/change projection and manager for these fields. The provider supplies
its `SetupDefinition`; ConsoleTester and UI.Settings consume it. Do not create a second setup flow.
Authentication success, catalog access and inference permission are separate reported results.

## 4. Model identity and discovery

Use canonical request IDs `google/<native-model-id>` and `anthropic/<native-model-id>`. Preserve
native version suffixes. The parser accepts exactly those publisher namespaces and validated model
segments; callers cannot inject project/location/operation paths through a model ID.

Each adapter-owned model profile records native ID, protocol, endpoint eligibility, context/output
limits, capabilities, supported MIME types, schema rules, reasoning mapping and known parameter
combination restrictions. Unknown values stay unknown. Do not infer capabilities solely from a
name prefix or treat every future Gemini/Claude model as equivalent.

Propose optional neutral model metadata on `LlmModel` and `LlmModelInfo`:

- `Availability`: `Unknown`, `Available` or `Unavailable` for the current connection.
- `AvailabilityCheckedAt`: timestamp for that evidence, if any.
- `AvailabilityReason`: sanitized explanation, never a raw provider response.

`Unknown` remains selectable; only an operation or authoritative access check can verify access.
An inaccessible catalog must not be returned as an empty successful list. Cache identity includes
project, location and auth scope, with explicit refresh through the existing service.

**Discovery remains a qualification item:** select the supported Google publisher-catalog endpoint,
its version and IAM requirements, then reconcile its metadata with owned capability profiles.
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
| Maximum output | `generationConfig.maxOutputTokens` | `max_tokens`; resolve a valid model-specific value when omitted |
| Reasoning level | Profile maps to thinking level or budget | Profile maps to thinking mode/budget and applicable effort |
| Tool declarations | `tools[].functionDeclarations` | `tools`, including input schema |
| Tool choice Auto/None | `AUTO`/`NONE` | `auto`/`none`, subject to model support |
| Tool choice Required/Specific | `ANY`; restrict allowed function names for Specific | `any`/`tool` plus name |
| Parallel-call control | Map only where the selected protocol can enforce it | Map to supported `disable_parallel_tool_use` setting |
| Previous calls | `functionCall` parts in assistant content | `tool_use` blocks |
| Tool results | `functionResponse`, with original call association | `tool_result`, preserving `tool_use_id` and `is_error` |
| Schema output | JSON MIME type plus supported schema field | Supported `output_config.format` JSON-schema configuration |
| Image/document bytes | Supported inline data parts | Supported base64 image/document blocks |
| Conversation key | Host affinity only | Host affinity only |

Do not flatten signed assistant output into visible text/calls. Preserve sequence and per-part
association through the continuation envelope in section 6. Keep original wire call IDs; if Gemini
does not supply one, allocate a stable Connect call ID and retain its wire association in state.
Repeated calls with the same function name remain distinct. Reject ambiguous imported tool histories.

`Strict` requests provider-enforced adherence. Gemini's available function modes and schema subsets
must be checked together; forcing a call to obtain schema enforcement changes Auto semantics and
is not an acceptable mapping. Unsupported strict/choice/reasoning combinations fail before dispatch.
Likewise, executing tools sequentially in our host does not enforce a request that the model emit
only one call. No silent removal of `AllowParallelToolCalls = false` [S4].

Validate output and tool schemas without deleting constraints. JSON-object fallback must actually
enforce the promised shape; JSON MIME type alone is not sufficient. Respect the existing explicit
fallback policy and report the actual enforcement. Tools-plus-schema and thinking-plus-forced-tools
must be qualified as combinations, not inferred from individual capability booleans [S3, S5, S6].

For rich results, preserve text order, structured content and error semantics. Use the existing
`LlmRichContentSupported` projection rules for unsupported parts, including omission reporting.
Advertise native image/document tool results only after qualification. Resource URIs are references;
Connect does not fetch them. Claude document support on Google Cloud requires base64 sources [S7].
MIME types, payload limits and attachment completeness are validated before dispatch.

## 6. Required continuation contract change

Current `LlmChatMessage`/`LlmChatResult` carry `LlmProviderResponseState`; Common's `LlmMessage`
and `LlmInferenceResult` do not. `ConnectLlmInference` consequently cannot round-trip it. Gemini
thought signatures make this a correctness requirement for applicable tool continuations [S8].

**Proposal C1:** replace the Connect-only carrier with one Common semantic carrier,
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

Refactor the current central `ProviderResponseStateCodec`, which assumes Responses/Anthropic output
shapes and derives scope from generic connection fields. Each owning route validates its own payload
and supplies its binding; shared runtime code works on the neutral envelope. Google project/location
and principal must participate in binding. Do not add Gemini JSON parsing to host/session code.

This is a public contract migration. Inventory exported types, serialized context captures and external
consumer handoffs before implementation. No second internal state mechanism or implicit conversation
cache. Decide the published-contract compatibility policy explicitly; a breaking coordinated release
and capture-format revision may be required. This document does not authorize a release.

## 7. Other contract deltas

**C2 — Tool controls:** add a shared `LlmToolCapabilities` descriptor to Common and Connect model
capabilities. Proposed members: supported `ChoiceModes`, `SupportsStrictInputSchema`,
`SupportsParallelCalls`, `SupportsParallelCallControl`, and `SupportsArgumentStreaming`.
Carry this descriptor through `ConnectLlmInference`. `SupportsToolUse` continues to mean that calls
and results can round-trip; it does not promise every control. Profiles additionally validate
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
Do not present a token-count estimate as a provider admission guarantee. This choice remains open.

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

## 11. Qualification and design completion

| Open item | Evidence required to close it |
| --- | --- |
| Model catalog | Exact endpoint/version, auth requirements, pagination, regional applicability, and catalog/access distinction |
| Initial supported profiles | Explicit Gemini and Claude IDs with documented limits and endpoint availability; fixtures per profile |
| Reasoning mapping | Supported neutral levels mapped to valid native values; temperature/output/tool-choice combinations verified |
| Strict schemas | Supported keywords, rejection/fallback behavior, Auto semantics, schema-plus-tools combinations |
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

Live qualification is explicit and bounded: one selected model per publisher, text, schema, image/PDF,
two-step tool continuation, reasoning state, cancellation and managed capture/import. Normal tests use
doubles and no paid calls. Missing credentials produce a visible skip, never a passing live test.
Use the existing ConsoleTester; a separate prototype is justified only for an unresolved protocol
behavior it cannot isolate. This design remains open until the table above is resolved and reviewed.

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

Repository baseline: [managed-agent design](enhancements.md),
[Connect integration contract](../README.md),
[portable LLM contracts](../../Common/src/Darkspyre.Inference.Common/LLM/LlmContracts.cs),
[portable bridge](../src/LLM/Darkspyre.Inference.Connect.LLM/ConnectLlmInference.cs),
[current state codec](../src/LLM/Darkspyre.Inference.Connect.LLM/Internal/ProviderResponseStateCodec.cs),
and [capability declarations](../src/LLM/Darkspyre.Inference.Connect.LLM/LlmProviderCapabilities.cs).
