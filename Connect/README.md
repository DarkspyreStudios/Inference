# Darkspyre.Inference.Connect

## Phase-1 build

The conversation libraries are independently consumable:

| Package | Contents |
|---|---|
| `Darkspyre.Inference.Connect.Common` | Universal conversation and embedding contracts, facades, shared protocol code and discovery |
| `Darkspyre.Inference.Connect.Codex` | Native Codex client and authentication |
| `Darkspyre.Inference.Connect.Claude` | Official Claude CLI conversations and authentication |
| `Darkspyre.Inference.Connect.Antigravity` | Official Antigravity CLI conversations, native resume, host tools and account usage |
| `Darkspyre.Inference.Connect.Grok` | Grok (xAI) Responses conversations on xAI's subscription proxy, with a Grok subscription's device sign-in, hosted search and account usage |
| `Darkspyre.Inference.Connect.HostTools` | The host-tools local service, bundled under `hosttools/service`, its client, and the `darkspyre-host-tools-mcp` program; source-only, built with Darkspyre.Common's local-service libraries |
| `Darkspyre.Inference.Connect.OpenAiApi` | OpenAI Responses conversations and text embeddings |
| `Darkspyre.Inference.Connect.SelfHost` | Conversations and text/image embeddings through caller-owned engines |

Each provider depends on the exact matching Connect.Common version. Common depends on
Inference.Common. Providers reference neither another provider nor a Tensor implementation.
The public `Darkspyre.Inference.Connect.LLM` namespace remains the universal contract.
The `Darkspyre.Inference.Connect` package contains the existing Image, TTS and Audio assemblies
and depends on Common; it includes no conversation provider.

Applications reference the provider libraries they use, then call `services.AddLlmConnect()` once
per service collection. Common inspects `Darkspyre.Inference.Connect.*.dll` beside its own assembly
and invokes each library's `LlmConnectProviderAttribute`. Discovery loads candidates through
Common's own assembly load context, including providers not previously loaded. Registration holds
no process-global assembly registry, so a collectible plugin generation can retire with its container.
Common alone registers no provider. The catalog lists only the discovered provider definitions.

Discovery registers services without reading credentials, calling providers or loading engines.
The caller supplies `ILlmConnectionSource`. Including SelfHost also requires caller-owned
`ILlmSelfHost` when resolving its clients. Connect borrows those engines through Common contracts;
the caller owns their loading and disposal. Codex, OpenAiApi, Grok and SelfHost use the shared managed
conversation implementation. Claude and Antigravity use their native universal conversation implementations.

Every library is one managed artifact for Mac, Linux and Windows. Existing runtime OS handling
stays inside the libraries. Discovery requires deployed assembly files. A deployment without a
Common assembly location fails with `Connect discovery requires its libraries to be deployed as
assembly files.` Filesystem, loading and registration failures surface during composition.
Provider package buildTransitive targets and source-mode targets preserve provider assemblies for
trimming. This rooting does not establish full application trimming or NativeAOT compatibility.

Every other LLM provider is out of the build. `Darkspyre.Inference.Connect.Common.csproj` excludes the
Anthropic API, Apple, OpenAI-compatible chat and Gemini API files with
`Compile Remove`. The Image project excludes the xAI image transport and the TTS project
excludes the xAI speech transport. The Bedrock, Vertex, Azure, STT, Realtime, Video, Resources and examples
projects are out of the solution, the `Darkspyre.Inference.Connect` package and the source references in
`eng/SourceReferences.targets`. The excluded source stays in the repository under the L2 exception in
`Inference/AGENTS.md`. Sections of this page that describe an excluded provider, transport or project
describe that kept source, which this build does not compile.

Stored data keeps its shape. `LlmConnection.Xai`, `LlmTransport.XaiResponses`, `XaiConnectionOptions`,
`XaiMultiAgentControls`, `ImageTransport.XaiImagine` and `TtsTransport.XaiTts` stay, so a saved connection or setting
that names them still reads. The Grok library serves `XaiResponses`. No adapter is registered for the image and
speech transports, so a request through one fails with the existing missing-client refusal.

The phase-1 build removes these public surfaces: `ILlmFileClient`, `IXaiFilePublicUrlClient`,
`IXaiSkillClient`, `IDocumentSearchClient` and their request and result types; `LlmProviderCapabilities.Skills`;
`LlmProviderService.CreatePublicUrlAsync` and `RevokePublicUrlAsync`; `AddLlmConnectXai`;
`LlmEmbeddingProviderService.ListModelsAsync` and its `providerService` constructor parameter; the `xai`
parameters of `IImageTransport`, `ImageProviderService` and `ITtsProvider`; `ImageConnection.Xai`,
`ImageSetupConfiguration.Xai`, `TtsConnection.Xai`, `TtsConnection.XaiDefaults`, `TtsRequest.Xai`,
`TtsStreamOptions.Xai` and `XaiTtsOptions`. `LlmProviderCapabilities.Files` always reports no Files operation.

The tokenizer facade (`LlmProviderService.TokenizeAsync`, `ILlmTextTokenizer`) supports the built Responses
adapter when `LlmConnection.LlamaCppTokenizationEndpoint` explicitly selects a native llama.cpp tokenizer.
An unset endpoint and other built adapters retain `This route does not implement plain-text tokenization.`
The context-cache facade (`BindContextCacheAsync`, `ILlmContextCacheClient`) stays compiled with no implementing
provider. Calls reach `No explicit context cache adapter is implemented.`

The console tester has no `prompt --request` option, and `claude-probe` runs only its `cancel` case.

## Conversation contract

`LlmAgentProviderService` and `ILlmAgentSession` are the host's one LLM conversation surface.
`LlmProviderService` keeps connections, sign-in, settings, model listing and capabilities. A one-off request is
a session with one turn: start it with the messages in `LlmAgentSessionOptions.Context`, run one turn with empty
`Input`, and dispose it.

The chat lane is internal adapter plumbing: `LlmChatRequest`, `LlmChatResult`, `LlmChatCompletionEvent`,
`ILlmClient`, `ILlmAgentClient`, `LlmProviderService.StreamAsync`, `CompleteAsync` and `Bind`, the
tool-output projection types and `LlmRichContentSupported`. `LlmChatMessage` stays public because hosts store
it. The constructors of `LlmProviderService`, `LlmAgentProviderService` and `LlmConnectionAccess` are internal;
`AddLlmConnect` registers them with factories.

These public surfaces are deleted: `LlmProviderService.MeasureRequestAsync`, `CountInputTokensAsync` and
`ProjectToolOutputAsync`; `ILlmProviderTokenCounter` and `LlmProviderInputTokenCount`; `LlmToolOutputLane`;
`ConnectLlmJson.ChatRequest` and `ChatResult`; `LlmAgentProviderService.ForkSessionAsync` and
`ILlmAgentForkClient`; `LlmAgentSessionOptions.UnboundContinuationState`;
`ProviderControls` on `LlmAgentTurnRequest` and `LlmChatRequest`; `Strict` on `LlmToolDefinition` and
`LlmAgentToolDefinition`; `Strict` and `Fallback` on `LlmStructuredOutputRequest`, `LlmStructuredOutputFallback`,
`LlmToolCapabilities.StrictInputSchema` and `LlmStrictInputSchemaSupport`; and the agent mechanics
`ContextOwnership`, `NativeFork`, `CompactionOwnership`, `ToolExecutionOwnership`, `Steering` and
`SupportedCallerServices` with their enums `LlmAgentOwnership`, `LlmAgentNativeFork`,
`LlmAgentToolExecutionOwnership`, `LlmAgentSteering` and `LlmAgentCallerServiceKind`.
`LlmAgentCapabilities.SupportsSteering` replaces `Steering`.

**Turn settings.** `LlmAgentTurnRequest` carries `Instructions`, `Input` (may be empty), `Attachments`, `ModelId`,
`ReasoningLevel`, `ServiceTier`, `AgentCount`, `OutputSchema`, `Temperature`, `MaxOutputTokens` and `ToolChoice`. A managed session applies
`ToolChoice` to the turn's first model step and `Auto` after it. Empty `Input` appends no user message: the turn
sends the context as it is, and with an empty context the turn completes at once as `Completed`. A Claude turn
with empty `Input` and no attachments sends the opening context alone, or completes at once when there is none.
Claude puts nonempty turn `Instructions` before the turn input as `Instructions for this turn:\n{text}\n\n`, and
sends no tool choice. Claude drops an effort when the model reports no effort control or no offered effort maps.
Antigravity puts nonempty turn `Instructions` before the turn input the same way, turns an image URL in `Input` into
`[Image: {url}]`, and appends the attachment notice for every attachment, because the CLI takes text only. A turn with
empty `Input` and nothing to introduce completes at once as `Completed` without writing to the CLI.

**Values the model cannot use.** Every request passes one normalizer before its client runs, and a managed
session normalizes each request it builds, so its measure sees what is sent. A dropped setting is not reported.

- Reasoning goes to the offered level closest to the request: the highest offered level at or below it, else the
  lowest offered level above it. An empty offered set sends the requested level.
- A sampling value the model does not support, or one out of range (TopP in (0,1], TopK > 0, Seed >= 0, all
  finite), is not sent. A connection or request provider option named as a standard sampling field is skipped.
- Empty stop strings are dropped and the first `MaxStopSequences` are sent. An output cap <= 0, or one the route
  cannot enforce, is not sent. A hosted-turn limit <= 0, or one on a route with no hosted tools, is not sent.
- Tool choice and parallel control on a request without tools are not sent. A Specific choice naming no request
  tool becomes Auto. The first definition of a duplicated tool name is sent. A message holding both calls and
  results is sent as two messages, calls first.
- An attachment the model or route cannot take is left out of the outgoing request only; the stored history is
  unchanged. This notice is appended to that message's text:
  `[Attachment {i} ({file name}): {reason} This content was not supplied to the model.]`. An attachment on a
  non-user message moves to the next user message, or is left out with the notice when none follows. A resource
  reference with no resource access gets the reason `No resource access was supplied.`
- A managed context is repaired in the copy that is sent: a duplicate entry id keeps the first entry, a duplicate
  call id keeps the first call, a result with no call is dropped, roles keep their recorded order, and function
  calls left unsettled get the error result `The tool was not run; no result is available.`
- An image URL in managed `Input` becomes the text `[Image: {url}]`. A deferred tool is presented with the others.
- Structured output is checked locally when the schema root is not an object or the route has no JSON format.
- Claude chat drops continuation state and prepares caller-owned resources inline; a prepared file its lane cannot
  carry gets the attachment notice. A Claude turn attachment the model cannot take gets the same notice.

A refusal remains only for an operation the conversation marks Unsupported, and it names the feature: resume on
Codex (`{transport} agent sessions cannot resume a provider thread.`); a hosted tool kind the route does
not implement (`This route does not implement X search.`); context capture, replacement or compaction handoff where unsupported; tool use on a model without
it; and an opening on a model that marks agent sessions or agent tool use Unsupported
(`The selected model does not support agent sessions.` or `... agent tool use.`).

**Tools.** A session's tool names are logical names, unique within the session. Codex and the OpenAI API send a
name that matches
`^[a-zA-Z0-9_-]{1,64}$` unchanged and any other name as its alias: `t_` followed by the first 40 lowercase hex
characters of SHA-256 over the UTF-8 name. The alias applies to the tool list, a Specific tool choice and the calls in
history, and returned calls carry the logical name again. Continuation state that stored a call under its alias still
matches the logical call. A tool goes out as `{"type":"function","name","description","parameters","strict":false}`.

An agent session's tool schema goes to the model without any `additionalProperties` keyword, at any depth
(`ToolSchemas.Sent`): in managed-session requests, in the MCP tool list of the Claude CLI and of Antigravity's host
tools, and through the MCP tool bridge. Nothing else in the schema changes. The arguments are still checked against
the schema the caller gave, so the change alters no check.

Before a handler runs, the arguments are checked against the tool's schema without downloading anything. Members the
schema does not declare are ignored at every depth: each `additionalProperties` or `unevaluatedProperties` of `false`
is left out of the check, so a `oneOf` or `anyOf` branch matches arguments that carry another branch's members. The
handler receives the arguments as the model sent them. Arguments that
fail the check go back to the model as the error result `Tool '{name}' arguments were rejected:` followed by one
`{location}: {message}` line per error, the handler does not run and the turn continues. A schema that cannot be
checked lets the handler run. The tool event records the model's arguments as JSON when they parse, otherwise as a
JSON string. Stored arguments may be any string; continuation state whose calls do not match the message, or whose
arguments do not parse, is left out and counted. A handler failure reaches the model and the tool event as
`{exception type}: {message} External outcome is unknown. Do not automatically replay.`, the message with the
connection's credential and header values removed.

With `ToolHandling = ReturnRequests` a Codex session opens without a handler. When a step returns calls, the context
keeps the assistant text only, each call is reported as an `LlmAgentToolEvent` with status `returned` (no success
value, no output) or, when its arguments fail the check, status `failed` with the rejection text, and the turn ends
with `ToolRequests`. No handler runs and no further step is sent. A caller with results starts a new session with
them in its context.

Codex prepares caller-owned resources inline, so a context with resources opens when the caller supplies resource
access.

On Claude the MCP bridge applies the same rules. A name outside the MCP rule `[A-Za-z0-9_.-]{1,128}` is sent as
its alias in `--allowedTools`, the MCP tool list and calls, and every event carries the logical name. The CLI offers
the model each bridge tool as `mcp__darkspyre-inference-connect__<name>`. History carried into a new CLI session as
opening context names each call to a bridge tool that way, so the model sees its past calls under the names it can
call; a call to any other tool keeps its logical name. The first definition of a duplicated name is used. A schema whose root is not an object is listed as
`{"type":"object","properties":{"value":<schema>},"required":["value"]}`, and `value` is checked and handed to the
handler. The bridge holds one lock per session around each call to a tool with `RequiresSequentialExecution`, so two
such calls never overlap. With tools and no handler, each call gets the error result
`No handler is available for tool '{name}'.` A tool-result resource part the MCP route cannot carry is left out
with the tool-output notice.

**Structured answers.** On Codex a schema with an object root goes out as the `json_schema` format named
`agent_output` with `"strict": false`. Any other root, or a route without a JSON-schema format, gets this line in the
instructions, followed on the next line by the schema: `Return the final answer as exactly one JSON value that
satisfies this JSON Schema, with no Markdown fence or other text.` The final answer is checked locally and the result
is `LlmAgentTurnCompletedEvent.StructuredOutput`; the completion reason does not change. A refusal or truncation keeps
its reason and reports `IsValid = false` with `The answer was not evaluated: {reason}.` No second request is sent.
Claude appends the guidance to the turn input for every root and checks the turn's final result text the same way.

**Continuing, forking and rewinding.** Codex continues, forks and rewinds a conversation by starting a new session
with its history through `StartSessionAsync`; the Claude CLI forks and rewinds the same way. Continuation state the
selected model, provider and account cannot replay is left out and counted, so a history from any provider or
connection continues, including one whose connection was removed.

`LlmAgentProviderService.ResumeSessionAsync` routes to the transport's internal `ILlmAgentResumeClient`; Codex has
none and refuses. Antigravity resumes natively: AGY keeps the conversation and reopens it with
`--conversation <id>`; `LlmAgentResumeOptions.Context` is the saved history its conversation measures and compacts
from, and none of it is sent. Claude resumes by the conversation's thread id with `LlmAgentResumeOptions.Context`, the
caller's history, and the session settings `ExecutionOptions`, `ToolHandling`, `SkillFolders` and `FallbackModelIds`:

- When the CLI saved that session for the working folder (`<config folder>/projects/<working folder>/<id>.jsonl`,
  where the config folder is `CLAUDE_CONFIG_DIR` or `~/.claude` and the folder name is the working folder with every
  character other than an ASCII letter or digit replaced by `-`), the first CLI session opens with `--resume=<id>`
  and sends no opening history.
- Otherwise the first CLI session opens under the same id from the supplied history, as a start does.
- Either way the conversation measures and compacts from the supplied history. The caller resumes only when nothing
  changed the history outside Claude's own turns since the session was saved; otherwise it starts from the history.
- A conversation reports `SupportsNativeResume` Unsupported while its live CLI session is not the one its thread id
  names: after a compaction, a reopen after a refused or paused step, or a turn that returned tool requests, and
  for an ephemeral session. The CLI saves every session except an ephemeral one or one that returns tool requests
  (`--no-session-persistence`).

Antigravity also rebuilds a conversation from host history, as Claude does. A fork or a cut history has no provider
thread to reopen, so the host starts a session with that history as `Context`; it reaches the new AGY conversation
once as the opening transcript. Every history attachment AGY cannot carry, resource references included, is replaced
in its own message by the attachment notice, so any history opens.

**Feature list.** `LlmAgentCapabilities` reports public features only. Values:

| Member | Codex | OpenAI API | Claude | Antigravity | Grok |
| --- | --- | --- | --- | --- | --- |
| HostedTools | ImageGeneration | ImageGeneration; on `api.openai.com` also WebSearch, CodeExecution, DocumentSearch, RemoteMcp | none | none | ImageGeneration, WebSearch, SocialSearch, CodeExecution, DocumentSearch, RemoteMcp |
| SupportsShellTools | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported |
| SupportsContextCapture, SupportsContextReplacement | Supported | Supported | Unsupported | Unsupported | Supported |
| SupportsContextStatus | Supported | Supported | Supported | Supported | Supported |
| SupportsCompactionHandoff | Unsupported | Unsupported | Unsupported | Unsupported | Unsupported |
| SupportsNativeResume | Unsupported | Unsupported | Supported | Supported | Unsupported |
| SupportsSteering | Supported | Supported | Supported | Unsupported | Supported |
| SupportsInterrupt | Supported | Supported | Supported | Unsupported | Supported |
| SupportsExternalToolHandoff | Supported | Supported | Unknown | Unsupported | Supported |
| ResourceObservation | Unsupported | Unsupported | Supported | Unsupported | Unsupported |
| SupportsFallbackModels | Unsupported | Unsupported | Unknown | Unsupported | Unsupported |
| SupportsTurnCap | Supported | Supported | Unknown | Unsupported | Supported |
| SupportsCompactNow | Supported | Supported | Supported | Supported | Supported |
| CompactionSide | Host | Host | Host | Host | Host |

`CompactionSide` says which side compacts: `Host` is the library's compaction described below, and `Provider` is the
model service's own. A managed session reports `Provider` for a model whose chat client compacts on the service side.

**The OpenAI API and its endpoint.** An OpenAI API connection has an endpoint, `https://api.openai.com/v1` by
default, and may point at any service that serves the same API, such as a local llama.cpp server or OpenRouter. The
API key is optional: the connection saves without one, counts as authenticated, and sends `Authorization` only when
a key is set. Requests send `store: false` and the whole history every time. The model feature list depends on the
endpoint's host. On `api.openai.com` it comes from the built-in model profiles (`OpenAiModelProfiles`): reasoning
levels, temperature and top-p, prompt-cache options, and the published context window and output limit. On any other
host the profiles are not read, so a local model named like an OpenAI model gets no OpenAI facts: every service
feature is Unknown, the reasoning level set is empty, and the prompt cache reports Unknown. Both list the image tool;
OpenAI's endpoint also lists web search, code execution, document search and remote MCP (see "Hosted search").
The listing is the service's own `/models`; on OpenAI's endpoint a model with no listed window takes its published
window and output limit, and elsewhere the listed values are used as they are.

**Grok.** A Grok connection (`LlmTransport.XaiResponses`, provider id `grok-subscription`, in the Subscription group)
signs in with a Grok subscription through the xAI device code and only that way; it has no API key field. Setup's
`authorize` action (`Sign in with Grok`) starts the device grant at `auth.x.ai` and shows the code and the https
link; its continuation polls at the device interval through the shared device-login seam, five seconds slower after
`slow_down`. The approved token is saved only after `GET https://cli-chat-proxy.grok.com/v1/user` returns a nonempty
`user_id` or `userId`. Other account fields do not refuse sign-in. A failed lookup fails sign-in without saving.
A denied or expired code ends with `Grok sign-in was denied or expired. Start sign-in again.` The check reports
Signed in when the connection holds a usable OAuth token. `LlmConnectionAccess` renews Grok tokens within 300 seconds
of expiry. A 401 before stream output forces renewal; renewal is serialized per connection. A waiting caller
re-reads the saved credential and uses a preceding caller's renewal. Each save checks the stored connection version.
A refused refresh ends with `Grok authorization is no longer valid; sign in again.`.

Every Grok request goes to xAI's subscription proxy, `https://cli-chat-proxy.grok.com/v1`, the host the Grok Build CLI
uses for a Grok sign-in; `api.x.ai` serves API keys only. Each request carries the bearer token and the headers the
proxy's auth middleware reads: `X-XAI-Token-Auth: xai-grok-cli`, `x-authenticateresponse: authenticate-response`,
`x-grok-client-version` (the Grok Build version, `1.0.45`, because the proxy refuses clients it does not know),
`x-grok-client-identifier: darkspyre-agent` and `x-grok-client-mode: headless`. A connection saved with another
endpoint refuses before sending with `This Grok connection uses an old endpoint. Open its settings and save it to move
it to the Grok subscription service.`; opening its settings sets the proxy endpoint. Setup has no region field.

The model list is the proxy's `GET models`, with `x-userid` and `x-email` when the credential has them. Hidden rows
and rows whose `api_backend` or `apiBackend` names a protocol other than `responses` are omitted. A row without a
backend stays. Each row's `context_window` is the model's window. Its `reasoning_efforts` objects are its reasoning
levels, the one marked `default` being the level the proxy uses when a request names none. A model the list does not
name offers no levels. `stream_tool_calls` enables argument streaming for that model and the same request flag;
`supports_backend_search` enables web and X search. `inference_idle_timeout_secs` sets the stream idle timeout,
floored at 10 seconds, with 600 seconds when absent. These three fields also accept `streamToolCalls`,
`supportsBackendSearch` and `inferenceIdleTimeoutSecs`, snake spelling first. Fields of the wrong kind are absent. The proxy lists no multi-agent model, so Grok
offers no agent counts. Grok conversations run on the shared managed session with the feature list above. The proxy
serves no `responses/compact` or `tokenize-text` route (both answer 404), so the host compacts Grok conversations and
the estimate is the local count. The proxy adds framing the request does not show, about 1,400 tokens on `grok-4.7`
and 360 on `grok-4.5`. The first reported step with no hosted calls teaches it, and later estimates add it; the first request after the
process starts is underestimated by that much. Grok's continuation state keeps its output verbatim and
in order: messages, reasoning, function calls, hosted calls (`web_search_call`, `x_search_call`,
`code_interpreter_call`, `code_execution_call`, `file_search_call`, `mcp_call`, `image_generation_call`,
`view_image_call`, `view_x_video_call`, `custom_tool_call`, `tool_search_call`, `tool_search_output`). A
`context_length_exceeded` code in an HTTP error or a stream `error` event is a too-long refusal. The proxy lists no
embedding model, so Grok offers no embeddings.

Grok account usage is the proxy's `GET billing?format=credits`, the read the Grok Build CLI makes for its usage view.
It carries the chat headers and `x-userid`, the account's `user_id` saved at sign-in; the facade refreshes the token
first. Chat replies carry no usage headers, so Grok has no observed usage. `XaiAccountUsageParser` maps the reply's
`config` to one quota window and dollar balances. The window is named by `currentPeriod.type`: `Weekly limit`,
`Monthly limit`, or `Usage limit` when the type is absent or unspecified. Its used fraction is
`creditUsagePercent / 100`, it resets at `currentPeriod.end`, it lasts from `start` to `end`, and it is Exhausted at
100%. The cent amounts (`{"val": n}`) become `usd` balances: `Prepaid credits` from `prepaidBalance`, and, when the
reply has `onDemandEnabled: true`, `On-demand credits` as `onDemandCap` minus `onDemandUsed`, never below zero.
`subscriptionTier`, when present, is the plan. The reply is proto3 JSON, which omits zero values, so an absent
percentage is 0% and an absent or empty amount is $0. A reply without `config` or `currentPeriod`, with an unknown
period type, a missing timestamp or a field of the wrong kind fails with `LlmProviderException` code
`xai_account_usage_unrecognized`, whose message names the field and carries no value from the reply.

**Settings a model or service does not accept.** No setting refuses a turn. On OpenAI's endpoint a reasoning model
that accepts temperature only at reasoning None loses temperature and top-p at any other level, and when the request
names no level; a model with no reasoning gets no `reasoning` object. Elsewhere, and for a model with no profile, the
value is sent. When the service answers HTTP 400 with OpenAI's `unsupported_parameter` or `unsupported_value` code
naming `temperature` or a `reasoning` parameter, or with a message that says unsupported or not supported and names
temperature or reasoning, the session records the value for that connection id and model, sends the step once more
without it, and leaves it out of later requests on that connection and model. A refusal that names nothing new ends
the turn with the provider's message. What refusals taught lives in memory for the process.

**Service tiers.** A turn asks for a tier with `LlmAgentTurnRequest.ServiceTier` (`LlmServiceTier`: Auto, Economy,
Standard, Fast; null is Auto). `LlmProviderCapabilities.ServiceTiers` lists the tiers the model offers. The request
normalizer maps the requested tier to the nearest offered one: Auto stays Auto, an offered tier is itself, and a
missing Economy or Fast becomes Standard when offered, else Auto. A model that offers no tier gets none. The
Responses payload sends `service_tier` `flex` for Economy, `default` for Standard and `priority` for Fast, and nothing
for Auto. Codex, Claude and Grok offer no tiers, so a requested tier is dropped and the turn runs. Grok never sends
`service_tier` and does not read saved `XaiConnectionOptions`. An OpenAI API connection
has a Service tiers switch, `CapabilityOverrides.SupportsServiceTiers`, shown as the `Service tiers` checkbox in its
editor. Unset, it is on for `api.openai.com` and off elsewhere; a checkbox value equal to the endpoint's default
stores null, so the default follows a later endpoint change. With the switch on, OpenAI's endpoint offers the
profile's tiers (the pricing page's) and another endpoint offers all four; with it off, the model offers none.

**Agent count.** A turn asks a multi-agent model for a number of agents with `LlmAgentTurnRequest.AgentCount`; null
leaves the provider's default in place. `LlmProviderCapabilities.AgentCounts` lists the counts the model accepts, and
is empty for a model that takes none. The request normalizer maps the requested count to the nearest offered count,
the larger of two equally near ones. A model that offers none gets none, and the turn runs. No provider offers a count.

**Context lengths in the editor.** An OpenAI API connection's editor shows `Context lengths` rows, none until
`Add row` is pressed (the CommonDesign D6 exception recorded in `AGENTS.md`). Each row names a model, chosen from the
connection's listing or entered by hand, and a length of at least 1 token. Each edit rewrites
`ModelCapabilityOverrides` from the complete rows: a row's model gets `MaxContextTokens`, a removed or incomplete
row clears only that model's length, and an entry left with no value is removed. A row without a model or a length,
or two rows for one model, block the save.

**Images out.** An `LlmImageGenerationTool` in `HostedTools` lets Codex and the OpenAI API generate images, and the session
commits each finished image through `CallerServices.Resources` and reports it as an `LlmAgentImageOutputEvent`.
A session that asks for images opens and runs on any model: the opening and each step drop the image tool when the
model's `HostedTools` lacks `ImageGeneration`, so Claude opens with it and runs without it. Every other hosted tool a
model lacks is still refused. The managed session also drops the image tool without resource access. An opening
adopts the caller's current resource selection in place of the stored context's, or clears it without access, so a
context whose selection came from an earlier run opens without a refusal; a message reference the access cannot
resolve still refuses. A committed image joins the selection, and a Responses request is prepared without the
selection, so the image is not sent again as an attachment: the next Codex or OpenAI API turn replays it from the
`image_generation_call` item in continuation state.

**Hosted search.** `HostedTools` can also ask for web search (`LlmWebSearchTool`), X search (`LlmSocialSearchTool`),
code execution (`LlmCodeExecutionTool`), document search over stored collections (`LlmDocumentSearchTool`) and a
remote MCP server (`LlmRemoteMcpTool`). The OpenAI API offers all of them except X search on `api.openai.com`; a kind
the model's `HostedTools` lacks is refused with its name, such as `This route does not implement X search.`. The
shared Responses client writes one wire tool per hosted tool through `WriteHostedTool`, in OpenAI's shape:

| Tool | Wire entry |
| --- | --- |
| Web search | `{"type":"web_search","filters":{"allowed_domains":[...]}}`, `filters` only when domains are listed |
| Code execution | `{"type":"code_interpreter","container":{"type":"auto"}}` |
| Document search | `{"type":"file_search","vector_store_ids":[...],"max_num_results"?}` |
| Remote MCP | `{"type":"mcp","server_label","server_url","allowed_tools"?,"headers"?,"require_approval":"never"}` |
| Image generation | `{"type":"image_generation","action"?}` |

The OpenAI shape has no excluded domains, image-search flags, user location or MCP server description, so those
fields are not sent there. `HostedIncludes` adds `web_search_call.action.sources`, `file_search_call.results` and
`code_interpreter_call.outputs` to `include` when the matching tool is present. The hosted-turn limit is not sent
on the OpenAI API: its Responses API has no `max_turns`. A provider whose wire differs overrides either hook per
tool.

Grok offers image generation on every model, and web and X search on models whose catalog row enables backend
search. Its `ILlmSessionHostedTools` supplies web and X search to managed steps only, not summaries. A caller's tool
of the same kind takes precedence. Application function tools keep their own definitions and execution handler.
The facade validates model-specific hosted tools immediately after attaching the catalog, before normalization and
sending (D20). Structural admission and credential/connection checks precede it. An unsupported hosted tool is
refused after any due credential renewal, with the same refusal.

| Tool | Grok wire entry |
| --- | --- |
| Image generation | The shared `image_generation` entry |
| Web search | `{"type":"web_search"}`, with `filters.allowed_domains` or `filters.excluded_domains` when listed |
| X search | `{"type":"x_search"}`, with optional `from_date` and `to_date` as `yyyy-MM-dd` |

Grok's `include` holds only `reasoning.encrypted_content`. It never sends `no_inline_citations`, so a searched reply
can carry its sources inline in the reply text (D18). It sends `MaxHostedTurns` as `max_turns` when set. It refuses
more than 350 wire tools, allowed and excluded domains together, more than 5 domains in either filter, a web-search
user location, either web image switch, and X handles or understanding switches. Code execution, document search
and remote MCP are not offered and refuse through the shared hosted-tool validation.

A remote MCP server's `Authorization` is resolved before the OpenAI API payload is sent by
the shared `RemoteMcpAuthorization`: each authority is asked once per request, its bearer token is written as the
`authorization` field of that server's `mcp` entry, and the token and the static MCP header values are removed
from diagnostics. An authority that returns no header, or one that is not a bearer token, fails the request with
`MCP authorization must be a bearer token.` or `MCP authorization returned no header.`

One observer, `ResponsesHostedToolsObserver`, reads every Responses reply. A hosted item (`image_generation_call`,
`web_search_call`, `x_search_call`, `file_search_call`, `code_interpreter_call`, `mcp_call`, and Grok's
`custom_tool_call` X-search calls, identified by an `xs_` call id or an X-search tool name) raises a started event on
`response.output_item.added` and a completed event with its output on `response.output_item.done`. A hosted item
that appears only in the terminal output completes there. Web search reports its sources, document search its
matches, code execution its code, logs and image URLs, and MCP its arguments, output and error. Each `url_citation`
annotation of a message raises one citation event, keyed by the message, its content part and the annotation, and
a top-level `citations` list adds each URL no annotation cited. The completed result carries every citation once.

**Fallback models, caps and retries.** Codex refuses a non-empty `FallbackModelIds` at opening. Claude passes `FallbackModelIds`, without the session model and without repeats, as
`--fallback-model a,b`; the CLI stops at launch when a fallback equals the main model. When `ExecutionOptions` is
supplied, Claude sends `--max-turns {MaxModelIterations}`. Every Claude opening of a conversation,
reopens included, sends the same arguments. A Claude result with subtype `error_max_turns` ends the turn with
`IterationLimit`, with the CLI's first error text. A Claude
`system` message with subtype `api_retry` becomes `LlmAgentResponseRecoveryEvent` with `NextAttempt` = the CLI's
`attempt`, `Delay` = `retry_delay_ms` and `Error` = `{error_status}: {error}` (the status is left out when null).
A Codex retry reports `Delay` as the wait before the next attempt and `Error` as the failure message. Managed
sessions route model sends, replacement steps and summary requests through `ILlmStepRetryPolicy`. Its default
policy permits three attempts for a 429, 502, 503, 504 or connection failure before output, and three interrupted
stream attempts with 1 and 2 second waits. A provider error that resource preparation wraps before any resource
effect reaches the policy, too-long refusal handling and final message as the provider's own exception.

Grok supplies its own policy. Transient 5xx failures except 525 and 526, connection failures and interrupted streams
get up to 15 sends. Waits start at 2 seconds, double to 30 seconds and carry ±20% jitter; the first connection
failure waits 200 milliseconds. A positive `Retry-After` replaces that wait, capped at 30 seconds before jitter.
A 429 gets one retry only as the step's first retry, capped at 120 seconds without jitter for a server hint.
`x-should-retry: false`, a too-long refusal and an out-of-credits refusal stop retries. Out-of-credits includes
402, a 429 naming free-usage or spending-limit exhaustion, and a 403 saying the account ran out of credits.
The entitlement message strips `[WKE=...]` markers. A renewed 401 resends at once, then after 1, 2 and 4 seconds.
An idle stream resends after 2, 10 and 30 seconds. Each stream read restarts the idle deadline on the injected clock.
The loop-check headers are `x-grok-doom-loop-check: 1024` and `x-grok-exact-repetition-check: 64`. A reported
`tail_repetition` on the thinking channel at threshold 64 or below resamples twice with the CLI reminder as a user
message on the next send only. Failed-attempt text and reminders never enter the stored context. A third report
ends with `Grok kept repeating itself; the turn stopped.`

Each resend reports `LlmAgentResponseRecoveryEvent` before waiting. Exposed output or an explicit policy request
starts a new step so partial output is replaced, not spliced. A started hosted call blocks replacement because it
may already have run. The guard also blocks requests whose caller named a non-image hosted tool. Session-added
search does not block replacement before a call starts. Stop cancels both the wait and the request.

`LlmAgentUsageEvent.ModelId` names the model a Codex step used: the step request's model on
streamed usage, and the reported model on the step's final usage. On Claude it is the `model` the CLI reports on
the turn's latest assistant message or message start.

**Context limits.** One rule sets every request's limits (`CompactionLimits.For`). The window is the smallest
known value of the listed model window, a learned limit when one is known and `LlmAgentContextPolicy.MaxContextTokens`.
A context length set on the connection for a model (`ModelCapabilityOverrides[model].MaxContextTokens`) is that
model's limit: the OpenAI API listing gives the model that window, and no learned limit applies to it.
The reserve is `ReservedOutputTokens`, else the model's output limit, else 8,000. The usable window is the window
minus the reserve. `NeedsCompaction` is true when the request measures at or above 85% of the usable window, and the
compaction target is `CompactionTargetRatio` (clamped to 0.05-0.80) of it. `LlmRequestAccounting.MaxContextTokens`,
`ReservedOutputTokens` and `InputBudgetTokens` report the window, the reserve and the usable window. Execution
options never refuse: a non-positive iteration or tool-concurrency limit uses the record default, a non-positive
reserve or cap is ignored, and a cap above the model limit uses the model limit.

**Compaction on Codex and the OpenAI API.** Every conversation reports `CompactionResponsibility = Provider`: both compact inside
Inference and never pause for the host, and their `ContinueTurnAsync` refuses because `SupportsCompactionHandoff` is
Unsupported. Codex's own compaction is never requested. Before every model step the session measures the request and
emits `LlmAgentContextStatusEvent`. A local estimate that does not rest on a provider-reported count is corrected
with what the model's reported counts taught. A reported step under a quarter of the usable window teaches the
model's framing: the reported input above the local estimate, which is text the service adds that the request does not
show, such as a hidden system prompt. A local count above the report teaches no framing. A step at a quarter of the
window or more teaches the rate, taken with the framing removed. The correction is the estimate times the rate, plus
the framing. The rate and the framing last only for the process, so a model's first request after the process starts
has neither. A step whose usage reports hosted calls teaches neither rate nor framing: its input usage includes
the service's internal search turns, which the local request does not contain. Its billing still contributes to run
usage. The context window counts the outgoing conversation, replayed output items including search calls, and the
reply, not the service's internal pages. No `context_details` or context-window header is requested. A context limit a refusal teaches is saved through the host's `ILlmLearnedLimitStore`, and
`LlmConnection.LearnedContextLimits` brings it back to later sessions; a limit learned in the process takes its place. Every measurement that is not entirely provider-reported then adds
300 tokens (`ManagedAgentPolicy.EstimateMargin`) to its estimated part, because a local tokenizer is not the model's
own and an estimate must err high; a count the provider reported in full gets no margin. The status, the request
accounting and the compaction measures all carry it. The estimate is asynchronous, so a chat client may ask its model
service to count text, and a failed count uses the local estimate for that text. The rate and any learned limit are
kept per connection id and model, so a limit learned on one service never applies to another connection. A host reads what the process has learned for a connection through `ILlmLearnedModelFacts.Read(connectionId)`, which `AddLlmConnect` registers: for each model, the estimated-to-reported token ratio, the framing tokens, the learned context limit and each refused setting, each a `LlmLearnedModelFact` with the time (`LearnedAt`) its value last changed. It copies the facts and makes no request. A context limit saved by an earlier process is not listed; it is on `LlmConnection.LearnedContextLimits`. At or above the trigger the session compacts toward the target: it cuts each
unprotected tool-result text part and structured content to its first and last 1,000 characters, sending cut structured
content as text, and replaces binary parts with a notice,
then keeps the current turn and the newest whole turns that fit in the room under the target that a 3,000-word
summary leaves, plus the newest part of the next turn from the earliest tool call that still fits, and replaces the
rest with one user entry `Summary of the earlier conversation:\n{summary}`. A current turn that alone leaves no such
room keeps only its first entry, its input, and its newest steps from the earliest tool call that fits; its older
steps are summarized, so the result lands near the target and the turn's next step does not compact again. The cut
never starts after the turn's first protected entry. The summary is built from the earlier summary and the entries it
replaces, and the new summary replaces both. The summary input cuts tool output the same way. Summary requests run on
the conversation's own connection and model, one part at a time, each within half the usable window, and the summary
holds at most 3,000 words. A summary request that runs past 10 minutes ends the compaction with `Compaction stopped: a
summary request of about {tokens} tokens did not finish within 10 minutes. The context is unchanged.` On Codex a summary request that is refused as overloaded or whose stream is interrupted is sent
again inside the session, up to three attempts. The working indicator shows `Pruning old context`, `Summarizing earlier messages` and
`Applying the compacted context` as `LlmAgentActivityEvent` text, cleared by empty text. A compaction that changed the
context emits `LlmAgentCompactedEvent` with the dropped-entry, shortened-output and removed-image counts, then an
`LlmAgentUsageEvent` with the summary requests' usage. Results of a current-turn call to a tool with
`ProtectedResults = true` are never shortened or summarized. A result at or above the usable window ends the turn with
`ProviderFailure` and `Compaction could not bring this request under the limit: {measured} tokens remain in a
{usable}-token usable window. The current turn alone measures {mandatory} tokens.` A failed summary request ends the
turn with the provider's message after the usage it reported; the context is unchanged.

**Provider compaction.** A chat client that implements the internal `ILlmProviderCompaction` and reports a model as
compacted on the service side gets that model's compactions, at the same trigger and on `CompactNowAsync`: the
session sends the request it would send to the service's compaction, and the returned entries replace every entry of
the context. `LlmAgentCompactedEvent` then counts every replaced entry as dropped. A failed compaction call compacts
with the library's compaction above, for that compaction only. No chat client implements it.

A too-long refusal teaches the model's size, compacts whatever the measure says and sends the same step again once; on
the OpenAI API the refusal is OpenAI's `context_length_exceeded` code, the llama.cpp server's
`exceed_context_size_error` type, whose `n_prompt_tokens` and `n_ctx` are learned as the request's size and the
limit, or a message naming the context length. A
second refusal in the turn ends it with `ProviderFailure` and the provider's message. With
`PreserveSuppliedContext` the session measures and reports but never compacts or resends. An opened history whose
entry held only continuation state the import left out, such as a native compaction from another connection or model,
has that entry replaced at the first step with a summary of the transcript turns the context no longer holds, or
dropped when no transcript is supplied. `CompactNowAsync` runs the same compaction on an idle conversation, keeps its
last turn whole, and returns the compacted event, or null when there is nothing to compact or no known window; it is
refused while a turn runs.

**Compaction on Claude.** A Claude start returns a conversation that owns the saved history and one CLI session at a
time. Every CLI process starts with `DISABLE_COMPACT=1`, so the CLI never compacts on its own. The saved history is
the opening history, then each turn as the host's native transcript records it: the input as one user message, each
model step as one assistant message with its calls, and one user message with their results; entry ids are
`{turnId}-{n}`. The conversation measures before the turn's input is written, the first turn's opening included, and
when a model step's last host tool call settles, before its result is written. A measure with a reported step is that
step's input and output plus the corrected estimate of the entries recorded after it; otherwise it is the corrected
estimate of the whole history and the input. After a native resume the CLI holds its own saved session, which the
saved history does not measure, so the measure reports no used size until the first step reports. The window and
output limit come from the CLI's model list, else the
published table. At or above the trigger, or after the turn's first too-long refusal, the conversation closes the CLI
session, compacts the saved history with this turn's entries as the current turn, and opens a new CLI session with the
compacted history as its opening. It then sends the turn's input, or `Continue from where you stopped. The tool
results above are complete; do not repeat finished tool calls.` when the turn already ran a step. The conversation
keeps its thread id and the turn keeps its id across CLI sessions; usage totals add up across them. A second refusal
ends the turn with the provider's reason. `GetContextStatusAsync` answers from the same measure, and `CompactNowAsync`
compacts an idle conversation and reopens from the result. The CLI exports no context, so each compaction's
`LlmAgentCompactedEvent` carries the compacted history as `Context`, at its `ContextRevision`; during a turn it holds the
turn's entries so far under the event's `TurnId`. Stages, events and failures are those of Codex.

**Compaction on Antigravity.** An Antigravity start or resume returns a conversation that owns the saved history and
one AGY conversation at a time. The saved history is the opening history, or a resume's `Context`, then each turn's
input as one user message and its final answer as one assistant message. AGY runs its own tools inside a turn, so the
conversation measures only before the turn's input is sent, the first turn's opening included. A measure with a
reported model response is that response's input and output plus the corrected estimate of the entries recorded after
it; otherwise it is the corrected estimate of the whole history and the input. AGY reports no window, so the window and
output limit come from a published table: 1,048,576 and 65,536 tokens for `gemini-3.8-flash-low`, `-medium` and
`-high`, 1,000,000 and 128,000 for `claude-sonnet-4-6` and `claude-opus-4-6-thinking`, and 131,072 with the default
reserve for `gpt-oss-120b-medium`. Any other model, `auto` included, has no known window, so its status reports no
trigger and the conversation never compacts it. At or above the trigger the conversation compacts the saved history
with the turn's input as the current turn, summarizing in print mode on the same connection, then closes the AGY
conversation and opens a new one with the compacted history as its opening transcript, followed by the turn's input.
A failed compaction ends the turn and keeps the open AGY conversation. The conversation keeps its thread id and the
turn keeps its id; usage totals add up across the summary requests and the turn. `GetContextStatusAsync` answers from
the same measure, and `CompactNowAsync` compacts an idle conversation and reopens from the result. Each of these
compactions carries the compacted history as `LlmAgentCompactedEvent.Context`, as on Claude. AGY also compacts on
its own with a `checkpoint` step, a model-written summary that replaces earlier steps in its context; the conversation
reports it as `LlmAgentCompactedEvent` with no counts and the step's usage. The conversation reports
`SupportsNativeResume` Unsupported once a compaction opened a new AGY conversation, because a resume by the thread id
reopens the first one.

An unclassified managed failure ends the turn with `{exception type}: {message}`, the message with credentials
removed. Provider errors keep `LlmProviderException.Message`.

## Support Evidence

Capability flags use Common's `InferenceSupport` enum. `Unknown` permits an explicit attempt through
an implemented mapper. `Supported` reports affirmative evidence. `Unsupported` reports an actual
inability or an explicit caller restriction. Default capability values are `Unknown`.
Tool-output `PreservesOrder` and `AllowsEmpty` use the same contract.

`LlmCapabilityOverrides` uses nullable support values. Null inherits adapter or connection metadata.
An explicit `Unknown` remains unknown. The settings editor exposes Default, Unknown, Supported,
and Unsupported for these fields. Per-level reasoning choices remain Default, Enabled, and Disabled.
Common bindings and SelfHost preserve the enum values. JSON carries enum names without boolean
compatibility conversion. The host consumes the same source contract.

Capability queries do not treat an explicit Unknown override as an affirmative implementation claim.
Gemini and Vertex retain known missing JSON-object/account-usage operations as Unsupported by default.
An unfamiliar Gemini API model reports Unknown schema support and can use the existing unchanged
`responseFormat.text.schema` request. Provider acceptance remains unqualified.

Unknown native background-work observation permits the caller's explicit request without promising
a resource feed. Explicit Unsupported still refuses it. Grok document search does not inherit the
generation tier; its payload omits a tier field and leaves the saved choice unchanged. Realtime MCP
labels retain periods unchanged. Chat Auto tool choice and explicit parallel-call controls can be
sent without tool definitions; Required still needs an available tool and Specific must name one.

## SDK HTTP composition

SDK HTTP registrations use one generation-owned pool for the finite SDK named pipelines,
including ComfyUI's empty default name. Named `AddHttpClient` actions, public handler-builder filters,
handler scopes and selected meter factories configure the owned chains. Direct client and factory
constructors remain borrowed. The SDK never replaces the application's `IHttpClientFactory`.

`AddLlmConnect(configureHttp)` and `ConfigureInferenceHttpPipelines(configureHttp)` select
`SdkGenerationOwned` or explicit `BorrowHostFactory`. The latter delegates creation to the registered
host factory and does not dispose it. Configuration accepts only the SDK names and freezes
before client construction. Conflicting creation modes are rejected.
The speech pipelines are `XaiTtsProvider`, `XaiSttProvider` and `XaiRealtimeProvider`.
Their registrations disable redirects and set an infinite HTTP-client timeout; each operation
retains its explicit connection timeout. The same generation owns their HTTP handlers and leases.

Custom logger registrations in owned mode use `ConfigureLogging(name, logging => ...)` and its
`AddLogger` methods. Legacy framework `AddLogger` actions are not inspected or automatically migrated.
Logger factories run after the named handler-builder actions in the actual handler scope. Public
default logging, header redaction and meter filters remain active. HTTP configuration objects refuse
nonnull JSON serialization and contain no transferable credential authority.
Their attributes name one public closed converter factory for the two configuration types, so
nonfriend generated contexts preserve the same nonnull refusal and null handling.

Handler expiry stops new assignment to the old chain. Live issued clients and response/body leases
keep it alive; weak client registrations do not retain dropped typed consumers. A generation-owned
10-second cleanup timer reconciles collected wrappers while expired entries remain pending. Root
retirement stops and drains that timer, closes live wrappers and awaits owned chain cleanup. Cleanup
failures remain owned and are returned to all repeated disposers; borrowed host resources are untouched.
The registered pool regression verifies held body-read drain, handler release and remembered cleanup
refusal through the actual owned pipeline. Framework container disposal remains a separate lifetime:
the current dependency stops disposal at the first failing service. A transport cleanup refusal can
therefore prevent container disposal from reaching the pool. Pool retirement does not establish
successful disposal of that container, and the SDK does not replace the framework disposal algorithm.

All SDK-owned response consumption and result/event publication use one internal helper. It fences
cached strings/bytes/copies and buffered streams as well as unbuffered reads, without changing HTTP
completion options or body values. Raw arbitrary caller use of .NET `HttpContent` is not transparently
intercepted: its nonvirtual cached APIs can bypass content overrides. Direct supplied clients and
`BorrowHostFactory` keep ordinary body and ownership semantics.

The shared LLM HTTP failure helper redacts known credentials and observed outgoing request headers
from the response body, provider code, request ID and rendered message. This includes borrowed-client
default headers. Each send owner supplies its live request headers after the send; redaction does not
depend on the response retaining its request. Credential-aware ID reads also sanitize when no extra
secret context is supplied.
Missing IDs remain absent in fields and render as `unknown`; observed HTTP status and retry delay remain intact.
Responses generation carries one request-local header and hosted-authorization snapshot through both
complete and streamed body conversion. Malformed-response and provider stream-error diagnostics use
that context for their message, nullable provider code, request ID and diagnostic body.
Stream-error codes and messages come from the redacted native error envelope through the shared
HTTP error reader, so named secret values are also removed from those fields.
Anthropic Messages carries the actual sent headers through streamed and complete body conversion.
Its protocol errors and terminal diagnostics redact the request ID and body with that context.
Native errors retain their Anthropic type and explanatory message from the redacted envelope;
overflow classification reads the original provider facts.
Shared protocol errors retain JSON parsing failures with their actual line and byte positions.
Their inner exception uses a fixed parsing message and no input path because parser text can quote secrets.
Compatible chat carries actual sent headers through streamed generation and its canonical complete decoder.
Protocol/native errors and terminal diagnostics use that context for their safe fields and body.
Native error codes and messages come from the same redacted-envelope reader; overflow classification
retains the original provider facts.
Shared OpenAI-compatible, Anthropic and Codex model discovery readers carry actual sent headers
to the existing protocol-error boundary. Codex/OpenRouter account parsing uses that boundary too.
Malformed JSON and Codex's missing-catalog response retain known HTTP status and a sanitized nullable
request ID. Parser diagnostics omit input-quoting text; OpenRouter's stream parser supplies no raw body.
Claude's optional HTTP catalog uses this boundary without launching the CLI.
OpenAI embedding conversion uses actual sent headers for model-identity checks, request IDs and
diagnostic bodies. Invalid embeddings retain sanitized validation details and use the same safe
JSON-parser projection as chat protocol errors. Successful vectors, usage and safe model identities
remain unchanged.

Mapped task results publish after response, body and request cleanup completes; retirement begun by
cleanup prevents late success without replacing an earlier operation failure. If an owned logger
fails after receiving a response, that unhanded response is cleaned up once. Refused cleanup remains
owned with its exact error; independent logger and cleanup failures are preserved together.

Comfy's in-transport reference materialization stays admitted through resource commit and cleanup.
Returned Image inference has a separate caller-owned lifetime across download, resource commit and
publication. Pool retirement alone does not drain that inference after copied download bytes return;
whole-generation retirement must also retire the returned inference owner. Committed and uncertain
resource facts remain actual observations, not rollback claims.

## Grok Image

`AddImageConnect` registers `XaiImagineImageTransport` alongside ComfyUI. A Grok image
connection borrows an `XaiFamilyBinding` backed by the selected LLM facade and its connection
access. Image discovery uses that facade's image catalog. Manual workflows carry their native
model in `TransportConfiguration`; discovery does not gate a manual model.

Generation and editing use one synchronous JSON request. The transport accepts up to ten outputs
and five ordered references, including explicit duplicates. Binary and logical references upload
through the same Files owner. Imported native file IDs receive a selected-token metadata check.
The canonical request codec refuses non-null `quality` for the known `grok-imagine-image`,
`grok-imagine-image-quality` and `grok-imagine-image-pro` slugs before uploads or HTTP.
[xAI documents this control for `grok-imagine-image-2.0` only](https://docs.x.ai/developers/model-capabilities/images/generation#quality).
Explicit null remains null. Unknown manual models retain provider-side admission. Model slugs
remain literal; the transport does not predict provider redirects or replace the selected model.
Null original filenames remain null; an unnamed upload gets a separate local multipart label.
Selected and Image additional headers retain their values without replacing selected authorization.
Image sends capture their effective headers, including borrowed-client defaults, on success or failure.
HTTP diagnostics and native output admission use that same redaction context.

`SubmitAsync` returns a transport-local completed-result handle, not a provider job ID.
Copied equal handles remain usable on that instance until retirement. `GetJobAsync` returns its
ordered completed snapshot without polling or credential refresh. Completed cancellation returns
false without contacting the provider. Inline downloads use actual retained data; native file and
returned URL outputs use the selected Files owner or its unsigned SDK-generation download path.
Unknown model, MIME and dimensions remain unknown. The shared image decoder preserves raw source
representations and checks caller byte limits before decoding batch output.

The bound Image inference commits generated bytes through caller-owned resource access. Its
operation fence follows the entered binding and detaches after children and cleanup drain.
`InferenceResourceOperationFailure.GeneratedResources` retains ordered actual commit-and-selection
receipts when a later reference, output, progress callback or cleanup fails. It uses the existing
copied receipt graph and does not replace failed work with success or infer rollback.
The transport owns its local results and HTTP request/response cleanup, not the borrowed core,
HTTP client, resource access or source streams. Catalog health confirms discovery access only;
generation entitlement, OAuth media compatibility and runtime retirement remain unqualified.
Image provider failures retain the redacted error code, request ID, status and retry delay.
Their diagnostics withhold private response bodies through transport and inference wrapping.

## Batch and deferred contracts

`Darkspyre.Inference.Connect.Resources` declares `ILlmBatchClient` for explicit-connection batch
creation, incremental requests, one-page queries/results, cancellation, JSONL creation and deferred
submission/retrieval. Its closed operation union carries actual chat, image/workflow and video
requests. Results retain caller `CustomId` correlation, terminal video outputs and committed caller
resource descriptors. Counts and timestamps remain nullable observations; absent counts are not zero.
`LlmBatchVideoResult.Result` carries the actual terminal `VideoOutput` without requiring a native job
ID. Its optional `JobSnapshot` carries a separately observed real job only. Image and video batch
results have optional `Usage` for actual provider-reported costs and counts, not estimates.
`GeneratedImage.Selection` and `MaterializedVideoResult.Selection` carry optional full post-commit
membership receipts. An absent receipt remains unknown; neither result reads current membership.
`LlmChatResult.GeneratedResources` and Common `LlmInferenceResult.GeneratedResources` carry ordered,
copied actual commit-and-selection receipts for generated session artifacts. Every receipt includes
the generated resource in its exact successful selection snapshot. Workspace-owned resources can
have a null session; a reported session must match that snapshot. Duplicate observations retain their
order. These receipts grant no access and never substitute a later membership read. Current providers
leave the collection empty until they perform actual generated-artifact materialization.
Media materialization budgets are explicit positive caller policy, not provider defaults. Page limits
retain the caller's raw nullable integer; these data declarations impose no provider paging bounds.

`ConnectResourcesJson` exports 28 exact data-type getters with one generated closed Common/LLM/Image/
Video graph. Pages copy their ordered lists, including assignments through `with`. Nested existing
request/result objects retain their own contracts; constructing a batch wrapper does not admit a
provider operation or resolve content. The codec preserves fixed xAI hosted-tool and video-option
variants and mixed function/shell tool graphs. It composes the existing LLM-owned caller-service refusal
policy and refuses nonnull resource access, handles, authorization and batch clients without calling
them. Diagnostic strings hide resource, prompt and native receipt data.

`LlmResourceOperationObservation` carries ordered upload attempts, partial results and completed
commits. Independent dispatch facts report possible batch mutation, deferred submission or consume-once
retrieval; they do not prove remote success or authorize retry. Response status and request ID require
an observed response. Upload receipts require dispatch, file and image receipts are exclusive, and
operation-created JSONL belongs only to creation from requests. Collections copy their data, including
result bytes and opaque JSON. The owned codec rejects contradictory facts and undefined operations.
Provider failures retain the current response's sanitized request ID in both the operation observation
and the nested provider exception. HTTP status and retry delay remain available without response bodies.
Batch/deferred exchanges add actual sent headers, including borrowed-client defaults, to their existing
operation secret context before observing a response. HTTP rejection and canonical chat decoding use
that same context; batch row errors and native field admission also use it.
The exchange captures headers even when the send throws. Typed provider failures and HTTP send
failures retain their credential-and-header-redacted explanation. The raw inner exception remains
private evidence. A failed send does not manufacture a response observation.
`LlmResourceOperationException` and `LlmResourceOperationCanceledException` expose a copied observation
with fixed diagnostic text. Cancellation preserves its actual cause and token. Their constructors do
not sanitize caller-supplied inner exceptions. Exceptions remain runtime objects, not JSON roots.

`LlmResourceProviderService(connectionAccess, providerService: null)` borrows an existing
`LlmConnectionAccess` and optionally an existing core `LlmProviderService`. A supplied core must borrow
the same access; construction validates that identity before allocating owned state.
`AddLlmResources()` registers one scoped concrete facade and its same-instance `ILlmBatchClient`
alias with the existing access and optional registered core. The caller registers connection access
separately; this extension creates no connection source or HTTP factory. With an implemented xAI core
route, creation, retrieval, one-page batch/request listing and cancellation use one acquired connection
snapshot and the core's selected HTTP owner. Cancellation and request listing first authorize the
literal batch ID through one nondestructive Get using that same snapshot. Receipt scope records the
fixed batches route and originally selected connection ID, not account or principal proof; imported
receipts can use another selected connection ID when the route matches and the current token authorizes.
Supplied page controls are forwarded without inferred limits or automatic paging. Response counts,
cursors and timezone-qualified timestamps retain actual observations. Valid provider calendar dates
produce null time observations without rejecting the receipt or inventing an instant.
The shared batch/metadata timestamp reader uses the BCL JSON parser
for [xAI's Protobuf timestamps](https://github.com/xai-org/xai-proto/blob/main/proto/xai/api/v1/batch.proto).
It accepts the provider's nine fractional digits and retains the first seven at `DateTimeOffset`'s
100-nanosecond precision. A reported instant requires explicit UTC or a numeric offset; absent values remain null.
The same reader accepts observed nonnegative 64-bit counts as JSON integers or quoted decimal
strings, matching the provider's `int64` wire type. It does not pass counts through floating point.
Mutation dispatch and validated prior/returned receipts survive late
cancellation and failure, with response facts belonging only to the current HTTP stage.
Deferred submission also uses that same selected snapshot, borrowed HTTP/Files owners and canonical
Chat Completions body. Typed controls, schema numeric guarantees, function-only tools and supplied
native history are checked before credential refresh or resource effects. Logical prompt and tool
resources use the existing chat preparation boundary. Tool messages carry ordered text and file
blocks with actual uploaded file IDs; text-only messages retain their string form. Distinct uploads
are observed at actual dispatch and receipt parsing before cleanup. The returned deferred receipt
preserves the actual `request_id`; its scope describes the fixed deferred route and selected
connection, not account authority.
Submission timestamps and expiry remain unknown when absent from the response. Chat continuation
state has a distinct xAI Chat-lane identity and is never replayed as Responses state.
Deferred retrieval performs one selected-token GET on the receipt's fixed route, including imported
receipts. It performs no preliminary consumption, polling or retry. An actual `202` returns Pending
without reading its body; an actual `200` uses the same canonical Chat decoder. Generated `output_files`
are downloaded incrementally through the same Files owner in their returned order. Each output uses
one owned resource write handle with actual observed metadata and commits with session selection before
successful publication. `GeneratedResources` contains the exact ordered commit receipts. A null resource
access is legal for artifact-free results; observed artifacts require access before any download or
write, while failure still reports the preceding consume-once dispatch and response. Earlier committed
outputs survive a later transfer, commit, cleanup or publication failure without deletion or retry.
The writer's commit callback updates the recovered chat's `GeneratedResources` before cancellation
and cleanup fences. A canceled retrieval therefore retains each valid committed file in both the
recovered chat and the operation's commit observations.
Incremental batch requests and creation from requests share one ordered snapshot and the canonical
Responses, image and video body builders. Every row is validated before credential refresh or input
transfers. Chat selections are frozen once per prompt; media reads only its named sources. One batch
resolves each borrowed MCP authorization once. Upload attempts retain their actual row/source and
Files receipt without merging distinct transfers of the same resource.
Each native request is limited to 25,000,000 UTF-8 bytes. Add sends one inline POST to the authorized
existing batch; it does not impose that limit on the aggregate body, split requests or retry.
Creation from requests uses create then one add when the measured aggregate inline body is at most
25,000,000 bytes. Larger input uses one JSONL upload and file-based creation, limited to 200,000,000
UTF-8 bytes and 50,000 rows. Both representations use the same prepared bodies. Inline-created batches
remain open; file-created batches are sealed by the provider. An observed Create receipt survives
later Add, cancellation and cleanup failure with its original observed counts, not synthetic totals.
Supplied video audio uses its actual public URL or a bounded data URL from actual bytes. Unknown
MIME uses application/octet-stream as a wire label without changing content metadata. Native file
audio is unsupported; no signed-URL or library-side URL-fetch fallback is used.
Batch result retrieval returns one provider page in its actual order with the provider's pagination
token. Each result uses its actual custom row ID. Chat results use the canonical complete Chat decoder,
including tools, reasoning, citations, continuation and named generated files. Images use the canonical
image codec and commit/select every output into the borrowed Session/Workspace. Imported URL image
outputs use the same unsigned download path and retain their actual bounded bytes. Terminal video results
stream into the same named writer through Files or the core's unsigned media download pipeline. No
custom row ID becomes a native video job ID. That unsigned pipeline sends no provider headers or
cookies, follows no redirects and has no SDK loggers; caller-supplied HTTP factory behavior remains
the caller's obligation. Batch transfers keep the LLM route's existing caller-cancellation timeout
policy and do not change a borrowed client's Timeout.
The raw UTF-8 page and aggregate decoded inline image bytes each have the caller's independent
MaximumInlineResultBytes budget. Each image also has MaximumInlineImageBytes. Known lengths are
checked before allocation or writing. Nullable usage and media metadata remain unknown when absent.
ObservedResults and ObservedCommits preserve earlier output and exact commit/selection receipts after
a later transfer, cancellation, commit, publication or cleanup failure. Missing resource access refuses
artifact materialization without hiding the preceding result-page dispatch and response.
Disposal fences entry, drains admitted operations, shares retirement completion and releases borrowed
references without disposing them. Actual HTTP and preparation cleanup refusal retains the failed owner and is reported
by retirement; ordinary operation failures do not poison retirement. No operation retries or rolls back.
Caller cancellation retains the existing facade entry precedence. The owned codec refuses both
nonnull concrete and interface facade values; null runtime values remain portable.

The existing LLM `ResourcePreparationTests` exercises imported deferred receipts through the real
resource facade with a consume-once HTTP stand-in. It checks one retrieval request and the canonical
returned text, reported model, terminal status and usage. Its batch case checks authorization followed
by one result-page request, the native `chat_get_completion` envelope, ordered row outcomes and the
returned cursor without automatic paging. These managed cases do not establish provider access,
media materialization or independent transport/cleanup fault qualification.
The cancellation-after-commit case uses a memory-backed write handle and checks the portable recovered
chat and its exact selection receipt. It does not qualify remote Files delivery or cleanup refusal.

This assembly activates no global provider registration and makes no OAuth or endpoint-qualification claim.
Caller workspace storage remains caller-owned. Constructing or serializing a receipt performs no
upload, polling, materialization, cache creation or tool execution.

## Video bindings

`VideoProviderService` binds the existing Common Video API to explicitly registered transports.
`AddVideoConnect` registers the family service and the Grok Video transport. The adapter uses the
selected xAI core and Files owner for generation, editing, extension, lookup and streaming download.
Registration does not establish provider entitlement or live compatibility.

The bound facade validates the complete native request, source metadata and known workspace
incompatibility before credential acquisition. Entered capability and submission bindings carry
the facade's synchronous retirement fence. The creator detaches that fence after the awaited
operation and cleanup settle. Selected authorization is composed before Video additional headers;
a collision with a selected secret header refuses before credential refresh.
Video sends capture effective headers, including borrowed-client defaults, even when no response arrives.
Provider error explanations, codes and request IDs use the selected credential and those headers for
redaction. Submission and polling fields use the same context before publishing native identity or output.
Request timeout validation follows the .NET send-through-headers timer's range; it is not a
provider limit. Settings and direct binding enforce the same range.

Materialization streams actual downloaded bytes into one caller-owned write handle and commits
with session selection. It returns the existing resource descriptor. Actual full commit-and-selection
receipts remain in failure `GeneratedResources` if a later fence or independent download/writer
cleanup refuses. Image and Video attach those observations through one shared resource helper.
Only known conflicting session or MIME facts refuse; null metadata and missing MIME parameters
remain unknown. These source paths do not establish Grok Video entitlement or runtime qualification.

## Hosted STT bindings

`Darkspyre.Inference.Connect.STT` binds a host-selected `SttConnection` to the Common hosted
`ISpeechTranscriptionClient`. `SttProviderService` resolves registered `ISttProvider` adapters.
`AddSttConnect()` registers the facade and `XaiSttProvider` for file and streaming transcription.
Its setup catalog contains the registered adapter's definition. Registration performs no OAuth,
resource or provider request and does not prove provider entitlement. An unregistered transport
refuses before credential or resource effects. Hosted STT does not replace Tensor's model-instance
transcription API.

The connection carries a durable `XaiConnectionSelection`, optional borrowed authorization and copied
case-insensitive protected headers. `Bind(connection, xai, resources)` passes the matching borrowed
`XaiFamilyBinding` and caller workspace access to the selected adapter. These collaborators remain
caller-owned. Bound Common clients implement `IDisposable` and `IAsyncDisposable`. A successfully
handed-off streaming session owns its transport independently of the client.
Adapter operations require one validated snapshot and known-incompatibility validation before
credential refresh or source effects. Binding validates the existing fixed xAI regional API bases,
positive timeout and matching selection without acquisition. Direct binding and setup use the shared
header validator before provider or binding-factory callbacks. Reserved, malformed and newline-bearing
headers refuse; static authentication headers cannot override the borrowed authorization. The
five-minute request timeout is a library default, not a provider limit.

`ConnectSttJson` exports exact connection/transport/setup data getters with a complete generated graph.
Live authorization, family bindings, factories, services and sessions refuse nonnull portable JSON.
Diagnostics hide connection and header values. Setup preserves all nullable transcription/streaming
defaults and explicit stream budgets; omitted limits remain omitted. Commands still require explicit
valid budgets before opening a stream. Field edits are effect-free. Only registered adapters supply
capability checks. The disposable settings manager fences and drains admitted operations and saves,
then releases borrowed targets/factories/access without disposing them. Its own synchronous retirement
fence returns structured cancellation for admitted checks/queries even before linked cancellation
propagates. Admitted saves preserve the host's actual committed or uncertain outcome.
Checks retain matching borrowed authorization. Portable setup changes omit that runtime authority;
the selected connection ID remains data, and only explicit named header edits reach the commit target.

`XaiSttProvider` also supports explicit composition with a borrowed `HttpClient`. REST
transcription sends one multipart request with the file field last, or asks the provider to fetch the
supplied URL. Named workspace inputs use the existing resource reader without reading or changing
selected-resource membership. The adapter borrows input streams through their owned read handles.
It preserves actual transcript, channel, speaker and timestamp observations; absent response model
identity remains null. Known-invalid media headers, filenames and incompatible declared-container
raw controls refuse before connection acquisition. The adapter does not inspect payload bytes or
infer audio format from filenames.

The reusable WebSocket session waits for `transcript.created`, sends audio packets and ordered
`finalize`/`audio.done` commands, and exposes one bounded event feed with caller-supplied limits.
Opening rejects transcript IDs that echo a credential or protected outgoing header, including
borrowed-client defaults, before publishing the created event. It never rewrites the native ID.
Opus packets remain indivisible; explicit accepted sample-rate controls are sent unchanged, but the
provider ignores them for Opus. Streaming maps explicit text normalization to the nullable `format`
query parameter documented in the [xAI STT reference](https://docs.x.ai/developers/rest-api-reference/inference/speech-to-text).
Omission sends no `format` parameter. Both REST and streaming require a language when normalization
is enabled and preserve explicit `false` without requiring one. Actual per-channel final observations remain
ordered as received. The convenience stream owns one temporary session and its input enumerator,
and client retirement cancels and drains that work without requiring a paused reader to resume.
Explicitly handed-off sessions remain caller-owned. Cleanup refusals retain actual unsafe owners
and failures; borrowed HTTP, authorization, resource access and input sources remain undisposed.
WebSocket error events retain the provider's redacted explanation. Protocol failures use fixed speech
labels without exposing provider payloads. OAuth endpoint
acceptance, the provider's ambiguous 500 MB/50-character limits and real-time behavior remain
provider qualification boundaries; the library does not invent byte or Unicode cutoffs.

`SttProviderService` and `VideoProviderService` implement `IDisposable` and `IAsyncDisposable`.
Retirement immediately fences new facade calls, drains admitted synchronous setup/binding callbacks,
then releases borrowed adapter references. It does not interrupt those callbacks or replace their
original results/exceptions. Adapters, handed-off clients/inference and settings managers keep their
independent caller ownership; facade disposal never disposes them. Concurrent retirement callers share
the same completion. Use asynchronous disposal while another thread is inside a facade callback.

> **Proprietary software.** Copyright © 2026 Brandon Mathis. All rights reserved.
> No permission is granted to use, copy, modify, or distribute this software
> without prior written authorization. See [LICENSE.md](LICENSE.md).

`Darkspyre.Inference.Connect` is the out-of-process inference NuGet used by Darkspyre applications.
It references `Darkspyre.Inference.Common`, which supplies the typed portable LLM and image inference
contracts implemented by both Connect and Tensor; it does not embed the Common assembly.
The `Darkspyre.Inference.Connect.LLM` assembly supports OpenAI Responses, OpenAI-compatible chat endpoints,
Anthropic Messages, direct ChatGPT subscription access and the official Claude and Antigravity CLIs,
host-executed tools, managed agents, structured output, streaming, discovery, account usage, and
embeddings. The `Darkspyre.Inference.Connect.Image` assembly supplies the image-provider contracts
and built-in ComfyUI HTTP/WebSocket transport. AWS Bedrock Converse and AgentCore Harness remain
isolated in the `Darkspyre.Inference.Connect.LLM.Bedrock` assembly.
Azure resource OpenAI v1 Responses and Chat Completions use the optional
`Darkspyre.Inference.Connect.LLM.Azure` assembly inside this same package.

The package also includes `Darkspyre.Inference.Connect.TTS` for external
PocketTTS HTTP synthesis and `Darkspyre.Inference.Connect.Audio` for external TangoFlux generation.
They are included in the Connect product project, not separate NuGets. See
[PocketTTS integration](#pockettts-speech) and
[TangoFlux integration](#tangoflux-audio). The coordinated source package version is **11.16.0-rc.1**.

The packages target .NET 11. Credentials
for direct API transports remain owned by the host through `ILlmConnectionSource`; the connector
never owns an application database or secret store. Wrapped CLI transports instead use the official
CLI's existing login, which Darkspyre.Inference.Connect.LLM never reads or copies. A Claude subscription connection
can instead be signed in to a Claude account: its credential then holds a one-year token, and every
Claude CLI process for that connection runs as that account (see "Anthropic API-key and Claude CLI transports").

`LlmCredential` and `LlmOAuthTokens` return constant redacted diagnostic strings. Formatting either
record exposes no credential values or account metadata. Their properties, record copies and
protected-store `Serialize`/`Parse` wire format retain the original values. Serialized credential
JSON remains secret and is not a diagnostic representation.

ACP and Copilot runtime configuration uses closed typed profiles, state selections and
authentication selections. `ConnectLlmJson` exports the corresponding data and staged-login
observation metadata. Live launch inputs, login services, factories and runtime authentication
owners refuse non-null serialization without invoking borrowed callbacks. Null runtime inputs
round-trip. Copilot credentials use the existing protected Web/null-omit codec; their diagnostic
strings redact tokens. These declarations do not register an ACP or Copilot adapter, launch a
runtime, perform login, validate state-directory isolation or certify a runtime/SDK version.

Existing transports reject foreign ACP/Copilot configuration, explicit runtime launch inputs
(including an empty object), and dedicated Copilot credentials before refresh, catalog, process,
HTTP or host inference effects. Direct adapters and resolved facade calls use the same admission
rule. Credential refresh and login results also refuse foreign runtime credentials before a
conditional save. Settings report invalid foreign inputs without calling environments or the
commit target. Runtime profiles and launch inputs copy their collections on initialization;
admission does not remove supplied inputs or select a different transport. Diagnostic redaction
includes both the GitHub token and the separate runtime connection token.

`LlmAgentProviderService.BindRuntimeAuthentication` exposes saved-ID and explicit draft/identity
overloads. Both reject runtime authentication as unavailable before resolving a connection or
starting login. `CreateSettingsManager(target, agents)` optionally borrows the agent facade from
the same connection source. The settings manager fences new actions, choices, opens and saves
when retirement starts, drains admitted calls, then drops borrowed collaborators and its commit
target. It never disposes them. A save that settles during retirement retains its actual commit
state and version, including committed or uncertain outcomes.

Every returned `LlmConnection` includes a required opaque `Version`. The version must cover both
connection metadata and credential state. `SaveCredentialAsync` must atomically compare
`expectedVersion` and replace only the credential when it still matches; it must reject changed,
replaced, or deleted connections and must never upsert. A separate read/check/write sequence is not
sufficient because delayed OAuth completion or refresh could otherwise overwrite a concurrent provider
change.

## Packages

The coordinated release contains Common, Connect, independently consumable Tensor libraries and UI.Settings. They always use the same version from the repository
`Directory.Build.props` and publish together. Version numbers use
`<net>.<release>.<minor>`: .NET generation, breaking-contract release, and compatible tweak/fix.
The direct Codex and host-owned managed-agent behavior from the former `Mathis.LlmConnect` 3.0.0 is
retained; Claude and AgentCore retain provider-owned sessions.
See the [Darkspyre handoff](docs/agent-ds-contract-handoff.md),
[technical design](docs/enhancements.md), and [verification record](docs/managed-agent-verification.md).

The Apple on-device adapter is registered by `AddLlmConnect`. Its separately built Swift
helper and release gates are described in the [connector design](docs/apple-foundation-models-design.md).
This source implementation has not been published as a signed helper release.

Google Vertex AI accepts Google text and Claude model IDs through shared provider profiles.
Register `AddLlmConnectVertex()` alongside `AddLlmConnect()` and supply a typed `LlmConnection.Vertex`
with project, location and ADC or service-account authentication. `ApiKey` selects global projectless
express mode for Gemini only; `ProjectId` and `Location` are null and the protected credential holds
the Google Cloud API key. Express mode has no model listing. Catalog entries report unknown invocation
access, not regional eligibility or permissions. Unknown limits and Claude strict-schema support stay
unknown; an explicit schema capability override enables a native schema attempt. The adapter assembly
is included by the Connect package project. See the [technical mapping](docs/google-vertex-ai-tech-design.md).
Live cloud qualification remains; the shared contract requires host migration.

The Gemini API (Google AI Studio) adapter is registered by `AddLlmConnect` under the provider id
`gemini-api` and transport `GoogleGeminiApi`. It needs only an API key, sent in the `x-goog-api-key`
header, and a fixed endpoint. It shares the Gemini request, stream, schema and continuation code with the
Vertex Gemini route. Both routes use native `responseFormat` with their documented object/array
shapes. Both carry the original JSON Schema unchanged, including unfamiliar annotations, boolean/null
enum members and reference siblings. Older responseSchema/responseJsonSchema subset lists do not
establish an inability on this field. Missing model support remains Unknown and permits a native
attempt. Explicit Unsupported overrides still select only a caller-permitted fallback or refuse.
`ProviderJsonSchema` names the selected request mechanism, not proof of every native constraint;
the result separately reports local evaluation and validity. No automatic retry after a provider
rejection runs. The native field is documented in the
[GenerateContent reference](https://ai.google.dev/api/generate-content#v1beta.TextResponseFormat).
Provider options go into
`generationConfig` and cannot replace fields written by the canonical builder. See the
[Gemini API design](docs/gemini-api-tech-design.md) for model discovery, capabilities and limits. Live
qualification remains.

Gemini API discovery accepts syntactically valid `models/` entries whose catalog methods include
`generateContent`; a `gemini-` brand prefix is not required. Known Gemini non-text output families
retain their decoder exclusions. Vertex likewise accepts Google text model IDs without requiring
that prefix. Unknown IDs retain unverified invocation access; neither discovery nor a local payload
proves model availability on the selected account or Vertex location. The two documented hosted
Gemma 4 IDs, `gemma-4-31b-it` and `gemma-4-26b-a4b-it`, map reasoning to native minimal/high choices.
`Medium` maps to minimal, `Maximum` to high, and `None` to minimal. Google documents their Gemini API
text and thinking protocol in [Gemma on the Gemini API](https://ai.google.dev/gemma/docs/core/gemma_on_gemini_api).

Gemini API prompt images accept PNG, JPEG, WebP, HEIC and HEIF through `inlineData`. An explicit MIME
type remains unchanged. Without one, the shared attachment detector identifies HEIF/HEIC from the
leading ISO BMFF file-type box's compatible brands, as registered by
[IANA](https://www.iana.org/assignments/media-types/image/heic). It does not infer the type from a
filename or scan outside that box. Detection does not widen another adapter's format contract.
The Gemini API filter has no per-image 7 MiB limit; its
[image guide](https://ai.google.dev/gemini-api/docs/generate-content/image-understanding) describes a
total request limit, which the provider adjudicates. Connect does not upload or split an inline
request automatically. Vertex permits `image/*` MIME values through its existing inline writer,
including unverified formats. Its [inference reference](https://docs.cloud.google.com/gemini-enterprise-agent-platform/reference/models/inference?hl=en)
lists PNG/JPEG/WebP without establishing a negative for every omitted type. Gemini API's positive
HEIC/HEIF evidence does not qualify Vertex. Vertex retains its existing 7 MiB cutoff; its reference
documents a 7 MB image bound. Connect does not apply that bound to Gemini API images.
Neither route applies that image bound to UTF-8 text documents. Their existing 50 MiB PDF bound
remains separate. Each route's cache-prefix preparation uses its own prompt image MIME admission,
including Gemini API Files uploads and Vertex inline/Cloud Storage staging. A resource also used
in a tool result follows the narrower function-response MIME contract. The additional prompt
formats do not widen function-response media.

Gemini API and Vertex Gemini generation errors retain observed HTTP status and retry intervals,
including provider error objects inside successful HTTP responses. Their diagnostics keep fixed
messages and omit raw provider bodies, provider codes, request IDs and nested payloads.
Context-overflow classification and already-delivered partial text remain intact; no retry or
successful completion is produced for an error response. Focused managed regressions exercise
the real adapters with scripted HTTP, including Vertex express API-key authentication.
These regressions do not qualify live provider endpoints or OAuth.

### Contract migration

Continuation state is now Common's opaque `LlmContinuationState`, carried by both portable and
Connect messages/results as `ContinuationState`. Continuation envelopes use format 2; context captures
use format 3. The owned context codec accepts the closed format-2 binary/function shape and upgrades
it without changing transcript revision, IDs, order, bytes, omissions or opaque continuation. Old
`ResponseState` JSON members and unversioned captures are rejected. Hosts must migrate stored records
before adoption; see [DSA-68 handoff](docs/agent-ds-contract-handoff.md). Explicit state omission is
reported by `OmittedContinuationStateCount`; default handling rejects unbound state.

### JSON metadata

Each metadata getter owns its options and type cache. Callers can retain the returned metadata for
their generation and release it at retirement. Rejected foreign types have no static metadata cache.
Common binary-data and Resources caller-service converters are taken from their existing owned
type metadata. Converter composition does not use the reflection-oriented `GetConverter` API.
Retained chat adapters append request, replay, tool and embedding JSON nodes through the nongeneric
`JsonArray.Add(JsonNode)` overload. This avoids reflection serialization without changing node order
or payload values. Typed HTTP registration declares the public constructors required by the framework's
typed-client factory so trimming preserves its construction path.

`ConnectLlmJson.PortableRequest` closes the Common request graph, also supported by `ChatRequest`
metadata. It adds no arbitrary CLR subtype registry and
does not change Common's metadata. Owned options compose Common's exact runtime-resource and
authorization refusal converters; live authorization cannot be serialized or reconstructed as
data, and serialization never invokes its callback. The separate credential codec keeps its
existing Web names and null omission.

The Grok subscription adapter maps ImageGeneration and model-offered WebSearch and SocialSearch. Web controls
accept allowed or excluded domains under `filters`, at most five in either list, with no image controls or location.
X controls accept only date bounds, projected as `yyyy-MM-dd`. DocumentSearch, CodeExecution and RemoteMcp refuse
before provider effects. The shared Responses observer still retains the service's actual hosted output items.
Pure preflight counts every function and hosted wire tool before effects, including session-added search.
Other built-in adapters and unmapped hosted kinds reject unsupported options before provider effects.
Native start/resume also rejects these unmapped options. Existing Anthropic server-tool configuration
is separate and does not advertise the new hosted API.

The same Responses parser observes hosted calls in streamed and complete JSON responses. A generic
added item produces Started only with a usable actual provider ID. The first terminal output supplies
ordered Completed observations; it does not infer progress phases or successful execution. Every
ID-less occurrence remains distinct. Repeated usable item IDs identify one call within that
response. Unreported identity, status, result collections and scores remain null; actual empty
collections, strings and numeric zero remain exact. The provider's documented omitted-score default
does not replace the shared API's unreported-score observation. Full terminal output remains unchanged
in opaque replay. Result-only `CompleteAsync` retains its terminal chat result; callers enumerate
`StreamAsync` for hosted observations. This mapping does not qualify live OAuth acceptance or
provider-specific file-search phase events. X custom calls use the
[documented `xs_` call-ID prefix](https://docs.x.ai/developers/tools/tool-usage-details) as positive
SocialSearch evidence and retain the actual name and literal string input. The observer also recognizes
the published search names. Actual call IDs stay separate from opaque item IDs; an item's `xs_` prefix
alone does not classify its call. Attachment-search `as_` and unknown prefixes with unknown names remain
unmapped, with their complete items retained in opaque replay. Image progress uses only
documented lifecycle events. Unpublished understanding output/event shapes remain unmapped.
The observer also recognizes `x_search_call` through the same lifecycle owner. The current guide
documents `custom_tool_call` with an `xs_` call ID; the separate `x_search_call` item shape remains
live-unqualified. Its observed ID and status
remain exact. Undocumented input/output facts stay null; the complete native item remains in opaque
continuation. Classification does not qualify live OAuth X Search execution.
Two bounded OAuth requests against production source `8db9204` use Global/Default
`grok-4.20-0309-non-reasoning`, X Search restricted to the `xai` handle, 64 requested output
tokens and two hosted turns. The prompt asks for one recent Grok post's topic in at most five
words, or `none` if no post is found. Streaming reports three actual `x_keyword_search` starts
and matching terminal calls with status `completed`, eight separate citation occurrences and
the answer `Voice Agent Builder`; usage is 5162 input and 76 output tokens. Nonstreaming reports
one terminal `x_keyword_search` and one `x_semantic_search`, both `completed`, without fabricated
starts; it answers `none` and emits no citation events, with 2613 input and 89 output tokens.
Both chat results are `Completed` with finish reason `completed`. These runs qualify selected
OAuth X Search execution in both modes, not the native `x_search_call` item shape, post contents,
date filters, understanding controls, output-token-cap enforcement or other models/routes.
The lack of citation events does not establish provider-reported absence. The tester uses its own saved
OAuth credential without retries or account/route fallback.
Two bounded OAuth requests on Global/Default `grok-4.20-0309-non-reasoning` use the CodeExecution
descriptor to calculate `19 * 23`. Streaming reports an actual `code_interpreter` start and a
terminal call with status `completed`; nonstreaming reports the terminal call without a fabricated
start. Both return `437`, chat status `Completed`, finish reason `completed`, and usage of 1613 input
and 17 output tokens. Caller bounds are 128 output tokens and two hosted turns. This qualifies that
selected code-tool route in both modes, not downloadable artifacts, progress events or other hosted
families. The tester uses its own saved OAuth credential without retries or account/route fallback.

Citations preserve first-observed annotation order and suppress terminal copies of the same occurrence.
Grok does not request `no_inline_citations`; sources can appear inline in reply text (D18).
Offset units are unqualified, so positions remain absent.
Two bounded OAuth requests against source `8db9204` use Global/Default
`grok-4.20-0309-non-reasoning`, WebSearch restricted to `docs.x.ai`, 64 requested output tokens
and two hosted turns. Each reports three actual terminal web calls with status `completed` and
eleven citation occurrences. Streaming also reports three actual starts; nonstreaming invents
none. Both return `Code Execution Tool | xAI` with separate citations and no inline citation
links, chat status `Completed` and finish reason `completed`. Streaming reports 11905 input and
75 output tokens; nonstreaming reports 11905 input and 135 output tokens and adds explanatory
prose. These runs qualify the selected web-search and citation route in both modes, not exact
title-only formatting, output-token-cap enforcement, positional offsets or other models/routes.
The output-token discrepancy remains unresolved; controlled outgoing-request tests verify the
requested limit is sent unchanged. The tester uses its own OAuth credential without retries or
account/route fallback.

Code outputs preserve actual code, logs and image URLs without fetching remote
artifacts. MCP arguments, output and error remain actual JSON values, including JSON strings.
Generated images decode bare base64 and identify JPEG/PNG from bytes. Without Resources they carry
copied bytes. With Resources they use the existing admitted scope and staged resource writer,
atomically commit-and-select, and release the owned write handle before hosted completion. The
final result carries actual ordered commit receipts. Cleanup errors preserve original failures and
uncertain mutation facts. The admitted hosted preparation frame retains the same receipt list through
terminal parsing, publication and owned cleanup. A later malformed output or failed publication
preserves earlier committed images in the existing resource failure carrier; it does not roll them
back or publish a successful chat result. Borrowed authority detachment does not erase commit facts.
Operation-local MCP secrets reach existing HTTP, SSE, protocol, model and
terminal diagnostic sanitization; retained opaque terminal output is not rewritten.
Managed sessions snapshot hosted data, borrow authorization and preserve actual streamed hosted
and citation observations under the invocation RequestId; they never route hosted observations to
the host function handler. Terminal chat/portable results preserve ordered citation lists.
Completed hosted calls preserve independently nullable observed IDs and status through the same
snapshot and nested agent/realtime metadata. Actual empty or whitespace strings remain unchanged;
they are not usable lifecycle correlation IDs. Owned portable JSON requires both nullable members
explicitly. Started/progress events still require usable actual IDs and observed lifecycle facts.
Completion reports an actual terminal item, not successful tool execution or an inferred start.
Facade generation, input counting and request measurement freeze hosted descriptors before the
first connection-source await. Later caller mutations cannot change admitted filters or headers;
authorization stays borrowed. Owned hosted-event metadata requires actual scope and observation
facts, including explicitly reported compaction revisions. Nested hosted image output retains
Common's property-specific null-byte refusal and its new closed phase/action enum policies.
Managed disposal shares its completion across callers, drains the producer even after cancellation
callback failure, then releases borrowed hosted authorization without disposing it. Cleanup failures
remain visible to every disposer. These controlled contracts do not qualify provider wire mappings
or native execution. Each accepted terminal model result projects one
`LlmAgentCitationsSnapshotEvent` with its actual request and turn IDs and a copied ordered citation
list, including non-successful result statuses. The snapshot precedes tool execution and turn
completion. It is a terminal-collection observation, not an append, deletion or clearing instruction.
An empty terminal collection does not prove provider-reported absence. Streamed individual citation
observations are not compared or deduplicated against terminal snapshots. The new concrete event has
an exact type-name getter on `ConnectLlmJson`; it adds no portable base-event union.

`ConnectLlmJson` exposes owned source-generated metadata for closed LLM connections, saved setup
configuration, chat messages/requests/results and captured agent context. Pass the matching typed
metadata to `JsonSerializer`. Credential `Serialize`/`Parse`, context-entry equivalence and continuation
scope hashing use owned metadata. Credentials retain their camel-case Web format and null omission;
captured context retains its property names, Common tool-output discriminators and
binary/null-versus-empty representations. Resolved internal services are never serialized.
Connection and setup metadata retain typed Vertex, Azure, Chat Completions and xAI options, including
unknown numeric modes for validation by their owning adapter; writing them does not enable a provider.

Format-3 context requires an explicit `SelectedResources` list. Its optional
`ResourceSelectionVersion` records the caller's workspace, logical session and selection revision,
independently of transcript revision. The codec copies and validates this data without accessing
storage. Legacy captures upgrade to empty membership with unknown provenance, not revision zero.
Byte-excluded captures retain logical references and the selection snapshot. A session opening replaces a
stored selection with the caller's current one, or clears it without caller access; it never installs or deletes
caller membership.

Portable context and its owned codec preserve a final incomplete mixed tool-call group with each
actual completed same-kind result. No later transcript message can follow an unresolved group.
Missing results do not imply an exit code, timeout, rollback or an unstarted call. Runnable imports,
next turns, provider projection and `ReplaceContextAsync` still require settled matching groups.
Callers reconcile with actual results and revision-checked replacement; the library does not replay
an executor, manufacture outcomes or prune the transcript. This data contract does not enable managed
shell execution.

Chat messages and turns use the Common ordered binary/native-file/workspace-resource attachment
union. `LlmChatRequest.Resources` and its Common projection preserve the same borrowed authority.
Owned request JSON refuses non-null authority and request diagnostics never traverse it.
Current built-in routes reject unimplemented references and authority before refresh, catalog,
provider, process or model execution. `SupportsFileReferences` defaults to `Unknown`; capability overrides
cannot enable a missing native-file route.

Chat admission copies each ordered attachment list, binary payload and omission list before
connection resolution, refresh or catalog work. Direct Vertex, Bedrock Converse and SelfHost
requests use the same snapshot before authentication, model resolution or engine acquisition.
Native Claude turns snapshot input lists and attachments before control acknowledgments and turn-start events;
managed turns use the same copier before their asynchronous work.
Caller mutation after admission cannot change the attachment graph used by the request builder.
Runtime resource authority stays borrowed; snapshotting does not fetch or materialize references.
The same admitted request freezes string stops, provider-option JSON and structured-output schemas
before connection resolution; disposing the caller's JSON document does not invalidate that snapshot.

Gemini API and Vertex local text estimates serialize requests with `ConnectLlmJson.ChatRequest`.
The projection retains default property names/order, numeric enums, explicit nulls and escaping.
Opaque continuation state and media still produce unknown local occupancy; this metadata does not
turn a local estimate into a provider count or admission check.

Contexts and scope helpers remain internal. The package installs no reflection fallback for these
families. Runtime settings, one-way secret edits, executable handlers and undiscriminated agent/event
bases are not persisted snapshots. Provider CLI/HTTP protocol writers remain separate owners;
metadata for these closed families does not establish retirement of an active provider session.
The LLM serializer retirement check loads Common and Connect LLM privately into one collectible
context. It exercises the owned LLM metadata roots and a result with nested Common usage and tool
data. Its weak-reference checks cover both assemblies, metadata, options and result data after
unload. The separate Resources case privately loads Common, LLM, Image, Video and Resources.
It exercises the Resources metadata getters and round-trips a batch page with nested chat usage,
tools and a generated-resource commit/selection receipt. It checks release of all five assemblies,
the owned metadata/options and the nested result/receipt objects after unload. These checks do not
execute or retire a provider or native engine.
The combined Common/Connect case exercises all 504 declared metadata getters across the nine fixed
owners in one private generation. It retains the same batch-result roundtrip and checks release of
all nine assemblies and their metadata/options. Test-only source references supply Audio, Realtime,
STT and TTS; shipping dependencies stay unchanged. Getter execution does not serialize every possible
request/result value or establish Tensor metadata, active provider lifetime, NativeAOT or release-package
qualification.

Retained chat protocol builders write stop arrays, model aliases, numeric/string control IDs and
Responses output joins through structured JSON nodes and elements. Vertex argument-path identity
uses ordered string/index JSON segments and the BCL parser for escaped names. These wire paths
do not call the unconfigured serializer or depend on its reflection defaults. Portable contracts
continue to use their existing owned metadata; protocol projection adds no public metadata roots.
Compatible-chat reasoning continuation wraps its message through the JSON node-array constructor,
not the generic collection-expression append path.
Retained JSON-object HTTP requests use the same `RequestJson` owner with private DOM metadata
generated by the existing internal context. Request bodies remain streamed by `JsonContent`; the
helper does not copy them into an extra full-body string or byte buffer or use reflection defaults.

Apple helper generation and cancellation commands use their own internal generated metadata,
including the recursive typed schema plan. The helper protocol retains its camel-case Web names,
null omission, escaping, sampling fields and maximum JSON depth of 128. No helper/session lifetime
behavior or Apple private framework format is changed by this serialization boundary.
Schema-guidance property paths use the existing Connect generated string metadata. Quoting and
escaping do not depend on reflection-based serialization or add a public metadata root.

PocketTTS voice responses and JSON bridge requests, and TangoFlux generation requests, use their
provider-owned internal generated metadata. PocketTTS retains explicit null voice/speaker fields;
TangoFlux still omits absent seed/cache fields while preserving explicit zero/false values. These
private wire types do not add public request contracts or change HTTP/cancellation behavior.

ComfyUI output state retains its existing Pascal-case persisted format through separate owned
metadata. Prompt/delete requests and diagnostic payloads use their own Web-format metadata; workflow
JSON remains opaque DOM data, including nulls and unknown node fields. Serialization does not change
workflow interpretation or progress/cancellation lifetime.

Claude and Codex's private OAuth JSON requests are written as DOM data, not reflection over anonymous
library types. Their field names/order, escaping and explicit null fields remain unchanged; Codex's
authorization-code exchange remains form encoded. This does not change either authentication flow.

Bedrock serializes AWS `Document` values through the SDK's public `DocumentMarshaller`, without
reflection metadata. Recursive and numeric JSON retains the SDK's representation. An absent nullable
document writes `{}`; an explicit JSON `null` remains distinct. Bedrock and AgentCore read documents
through the SDK's public `DocumentUnmarshaller` after strict BCL JSON validation. A private envelope
supports primitive roots through the SDK's container-oriented context and is removed before use.
The complete captured input supplies the reader buffer size; EOF returns its rented buffer even
when mapping fails. These paths do not use attributed reflection deserialization or copy a parser.

Claude CLI input envelopes and Antigravity chat/agent stdin envelopes use DOM writers with the same
field order, escaping, explicit null fields and final newline. Claude input keeps text before ordered
attachments. Missing Antigravity tool parameters and failed managed-tool parameters remain empty JSON
objects. These writers do not serialize library-owned anonymous types or change process/session lifetime.

Native Claude agent initialization, controls, user turns and MCP envelopes use private DOM writers.
Tool manifests retain ordered schemas and boolean `anthropic/alwaysLoad` metadata. MCP responses keep
numeric and string request IDs distinct; input/tool parts retain their ordered media/resource fields,
explicit nulls and omitted optional fields. The process wrapper owns the final newline. JSON writing
does not change host-tool cancellation, response routing or session retirement.

Tool definitions carry no strictness flag; see [Conversation contract](#conversation-contract).
Gemini API and Vertex Gemini resolve native tool formatting inside the shared Google writer.
With functions present, implicit or explicit Auto selects `VALIDATED` for the entire tool set,
including ordinary tools. Consumers do not select a provider format or arrange strictness flags.
Both mixed-tool orders produce the same mode; no declaration cancels another.
Required and Specific use `ANY`, Specific retains its allowed function name, and None uses `NONE`.
Schemas retain the inputs each tool actually accepts, including optional properties.
Google's writer does not send per-tool `strict` fields. Native enforcement can therefore strengthen
an ordinary tool or provide less than a requested strict guarantee without denying Auto.
Strict-input capability remains Unknown because enforcement varies by model, schema and mode.
Older Vertex VALIDATED does not guarantee required-parameter presence; the adapter still attempts
Auto and does not force a call. The managed loop validates returned arguments against the original
tool schema before invoking the caller. Native mode selection does not replace that validation.
Cache-read/write and reasoning usage details are optional subsets of normalized input/output totals.

### Vertex ConsoleTester

Use the existing console application with ADC configured on the machine:

```bash
dotnet run --project Connect/tools/Darkspyre.Inference.Connect.ConsoleTester -- vertex-probe --provider vertex --google-project YOUR_PROJECT --google-location global --model google/gemini-3-flash-preview --case all --repeat 1 --timeout-seconds 120
```

Use `anthropic/claude-sonnet-4-6` to exercise the Claude route. Cases are `text`, `structured`, `tools`,
`agent` and `cancel`. Calls are explicitly metered; failed cases are not retried. Unavailable auth or
catalog discovery produces a visible skip. The existing `prompt --image` and `prompt --document`
commands cover attachment experiments separately. These live probes are not normal test gates.

Express mode reads `VERTEX_API_KEY` during explicit login and stores it in the operating system vault.
The key never appears in a URL or command argument. Do not pass a project or location in this mode:

```bash
dotnet run --project Connect/tools/Darkspyre.Inference.Connect.ConsoleTester -- login --provider vertex --google-authentication ApiKey
dotnet run --project Connect/tools/Darkspyre.Inference.Connect.ConsoleTester -- vertex-probe --provider vertex --google-authentication ApiKey --model google/gemini-3-flash-preview --case all --repeat 1 --timeout-seconds 120
```

The express status check validates configuration only. Generation verifies remote model access.

### Gemini API ConsoleTester

Store the key once from the environment, so it never appears in a command argument, then run the probe:

```bash
GEMINI_API_KEY=YOUR_KEY dotnet run --project Connect/tools/Darkspyre.Inference.Connect.ConsoleTester -- login --provider gemini
```

```bash
dotnet run --project Connect/tools/Darkspyre.Inference.Connect.ConsoleTester -- gemini-probe --provider gemini --model gemini-3-flash-preview --case all --repeat 1 --timeout-seconds 120
```

The cases match the Vertex probe; both commands run `GoogleProbe`. Calls are metered against the key's project.

| Package | Purpose |
|---|---|
| `Darkspyre.Inference.Common` | Typed LLM, image, and decision requests, results, capabilities, streaming events, provenance, and failures shared by Connect and Tensor |
| `Darkspyre.Inference.Connect` | LLM, Bedrock/AgentCore, ComfyUI, PocketTTS and TangoFlux connections |
| `Darkspyre.Inference.Tensor` | In-process LLM, image, typed-decision and Whisper speech transcription |
| `Darkspyre.Inference.UI.Settings` | Host-placed Avalonia editor using only Common setup contracts |

Connect, Tensor and UI.Settings depend on the exact synchronized Common NuGet; none carries
another product's DLLs. UI.Settings does not depend on Connect or Tensor. The release-candidate
suffix reflects the .NET 11 RC ASP.NET dependency, including on the private package feed.

There is no `Darkspyre.Inference.Connect.Common` package or assembly. Connector-only endpoint,
authentication, provider-management, and job contracts remain in their capability assemblies. Only
semantic inference contracts that are also implemented by Tensor belong in
`Darkspyre.Inference.Common`.

Connect's internal chat lane uses Common's LLM primitives directly: the stream events, tool definitions, tool
choice, tool calls, tool results with `LlmToolOutput`, attachments, structured-output types, completion
status and `LlmUsage`, all in `Darkspyre.Inference.Common.LLM`. Connect adds only what carries
Connect-specific data: `LlmChatMessage` and `LlmChatResult` with provider diagnostics and the shared continuation envelope,
`LlmChatCompletionEvent` for the final result, and `LlmModel` with provider capabilities. An attachment's
`MediaType` is used as given; without one, the adapter detects the type from the bytes.

## Portable inference lane

`LlmProviderService.Bind(connectionId)` is internal. It binds Common's `ILlmInference` contract to one Connect
connection for code inside Inference; hosts use agent sessions.

Image workflows expose the same boundary through `ImageProviderService.Bind(connection, workflow)`.
Binding is explicit and retains exactly one backend. Neither adapter retries through a different
provider nor falls back from Connect to Tensor.
The bound Common LLM facade maps provider HTTP 401 to `AuthenticationRequired` and HTTP 403 to
`AccessDenied`. It preserves the original provider exception, safe message and diagnostics as the
inner exception. These outcomes remain distinct from execution failure; the facade neither logs in
nor retries automatically.
The returned `IImageInference` exposes asynchronous disposal directly. Callers own its retirement;
the bound instance still borrows its collaborators and does not dispose them.

`LlmChatResult.ModelId` and `ModelRevision` carry actual adapter observations. Both are nullable.
The portable result preserves them in Common provenance; it does not substitute the requested
model. A self-hosted result preserves its actual loaded model identity and separately reported
revision. Existing result metadata and nested batch/deferred graphs carry both optional fields.

HTTP chat adapters read the returned `model`. Gemini and Vertex Gemini read the actual
`modelVersion` unchanged as the model identity, without deriving a separate revision from its name.
Missing observations remain unknown. Conflicting observations within one HTTP response, malformed
values and protected credential/header values refuse publication through a fixed safe error.
The shared model-observation reader includes actual sent headers, including borrowed-client defaults,
as well as selected credentials and explicit operation context. Gemini and Vertex use this same reader.
Claude streaming reads the most recent actual main assistant message or message-start observation;
init selections, subagent messages and aggregate usage keys do not supply identity. Its non-streaming
terminal format and the current Apple, Antigravity and Bedrock protocols report no model identity.

### Reasoning levels

`LlmInferenceRequest.ReasoningLevel` takes an `LlmReasoningLevel`: `None`, `Minimal`, `Low`, `Medium`,
`High`, `ExtraHigh` or `Maximum`. On one model, each level asks for at least as much reasoning as the
level below it; a level does not cost the same on two different models. Null leaves the provider's
default in place. Each model reports the levels it accepts in `LlmCapabilities.ReasoningLevels`, and
`SupportsReasoning` is `Supported` when that set contains a level above `None`, `Unsupported` when
it contains only `None`, and `Unknown` when the set is empty. Direct Codex, Gemini API, Vertex Gemini,
Antigravity CLI, Grok Responses/deferred Chat and the Claude transports
map desired effort inside their adapters. Other adapters currently reject a level outside their declared set with
`InferenceFailureCode.InvalidRequest` before any provider call. Each adapter owns its provider's wire form.
`LlmCapabilityOverrides.ReasoningLevels` replaces a transport's set for a connection or one model,
and the setup editor exposes it as one Default/Enabled/Disabled choice per level. The design is
`docs/llm-reasoning-level.tech-design.md` in the repository root.

Anthropic API-key, Gemini API and ChatGPT subscription connections read each model's levels from the
provider's model catalog, which `LlmModelListCache` keeps. The first capability query or generation on a
new connection version therefore lists the connection's models once. When that listing fails, the adapter
uses its own model-family table and the operation proceeds.

Codex also reads `supports_reasoning_summary_parameter` and `input_modalities` from the selected
model's catalog entry. A false summary flag disables public reasoning and summary requests without
changing effort or retained state. An explicit modality array without `image` disables prompt images
and image tool results in chat and managed-agent sessions. Missing fields retain the adapter defaults.
Connection and model overrides keep their existing precedence over these catalog facts. Other tool
content and prompt documents keep their existing support.

Direct Codex carries the caller's desired effort unchanged until its native payload projection.
`OpenAiCodexLlmClient.MapReasoning` owns explicit mapping rows for all seven caller levels. Each row
lists the same native level first, then lower native levels, then the below-minimum choices from
lowest to highest. The first catalog/override-listed native choice in that explicit row is sent.
For a model listing `none` and `high`, `Maximum` sends `high` and `Medium` sends `none`.
When a model lists only `low` and `high`, `None` sends `low`; this does not disable reasoning.
Null leaves the provider default unchanged. Missing or empty native-level observations retain
Unknown support and send an explicit requested effort with its existing native spelling.
No shared distance/rank calculation or host-side native selection runs. The caller request and
reported native-level catalog remain unchanged. Summary requests follow the mapped native effort.

Grok's direct Responses and deferred Chat writers use `XaiModelProfiles.MapReasoning`. The Grok-owned
literal rows select the requested native choice or a lower offered choice; a request below every
offered choice selects the lowest one. `Maximum` sends `xhigh` for Grok 4.6/4.7 and `high` for 4.5.
`None` sends `low` on these reasoning models; it does not disable their reasoning. Catalog observations
and explicit overrides retain all seven native spellings, including `none`, `minimal` and `max`.
Missing or empty observations remain Unknown and send the requested spelling unchanged. Null sends
no effort, leaving the provider default unspecified. Validation, batch projection and managed requests
use the same adapter mapping without changing caller effort or reported model choices.
The known multi-agent model's effort field selects agent count, not reasoning depth: it offers no
reasoning levels, so a requested level is dropped there and `AgentCount` selects the effort (see "Agent count").

Gemini API and Vertex Gemini use `GeminiModelProfiles.MapReasoning` before admission and native
projection, including token counting. Its explicit rows map `ExtraHigh` and `Maximum` to `High`,
and map `Medium` to `Low` for Gemini 3 Pro's low/high choices. Named-level models map `None` to their
lowest native level; this does not disable thinking. Gemini 2.5 retains its token budgets: Low 1024,
Medium 8192, High 24576 for Flash or 32768 for Pro. Flash maps `Minimal` to its zero budget;
Pro maps `None` and `Minimal` to Low because it cannot disable thinking. Null leaves effort unset.
Effective native-level overrides reach both admission and payload construction. Missing profile
facts remain Unknown and attempt the existing `thinkingLevel` vocabulary with minimal/low/medium/high
choices. An explicit catalog `thinking: false` offers only `None` and sends no thinking configuration.
Thought-summary requests follow the mapped native effort. These native forms follow Google's
[GenerateContent thinking guide](https://ai.google.dev/gemini-api/docs/generate-content/thinking).

OpenAI Responses levels are per model, from a table of verified model pages, because the OpenAI Models
API reports no capabilities. `None` sends `reasoning.effort: "none"` and `Maximum` sends `"max"`. A
verified reasoning model accepts `temperature` only with `ReasoningLevel = None`; a request without a
level counts as reasoning, because these models default to effort `medium`. A model without a table row
offers `Minimal` to `ExtraHigh` and accepts temperature at any level.

## PocketTTS speech

`ITtsClient` accepts `TtsRequest` and returns `TtsResult` with WAV bytes and provenance. Register
`services.AddTtsConnect()`, then bind a host-supplied `TtsConnection` with `TtsProviderService`:

```csharp
await using ITtsClient speech = ttsProviders.Bind(new TtsConnection
{
    Endpoint = new Uri("http://127.0.0.1:8020"),
    PocketTts = new PocketTtsEndpointOptions { Protocol = PocketTtsProtocol.Multipart }
});
IReadOnlyList<TtsVoice> voices = await speech.ListVoicesAsync(cancellationToken);
TtsResult clip = await speech.SpeakAsync(new TtsRequest
{
    Text = "The next room is quiet.",
    VoiceId = selectedVoiceId,
    Format = TtsAudioFormat.Wav
}, cancellationToken);
// The host owns playback or storage of clip.Audio.
```

The connector never starts a bridge, installs models, bypasses TLS validation or manages playback.
The endpoint is external even on localhost. Multipart sends `text`, `output_format` and the
configured voice field (default `voice_url`). It rejects non-default speed. BridgeJson sends `text`,
`voice`, `speaker`, `speed` and `response_format`. PocketTTS advertises and accepts only WAV.
Voice discovery uses `/voices`, falling back to `/health` only after a successful empty list. It
preserves both voice-option objects and voice-name arrays; it never invents fallback voices.

`SetupDefinitions` and `CreateSettingsManager` use the shared typed setup editor. Endpoint, timeout,
protocol, paths, protected headers, voice and speech defaults are editable. `TtsSetupChanges`
contains the host's identity/version, the typed configuration with no header values, and explicit
protected-header sets/removals. Untouched headers are omitted from mutations, not deleted. Save
does not list voices or synthesize. Endpoint/voice verification and potentially billed synthesis
are separate actions; protocol changes update the speed constraints and mark test evidence stale.
The host retains its usual atomic settings/secret commit responsibility.

HTTP authentication/access errors, timeouts and malformed responses have distinct
`InferenceFailureCode` values; caller cancellation remains `OperationCanceledException`. Requests
are never automatically retried. Raw response bodies, prompts and protected headers are not logged
or copied into errors. Registration disables automatic redirects and gives each request its
connection timeout. If constructing `PocketTtsHttpProvider` with your own `HttpClient`, disable
automatic redirects yourself so custom secret headers cannot follow a redirect to another origin;
the host owns that client's lifetime. Endpoint paths cannot select a different origin.

### TTS data and streaming contracts

`TtsRequest` and `TtsSetupConfiguration` carry optional language, sample rate in Hz, bit rate in
bits per second and timestamp selection. `TtsConnection.XaiDefaults` owns saved typed
`XaiTtsOptions`; request options inherit each null member, while false, zero and empty maps
override it. The owned setup codec rejects the removed setup-level `Xai` field instead of
silently discarding saved values. The xAI options preserve nullable
latency/normalization controls and an ordinal text-replacement map. Absent and explicit empty
maps remain distinct. PocketTTS rejects every explicit new control, including `false`, before
HTTP. Its settings preserve the controls but refuse unsupported test/save actions.

`TtsResult` carries nullable duration and ordered `TtsTimestamp` observations. Each timestamp
retains actual text and the observed start/end timeline; it does not invent text offsets.
Streaming timestamps use the observed session timeline, not a per-chunk reset. Missing timing
stays null; observed empty remains empty. Audio, timestamp lists and replacement maps are copied
at their data boundary, including `with` updates. Diagnostic strings omit content and credentials.

`ConnectTtsJson` exports exact type-name metadata getters for TTS connection, setup, request,
capability, result, option and concrete event data. Its closed generated graph composes Common
provenance/failure data without reflection fallback. Non-null runtime clients/providers/sessions
refuse serialization and restoration without inspecting their callbacks; null remains null.
The provider-private Pocket wire codec remains separate and preserves its existing request bytes.
`ConnectTtsJson.TtsStreamEvent` owns the closed audio, completion, error, replacement-update and
utterance-clear union. Replacement updates and clear commands complete after send; their
observations preserve the actual acknowledgment. The convenience stream returns all events.
Replacement updates copy caller maps inside session admission before waiting to send. Events
reader admission refuses a retired session without holding a lease across event yields.

Bound `ITtsClient` instances support synchronous and asynchronous disposal. Pocket clients
fence new calls, cancel and drain admitted work, then drop borrowed HTTP/provider/resource
references. A nonnull resource argument selects artifact mode: Pocket commits validated WAV
bytes and session membership atomically before returning `TtsResult.Resource`. It does not
invent a filename. Null resources preserve standalone bytes. The caller owns the access and
returned clients; facade retirement does not retire handed-off clients. Settings retire their
temporary clients and do not bind resources for readiness checks or sample clips.
Pocket exchanges own request bodies before the first send fence and clean response and request
owners once. Refused cleanup retains the actual unsafe owner and reaches the client's cached
retirement failure. Independent operation and cleanup errors remain ordered and intact.
TTS and hosted-STT settings retire every temporary client, including a binding rejected by a
late owner fence. They preserve genuine cleanup failures across repeated disposal after draining
work and dropping borrowed collaborators. Ordinary action failures do not poison retirement.
Retained cleanup failures can retain the refused client's own collaborator and metadata graph.

`ITtsStreamingClient` and `ITtsStreamingSession` declare reusable disposable streaming and one
ephemeral iterator wrapper. Opens and wrappers require explicit positive `InferenceStreamLimits`
for event count, buffered payload bytes and assembled message bytes. These caller-local budgets
are not provider limits. Session commands complete after ordered transport send, not terminal
acknowledgment or event consumption. One event consumer drains the feed; disposal closes the
feed before draining admitted work. No automatic replay/reconnect is part of these contracts.

PocketTTS implements neither streaming interface, advertises the streaming capabilities as `Unsupported` and
leaves unknown rate lists null. `AddTtsConnect()` registers PocketTTS and `XaiTtsProvider`.
The xAI adapter implements REST synthesis and reusable streaming sessions; OAuth and actual
provider readiness remain unqualified. `Bind(TtsConnection, XaiFamilyBinding?,
IInferenceResourceAccess?)` borrows the entered owners without acquiring credentials or making
requests. Connection headers are copied, and xAI selection/authorization must match the binding.
PocketTTS refuses the new xAI authority fields before HTTP; existing static-header workflows
remain supported. Runtime authority, resource access, binding factories and settings owners refuse
non-null serialization through the owned codec without invoking callbacks.

TTS settings managers fence new admissions synchronously, drain admitted actions and conditional
commits, then release borrowed collaborators without disposing them. Opening settings checks the
owner fence after reading the borrowed setup definition. Late check/test/query results
cannot verify an edited or retiring draft. Portable changes omit runtime authorization and retain
protected-header patches separately. Typed xAI setup controls preserve explicit false, zero and
empty values; setup does not automatically register an xAI provider.

The explicit xAI client acquires one OAuth snapshot per REST operation or stream opening. Voice
listing requires the binding's optional borrowed LLM facade and uses its selected HTTP/admission
owner without a second resolve or refresh. Voice observations remain ordered actual provider data,
not a separate cached model catalog. Without that facade, voice-listing capability is `Unsupported` and the
operation refuses before resolution or HTTP. REST timestamps preserve graph text and timeline
pairs; raw responses preserve their observed media type. Speech with no reported model has required
`Provenance.ModelId = null`, rather than a fabricated model identity.

Streaming commands map `text.delta`, `text.done`, `text.clear` and `session.update`; observations
preserve actual `audio.delta`, `audio.done`, `audio.clear`, `session.updated` and sanitized errors.
Resources use one incremental handle per utterance and commit bytes plus selected membership before
completion publication. Pocket and xAI speech use the same internal `SpeechResourceWriter` in the
LLM assembly for scope validation, commit/select receipts and owned-handle cleanup. Its selection
version exposes only the actual observed commit receipt; it does not query the store again.
REST synthesis and streaming utterance completion retain the complete validated
commit-and-selection receipt in failure `GeneratedResources` if writer cleanup, cancellation
or final result publication fails.
The receipt contains the actual selected membership snapshot, not a later store read.
Chunks remain transient; clear discards uncommitted staging. The handed-off
session is independently caller-owned and survives client retirement. Client and session retirement
fence, cancel, drain and detach borrowed references; genuine refused cleanup owners and failures remain
retained without retry. The convenience iterator owns its temporary session and input enumerator;
client retirement closes that session and drains input even while the public iterator is paused.
The wrapper uses the same bounded event feed. It never disposes the borrowed input enumerable.
Session retirement closes observations promptly without publishing late completion. Its cached
retirement result preserves actual late artifact commit/selection facts, including unknown acknowledgment,
and any independent cleanup refusal. Ordinary receive cancellation remains healthy.
The convenience result preserves the original resource failures once, including typed cancellation
from borrowed input; input-only operation failures do not poison client retirement.
No automatic reconnect/replay occurs. OAuth endpoint acceptance and real
voice/latency qualification remain open: the guide's latency value 2 is passed through unchanged despite
the standalone schema's narrower 0/1 declaration. Controlled tests do not qualify live provider access.
REST synthesis and transcription failures use the shared xAI HTTP classifier. Its redaction context
includes selected-connection headers, family headers and effective outgoing request headers, including
borrowed HttpClient defaults. Public errors retain sanitized provider codes, request IDs, actual HTTP
status and retry delay. They do not expose reflected protected headers or the raw speech response body.
Structured HTTP rejections retain the redacted named error explanation. Unrelated response fields
and unstructured bodies remain withheld; an absent explanation keeps the fixed speech rejection label.
REST HTTP transport failures retain their bounded, credential-and-header-redacted explanation,
`HttpRequestError` and any reported status. Both speech adapters use the same boundary. Original
exception evidence stays private; its raw message and inner graph are not diagnostic output.
Failed BCL WebSocket upgrades retain collected actual HTTP status, sanitized request ID and delta
retry delay. The shared classifier uses the selected credential, selected/family request headers and
borrowed HttpClient defaults for redaction. Private opening callbacks carry the already selected
credential; they do not reacquire it. Status 401/402 retains the shared authentication/entitlement
rules, while 400/403 cannot imply hidden body-dependent facts.
Unobserved status remains a safe transport failure. Raw bodies, headers and transport diagnostics
are not public errors; original transport evidence remains internal. Cancellation is not reclassified.
Streaming synthesis and transcription preserve the provider's error explanation through the shared
bounded redactor. Each session captures selected credentials, sent headers and borrowed-client
defaults at opening and releases that context after its receiver drains. A pre-ready transcription
error fails opening with the redacted explanation. An active-session error publishes that explanation
without forcing retirement; the provider determines whether the connection stays open.

`TtsProviderService` supports synchronous and asynchronous disposal. Retirement fences new facade
calls, drains admitted synchronous setup/binding callbacks, then releases borrowed provider references.
It does not interrupt those callbacks or replace their original result or failure. Returned clients
and settings managers keep independent caller ownership; facade disposal never disposes them or
the borrowed providers. Concurrent disposal callers share the same completion. Use asynchronous
disposal while another thread is inside a facade callback.

## TangoFlux audio

`ConnectAudioJson` exports exact type-name metadata getters for the nine public Audio
connection, generation, capability and setup data roots. Its finite generated graph preserves
optional defaults, explicit zero/false values and unknown measured duration. Observed capability
and result facts cannot be omitted or replaced with null. Unknown transport/member values refuse.
Use these getters for portable data; the private TangoFlux wire codec retains its own Web naming
and seed/cache omission policy. Audio-owned options refuse non-null live clients, providers,
settings, managers, commit targets, streams and authorization. Null runtime values round-trip;
metadata does not invoke callbacks or acquire provider resources.

`IAudioGenerationClient` accepts typed generation requests and returns WAV bytes and provenance.
Register `services.AddAudioConnect()`, then bind a host-supplied `AudioConnection` with
`AudioProviderService`:

```csharp
IAudioGenerationClient audio = audioProviders.Bind(new AudioConnection
{
    Endpoint = new Uri("http://127.0.0.1:8769"),
    RequestTimeout = TimeSpan.FromMinutes(2)
});
AudioGenerationResult clip = await audio.GenerateAsync(new AudioGenerationRequest
{
    Prompt = "Rain tapping on a wooden roof.",
    Duration = TimeSpan.FromSeconds(5),
    Steps = 20,
    GuidanceScale = 4.5,
    Seed = 42,
    UseCache = false
}, cancellationToken);
// The host owns playback, storage and its own cache. MeasuredDuration may be null.
```

The adapter sends the donor `/generate` protocol: `prompt`, duration in seconds, `steps`,
`guidanceScale`, and optional `seed`/`useCache`. Duration must be 1–30 seconds, steps 10–100 and
finite guidance 0.1–20. Invalid values fail before HTTP; they are not clamped. Omitted steps/guidance
use 10/4.5. An omitted seed remains omitted, including when `UseCache` is false. No application cache
policy, random seed, model installation, provider process, playback or soundscape logic is included.
WAV framing is validated; decoding and duration measurement are not performed, so `MeasuredDuration`
is null rather than copied from the requested duration. An absent content type is accepted only
with valid WAV bytes; an explicitly unsupported content type fails.

`SetupDefinitions` and `CreateSettingsManager` expose typed endpoint, fractional timeout, relative
generation path, protected headers, duration, steps, guidance, optional seed and a three-state
provider-cache choice. `AudioSetupChanges` carries create/edit identity and expected version, typed
settings without header values, and explicit protected-header sets/removals. Keep and omission do
not delete existing headers. The host owns the atomic configuration/secret commit. Save performs no
network request; tests use the settings' current values, and an edit marks their result stale.

TangoFlux advertises no model listing or independent health check. Its explicit test sends a real
generation request and declares network/potential-billing effects. Listing implementations and
reading capabilities do not contact the server. Audio generation and PocketTTS speech synthesis
remain distinct typed contracts grouped under the shared Audio capability. The compiled
`Common/examples/Darkspyre.Inference.Setup.Examples/AudioSetupExample.cs` demonstrates both with the
same generic settings renderer and a host mapping saved audio defaults into a request.

Authentication/access errors, timeout, caller cancellation, HTTP failures and malformed results
remain distinct. No potentially accepted generation is retried, and errors contain neither prompts,
protected headers nor raw provider bodies. Registration disables automatic redirects. Hosts passing
their own `HttpClient` to `TangoFluxHttpProvider` must also disable redirects so protected custom
headers cannot follow them across origins; the host owns that client's lifetime.

## Restore from GitHub Packages

Add the private source to the consuming repository's `NuGet.Config`:

```xml
<add key="DarkspyrePrivate" value="https://nuget.pkg.github.com/DarkspyreStudios/index.json" />
```

Keep credentials outside source control. NuGet recognizes this environment variable:

```bash
export NuGetPackageSourceCredentials_DarkspyrePrivate='Username=brandonm4;Password=YOUR_GITHUB_TOKEN'
```

The token needs `read:packages`; publishing also requires `write:packages`.

## Host integration

Register the connector and provide the host-owned connection source:

```csharp
services.AddLlmConnect();
services.AddScoped<ILlmConnectionSource, MyProtectedConnectionSource>();

services.AddLlmConnectBedrock();
```

Open a conversation with an opaque connection ID and a model selected by the host:

```csharp
await using ILlmAgentSession session = await agents.StartSessionAsync(
    connectionId,
    new LlmAgentSessionOptions { ModelId = modelId, Instructions = "Return a concise answer." },
    cancellationToken);
await foreach (LlmAgentEvent item in session.RunTurnAsync(
    new LlmAgentTurnRequest { Input = [new LlmAgentTextContent(prompt)] }, cancellationToken))
{
    // Text deltas, tool events, usage and the completion event arrive here.
}
```

### Credential access

`LlmConnectionAccess.AcquireAsync` resolves a connection and refreshes its OAuth credential through
the registered authenticator. A changed credential is saved through `SaveCredentialAsync` with the
resolved expected version. A rejected save fails the operation without retry or fallback. Adapters
that ignore stored credentials retain that policy. Non-OAuth credentials remain unchanged.

`LlmAuthorizationSession` and `LlmRedirectResult` diagnostic strings omit URLs, authorization
codes, state and PKCE verifiers. Their members retain the actual values for authentication.

`AddLlmConnect` registers one scoped access owner shared by chat and embeddings. A facade built by
hand without an injected access owner creates and retires its own. An injected owner must use the
same connection source and remains borrowed. Retiring a facade cancels its credential operations
without retiring a shared owner.

The embedding facade accepts an optional borrowed `LlmProviderService` for the shared xAI family
catalog. Supplying it requires an explicit credential-access owner shared by both facades.
Construction rejects a mismatched pair before allocating private access. `AddLlmConnect` passes
the same scoped access and provider facade to embeddings.

Access retirement fences acquisition, cancels and drains resolution, refresh and saving, and clears
borrowed source and authenticator references without disposing those collaborators. Late refreshes are
not saved; late save results do not return credentials. Cancellation during a save can race a source
commit, so retirement waits for that save and does not undo or retry it. The source remains responsible
for atomic version checks and rejecting writes to changed or deleted connections.
Entered acquisition and refresh operations check the synchronous owner fence after awaited work,
before saving and before returning. They reject late results while linked cancellation callbacks
are pending. New operations reject an already retired owner.

`LlmConnectionAuthorization` binds a connection ID to a borrowed access owner. Its
`IProviderAuthorization.GetAsync` returns the current OAuth Bearer token after acquisition. Missing
OAuth fails; API keys are not substituted. The returned header's diagnostic text redacts its fields.

### Model lists

OpenAI-style discovery preserves positive integer `context_length`, `top_provider.context_length`,
`top_provider.max_completion_tokens` and `max_model_len` limits when reported. Missing or invalid
values remain unknown. The adapter does not invent limits for OpenAI model-list entries.

`LlmProviderService.ListModelsAsync` keeps each connection's model list in `LlmModelListCache`, which
`AddLlmConnect` registers once for the registration owner's lifetime. A list stays until the exact
tuple of connection `Version` and nullable resolved continuation scope changes, or until
`RefreshModelsAsync` lists again and replaces it. Every connection or credential edit changes
`Version`. The tuple compares both components separately and distinguishes null from empty scope.
For xAI, an epoch also compares an allowlisted immutable copy of the actual admitted credential,
endpoint, region, service tier, agent count, headers, provider options and capability overrides.
This catches a refreshed credential returned with its original source version; equality is local
change detection, not account or principal proof. Unrelated runtime and authority graphs are not
retained in this catalog projection.
The focused catalog regressions exercise an actual credential-access refresh and conditional source
save through the chat facade. The refreshed original-version snapshot replaces the old catalog and
uses the new token for both REST listings. The HTTP boundary is offline; this does not qualify xAI
OAuth or account entitlement.
Concurrent requests for one connection share a single listing, request accounting and managed sessions
read the same list, and a failed listing is not kept. Common's `ILlmInference` carries the same pair:
`ListModelsAsync` may answer from a kept list, and `RefreshModelsAsync` lists again. A service built by
hand without the shared cache keeps its own.

A tuple change or explicit refresh fences publication from the replaced in-flight listing.
Its waiting callers follow the replacing epoch to the newest listing and receive that listing's result or failure.
Only their own cancellation or cache retirement cancels their wait. Replacement does not cancel the provider's
entered call. An already-cancelled request
does not start or replace a listing.
An xAI epoch shares one generic REST listing and lazily reads only requested chat, image, video or
embedding family listings. Replacement fences each subsequent route and publication. Exact model
IDs take precedence; ambiguous aliases do not enrich facts or choose a profile. Missing fields remain
unknown, and manual model selection does not imply entitlement. This internal family catalog does not
register or enable unfinished media adapters. Voice listing remains an uncached TTS projection.
The optional same-access LLM facade on `XaiFamilyBinding` now supplies this internal cached family
path using its explicit selection ID and already acquired snapshot. It does not resolve or refresh
again, and its caller fence belongs only to the waiter, never to shared catalog production.
Publication copies the ordered list, catalog JSON and mutable capability sets. Adapter-owned lists,
sets and JSON documents do not remain authoritative after publication. Every caller reads the same
read-only snapshot until replacement.

`AddLlmConnect` also registers one disposable `LlmAccountUsageObservations` for its owning service
provider. That provider owns one dependency generation, not an application-global store shared across
reloads. Chat and native-agent facades in that provider share the observation store.

`LlmProviderService` and `LlmModelListCache` implement `IDisposable` and `IAsyncDisposable`. Facade
retirement fences new calls, cancels active calls and waits for them to leave. It retires a privately
created cache and credential-access owner, but not injected shared owners. Clients, authenticators
and the connection source remain borrowed and are not disposed by the facade.

The chat producer owns its provider enumeration through cleanup. Independent provider or projection
and enumeration-cleanup failures remain together. Failed cleanup retains the actual enumerator through
the cleanup failure, which remains visible to repeated facade retirement callers without retry.
Abandoning a chat stream reports typed cleanup failures even when provider cleanup fails during
the final pull, before outer enumeration disposal. Retirement also retains those final-pull cleanup
causes without treating the provider failure as a retirement failure.

Credential access and the chat, embedding and agent facades release their borrowed source references
after admitted work drains. Chat and embedding facades also release credential-access references;
chat releases setup environments and adapter collections. Cleanup failures do not skip this release.
Concurrent disposal callers share the same completion and retained failure. Chat disposal preserves
failures from its operation, credential and privately owned catalog retirement. Embedding disposal
preserves admitted cleanup outcomes and owned credential-retirement failures. Agent disposal preserves opening and
every owned session-retirement failure. Each waits for all owned cleanup before reporting failure.
Agent retirement includes sessions returned after its initial snapshot. Failed session cleanup keeps
the actual session owner; shared disposal reports that failure without retrying its release.
Cancelled opening retains its cleanup failure as the cancellation cause.
A single original error remains unchanged; multiple errors are retained in an `AggregateException`.
Repeated disposal returns the same cached failure instance. Synchronous settings
manager creation and Claude login participate in admission and drain. An admitted synchronous login
keeps its existing result or exception; disposal waits for that call to leave.
Shared catalog producers use the cache's admission, immutable snapshot and cancellation fence, not
the originating facade's fence. An injected cache can finish and publish after that facade retires.
Cache retirement drains producers and clears executable client references; borrowed clients and HTTP
clients are never disposed. Refused owned catalog request/response cleanup retains its safe failure
and actual owner, and repeated cache retirement reports the same failure without retrying cleanup.
The exact cleanup cause is retained as private evidence without exposing its untrusted text through
the public exception graph; canceled cleanup retains its actual cancellation token. Applied request
header values join credential redaction for catalog error and malformed-body diagnostics.

A disposed default-context facade or authorization object can remain retained without keeping a
separately collectible source or adapter generation alive through those released references.
Retained caller metadata, live settings objects and genuine foreign exception objects remain separate
roots. This does not establish retirement of an active provider, native runtime or whole SDK graph.
The focused collectible-generation regression executes actual `AddLlmConnect` registration and
resolves all nine default chat clients, the SDK HTTP pool, chat/agent/embedding facades and the
Files alias. The alias resolves the same chat facade. Connect and the reused connection-source
fixture execute in the foreign load context with matching source MVIDs. After real container
disposal, the load context, assemblies, source, clients, pool and facades collect from an outer
frame that retains only weak references. No network request, credential callback, CLI process or
native engine runs. This checks healthy default-registration retirement, not every extension,
active session/body, cleanup refusal, retained exception or target platform.

Cache retirement clears kept models, cancels provider listings through an owner token, and waits
for every listing, including entries replaced by an edit or refresh. A cancelled caller only stops
its own wait. A provider that ignores owner cancellation keeps retirement pending until its call
ends; its late result is not published. Abandoned provider faults are observed.

LLM settings actions and model-choice queries own the temporary provider facade they create. Each
operation retires that facade and its private catalog cache before returning, including cancellation
and failure paths. Cancelled discovery waits for its provider listing to drain; an ignoring provider
keeps the operation pending until it returns. Injected clients and authenticators remain borrowed.
Another settings operation creates a fresh facade and can reuse those clients.

Use asynchronous disposal while provider work is active. Chat streams use an ordered, non-dropping
32-event buffer. Their producers own active operations through provider enumeration cleanup.
Disposal cancels blocked writes and drains producers without waiting for a paused event reader.
An operation that ignores cancellation still keeps disposal pending. A registration owner disposes
its shared cache; sharing a cache across
containers does not transfer that responsibility to a scoped facade.

### Explicit cache binding

`LlmProviderService.BindContextCacheAsync(connectionId, ct)` is the asynchronous entry point for
`ILlmContextCacheClient`. It resolves one versioned connection, snapshots its data and validates that
the selected transport is registered. Binding does not refresh credentials, prepare a provider,
discover models or create a cache. Gemini API and standard-project Vertex Gemini implement these
operations. Other adapters and projectless Vertex express mode report `NotSupportedException`.

The facade's admission and retirement cover the source read. Cancellation waits for a source that
ignores its token to return, then rejects its late result. Facade retirement also drains that read
before releasing borrowed collaborators. The returned client borrows the facade. Later operations
perform explicit cache mutations or validate receipt scope through that same owner; binding itself
does not create a paid resource.
Receipt admission checks the facade's cancellation and retirement fence after acquiring the
binding gate, before installing receipt evidence. Publication checks again after gated work.
A late create, expiry update or delete keeps its actual observed receipt in the existing
possible-mutation failure; it does not publish a successful result into a replaced binding.
Failure-side uncertainty recording remains available after cancellation and performs no
provider mutation or automatic retry.

Focused managed regressions exercise this boundary through the real Gemini cache adapter and
scripted HTTP. Retirement drains an ignoring read and fences all five retained bound operations
without disposing the borrowed source or HTTP client. A create receipt parsed before response
cleanup triggers retirement remains on the cancellation observation with its actual request ID.
These checks do not qualify live cache creation, credential rotation or provider eligibility.

Exact provider-token counting rejects explicitly cached Vertex requests before credential refresh,
authentication, connection preparation, model discovery or HTTP dispatch. This restriction is
independent of generation-cache capability. Uncached counting and local estimated accounting
retain their existing behavior.

### Files and Skills facade

`LlmProviderService` exposes `ILlmFileClient`, `IXaiFilePublicUrlClient` and
`IXaiSkillClient`. `AddLlmConnect` registers those interfaces as aliases of the same scoped facade.
The facade owns their admission and retirement; resolving an interface does not create another
connection source or credential owner. Skills methods use the `IXaiSkillClient` interface to
distinguish skill IDs and results from file IDs and results.

`LlmProviderCapabilities.Files` and `.Skills` report separate resource-operation facts. The
builtin xAI Responses client exposes the seven implemented native Files operations through
the facade. The facade resolves one selected connection, snapshots its configuration and validates
arguments and headers before its existing credential owner refreshes that connection. The private
transport borrows the same HTTP client as chat. `AddLlmConnect` includes the Grok adapter.
Skills list, metadata retrieval, raw ZIP download and deletion use the same selected resource
transport and facade lifetime. Skills pages preserve the actual `has_more`, `first_id` and `last_id`
facts; caller-directed next pages send `after`, not the Files `pagination_token`. Version strings
and Unix creation times remain actual receipt values. ZIP downloads copy bytes to the borrowed
destination without extraction, rewinding or implicit flushing. Skills upload and generation
attachment remain unavailable. `Skills.ResourceOperations` stays false because the complete
operation set is not implemented.
Both borrowed Skills upload variants require positive caller-selected `MaximumUploadBytes`,
`MaximumEntries` and `MaximumExpandedBytes`. Input bytes exclude multipart framing. ZIP entry counts
include directories; expanded bytes count uncompressed file content. For explicit directory uploads,
entries count supplied files and both byte limits apply to their total content. The library supplies
no defaults or numeric provider limits. These contracts carry policy for the unavailable upload path;
they do not perform stream reads, archive inspection or provider calls. Grok's directory multipart
path encoding, accepted ZIP layout and uploaded-skill generation-attachment mapping are unknown.
Upload and generation attachment remain unavailable; the library does not guess those protocols.
Other Files routes remain unavailable. When no builtin Files adapter is
composed, refusal precedes source resolution and stream inspection. Caller cancellation and
retired-facade admission retain the existing lifetime behavior.
Facade capability queries suppress Files flags for clients without an implemented facade route.
They fence publication after the adapter's capability getter returns.
Upload and download streams remain borrowed, including on rejection and facade disposal. These
resource interfaces alone do not enable native-file prompt references or skill generation attachment.

The private xAI Files transport implements upload, bounded one-page listing, metadata retrieval,
raw download, deletion and explicit public-URL creation/revocation against an admitted connection
snapshot. It shares the chat adapter's OAuth-only regional header policy. Upload sources and
download destinations remain borrowed; transfers do not seek, flush or close them. Download receipts
follow complete copy and owned response cleanup. Premature EOF against an observed HTTP content length
is a failure; text extraction is not compared with original file metadata size. Partial failures can
leave written destination bytes. Observed public URLs must parse as absolute URIs without literal
controls. Their accepted strings remain unchanged; the transport does not fetch them or impose an
undocumented scheme/host policy.
The transport fences each awaited boundary and drains entered work before dropping its borrowed HTTP
client. Owned response and request cleanup each runs once. Independent operation and cleanup failures
remain visible together; a lone failure retains its original identity. Publication follows cleanup
and the final owner fence. Its protocol errors retain actual status and authentication/entitlement
classification, but exclude provider response bodies and capability URLs from diagnostics. The facade
drains admitted operations before retiring its private Files helper. Borrowed clients, authenticators,
sources and streams are not disposed. Logical Session/Workspace materialization remains a separate
resource path; native file IDs and public URLs are not portable resource identities.

The builtin xAI Responses route accepts native file prompt references and materializes logical
document resources through one borrowed `LlmChatRequest.Resources` authority. It freezes one selected
membership snapshot after continuation projection. Explicit history occurrences retain their order
and duplicates. Selected references absent from the supplied graph append to the current user prompt.
The route reads authorized committed bytes through owned handles and uploads them through the same
private Files transport. Unknown byte counts remain unknown; missing filenames refuse the file
destination. Native receipts belong to the admitted request and do not replace logical captured history.
After a connection change, the caller reattaches its resource authority to the portable context.
The Grok destination uploads the named committed content again; it does not reuse another connection's file IDs.
OpenAI Responses, Anthropic Messages, Gemini API, Vertex Claude, Vertex Gemini, Bedrock Converse and streaming Claude CLI chat read the same logical resources through this shared owner
and send their committed content through the existing native inline attachment lane. Materialization
retains actual filenames and text bytes; PDF and declared image content use the existing binary
attachment projection. Native attachment validation still rejects unsupported formats. Committed
tool resources use the existing typed binary-output projection. Portable history keeps its logical
references, selection and transcript revisions. Terminal completion follows owned-reader cleanup;
operation and independent cleanup failures remain visible together. This lane does not implement
provider Files CRUD or create a paid cache.
OpenAI Responses, Anthropic, Gemini API, Vertex and Bedrock managed-session opening admits resource-bearing portable contexts only with explicit
caller-owned access and a document-capable model. The shared managed session adopts the current
Workspace/Session selection snapshot at opening and validates the context against it. Capture keeps logical references after generation.
OpenAI Responses carries committed bytes and filenames in its existing native inline `input_file`
projection ([official file inputs](https://developers.openai.com/api/docs/guides/file-inputs)).
The Responses operation owns inline preparation, terminal publication and reader release. Its SDK
generation fence covers resource callbacks. Materialized attachments retain model capability validation.
Both native streaming and nonstreaming use this same path. Codex uses the same inline preparation.
Bedrock's existing factory lifetime covers inline preparation and native Converse execution. The shared
materializer retains logical context and ordered selected references; both Converse response modes
use the existing binary document and tool-output builders. Bedrock carries UTF-8 text, JSON and XML
bytes as native `txt` documents without changing stored MIME types. Converse assigns neutral document
names from one request-scoped ordinal across prompt attachments and every tool-result message.
Repeated filenames and repeated tool calls retain their bytes without duplicate native names.
The name field excludes filename punctuation
([native document contract](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_DocumentBlock.html)).
Reader cleanup precedes terminal publication, and its actual failure remains visible at factory retirement.
Claude CLI streaming chat sends materialized prompt and tool files through its existing Anthropic
stream-json content blocks. Its shared tool-output projection supplies text to the labelled history
and appends native image/document blocks after the transcript and prompt attachments. Capability
restrictions apply to that projection. The plain-text nonstreaming lane refuses resource-bound
requests before resource callbacks.
The focused resource tests supply Claude's catalog initialization reply before exercising chat through
the facade. They check the actual chat input's document bytes and logical selection, and catalog
process retirement. They do not launch the installed CLI or qualify a live provider.
Native Claude Start, Resume and Fork borrow `CallerServices.Resources`. Opening validates the
Workspace/Session selection snapshot before starting the CLI. Imported history stays logical until
the first prompt; that prompt materializes the opening history and current input together. Later
prompts read the current selection and append references absent from the supplied graph. Existing
references keep their order and duplicates. Copied native content is built and its readers settle
before the prompt is written. This path neither changes caller selection nor restores a native
continuation from foreign history. Native context capture and replacement remain unsupported;
the caller retains its resource authority and selection.
Claude MCP tool results materialize only their explicitly named, already-selected resources. They
do not append unrelated selected files. The existing MCP projection carries text/JSON and images;
logical content it cannot carry, including PDF on this route, is left out with the tool-output notice. Public tool
results retain logical references. Background failures reach the session observer. Retirement drains
ignoring resource callbacks before releasing the process, detaches borrowed access, and preserves
operation and independent cleanup failures once for every retirement caller.
Gemini API and Vertex use the shared inline materializer before their existing native request builders. Preflight
validates the selected route and cache binding before resource reads. The materialized request retains
native format and size validation. Claude uses the shared Anthropic document builder. Gemini sends
UTF-8 text, JSON and XML bytes with its native `text/plain` inline label and the actual filename.
The stored resource MIME type remains unchanged. Both Gemini routes share this binary projection.
Managed turn requests retain the caller's explicit unbound-continuation policy from session opening.
The chat facade admits logical references only for clients with implemented resource preparation.
Provider file-reference capability remains a separate fact.
Local estimates remain unknown for resource-bound requests and never open their contents. Exact
Anthropic, Gemini API and Vertex counting refuse these unprepared requests before effects; they never count only the text
while silently omitting selected files. Explicit binary attachments retain the existing count route.
Local accounting does not read or upload selected resources. `AddLlmConnect` includes Grok registration.
OpenAI Responses, Bedrock and Claude CLI have no implemented provider-count adapter; exact input counting remains unavailable.
Managed sessions created through the shared internal factory validate resource-bound imported
contexts against the caller's exact scoped selection snapshot. Only a new session without an imported
context adopts the current selection; an unbound manual or upgraded legacy context must be explicitly
bound first. Capture freezes transcript state, then reads selection outside the session lock and
rechecks transcript revision before publication. Replacement and continuation similarly recheck idle
state and revision after caller callbacks, without mutating the store. Committed caller-tool resource
outputs are authorized before settlement; they remain logical history, not provider file IDs.
Resource-backed requests do not automatically retry a failed model request after upload.
Typed resource cancellation reaches a live reader even when session retirement cancels the producer.
Healthy cleanup leaves session disposal successful. Actual owned handle or HTTP cleanup refusal uses
`InferenceResourceOperationPhase.Cleanup` and remains the same cached reader/disposer failure;
independent operation and cleanup causes remain visible. Borrowed authority detaches after callback
drain, while refused owned resources remain reachable through the actual retained cleanup failure.
SSE parsing borrows its caller-owned stream. A refused async body-stream release skips dependent
response/content cleanup rather than retrying that same stream synchronously; the failed stream and
dependent response stay with the cleanup error until that error's references are released. This is
not process-exit quarantine. `AddLlmConnect` includes the Grok agent opener;
registration does not qualify OAuth, provider behavior or other destinations.

### Embedding lifetime

`LlmEmbeddingProviderService` implements `IDisposable` and `IAsyncDisposable`. Its registration owner
retires the facade. Embeddings refresh OAuth through the same access implementation as chat before
dispatch. Every `ILlmEmbeddingClient` supplies an effect-free `ValidateRequest`; the facade calls it
before refresh, and the direct adapter validates before HTTP. `LlmEmbeddingRequest.Dimensions` is
nullable and positive when supplied. Raw `dimensions`, `model` and `input` are reserved in both
connection and request options. Unknown model-specific limits remain provider decisions. Requests
snapshot typed `EmbeddingInput` lists and JSON options before asynchronous work; media bytes remain borrowed until completion; connections snapshot embedding options
and headers before refresh. OpenAI-compatible responses require one unique in-range index per input,
finite nonempty vectors and the requested dimensions. Results retain the reported model ID when
present; an absent ID stays null and is not replaced by the requested alias. Protected values in
model metadata refuse publication. Diagnostics redact credentials and additional header values.
The three embedding data types expose owned metadata through their exact type-name getters on
`ConnectLlmJson`.

`LlmEmbeddingProviderService.ListModelsAsync(connectionId)` lists observed xAI embedding-family
models through the explicitly borrowed LLM facade and its sole model cache. Other transports and
missing catalog composition refuse before credential refresh. The xAI embedding path consumes one
resolved and refreshed connection snapshot for catalog and POST; nested catalog work does not
reacquire credentials. The returned facts are installed only on that admitted internal snapshot.
An unknown manually selected model remains unknown, not unsupported solely because a listing omits
it. Without the shared facade, catalog-dependent xAI embedding calls refuse before refresh.
Non-xAI embedding calls retain their existing construction without a catalog facade. Live embedding
facades are runtime composition, not portable data: owned options preserve null and refuse non-null
values without serializing their collaborators. No runtime facade getter is exported.

`AddLlmConnect` discovers the SelfHost embedding client when its library is included. It embeds through the host's
`ILlmSelfHost.GetEmbeddingInferenceAsync(engineId)`, where the connection's endpoint names the
engine. It shares chat's connection refusal policy for provider options, HTTP headers and capability
overrides, and refuses request provider options before acquiring the engine.
`LlmEmbeddingRequest.Inputs` uses Common's `TextEmbeddingInput` and `MediaEmbeddingInput`. The latter carries encoded bytes through `BinaryMediaSource`. Text-only remote adapters reject media before HTTP.
The engine formats inputs: `LlmEmbeddingRequest.Kind` states
whether inputs are queries or documents, `Instruction` states a query's task, and `Truncate` decides
whether an over-limit input is cut or fails the request with `EmbeddingInputTooLongException`. The
result reports `ModelId`, `ModelFingerprint` and `TruncatedInputs`. Remote adapters send inputs as
given; they ignore `Kind`, `Instruction` and `Truncate` and report no fingerprint.

The internal xAI embedding adapter implements ordered text inputs, float vectors and typed dimensions.
It accepts at most 128 non-empty inputs and preserves supplied `user` and boolean/null `preview`
options, including explicit nulls. Its direct path consumes the supplied OAuth connection and the
stateless embedding catalog reader; it does not refresh credentials or own another model cache.
It refuses non-default typed service tiers and raw `service_tier` options before HTTP because it
does not implement an embedding tier mapping. Raw `encoding_format` accepts only `float`.
Responses require complete unique input indexes, finite consistent vectors, actual model provenance
and nonnegative observed usage. The validated response model populates `LlmEmbeddingResult.ModelId`;
the requested alias never substitutes for that observation. Validated float values use primitive
JSON nodes without default reflection metadata. Successful diagnostics serialize only validated
model and numeric facts. A model string containing a known credential or protected header value causes a local
confidentiality refusal after the response; it is not provider rejection or remote rollback proof.
Input text is not a secret-replacement authority, and numeric JSON is not text-redacted.
HTTP request/response cleanup preserves independent failures and
retains actual refused owners behind safe diagnostics. Provider failures retain the redacted code,
request ID, status and retry delay without exposing the response body. The adapter is not registered. Subscription
OAuth, model entitlement, preview format and the published input-schema/example disagreement remain
unqualified; no default embedding model or working xAI availability is advertised.
Embedding and document-search failure conversion uses selected and effective outgoing header values
captured when the send exits, including when it throws before returning a response. Typed provider
and HTTP transport failures retain their redacted explanations. Reflected HttpClient-default headers
remain redacted from messages, codes and request IDs. An absent response supplies
no response observation. A thrown `HttpRequestException` retains its observed status when no
response status exists. A failure with neither source leaves the status unknown.

Retirement fences new requests, cancels
connection lookup and provider calls, and awaits all active requests before releasing its adapter references. Supplied adapters and the
connection source remain borrowed and are not disposed by the facade. Caller cancellation affects
only that request. A provider that ignores cancellation keeps retirement pending; its late result
is rejected. Use asynchronous scope disposal while embedding requests are active.
Post-await boundaries check the synchronous retirement fence even when a cancellation callback
delays propagation to a request's linked token.
The internal xAI dispatch also checks that same facade fence through send, read, parsing and
publication. Cache and embedding owners share one finite cleanup-outcome policy with separate
owner-scoped state. Only actual cleanup carriers affect retirement; ordinary provider, validation
and cancellation failures do not. Refused cleanup retains its actual safe carrier and resource,
and repeated disposal reports the same result without retry. Borrowed catalog facades are detached
after drain and are never disposed by embeddings.

### Agent-session lifetime

`LlmAgentProviderService` implements `IDisposable` and `IAsyncDisposable`. It owns sessions returned
by start, resume and fork. Facade retirement fences new openings, cancels active openings and
retires every returned session. Existing sessions retire without waiting for another opening to
finish. A session that finishes opening after cancellation is disposed before its opening call
ends. Supplied clients and the connection source remain borrowed.

Each returned session fences its methods on retirement, cancels and drains active calls and stream
producers, then disposes its adapter session. Turn and observation streams use ordered, non-dropping
32-event buffers. The producer retains its operation lease through adapter enumeration cleanup;
the public reader holds no lease while paused. Concurrent callers share one retirement completion.
Disposing one session leaves its siblings and the facade usable. Caller cancellation affects only
the named operation. Late results and events from cancelled work are rejected. A failed session
retirement remains visible when its owning facade retires; another session is not skipped.

Use asynchronous scope disposal while sessions are active. Retirement cancels full-buffer writes
and drains producers independently of paused readers. Disposing an enumeration cancels and drains
its producer. Consuming readers receive provider failures. Abandoned enumerations still report
cleanup and cancellation-callback failures. Independent read and producer failures are preserved;
actual enumeration cleanup failures remain visible to repeated retirement callers.
An adapter that ignores cancellation keeps retirement pending until its work leaves. Direct
`ILlmAgentClient` callers own the sessions they create outside the facade.

### Vertex authentication lifetime

`AddLlmConnectVertex` registers one authentication owner per container. Container disposal fences
new authentication calls, cancels active calls, and awaits their completion before clearing cached
credentials and credential-source callbacks. Concurrent disposal shares the same completion.
Use asynchronous container disposal while authentication calls are active. Caller cancellation
affects only that call. Tokens returned after caller or owner cancellation are rejected.

Service-account and ADC-file acquisition share one credential constructor per key. Failed
construction is removed from the cache and can be retried. ADC files are re-read on acquisition;
replacement changes the credential and observation scope. SDK discovery without an ADC file stays
inside its active call until discovery ends, because SDK cancellation only cancels the wait for
that shared task. SDK/parser errors never expose credential diagnostics or inner exceptions.

This owner releases its cache references. Google's shared ADC discovery cache and SDK-internal
HTTP/refresh lifetimes are separate from these authentication-call retirement guarantees.

### Bedrock SDK lifetime

`AddLlmConnectBedrock` registers one SDK client owner per container. It caches each runtime,
control-plane and AgentCore client by an opaque fingerprint of region, profile, access key, secret
and session token. Concurrent acquisition constructs one client; a failed construction can be
retried. Changes to configured credential fields use a different client and logging-verification key.
The internal generic acquisition declares `Lazy<T>`'s public-parameterless-constructor requirement
for trimming. Client creation still uses the supplied factory. Signed, redacted and tool continuation
blocks enter their existing JSON array through the nongeneric node overload without reflection.

Every SDK call holds an owner lease. Streaming leases cover response reading and disposal, not
just response creation. Owner retirement fences new acquisition, cancels active calls, waits for
their leases, and disposes every cached client and explicitly resolved disposable credential.
Response cleanup failures remain visible to the SDK owner. One disposal failure does not skip
another resource. Logging verification also fences and drains its lookups and clears its cache.
Use asynchronous container disposal while work is active.

SDK acquisition, awaited results, catalog follow-on calls, logging verification and each published
Converse response event check the synchronous owner fence. Delayed linked-token propagation cannot admit
a late successful result. Successful retirement detaches client-creation callbacks and disposed
stream response references. The owner retains exact resources whose cleanup fails and the original
errors; repeated disposal shares that failure and does not retry cleanup. A failed stream opening
preserves both its original operation error and a response-cleanup error when both occur.

Model discovery preserves every successful catalog lane, including an empty response. A failed lane
logs a warning through the standard host logger. Two failed lanes also log an error and return ordered
safe failures, not an empty catalog. An optional resolution failure logs a warning and retains the exact
requested model ID for invocation. Diagnostics contain fixed operations, observed status and protected
SDK error-code/request-ID fields, never raw exception graphs or unredacted provider content. A refused logging sink
preserves the discovery failure and the separate logging failure behind safe diagnostics.
Uncaught SDK client, service and HTTP failures during client acquisition, catalog listing or
availability checks fail the listing and log one credential-safe warning per observed failure. Concurrent
availability failures use the existing error queue and retain every observed SDK/HTTP failure, including
when diagnostic logging also fails. Discovery uses the shared
Bedrock failure classifier with the captured connection. Actual explanations and observed status,
code and request ID remain available; missing metadata remains null. These failures add no catalog
fallback or retry. Modeled lane failures retain the partial-catalog behavior described above.

Clients use a private default AWS credential resolver and SDK-client-local HTTP caches. Retiring
the SDK owner disposes the resolver and the SDK pipelines' HTTP pools. Runtime and AgentCore HTTP
clients have infinite generation timeouts; control-plane clients retain their SDK timeout.
An adapter that ignores cancellation keeps retirement pending until its call leaves. Consume or
dispose open streaming responses and enumerators so their leases can leave.

### Streaming, reasoning text and usage

`LlmProviderCapabilities.SupportsStreaming` says whether the adapter streams provider output as the model
generates it. `LlmCapabilityOverrides.SupportsStreaming = InferenceSupport.Unsupported` selects the provider's non-streaming request:
the adapter sends it and emits the complete response as events after it arrives. A transport whose provider
has no non-streaming request rejects every request while streaming is off. Common's
`LlmCapabilities.SupportsStreaming` carries the same flag to `ILlmInference` hosts.

Compatible Chat Completions reports `Unknown` for an unrecognized or missing stop reason unless
the response contains an explicit refusal. Tool calls alone do not establish a recognized terminal
reason. Anthropic also keeps an unrecognized or missing stop reason `Unknown` even when calls are
present. Both retain the observed calls and publish no successful continuation from that unknown
outcome. Each HTTP event publication checks caller cancellation, including buffered
non-streaming events and adapters using a borrowed HTTP client.
Claude CLI and Antigravity also check caller cancellation before each buffered non-streaming event.

Bedrock Converse and ConverseStream also retain decoded tool calls for an unknown stop reason.
Their terminal result preserves partial text, reported usage and the observed stop reason without
continuation state or structured-answer evaluation. Observed calls do not authorize tool execution.

AgentCore chat classifies both `content_filtered` and `guardrail_intervened` as `Refused`.
It preserves returned text and reported usage without evaluating that text as a structured answer.
Both stop reasons map to `content_filter`, matching the Converse refusal mapping.

Compatible Chat Completions skips structured-output validation for a refused result, including
`content_filter` without refusal text. Its partial text, reported model, usage and finish reason
remain available. Filtered partial JSON is not published as an evaluated or valid structured answer.

Gemini API and Vertex Gemini retain complete decoded tool calls when the actual terminal reason
is unrecognized. The result remains `Unknown` and carries no continuation state. Open streamed
arguments do not become complete calls. Existing known refusal, truncation and malformed-call
outcomes keep their classifications.

Vertex typed function-argument streams accept dot names and single- or double-quoted bracket
names in their JSONPath property selectors. Both quoted forms preserve the exact decoded property
name and share indexed-array reconstruction. Returned calls, portable results and retained
thought-signature continuations carry the same reconstructed arguments.
An initial named no-argument call also completes when `willContinue` is explicitly false and
`partialArgs` is absent. The [Vertex function-call contract](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/reference/rest/v1/Content#FunctionCall)
defines false and omitted `willContinue` as final. Both forms retain the actual call identity and
thought signature, expose empty portable arguments and produce a replayable continuation after a
recognized successful tool stop. An unnamed initial fragment still refuses.
Complete calls with `args` and `willContinue:false` use the ordinary call parser in streaming and
nonstreaming responses. The parser consumes the final control without dropping arguments, call
identity or thought signatures from portable results and canonical continuations. An open fragment
stream still uses its existing completion checks.

Responses reports `Unknown` for an unrecognized native status and does not publish continuation
state from that outcome. Tool calls alone do not establish successful completion. Direct Codex
also accepts the actual terminal SSE event type when its response omits `status`, matching the
selected CLI protocol. An explicit unknown status is not replaced by the event type. Other
Responses routes require their native status; a missing status remains unknown. Managed sessions
retain tool calls from an unknown outcome in their context but end with `Unknown` without executing
the calls or starting another model step. Structured-output validation does not discard those
unknown tool observations or replace their terminal reason.

`SupportsReasoningText` describes public reasoning separately from reasoning-level control. Common
mirrors the flag. The **Reasoning text** override disables public reasoning events and provider
summary requests without dropping opaque continuation state. Model overrides take precedence over
connection overrides. An override cannot enable a producer the adapter does not implement. Apple
Foundation Models and Antigravity report false. SelfHost reports the loaded engine's flag.

Responses requests `reasoning.summary = auto` when a positive level is selected or the resolved
profile says the model reasons by default. An unprofiled API-key model gets no implicit summary
request. An explicit `None` sends no summary request. Codex uses the same builder and display switch;
its catalog supplies reasoning support, with the subscription adapter's default when absent.
Gemini and Vertex Gemini request `includeThoughts` for active
reasoning, including a thinking profile with no explicit level. Turning display off leaves effort,
thinking levels and budgets unchanged. Claude CLI chat maps `thinking_delta` to public summaries
and ignores signature deltas. AgentCore chat emits reasoning events, not activity text. Native
Claude and AgentCore sessions honor the same display override. Streaming Claude CLI chat writes
`showThinkingSummaries: true` to a per-call settings file and passes its path through `--settings`
when display is enabled. Streaming chat also supplies the official SDK's `--thinking-display`
argument: `summarized` when display is enabled and `omitted` otherwise. The file is removed with
the call's working directory on completion, cancellation, failure or reader disposal.
Authentication probes and non-streaming calls receive no summary settings file or display argument.
The request does not guarantee that the CLI returns a summary.
Native Claude sessions pass the resolved initial `showThinkingSummaries` boolean as inline
`--settings`. Before each host turn, the existing `apply_flag_settings` control carries that turn's
resolved display choice and any explicitly supplied effort. Session initialization and each host
turn also await the headless `set_max_thinking_tokens` control with only `thinking_display`.
The budget field is absent. This requires the current official headless protocol's display-only
update semantics, present in Claude 2.1.288. An omitted effort does not reset the CLI's existing
effort, and a display-only update preserves its thinking configuration. A rejected control fails
the operation before the prompt is written. The session keeps the last model acknowledged by
`set_model`; later omitted model choices and provider-originated turns use that model's capability
overrides. An explicit return to the startup model sends a model switch when the CLI has selected
another model.
Native effort mapping reads the same resolved model profile before any CLI control is sent.
Chat and native-session thinking deltas carry `IsSummary = true`; neither path exposes signature
or encrypted state as reasoning text.

Anthropic Messages and Vertex Claude share model profiles. Positive adaptive reasoning requests
`thinking.display = summarized` on families whose default display is omitted. A null level requests
only that display with adaptive thinking on the named default-thinking Opus 5, Sonnet 5, Fable and
Mythos families; it invents no effort or budget. Explicit `None`, budget models, effort-only models
and older adaptive models retain their existing controls. Disabling display omits the summary request
without changing explicit reasoning controls. Catalog evidence remains authoritative for thinking
support. Vertex Sonnet 4.6 receives no new display field.

Claude profiles reject temperature and forced tool choice while adaptive or budget thinking is
active, including a profile's default thinking. Profiles that prohibit forced tool choice reject it
at every level. Explicitly disabled thinking and effort-only reasoning keep the model's supported
sampling/tool controls. Anthropic Messages, Vertex Claude and Bedrock Claude use this validation.
First-party post-Opus-4.6 Messages models additionally accept the documented temperature and top-p
defaults described under Sampling Defaults. Those values do not represent controllable sampling.

Anthropic Messages and Vertex Claude report `pause_turn` as `Incomplete` with its native reason and
opaque continuation state. Ordered server-tool calls, their result blocks, caller metadata and
citations remain replay data; they do not become host tool actions. Streaming retains partial
server-tool argument deltas before constructing that state. Calls from native code execution retain
their `caller` metadata, including client calls that still require a tool result. A caller replays a
paused assistant message with its unchanged text and continuation state. Vertex accepts that opaque replay without
allowing ordinary assistant prefilling. Unknown stop reasons stay Unknown;
`model_context_window_exceeded` and `max_tokens` are Incomplete without paused-turn state.
Native continuation content is materialized only for replay-eligible terminal outcomes. Truncated
streamed tool arguments do not hide an Incomplete or Unknown result behind a JSON parse failure.
Raw argument events remain unchanged; Unknown results retain actual calls without successful
continuation. An Incomplete result exposes no actionable terminal calls.

The private Anthropic continuation payload contains ordered content and an optional opaque
code-execution container ID. The ID shares the content's account/model binding and survives context
capture/import. Replay sends the most recent retained ID as the top-level `container` field.
It retains no container expiry, skills or arbitrary response metadata. Raw provider options cannot
replace a retained ID. Container errors propagate without retry, recreation or silent identity removal.
The count-tokens operation rejects requests containing `container`, which its API cannot represent.
Legacy array-only Anthropic payloads are not accepted; other providers' payload shapes are unchanged.

Mixed server/client responses with pending client tools report `tool_use` and RequiresAction.
Only client calls emit tool events and reach a managed session's handler. Their results accompany
the retained ordered server/client content and container on the next request. A contradictory
`pause_turn` response with pending client calls fails without returning a resumable completion.

Anthropic Messages and Vertex Claude consume `ProviderOptions["anthropic_tools"]` as an adapter-owned
object. `server_tools` is an array of native server definitions. `client_tools` is an object keyed
by typed `Tools` names; its values accept `allowed_callers`, `defer_loading` and `input_examples`.
Typed client schemas, names, descriptions and strictness remain authoritative. The request option
replaces the entire connection option; an empty object clears it. Neither source is mutated.
The adapter emits one combined `tools` array, not the `anthropic_tools` wrapper, for generation and
provider counting. Typed tool choice and parallel controls apply to the combined names. Raw `tools`,
`tool_choice` and `temperature` options fail even when the typed fields are absent. The temperature
guard applies to generation and counting before Vertex authentication. Vertex Gemini rejects
`anthropic_tools`.

Native definitions check name/type pairs for the known web search/fetch, code execution, tool search
and advisor versions. Unfamiliar descriptors with a nonempty name and type remain opaque and unchanged.
The documented client-executed native types, duplicate names and native schema/strict overrides fail
locally. Programmatic tools require a named code-execution definition outside the known pre-programmatic
versions; deferred tools require a named tool-search definition. Neither check requires a locally
recognized server revision. Provider errors
determine route/model availability and tool-specific parameter support. The adapter never rewrites
definitions, retries an invalid configuration or switches tools. Advisor results, including encrypted
advice, remain opaque ordered continuation content, not public reasoning or host tool events.
Beta tools require the caller's provider beta header; the adapter does not enable paid tools implicitly.

Client `allowed_callers` accepts nonempty unique lists of `direct`, `code_execution_20260120` and
`code_execution_20260521`. A programmatic client requires a declared code-execution tool at either
of these two versions. It cannot be strict or disable parallel tool use. A forced client choice
must allow direct calling. Deferred clients require a declared tool-search tool. These checks do
not establish live tool provisioning or complete tool-specific compiler validation. See the
[programmatic calling contract](https://platform.claude.com/docs/en/agents-and-tools/tool-use/programmatic-tool-calling).

Programmatic client schemas reject proven local ordinary-`$ref` cycles before generation or provider
counting; Vertex rejects these cycles before authentication. Direct-only recursive schemas remain
unchanged, including in mixed requests. The guard indexes draft 2020-12 schema locations, local
resource IDs and anchors without fetching references or evaluating instances. It handles JSON Pointer
escaping and shared acyclic references. `default`, `const`, `enum`, `examples` and unknown annotations
remain opaque data. Unresolved external references, unknown dialects and ambiguous identities remain
provider validation. The guard never rewrites schemas or adds strict-schema restrictions. Dynamic
`$dynamicRef` evaluation is outside this ordinary-`$ref` cycle check.

The canonical Claude builder validates scoped, projected continuation before HTTP dispatch. Mixed
turns with waiting native tools and programmatic turns require an immediate user message containing
only results, exactly once for every waiting client call. Results can arrive in a different order.
Programmatic results accept strings or text blocks only; direct client results retain supported rich
content. Programmatic caller IDs must refer to retained code-execution invocations. An active result
request requires its native tool definitions and a retained container for programmatic calls. A terminal
assistant message with waiting native calls and no client calls requires those native definitions too;
a client tool with the same name cannot substitute. Bash and file-operation invocations use their
owning code-execution definition. The documented dynamic-filtering web tools also provide native code
execution without an extra definition. Native results match by `tool_use_id`, not position. Completed
historical exchanges do not require those tools or a container to remain configured now. The adapter
does not rewrite history, strip extra input or binaries, synthesize results, execute native tools or retry.
This validation follows principal resolution and explicit unbound-state projection; Vertex resolves
authentication before checking principal-bound history. Raw `thinking` cannot override typed reasoning,
even when the typed setting leaves the native field absent.

Managed sessions retain an Incomplete reply that carries continuation state and no host tool calls.
They emit its message, finish the turn with Truncation, and preserve the state through capture/import
for a caller-directed later turn. They do not automatically retry or execute server tools.

`SupportsStreamingUsage` describes usage reported by completion. `SupportsUsageDeltas` describes
running `LlmUsageDeltaEvent` observations and is mirrored on Common's capabilities. Each observation
replaces the previous value for the entire call. Null counts remain unknown. Duplicate observations
are suppressed. Only completion usage is authoritative, including when it corrects an earlier value.
ConsoleTester completion diagnostics render each unreported token count as `unknown`, preserve
reported zero as `0`, and render an absent usage object as `unavailable`. Missing provider counts
never appear as measured zero.
Prompt usage deltas and completion diagnostics include the complete normalized `LlmUsage` as
JSON through the owned `InferenceJson.LlmUsage` metadata. Agent usage diagnostics preserve the
actual session, turn and request IDs, followed by separate current-request and run-total usage
objects. Every reported token subset, provider cost, hosted count and source count remains exact;
unreported fields and absent objects remain JSON null. One serializer serves all three paths
without reflection, local accounting or raw provider-body logging. Diagnostics stay on stderr,
separate from answer text.
`LlmUsage` also carries nullable `ProviderReportedCostUsd`, `HostedToolCallCount`, `HostedSourcesUsed`
and a frozen `HostedToolCalls` map keyed by `LlmHostedToolKind`. These are independent provider facts;
no local price calculation or named-count sum invents an unreported total. Distinct completed calls
sum a field only when every contributor reports it without overflow. Named counts use the intersection
of reported valid keys; null maps remain unknown and empty maps report no named detail. Assignment and
owned JSON import normalize negative hosted counts to unknown, retaining explicit zero. Interim usage
replaces the current request's observation. These shared fields do not enable a hosted-tool capability
or claim a provider mapping that has not been implemented.
Anthropic and Vertex Claude report observations from `message_start` and `message_delta`. Gemini
and Vertex Gemini report normalized `usageMetadata`, including reasoning output tokens.
Each Gemini `usageMetadata` object replaces the complete observation. Unreported fields stay
unknown and never inherit earlier tool-use, reasoning or cache counts. A chunk without
`usageMetadata` leaves the latest observation unchanged.
Anthropic's initial output count is retained when reported. Later output counts replace it;
cache-read and cache-creation counts join uncached input in the normalized input total.
Claude CLI chat and native sessions share a ledger keyed by API message ID. It sums distinct calls
and ignores replayed calls and subagent events. Its observations and final `result.usage` cover the
main loop for the current turn, as described in the [Claude usage contract](https://code.claude.com/docs/en/agent-sdk/cost-tracking).
Native sessions use one `LlmAgentUsageEvent.RequestId` for the turn's running and final observations.
A final result without usage replaces the running estimate with unknown usage.
Antigravity chat and native sessions share completed-step accounting. Model-response and checkpoint
usage replaces earlier usage for the same step. ACTIVE snapshots, user-input steps, native-tool steps
and previously accounted process-generation steps add no tokens. Native sessions reconcile final
usage against a trusted same-process cumulative baseline without adding it to the step ledger.

OpenAI-compatible connections default running usage off. The **Running usage** capability override
opts a vLLM-compatible endpoint into `stream_options.continuous_usage_stats` when streaming usage
is enabled. Other compatible endpoints do not receive that extension by default. Non-streaming
responses emit no running usage events. Managed agent loops project observations into their existing
`LlmAgentUsageEvent`: a repeated `RequestId` replaces that step's observation, and `RunTotals` includes
completed steps plus the current estimate. Only a step's completion updates the settled run total.
An override that enables running usage on an adapter without a producer is rejected. A model override
takes precedence over the connection override.

A response that arrives complete produces its events in a fixed order: reasoning text, the answer text as
one `LlmTextDeltaEvent`, the refusal text when it differs from the answer text, then for each tool call its
`LlmToolCallStartedEvent`, `LlmToolCallArgumentsDeltaEvent` and `LlmToolCallCompletedEvent`, and last the
completion. The completion carries the same result a streamed response of the same content produces. Gemini
and Vertex Gemini read a `generateContent` response with the streaming reader, so their events follow part
order in both modes.

A non-streaming response sends its HTTP headers only when generation ends. The generation HTTP clients
(OpenAI Responses, OpenAI-compatible, Anthropic, Gemini API and Vertex) therefore have no client timeout; the
caller's cancellation token ends a request. Anthropic's SDKs refuse non-streaming requests they expect to run
longer than 10 minutes, and the API can end a long non-streaming request with `504 timeout_error`. Connect
does not pre-validate `max_tokens` against that limit, so a long non-streaming Anthropic request can fail
where the same streamed request succeeds.

| Transport | Streams | With `SupportsStreaming = InferenceSupport.Unsupported` |
| --- | --- | --- |
| OpenAI Responses | `stream: true` | `stream: false` |
| Codex | `stream: true` | Rejected |
| OpenAI-compatible | `stream: true` | `stream: false`, no `stream_options` |
| Anthropic Messages | `stream: true` | `stream: false` |
| Claude CLI | `--output-format stream-json` | `--output-format json`, prompt as text on standard input, no attachments |
| Bedrock Converse | ConverseStream | Converse |
| AgentCore | InvokeHarness | Rejected |
| Vertex Gemini | `streamGenerateContent` | `generateContent` |
| Vertex Claude | `streamRawPredict` | `rawPredict` |
| Gemini API | `streamGenerateContent` | `generateContent` |
| Antigravity | `--output-format stream-json` | `-p` with `--output-format json` |
| Apple | `streamResponse` | `respond(to:)` |
| SelfHost | Engine-defined | Engine-defined |

### Wrapped CLI processes

Three transports start a local process: `AnthropicClaudeCli` starts the Claude CLI,
`GoogleAntigravityCli` starts the Antigravity CLI, and `AppleFoundationModels` starts the Apple helper.

On macOS and Linux each process leads its own session and process group and has no controlling
terminal. A Ctrl+C typed in the terminal of a console host does not reach it. The host's cancellation
and disposal paths end it.

The Antigravity CLI and the Apple helper end by `SIGKILL` to the process group on cancellation and
disposal. Every descendant that stays in the group ends with the process. A descendant that calls
`setsid` or `setpgid` leaves the group, and the kill does not reach it. On Windows, cancellation and
disposal kill the process tree.

A Claude CLI is never killed early. The CLI refreshes its OAuth tokens and saves the rotated pair to the
keychain; a CLI killed between the refresh and the save leaves a spent refresh token behind, and the next
refresh signs the user out. Every Claude CLI process (chat, `auth status`, model listing and agent
sessions) therefore ends by standard-input EOF. A one-shot run whose input is already closed also
receives `SIGTERM`, then `SIGCONT`, on its process group. The process exits on its own; its process group
receives `SIGKILL` only after 30 seconds, and a Warning log entry records that kill. A caller that cancels,
stops a stream early or disposes a session returns at once. The exit continues in the background, which
drains the process's output and reaps it, and logs any failure. The `ClaudeCliExits` singleton owns these
background exits; disposing the service provider waits for all of them, so host shutdown takes up to
30 seconds plus the 5-second kill bound for a Claude CLI that ignores EOF and `SIGTERM`. On Windows a
Claude CLI receives no `SIGTERM`, so a one-shot run ends on its own or at the limit.
The exit owner retains actual background failures after logging them and reports them on disposal,
including repeated disposal. Successful exits leave no retained outcome. A refused warning does not
skip forced termination. Failed group signals remain failures even if killing the root succeeds;
the existing bounded exit wait still runs. Independent signal, exit, pipe-drain and release failures
remain visible together. Cancelling an owned pipe drain during retirement is not a failed drain.

`ClaudeCliExitTests` uses real shell stand-ins to check SIGTERM and EOF grace paths
and forced termination of a same-group child that ignores SIGTERM. A refused warning
still permits actual process termination and remains visible on repeated disposal.
A failed background task and refused error log preserve both exception identities
without repeating logging during disposal. An actual stderr capture above the 16 MiB
limit interrupts the read and preserves the same failure through background retirement.
An exited shell parent with a child holding stderr open reports the bounded drain
failure on repeated retirement. That child is not signalled through the parent's
retired process-group ID. These cases do
not exercise an installed provider CLI, its OAuth refresh or Windows containment.

After a kill, Connect waits at most 5 seconds for the runtime to report the exit. When the bound
expires, the operation fails with a message that names the process ID:

| Lane | Failure |
| --- | --- |
| Antigravity CLI chat | `LlmProviderException` with `ProviderCode` `antigravity_cli_exit_timeout` |
| Antigravity native agent session | The cancelled operation keeps its own outcome, and the session faults. Later calls throw `InvalidOperationException` with the reason. |
| Apple helper | `InferenceException` with `InferenceFailureCode.ExecutionFailed` |

Claude CLI chat owns the process during stdin writes as well as output reads. Cancellation during a
blocked stdin write and early disposal of its output stream both close standard input and hand the
process to the background exit. Its stderr reader enforces a 16 MiB character limit while reading. A stderr read failure interrupts
blocked stdin/stdout operations. Retirement cancels and observes that reader with a 5-second bound.
Its stdout reader rejects a decoded line above 16 MiB characters before receiving a newline.
Total captured stdout is checked before a line and its normalized newline are appended.

Claude and Antigravity native session processes share one pipe-operation owner. Retirement fences new
reads, writes and exit waits, cancels active and queued operations, and waits for them to leave before
disposing the process. Concurrent disposal calls await the same completion, including its failure.
Caller cancellation affects only that operation. Each write flushes before completing. Closing stdin
discards buffered input from an abandoned write; it does not flush that input during retirement.
Antigravity rejects closing stdin while another write is active.

Native Claude stderr capture rejects input above 16 MiB characters during reading. Native Antigravity
keeps the first 64 KiB characters and drains the remaining stderr without retaining it. A capture
failure interrupts pending pipe operations. Both wrappers and Claude chat share a stdout line reader
that rejects decoded lines above 16 MiB characters during receipt. It preserves the pipe's encoding,
CR/LF/CRLF delimiters and an unterminated final line at EOF. Caller cancellation retains received
partial text for the next read. An oversized line faults the reader; later reads report the same
failure without truncating or resynchronizing its content.
The reader returns a complete interactive line while stdin remains open. It does not wait for
another input, process exit or another byte-buffer refill. The existing launcher fixture verifies
that behavior through Antigravity's actual pipe wrapper and a detached shell process.

Native-session disposal allows up to 2 seconds for graceful exit after closing stdin, then signals an
owned process that remains alive. The process exit and stderr EOF share a subsequent 5-second bound.
Cleanup cancels and observes the stderr reader with a separate 5-second bound. An exit wait or
disposal that cannot drain stderr fails with `IOException`; a capture-limit failure remains visible
as `InvalidOperationException`. A parent that already exited is not signalled through its former group
ID. Descendants holding its stderr pipe open therefore produce a drain failure, not successful
retirement. Antigravity drains its session reader even when process disposal fails.
Antigravity session disposal also shares one retirement completion across callers. It reports
termination and pipe-drain failures to those callers and closes its observation feed even on failure.

Direct Claude native sessions fence operational calls when retirement starts. Retirement cancels
and drains active turns, observation enumerations, control requests and host tool callbacks before
disposing their process, reader, resource poller and gates. A host tool remains owned through result
projection and the response write. An ignoring callback or write keeps retirement pending until it
returns; a cancelled late callback result is not emitted. Bounded turn and observation producers
drain without requiring a paused public reader to resume. Concurrent disposers share the completion
and its cleanup failures. A failed
process disposal does not skip the reader or resource poller. Cancellation and cleanup failures are
retained together. Tool cancellation matches the typed JSON-RPC ID; numeric and string IDs remain
distinct, and repeating an active ID fails the session without abandoning its first callback.
If a tool's cancellation callback throws when the process exits, the session retains that failure
for retirement, cancels the remaining tools, ends reported resources and closes its observation feed.

Claude and Antigravity share one launcher for chat and native sessions. Their default commands are
`claude` and `agy`. On macOS and Linux the launcher searches absolute inherited PATH directories
in order, then the current user's `~/.local/bin`. It binds the discovered file by absolute path
without changing the child's PATH or running a login shell. A candidate must be a file with OS
execute permission. On Windows the launcher searches absolute PATH directories in order for `.exe`,
`.com`, `.cmd` and `.bat` entries. An explicit executable path keeps its configured target.
Native executables receive structured arguments through `ProcessStartInfo.ArgumentList`.

For a complete argless Node wrapper generated by [npm/cmd-shim](https://github.com/npm/cmd-shim/blob/main/lib/index.js), the launcher resolves its literal
entry path and the adjacent `node.exe` or PATH Node executable. It invokes Node directly with
structured arguments and preserves the wrapper's `.JS` PATHEXT removal. Modified wrappers,
environment-setting wrappers and wrappers with Node options are not translated. On Windows, an
explicit `.js`, `.mjs` or `.cjs` entry also uses Node. The launcher
never installs or downloads Node or a CLI.

Other batch wrappers run through the system `cmd.exe`, with AutoRun and delayed expansion disabled.
Every argument is a quoted literal. Quotes, percent signs, exclamation marks, CR/LF, NUL and a
trailing backslash are rejected before starting the batch command. Spaces, Unicode, empty strings
and quoted shell metacharacters are retained. The complete command line, including the interpreter
and switches, cannot exceed [8191 characters](https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/command-line-string-limitation). These batch restrictions do not apply to native or
resolved Node arguments. An AGY prompt still has its separate platform argument-size bound and
process-list exposure; a batch launch can hit the smaller command-line bound first.

Native Windows and resolved Node invocations also check the complete prepared command before launch.
The check counts the quoted executable, all arguments with .NET's quote/backslash expansion,
separators and terminating NUL against the
[CreateProcessW 32,767-character limit](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw).
An oversized command fails with `ArgumentException` asking the caller to reduce the prompt or schema.
The launcher retains the original structured arguments and leaves rendering to `ProcessStartInfo`.
The existing launcher fixture checks expanded quotes and aggregate arguments through Windows
configuration, including accepted boundary arguments. It does not start a Windows process on macOS.

The POSIX launcher test checks inherited-PATH precedence, skipped non-executable files,
user-local CLI lookup and explicit paths with real filesystem entries. A shell fixture verifies
literal arguments without shell expansion. The test skips on Windows and does not qualify
Windows batch or Node invocation.

## Image provider integration

`ImageTransportJobSnapshot.ModelId` and `ModelRevision` are optional observed execution facts.
The portable image result preserves them in Common provenance. Workflow IDs and versions remain
workflow selection facts, not model provenance. ComfyUI history currently reports no execution-model
identity or revision, so both remain null.

Resource-bound generated images carry the validated resource descriptor and full selection receipt
returned by each output's own commit. Each selection preserves the actual workspace, session, revision
and ordered membership. The image path does not reread membership or manufacture a selection.

Register the image capability and resolve its provider-neutral transport through
`ImageProviderService`:

```csharp
services.AddImageConnect();

ImageProviderService images = services.GetRequiredService<ImageProviderService>();
IImageTransport transport = images.GetRequiredTransport(ImageTransport.ComfyUi);
```

The host owns connection persistence, credentials, workflow storage, durable job scheduling, media
storage, retry policy, and user authorization. It maps those records into short-lived neutral values:

```csharp
var connection = new ImageConnection
{
    Transport = ImageTransport.ComfyUi,
    Endpoint = "http://127.0.0.1:8188",
    RequestTimeout = TimeSpan.FromMinutes(10)
};

ImagePreparedReference uploadedReference = await transport.UploadReferenceAsync(
    connection, reference, content, cancellationToken: cancellationToken);

PreparedImageTransportRequest prepared = transport.Prepare(new ImageTransportPreparationRequest
{
    Workflow = workflow,
    PositivePrompt = prompt,
    NegativePrompt = negativePrompt,
    Width = 1216,
    Height = 832,
    Seed = seed,
    CacheKey = cacheKey,
    References = [uploadedReference]
});

string providerJobId = await transport.SubmitAsync(connection, prepared, cancellationToken: cancellationToken);
ImageTransportJobSnapshot status = await transport.GetJobAsync(
    connection,
    providerJobId,
    workflow,
    cancellationToken: cancellationToken);
```

`ImageWorkflow.TransportConfiguration`, `PreparedImageTransportRequest.ProviderRequest`, and
`ImageTransportOutput.ProviderState` are opaque adapter-owned data. `UploadReferenceAsync` returns
an immutable receipt; `Prepare` copies that receipt without authentication, reads, uploads, or native
ID lookups. Consumers may round-trip prepared data but it does not prove authority to reuse a native
reference. ComfyUI accepts native receipts only on the same unchanged transport/connection owner.
Changed static headers, a different connection, or a different transport owner require reupload of
logical content. The package does not own an image
database, media library, workflow catalog, cache, or background worker.

ComfyUI reference uploads borrow the input stream. Upload reads from its current position without
rewinding, flushing, or disposing it, including cancellation and failed uploads. The caller owns
the stream's eventual disposal.

ComfyUI accepts static protected headers. Its newly optional dynamic `Authorization` and xAI binding
members remain unqualified and refuse before callbacks or HTTP. Image generation accepts one binary
reference or one logical resource resolved through caller-owned access. URL, native-file, multiple
references, step/guidance overrides and Count greater than one refuse before effects. All observed
outputs are retained. Download MIME comes from actual HTTP metadata, not a workflow default.
Composite admission copies binary reference bytes before credential or capability awaits. Constructing
a BinaryMediaSource still borrows caller memory; the operation owns its entered byte snapshot.

When resource access is supplied, a binary reference is committed and selected for the caller's
session before ComfyUI uploads the authorized logical resource. Resource reads and uploads retain
the endpoint/header snapshot admitted before the first callback. Generated outputs are likewise
committed and selected before the composite publishes completion. Typed failures preserve known
content/selection outcomes; a dispatched commit with no matching response remains unknown.
Resource callbacks retain the admitted workspace and session scope. Returned content and selection
receipts must match the actual artifact. Successful owned-handle cleanup is followed by a final
scope and retirement fence before publication. Refused cleanup uses the typed Cleanup phase and
retains the actual unsafe handle. Independent operation and cleanup errors remain ordered. Bound
inference retirement reports the same retained cleanup failure to repeated disposal callers,
including reader refusals before job submission. Healthy borrowed collaborators detach after drain.

Image facades and settings managers fence new calls on disposal and drain admitted operations before
releasing borrowed transports, workflow catalogs, binding factories, resource access and commit
targets. Those collaborators are never disposed. Settings admission also applies through base and
interface references. Draft edits invalidate pending health, choice and workflow results; an admitted
conditional save still preserves the commit target's observed committed or uncertain result.
`ImageProviderService` also supports synchronous and asynchronous disposal. Retirement cancels
workflow listings, drains providers that ignore cancellation, and checks its own retirement fence
before publishing a completed listing. Provider failures retain their original identity. Admitted
synchronous facade calls keep their original outcomes; returned inference objects and settings
managers keep independent caller ownership. Concurrent disposal callers share completion, including
cancellation-callback failures. Disposal releases the borrowed transport map without disposing it.

`ConnectImageJson` exports exact type-name metadata for the complete public Image data graph.
Runtime authority, bindings, resource handles and binding factories refuse non-null serialization.
Download bytes reuse Common's exact required-data property converter: explicit JSON null is refused,
while empty bytes and unknown MIME remain valid observations. This is not a global byte-memory policy.
Image reference receipts, prepared requests, output receipts and job snapshots use fixed `ToString()`
labels that omit opaque provider data.
Workflow selection stores both `ImageWorkflowOrigin` and the exact ID. Host and built-in origins
remain separate; ComfyUI does not advertise a built-in workflow catalog.
Owned workflow-origin and selection metadata preserve `Host=0` and `BuiltIn=1` and reject undefined
numeric origins. This guard does not change other Image enum policies.
Each present selection requires an explicit origin; an omitted origin cannot silently select Host.

The shared [`XaiFamilyBinding` contract](docs/media-bindings.md) uses explicit selection and the
existing credential-access owner. It does not register or qualify an xAI Image transport.
Family acquisition rejects incompatible credentials before refresh and incompatible refresh results
before credential save; the Responses client uses the same OAuth-only authorization validator before HTTP.
The chat facade applies that selected adapter policy before credential refresh, credential save and catalog callbacks.

Private xAI media codecs now map native image generation/edit bodies and typed video
generation/edit/extension bodies. They retain ordered references, explicit false controls, actual
submission IDs and one-poll status/model/output observations without requested-model or MIME
fallbacks. The image codec copies ordered native `url` or `file_id` objects without inferred IDs or
metadata. It validates HTTP(S) provider-fetch URL data without fetching content and preserves
explicit `aspect_ratio` values, including null, for single-reference and multi-reference edits.
The [REST edit schema](https://docs.x.ai/developers/rest-api-reference/inference/images) and
[official SDK request builder](https://github.com/xai-org/xai-sdk-python/blob/main/src/xai_sdk/image.py)
admit this control alongside a single reference. The codec does not infer resulting dimensions.
Storage and nullable usage facts use finite owned projections
rather than raw response DTOs. These codecs alone perform no HTTP, upload, credential acquisition or receipt authorization.
The Image and Video transports consume these codecs through the selected core and Files owner.
Their registration does not establish OAuth entitlement or runtime qualification. Synchronous media
usage handoff remains unavailable.

## Video contracts

`Darkspyre.Inference.Connect.Video` supplies `VideoConnection`, `IVideoTransport`,
`VideoProviderService.Bind(connection, xai, resources)` and the five-operation Common
`IVideoInference` facade. Capability lookup, submit, one-poll lookup and owned download
remain explicit calls. Materialization streams actual downloaded bytes into the borrowed
caller resource store and atomically commits content with session selection. It retains
actual nullable MIME, duration and expiry instead of inferring them from request policy.
Returned download streams hold their operation lease until closure. Facade disposal closes
owned downloads, fences new operations and drains admitted work before detaching borrowed
transports, bindings and resource access. Callers can dispose the bound facade through
`IDisposable` or directly through `IVideoInference.DisposeAsync`. Returned-stream operations check
the synchronous owner
fence before effects and before returning, even when linked cancellation propagation is delayed.
Cancellation during successful materialization cleanup retains the observed committed reference,
session-selection outcome and revision in `InferenceResourceOperationCanceledException`.
Materialization validates admitted workspace/session scope and actual content/selection receipts.
Owned download and writer cleanup each runs once, including independent refusals. Cleanup failures
use the typed Cleanup phase and retain the actual unsafe owner. Bound retirement preserves those
failures even when a returned stream is disposed before the facade. Repeated disposal shares the
same outcome after drain and borrowed detachment; ordinary operations do not poison retirement.
`VideoJobSnapshot.ModelId` preserves an actual nullable provider model observation through the
existing Video and nested batch metadata. No requested model or job scope supplies a fallback.

`VideoConnection.MaximumEncodedReferenceAudioBytes` is a nullable caller-selected limit with no
default. A supplied value must be positive. Submit preflight requires it for binary or workspace
reference audio before opening content or acquiring credentials. Preset voices and ordinary
reference URLs do not require it. The encoder bounds combined data-URL bytes, including MIME prefixes,
base64 and repeated reference occurrences. It reads incrementally and refuses excess input. Video
settings preserve the value through load, edit and save; the existing connection JSON metadata includes it.

`XaiVideoOptions` is the fixed Video-owned subtype for Files-owned storage controls, signed
upload destinations and explicit user data. Common metadata refuses these provider options;
`ConnectVideoJson` owns their complete generation/edit/extension and scoped receipt graph.
The private Video codec validates all implemented request controls before resolving any media source.
It preserves supplied aspect ratios without establishing their effective provider behavior.
The canonical codec supplies the published model facts for both request admission and capability
lookup. `grok-imagine-video-1.5` and `grok-imagine-video-1.5-lite` offer generation only, with text/first-frame
output through 1080p. Lite's provider output is upscaled. Lite refuses references and pinned frames;
classic refuses reference audio. Reference-image limits are 14 for 1.5 and seven for classic, and
classic reference-to-video duration is at most ten seconds. The codec accepts the published `21:9`
and `5:2` aspect ratios unchanged. These facts follow the
[xAI Video model table](https://docs.x.ai/developers/model-capabilities/video/overview) and
[REST schema](https://docs.x.ai/developers/rest-api-reference/inference/videos).
The published 1.5 reference mode uses at most 720p, including last-frame and keyframe pins, image
references and either voice-reference variant. These known-invalid 1080p requests refuse before source
resolution. The [reference-mode guide](https://docs.x.ai/developers/model-capabilities/video/reference-to-video)
defines this mode and limit. An unknown model's null resolution list does not refuse an otherwise
mapped 1080p request, including reference and pinned-frame controls. The codec sends the requested
model, resolution and controls unchanged for a caller-selected attempt. This does not advertise
confirmed model support. Known model and mode restrictions still refuse.
Serialized job/output scope is data, not proof of authority. Each implemented transport owns
receipt validation and download-origin authorization before effects. Lookup and download carry a
private view of the same borrowed binding, with the facade's retirement fence, before acquisition or refresh.
Materialization performs one download, not a second credential acquisition. Extension requests
refuse an explicit `User` instead of dropping it. New settings carry an explicit xAI connection selection;
rendering, opening and saving do not acquire credentials. Only the explicit capability action
uses the supplied borrowed binding factory. Header changes use the typed protected commit target.
The existing LLM header parser rejects reserved or malformed explicit and acquired-snapshot
headers before credential refresh. The bound facade copies source bytes at operation admission;
the shared `BinaryMediaSource` data constructor still borrows its input.

`AddVideoConnect` registers the internal xAI transport. Capability lookup reads the selected connection's
Video model catalog. Submission retains the actual native request ID. Lookup performs one poll and
retains actual nullable progress, model, output and sanitized provider failure facts. Native downloads
authorize a Files receipt or freshly retrieve the output through its authenticated native job before
opening the same unsigned media pipeline. An imported arbitrary URL is not authority. Terminal batch
outputs with a committed logical resource use its named reader, without inventing a native job ID or
falling back to a provider URL. These implementations remain unqualified against a live provider.

Files and unsigned media transfers share one internal HTTP download owner. It exposes response
metadata before body opening, streams incremental copies, and caches one response/request cleanup
outcome. The response alone disposes content. A refused cleanup retains the actual response/body or
request and remains visible to the owning Files/core retirement. Borrowed destinations remain open.
The send-through-headers timer disarms before body processing; borrowed HTTP timeouts do not change.
Core-owned download opening retains the selected core, Files and HTTP generation operations until
its caller releases them after closure and read drain. Video uses this opening path for returned live
streams. Response closure starts before read drain; leases and creator fences release only after both
settle. Actual cleanup failures remain visible to the transport and facade as well as the core owner.
The focused Video regression enters this path through the registered transport, selected binding and
public facade in both owned and borrowed HTTP modes. A controlled HTTP body verifies held-read drain,
response-only closure and repeated cleanup failure reporting. Successful container retirement releases
the owned handlers after drain. A transport cleanup failure remains visible from the container, but
the framework's stopped disposal does not prove pool retirement. The HTTP stand-in does not establish
live provider entitlement or wire acceptance.

## Host-executed tools

For complete, compile-checked implementations for every supported provider, including connection
shapes, SecureStore ownership, and the separate chat and agent loops, see the
[`Darkspyre.Inference.Connect.LLM.Examples` guide](examples/Darkspyre.Inference.Connect.LLM.Examples/README.md).

`SupportsToolUse` describes the chat request/response contract: the provider can request a
host-defined function, return its arguments, and accept the host's result on a later request.
`SupportsAgentSessions` and `SupportsAgentToolUse` describe the live session contract, where the
provider invokes an `LlmAgentToolHandler` and receives its result during the same turn. Darkspyre.Inference.Connect.LLM
never grants a model direct access to host resources. The host must validate every call and apply its
own authorization and side-effect policy.

| Transport | Chat tools | Agent tools | Prompt images | Prompt documents |
|---|---:|---:|---:|---:|
| OpenAI Responses | Yes | Yes | Yes | Yes |
| OpenAI-compatible Chat Completions | Yes | Yes | Override | Override |
| ChatGPT Codex subscription | Yes | Yes | Yes | Yes |
| Anthropic Messages with API key | Yes | Yes | Yes | Yes |
| Grok OAuth Responses | Yes | Yes | Catalog | Yes |
| Claude subscription via official CLI | No | Yes | Yes | Yes |
| AWS Bedrock Converse | Yes | Yes | Yes | Yes |
| AWS Bedrock AgentCore Harness | No | Yes | No | No |
| Google Vertex AI (Gemini and Claude) | Yes | Yes | Yes | Yes |
| Gemini API (Google AI Studio) | Yes | Yes | Yes | Yes |
| Antigravity CLI | No | Yes | No | No |

Tool-result content support is declared per route and lane; see [Tool output](#tool-output).

OpenAI-compatible calls emit their tool start before argument deltas. Missing provider tool-call IDs
receive distinct Connect-generated IDs. Preserve these IDs unchanged in assistant history and tool
results; a late provider ID does not replace an already published generated ID.

Hosts define tools on the session as `LlmAgentToolDefinition` values and run them in the session's
`LlmAgentToolHandler`; see the agent example under [Direct ChatGPT subscription transport and managed agents](#direct-chatgpt-subscription-transport-and-managed-agents). The chat
request and its tool calls are internal adapter plumbing.

### 2.0 attachment migration

Version 2.0 replaces `LlmToolAttachment` with `LlmAttachment`, removes caller-supplied
`MediaType`, and adds `Attachments` to `LlmChatMessage` and `LlmAgentTurnRequest`. Replace the old
type name, delete `MediaType` initializers, and gate prompt inputs with `SupportsPromptImages` or
`SupportsPromptDocuments`.

The binary variant of a prompt attachment is a provider-neutral value. The host supplies the
high-level kind, filename, and bytes; Darkspyre.Inference.Connect.LLM detects the media type and owns base64 encoding,
temporary-file staging, sandbox roots, and every provider wire shape:

```csharp
var promptWithFiles = new LlmChatMessage("user", "Compare the chart with the report.")
{
    Attachments =
    [
        new LlmAttachment
        {
            Kind = LlmBinaryKind.Image,
            FileName = "chart.png",
            Data = chartBytes
        },
        new LlmAttachment
        {
            Kind = LlmBinaryKind.Document,
            FileName = "report.pdf",
            Data = reportBytes
        }
    ]
};
```

Check `SupportsPromptImages` and `SupportsPromptDocuments` before attaching prompt input. Prompt
attachments belong on user messages only. Adapters reject an unsupported kind, unrecognized content,
path-bearing filename, or empty payload instead of silently turning it into text.

OpenAI Responses, direct Codex, Anthropic Messages, Claude CLI, and Bedrock use native
image/document content blocks for prompt attachments. Direct Codex sends prompt attachments and
tool-output binaries inline through Responses, with no CLI staging directory. The caller supplies kind,
filename, and bytes; adapters own MIME detection, encoding, and wire representation.

### Explicit cache contracts

`ILlmContextCacheClient` declares explicit create, get, one-page list, expiry update and delete
operations. Its request, correlation, receipt, usage, page and delete-result records have exact
type-name getters on `ConnectLlmJson`. `BindContextCacheAsync` validates a selected connection
without remote effects and binds Gemini API or standard-project Vertex Gemini; other routes remain unavailable.
`LlmChatRequest.ExplicitCache` and Common's request carry the same data-only selection.
`SupportsExplicitCache` defaults to `Unknown` on both capability shapes; overrides cannot enable it.
The Google facade advertises its owner-admitted selection path; a raw direct adapter without this
internal admission remains unavailable. Other built-in routes refuse a supplied selection before
credential refresh, catalog discovery, counting, remote requests or engine/process entry. Registered implementations own their declared
capability and selection validation. Vertex exact cached counting refuses independently of
generation support. SelfHost does not forward native provider cache receipts. Portable projection
preserves the selection; advisory `PromptCache` remains separate. No operation creates, renews,
transfers or falls back from a paid cache implicitly.

Each Google cache operation resolves one selected connection snapshot. The same facade retains
only derived provider-validated receipt facts, swept at observed expiry and cleared at retirement;
it retains neither prefixes nor complete listings. Scope IDs are data, not account authority.
Matching owned record/JSON copies can reuse that receipt evidence. Restored references and references
with no current receipt observation can attempt the selected operation through the current authenticated
fixed route. Missing or uncertain receipt evidence does not require a preliminary `Get`.
Credential/configuration replacement discards prior receipt proof without automatically revalidating it.
Credential equality is only change detection,
never a claim of rotation-stable principal identity. List does not authorize generation.

Vertex requests use the fixed configured project/location route. An authorized create, get or
consistent list response can establish that route's observed numeric project parent on the same
binding. Conflicting project, location, model or native-name facts refuse. Short `models/gemini-*`
response names preserve model identity but do not establish a project association. Replacement
configuration clears this derived association; imported names never select a request endpoint.
Express mode and Claude explicit caches remain unavailable.

Google generation and exact Gemini API counting use the same canonical cached-content suffix body. Cached
suffixes cannot replace system instructions, tools or tool configuration. Advisory occupancy is
unknown rather than a suffix-only total, and it never performs cache revalidation. Actual usage
remains the provider's observation. Create, expiry-only update and delete are explicit operations;
dispatched acknowledgement failures preserve mutation uncertainty without retry. A current-binding
successful-delete tombstone prevents generation but permits explicit repeat deletion, and
`AlreadyAbsent` requires an actual authorized 404. Expired selections and fixed-route not-found
reads use distinct typed unavailable reasons; authentication failures do not imply absence.

Cache creation materializes only explicitly named logical prefix resources, not the session's
selected chat resources or the workspace catalog. The shared scope/descriptor/read-handle owner
validates immutable metadata and counts streamed bytes. Gemini API Files staging uses a fixed-origin
multipart route, validates returned native identity/MIME/observed size, and observes PROCESSING via
fixed-route readiness reads under the same connection until ACTIVE. FAILED or contradictory facts
refuse; cancellation drains admitted work. Opened handles and responses are owned; workspace
access, source content and HTTP are borrowed. Derived staging copies follow Google's documented
expiry without implicit deletion, reupload or retry. Unknown-length multipart acceptance, staging
expiry versus cache lifetime and real model entitlement remain provider qualification gates.
Failed Gemini staging preserves observed HTTP status, request ID and `Retry-After` delta in its
safe provider error. These diagnostics do not expose the response body or authorize a retry.

Vertex's optional typed `LlmVertexCacheStaging` selects a Cloud Storage bucket and object prefix
under the same acquired Google authorization. It creates no bucket and accepts no separate staging
credential. Named and raw file media stream to derived objects with observed bucket, object name,
generation and size. Any present response MIME is validated; absent response MIME remains unknown.
Duplicate named references open and upload once while every
sent inline occurrence counts its raw bytes. Without staging, inline media preparation uses a
10 MiB aggregate raw-content budget; this is a local preparation bound, not a claim about the
backend's encoded-body measurement. The 10,000,000-to-10,485,760-byte interval reaches the provider
unchanged. System instructions, tool schemas, text, base64 and metadata have no added body-size cap.
Unknown-length named content reads once through the remaining occurrence budget plus a boundary
byte; oversize content refuses without truncation, reopening or automatic staging.

Cloud Storage upload query parameters encode the exact object name once. Native `gs://` file URI
values preserve slash, plus, percent and Unicode text without normalization or percent decoding.
The local unqualified-URI representation rejects literal `#`, `?` and controls in the configured
prefix; this is not a Cloud Storage object-naming prohibition. Insert-only upload preconditions
do not make an object immune to later external replacement. The caller maintains derived object
availability and unchanged content for cache use. The adapter does not append a generation fragment,
delete objects, retry uploads or recreate caches implicitly. Partial staging effects remain distinct
from dispatched cache-create uncertainty. Unknown-length upload acceptance, native URI character
interpretation, external object retention and actual model entitlement are not established by local
validation or controlled transport fixtures.

Upload requests own their content and receive one cleanup attempt after response cleanup. Media
content detaches its borrowed source and fence after admitted upload work drains, including when an
outer request-content wrapper refuses cleanup. The source stream remains borrowed.
Gemini and Vertex cache-prefix preparation use the shared named-resource failure boundary.
Existing typed failures keep their actual resource, phase, commit/selection receipts and cancellation
token. Untyped failures carry the current phase and known read bytes. Independent read-handle
cleanup failures remain alongside the original failure; prefix preparation does not manufacture
a dispatched cache-create outcome.
Cache CRUD exchanges also own their authorized requests. They attempt response then request cleanup
before result publication or typed uncertainty, preserving validated late receipt evidence. Admission
failures still clean requests once and do not imply a dispatched mutation.
If owned request, response or read-handle cleanup refuses, the same facade retains the actual refused
owner through a safe cleanup carrier. Retirement drains admitted operations, attempts independent
owned cleanup and reports its cached cleanup failure to every disposal caller; it does not retry
the refused release. Ordinary provider/transport/receipt failures do not fault retirement, and
healthy borrowed references detach even when a distinct owned resource cannot be released.

Receipts require observed expiry and preserve unknown usage/timestamps as null. List page size is
explicit and positive; a short page with a continuation token remains nonterminal. Collection
properties copy their lists, without claiming an admitted operation's deep prefix snapshot.
Create's optional `Resources` is borrowed runtime authority: non-null serialization fails and
diagnostics do not inspect it. The request does not validate a provider's lifetime or scope.

The private Google cache data boundary freezes prefix lists, binary bytes and tool schemas, accepts
one positive, Protobuf-representable TTL or future expiry, and preserves the exact reconciliation
display name. It converts CLR ticks without floating-point rounding and refuses observed timestamps
finer than representable CLR precision. Its receipt parser preserves independent unknown usage facts, validates required
observed expiry and returns one ordered page with its actual continuation token. These effect-free
helpers do not establish native resource/principal authority or enable cache dispatch.
Omitting both expiration fields sends neither field and lets the provider choose its default.
The receipt uses only the provider's observed expiry. Supplying both fields is invalid.

The shared Gemini request codec can construct immutable cache-prefix contents, system instructions,
function declarations and tool configuration without generation-only settings; this internal
preparation shares the canonical contents/tool owner used by the Gemini lifecycle; it does not
independently authorize a native receipt or prefix resource.

The private Google cache create send boundary borrows an already-authorized request and client.
It disposes the response, checks the existing caller and HTTP-generation fences, and never retries.
Cancellation before send remains ordinary cancellation. Failed acknowledgement after invoking send,
including a non-success HTTP status, conservatively reports possible creation through the typed
reconciliation errors. A local acknowledgement does not prove rollback or permit retry. A validated
late receipt remains exception evidence when retirement or response cleanup prevents success. Fixed diagnostics preserve
observed status and request identifiers without retaining untrusted body or exception text. This
boundary does not authorize routes, issue receipt scopes or make a provider cache route available.

The private Google cache read boundary borrows an already-authorized GET request and client.
It owns the response and executes one receipt or page callback without retrying or chasing pages.
Existing caller and SDK-generation fences cover send, body read, parsing and final publication after
response cleanup. Ignoring send, read and cleanup work drains before the admitted operation exits.
Read failures retain safe status and request identifiers as ordinary provider or cancellation errors,
with both safe operation and cleanup facts when they fail together. They do not imply a mutation,
publish late receipts or establish resource/account authority.

The internal Gemini cache read adapter builds fixed GET and one-page LIST routes from an admitted
connection. It reuses the Gemini API endpoint and API-key rules, validates native resource names and
model IDs, and replaces imported receipt scope data with the caller-supplied scope after an exact
GET identity check. LIST preserves actual page order and continuation tokens. Requests are owned;
HTTP and owner fences are borrowed. The adapter issues no scope authority, retains no cache catalog
and does not independently enable public binding, generation selection or cache creation.

The private Google cache mutation boundary borrows already-authorized PATCH or DELETE requests and
the existing caller and SDK-generation owners. It validates update receipt identity and observed
expiry before publication. Delete success acknowledges `AlreadyAbsent=false`; only HTTP 404 can
invoke the borrowed effect-free absence-authorization callback, fenced before and after invocation.
That callback supplies already-established binding evidence. A false result remains an uncertain
failed acknowledgement, not absence. Rejections and deletion acknowledgements do not read response
bodies. Failures after invoking send use the typed update/delete uncertainty errors, preserving safe
operation and response-cleanup facts and optional validated late evidence without retrying or
publishing success. Cancellation retains actual observed response status, request ID and retry delay
as safe evidence within one cancellation failure, not an extra failure or successful acknowledgement.
These internal exchanges do not authorize routes, issue receipt scopes or
enable public cache operations.

`LlmContextCacheCreateCancelledException` and `LlmContextCacheCreateUncertainException` retain
correlation, the actual request ID and an optional observed receipt without declaring success or
rollback. They are runtime exceptions, not portable metadata roots. Their presence does not
implement dispatch tracking, automatic retries, reconciliation or paid cache creation.

`LlmContextCacheUnavailableException` distinguishes a selected route's observed missing cache from
elapsed selected or observed expiry. Missing permits an absent HTTP status or HTTP 404, never a
contradictory authentication or transport status. Expiry does not establish deletion or account entitlement.
`LlmContextCacheMutationUncertainException` and `LlmContextCacheMutationCancelledException` identify
an expiry update or deletion that may have committed. They retain the selected reference, actual
request ID and optional matching late update or deletion evidence without declaring success or
authorizing another mutation. Update evidence preserves the same native resource and model while
allowing changed expiry and the current facade's receipt scope. A local scope ID is not provider
identity. A supplied reference expiry must match that receipt's observed expiry.
Cancellation retains its token. The existing create errors remain
distinct. These runtime exceptions have fixed diagnostics and retain already-safe inner failures;
the adapter boundary must sanitize untrusted failures before construction. They are not portable
metadata roots. `LlmContextCacheUnavailableReason` and `LlmContextCacheMutation` have closed,
numeric owned metadata. Constructor validation does not prove provider authority. These
contracts do not implement update/delete dispatch or make a cache provider available.

### Tool output

`LlmAgentCallerServices` groups borrowed permission, text-file, terminal-factory, shell-executor
and resource-access interfaces. The typed callback DTOs preserve actual session/turn scope,
informational content/diffs/locations, raw JSON, ordered permission choices and artifact descriptors.
New DTOs copy lists, binary data and JSON values on constructor/init/with assignment. Diagnostic
strings omit callback contents, paths and environment values. File read StartLine is one-based;
MaxLines=0 remains zero. Terminal OutputByteLimit preserves nullable/zero/64-bit values.
Absolute POSIX, Windows drive-rooted and server/share UNC path text survives across decoding hosts
without normalization or filesystem access. File URLs and relative paths are not operational paths.

Factories and caller services remain borrowed. A returned terminal handle is owned by its session;
Kill preserves readability, while DisposeAsync must kill/drain and invalidate that handle. These
interfaces contain no filesystem, process or shell implementation. Start/Resume borrow the typed
composition and reject unsupported supplied services before runtime work. The xAI Responses managed
lane maps shell execution through the supplied borrowed `ShellExecutor`; other built-in managed
adapters do not advertise that mapping. Managed sessions release their borrowed handler/service
references only after admitted work drains; they do not dispose them.
Caller-owned SelfHost portable shell support does not enable its managed shell executor or
caller-service mapping; supplied managed shell definitions/services still refuse before engine resolution.

`ConnectLlmJson` exports the callback data through exact type-name getters and closed fixed unions.
Runtime services have no public metadata getters. Their internal owned codecs refuse all non-null
read/write without invoking callbacks or diagnostic methods. Start/Resume are not data-codec roots.
Callback RuntimePolicy uses fixed `host-controlled` and `provider-configured` variants. Scope data
freezes caller-selected policy roots and does not grant filesystem or resource access.
The exact `LlmAgentRuntimePolicyKind` root and nested capability data reject undefined numeric values
and preserve both defined numeric values.

`LlmAgentToolPresentationEvent` carries a copied complete tool presentation without requiring a
tool name or turn ID. `LlmAgentContentObservationEvent` carries ordered typed user, assistant or
reasoning content. Both preserve actual nullable turn/item identities and explicit replay facts;
neither invents completion or invokes a tool. Append adds blocks. Replace requires an actual item
ID and represents the complete known item content. Changing an Append observation to Replace
through an initializer requires assigning its item ID first. Event payloads copy binary data,
JSON and nested collections. Diagnostic strings redact presentation contents.
`ConnectLlmJson` exports these two concrete events and their two closed enums through exact
type-name getters. Existing streams carry the events through `LlmAgentEvent`; there is no base-event
data codec. Resource links remain observations without fetching or artifact registration.
These contracts do not register an ACP/Copilot adapter or implement protocol replay and tool-update
merging.

`LlmAgentClientToolDefinition` is a closed function/shell data union. The existing
`LlmAgentToolDefinition` retains its schema, strictness, deferred-loading and sequential-execution
fields. `LlmAgentShellTool` wraps the Common shell definition. `ConnectLlmJson` exports these three
types with exact type-name getters and fixed `function`/`shell` discriminators. Start/Resume definitions
and Common/Connect request, history and terminal collections preserve one ordered function/shell
union. Unsupported shell definitions/history refuse before refresh, catalog, provider counting or
native entry. Facades freeze the new tool graphs before their first awaited resolution. Agent
Start/Resume/Fork freezes definitions before connection resolution. Managed openers freeze definitions
and supplied context before preparation or catalog awaits. Schemas and context bytes are copied;
handlers and caller services stay borrowed. Shell results
match actual preceding calls; output limits remain on that call, not duplicated on the result.
The xAI wire projection echoes the matched call's actual nullable output limit and preserves ordered
commands, output members and exit/timeout outcomes without truncation or inferred execution facts.
Terminal shell observations use the same Responses parser and opaque continuation codec; streamed
observations do not invoke the executor. Managed mixed function/shell calls execute once in terminal
order and retain each genuine completed result. A callback failure or interruption propagates and
leaves the unfinished tail unresolved for explicit reconciliation. Function-only scheduling and
error conversion are unchanged. Shell callbacks run outside the context lock with actual
thread/session/turn scope and no inferred directory or runtime authority. Retirement drains admitted
callbacks before detaching borrowed services. This source implementation is not OAuth/runtime
qualification. Skills directory multipart and uploaded-skill generation attachment remain separate
protocol gates; no local filesystem discovery, process runner or Skills upload is performed here.

`LlmAgentEvent.TurnId` is nullable for actual session-only observations. Concrete turn-scoped events
retain guarded non-null IDs, including construction and base-typed `with` assignments. Resource and
resource-output observations carry null, not an empty synthetic turn. The owned
`LlmAgentResourceSelectionChangedEvent` metadata preserves an observed revision and nullable actual
turn; its declaration does not implement selected-resource membership or hint publication.
Common shell-call observations and typed history remain distinct from function-shaped agent events.

A tool result carries one `LlmToolOutput`: ordered parts, optional structured JSON, and an error flag.
A chat request sends it as `new LlmToolResult(call.Id, output)`, and an agent `ToolHandler` returns it.

```csharp
var output = new LlmToolOutput
{
    Parts =
    [
        new LlmToolOutputText("The chart and report follow."),
        new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", chartBytes, "chart.png"),
        new LlmToolOutputBinary(LlmBinaryKind.Document, "application/pdf", reportBytes, "report.pdf"),
        new LlmToolOutputResource("https://example.test/spec") { Name = "spec" }
    ],
    StructuredContent = JsonSerializer.SerializeToElement(new { rows = 42 })
};
```

The host supplies each binary part's media type. A resource with neither `Text` nor `Data` is a link,
and Connect never fetches it. A resource with a byte body follows the carriage its media type selects,
and `BodyKind` reports that kind.
A text document is UTF-8 text with a `text/*` or `application/json` media type.

`LlmRichContentSupported` declares how a route carries each kind of content. `Native` sends the
provider's own content item, `Text` sends a text part that renders it, and `Unsupported` omits it.
`LlmProviderCapabilities.RichContentSupported` describes the chat lane, and
`LlmProviderCapabilities.Agent.RichContentSupported` describes agent-session tool calls. An override
with `SupportsToolResultImages` or `SupportsToolResultDocuments` set to `Unsupported` lowers those carriages.
An override never raises a carriage.

| Route and lane | Images | PDF | Text docs | Audio | Links | Embedded text | Structured | Error flag |
|---|---|---|---|---|---|---|---|---|
| Direct Codex (chat and agent), OpenAI Responses | Native | Native | Native | Unsupported | Text | Text | Text | Text |
| Anthropic Messages | Native | Native | Native | Unsupported | Text | Text | Text | Native |
| Bedrock Converse route baseline | Native | Native | Native | Unsupported | Text | Text | Native | Native |
| Bedrock Converse Nova Micro and gpt-oss 20B/120B | Unsupported | Unsupported | Text | Unsupported | Text | Text | Native | Native |
| Claude CLI agent | Native | Unsupported | Text | Unsupported | Native | Native | Text | Native |
| Claude CLI streaming chat | Native | Native | Native | Unsupported | Text | Text | Text | Text |
| AgentCore Harness | Unsupported | Unsupported | Text | Unsupported | Text | Text | Text | Native |
| OpenAI-compatible, Claude CLI nonstreaming chat, Antigravity CLI | Unsupported | Unsupported | Text | Unsupported | Text | Text | Text | Text |

Every route omits other binary content. `PreservesOrder` reports each route's ordering. Claude CLI
streaming chat appends native tool files after the flattened text history, so it reports false.
Direct Codex and the Claude CLI
agent rows are measured. Bedrock managed tool-result text documents are live-qualified on Nova Lite
with two native document callbacks, and on Nova Micro and gpt-oss 20B/120B with text conversion.
Each qualified turn completes after reading the returned content. The remaining Bedrock carriages
and the other rows follow each provider's protocol and are not live-verified.

The exact Bedrock IDs `amazon.nova-micro-v1:0`, `openai.gpt-oss-20b-1:0`,
`openai.gpt-oss-120b-1:0` and `deepseek.r1-v1:0` declare no prompt image or document input. The existing
recognized inference-profile prefixes retain their base model's declaration. Capability overrides cannot
enable those unsupported inputs.
For tool-capable models, chat and managed-agent tool results carry UTF-8 documents through the shared
labelled-text projection; PDF and image tool parts remain unsupported. The native request builder reads the same model declaration
as capabilities. Nova Lite and other model declarations remain unchanged. The console tester offers a
document-only tool when text-document carriage is available without native image carriage.
The published model cards identify their text-only inputs:
[Nova Micro](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-micro.html),
[gpt-oss 20B](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-oss-20b.html),
[gpt-oss 120B](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-oss-120b.html) and
[DeepSeek-R1](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-deepseek-deepseek-r1.html).
DeepSeek-R1's [Converse contract](https://docs.aws.amazon.com/bedrock/latest/userguide/model-parameters-deepseek.html)
accepts text content. Its prompt-media refusal is verified offline. The exact `deepseek.r1-v1:0`
identity and its recognized inference profiles report native tool use as unavailable, as documented
by [AWS](https://docs.aws.amazon.com/bedrock/latest/userguide/working-with-models-not-yet-optimized.html).
Tool definitions and structured tool history refuse before SDK acquisition. Capability overrides
cannot enable this native lane. Managed sessions remain available without host tools. Connect adds
no prompt-based tool emulation.

The text renderings are fixed. A resource link becomes `[Resource link: name] uri`. An embedded text
resource becomes `[Resource: uri]` followed by its text. A text document becomes `[File: name]`
followed by its text, or an embedded text resource where the route carries those natively. Structured
content becomes its JSON as the final text part. A converted error flag becomes a leading
`[Tool error]` text part.

Adapters project each tool output through one internal projection. Each part is delivered, converted to text,
or omitted because the route or the model cannot take it, it is invalid, or it is empty. Invalid content is
structured content that is not a JSON object, a resource with both a text and a byte body, text that is not
UTF-8, or a part over `MaxBinaryBytes`. An omitted part never fails the request, the turn or the tool, and never
changes `IsError`. After each omitted part the model receives the text
`[Content[{i}]: {reason} This content was not supplied to the model.]`, where `{i}` is the part's index:

| Omission | Part | Reason |
| --- | --- | --- |
| Model | image | `The selected model does not support tool-result images.` |
| Model | document | `The selected model does not support tool-result documents.` |
| Model | other | `The selected model does not support this content.` |
| Route | image | `The transport cannot carry tool-result images.` |
| Route | document | `The transport cannot carry this tool-result document.` |
| Route | other binary | `The transport cannot carry this binary kind.` |
| Route | resource | `The transport cannot carry this resource.` |
| Empty | any | `The transport cannot carry an empty attachment.` |
| Invalid | any | `The tool content is invalid.` |

### Continuation state

`LlmChatResult.ContinuationState` is opaque, versioned adapter state. Preserve it unchanged on that
response's `LlmChatMessage.ContinuationState`, alongside its visible text and tool calls, on ordinary
turns as well as tool turns. Keep every assistant record, not just the latest one. Responses adapters
retain ordered reasoning/text/calls; Anthropic retains thinking/signatures/content. The adapter
replays state instead of duplicating the message's visible text/calls. State is bound to transport,
model and account/connection scope; incompatible state fails before sending unless the request opts
in to omission, as described below. The former request-level `ContinuationToken` has been removed,
with no compatibility shim. Bedrock Converse state carries the reply's reported token counts. When the
reply contains reasoning, it also carries the ordered reply with its reasoning signatures, and Connect
replays that reply. A Converse request accepts only state bound to its own connection and model.
Compatible Chat Completions retains `reasoning_content` and supported `reasoning_details` in
assistant state. Tool-enabled requests replay structured details when present, otherwise retained
reasoning content. Requests without tools omit both replay fields. The codec validates the visible
text and tool calls, connection identity, model and a 16 MiB UTF-8 payload bound before dispatch.
Encrypted reasoning remains opaque and never becomes a public reasoning event. The adapter reads
`reasoning_content`, `reasoning`, text details and summary details in that display order, so
overlapping wire fields produce one event. AgentCore and CLI chat do not import this opaque state.
Nonstreaming responses retain complete reasoning blocks in order, including their IDs, indices and
signatures. Only streaming text and summary fragments are joined; merging complete blocks loses
the identities required for tool-enabled replay.

### Carrying history to another model or provider

A conversation that moves to another model or provider starts a new session from the host's retained history.
Each message whose state does not bind to the target transport, model and account is sent without it; its
visible text, tool calls, tool results and attachments are kept. A managed session's first turn emits one
`LlmAgentWarningEvent` with the count. The caller's context is not modified, so the original state stays in host
storage. Inside Inference the internal chat request keeps its own `UnboundContinuationState`; managed sessions
always send `Omit`.

Gemini API and Vertex Gemini accept visible foreign tool-call groups with explicit Omit. Gemini 3
places the documented dummy thought signature on the first imported call; original bound native
signatures remain unchanged. A function call with omitted arguments exposes typed `{}` arguments;
its native continuation keeps the omission. Explicit null, array or scalar arguments are rejected.
Tool results follow their original call order. The shared Gemini serializer carries PNG,
JPEG, WebP and PDF tool results as nested function-response media. The adapter has no known per-binary
size limit for this field. Explicit caller projection limits still apply. Gemini 3 model profiles
support this path. The documented pre-3 Gemini 1.5, 2.0 and 2.5 families omit binary function responses
through the projection report. Unfamiliar model profiles retain Unknown support and attempt the existing
media path; this carriage describes serialization, not verified provider acceptance. Gemini API's
[function-calling guide](https://ai.google.dev/gemini-api/docs/generate-content/function-calling?hl=en)
and Vertex's [function-calling guide](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/multimodal/function-calling)
document the native model scope and MIME types separately.
Vertex's supported Gemini 3 streaming profiles request preview path-based function arguments and
emit the tool name early. They retain complete reconstructed calls for replay; the route does not
advertise JSON-text argument streaming. Gemini API and non-streaming requests do not send that flag.
Array indexes and boolean argument values retain their JSON types through portable results and
native continuation replay.

Claude CLI chat and Antigravity CLI chat report `LlmProviderCapabilities.ToolHistory = Text`. Tool
calls and results already in the history reach their prompt as `[TOOL CALL id=… name=…]` and
`[TOOL RESULT id=… status=ok|error]` blocks. Offering tools to these routes still fails.

`LlmAgentCapabilities.ContextImport` reports how a session start takes `LlmAgentSessionOptions.Context`:

- Managed Codex and managed Bedrock Converse import it as host-owned context (`Structured`). Unbound state is
  removed and the first turn emits one `LlmAgentWarningEvent` with the count.
- Claude CLI and AgentCore sessions report `OpeningContext`. A start begins a new provider
  conversation and sends the history once, as a `CONVERSATION SO FAR` transcript ahead of the first
  turn's input. The first turn emits one warning that names what was not carried; AgentCore carries
  no history attachments.

A managed session's `GetContextStatusAsync` measures the request a turn sends.

### Host-owned managed agent runtime

[DSA-905](https://darkspyre.atlassian.net/browse/DSA-905) recovers interrupted Responses model steps.
A streamed HTTP body read that throws `IOException` (including `HttpIOException`), or valid SSE
that ends without a terminal response, carries `LlmProviderException.IsResponseStreamInterrupted`. Provider rejection, malformed
content, resource-cleanup failures and native CLI failures do not receive this classification.
The managed runtime retries only the current uncommitted model request, with three total attempts
and cancellable waits of one and two seconds. Completed application tools and their results remain
in history and never run again through recovery. Requests enabling provider-hosted tools do not retry, except a
request whose only hosted tool is image generation: it retries unless the failed attempt started a hosted call.
A failure after a terminal result does not retry. Interrupted usage leaves the exact run total unknown.
`LlmAgentResponseRecoveryEvent` names the failed attempt and the next attempt, or null when recovery
ends. Consumers discard unfinished text and reasoning without changing completed messages. The first
replacement event clears transient recovery progress through an empty `LlmAgentActivityEvent`.
After three interruptions, the turn reports that recovery failed and completed tool results are retained.

**Accounting:** `ILlmAgentSession.MeasureContextAsync(proposedContext, ct)` returns `LlmRequestAccounting`.
Session accounting uses base settings
while idle and the frozen executing model/instructions/tools while paused, including queued steering
without consuming it. Active execution is rejected. It never replaces context or consumes a pause.

The result distinguishes `IsSupported = false` from supported-but-unknown measurement, exposes raw
nullable model limits separately from the effective host ceiling, reserve and usable
input budget, and preserves `LlmContextMeasurementSource`. Neither `IsSupported`, an estimate, nor
`NeedsCompaction = false` proves fit. Native-owned context accounting is unsupported. See the
[accounting design](docs/enhancements.md#public-nonmutating-accounting) for failure and concurrency semantics.

`OutputLimitEnforcement` is exposed by both `LlmProviderCapabilities` and Common's `LlmCapabilities`.
OpenAI Responses, Anthropic Messages and Bedrock Converse advertise `ProviderEnforced`.
Direct Codex, Claude CLI, generic compatible endpoints and the custom AgentCore harness report
`Unknown`: forwarding a field is not evidence of enforcement. Antigravity reports `Unsupported`. A reserve
does not enforce `MaxOutputTokens`. Grok Responses also reports `Unknown` for total-output enforcement.
Its native `max_output_tokens` limits visible output only, excluding reasoning and function tokens,
as the [xAI Responses reference](https://docs.x.ai/developers/rest-api-reference/inference/responses.md)
documents. Connect forwards the requested cap unchanged and retains complete provider usage.
Accounting does not treat that visible-only cap as a qualified total-output reserve.
Bounded summarization must reject an unsupported or unverifiable
route, not switch models or silently omit its cap. No new inference retries or compaction orchestration
are introduced by these APIs.

Agent sessions report adapter-owned progress as `LlmAgentActivityEvent`; it is not assistant transcript
text. `LlmAgentToolEvent.Output` carries the tool result the runtime reported, or null when none was
reported. It is an observation: a tool the runtime owns never reaches a host handler through it. The
Claude CLI, AgentCore and managed runtimes set it on the completed or failed event of a host tool. The
Antigravity CLI sets it on native tool observations when AGY reports output or an error.

`LlmProviderCapabilities.Agent` declares context and tool-execution ownership, capture/replacement,
context status, host compaction handoff, native resume/fork/compaction, steering and interruption.
Check these before selecting a pathway. `SupportsAgentTurnInstructions` separately gates a complete
per-run instruction override: null uses base instructions, empty clears them for the run, and
nonempty replaces them. Native adapters currently reject any non-null override before starting.

The existing `ILlmAgentSession` now includes async capability/status queries, context capture,
idle compare-and-swap replacement, and streamed same-run continuation. `LlmAgentContext` is a
versioned list of stable `LlmAgentContextEntry` records, not a durable run scheduler. Darkspyre owns
its existing session store, restore/fork decisions and compactor; Darkspyre.Inference.Connect.LLM adds no storage seam.

`LlmAgentContextStatus.NeedsCompaction` and token counts are nullable: unknown is not zero/false.
`MaxContextTokens` is effective context capacity; model discovery also exposes nullable context
and output limits. Context occupancy includes cached input and differs from accumulated usage or
account quota. `LlmAgentCompactionRequiredEvent` and `ContinueTurnAsync` are the host-compaction contract for a
conversation that reports `CompactionResponsibility = Host`; no phase-1 conversation does. Managed clients cover direct
Codex, API-key OpenAI Responses, Anthropic Messages, OpenAI-compatible Chat, Gemini API, Apple Foundation Models
and Grok OAuth Responses through `AddLlmConnect`. Separate registration
extensions add Bedrock Converse and Vertex Gemini/Claude. SelfHost opens a managed session only after resolving the selected host
engine/model and checking its requested operation capabilities. Each managed client checks the
selected model/connection before opening. Each managed lane uses its actual request estimator,
continuation scope and facade stream as its chat route; it does not enable Azure agent sessions.
`LlmProviderException.IsContextOverflow` identifies recognized provider input-context failures.
Adapters classify their own HTTP, stream or typed SDK errors and keep `ProviderCode` unchanged.
Grok's provider-owned automatic mode ends
`ProviderFailure` without a host pause. Responses/Codex, compatible chat, Anthropic Messages,
Gemini/Vertex, Bedrock Converse and Apple recognize their supported overflow error patterns.
Bedrock Converse SDK failures surface the actual error kind and credential-redacted message,
with observed status and request identity. Unrelated host failures retain the managed generic
diagnostic; cancellation and independently failed owned cleanup remain distinct.
Model-stream errors include the sanitized original model message and its reported status in the
explanation. The outer HTTP status remains separate.
Pre-classified Bedrock input-overflow failures use the same credential and selected-header redactor
for public diagnostics. They retain the actual AWS error kind and explanation instead of replacing
the explanation with a generic context-window message. Their observed status and overflow flag remain intact.
xAI input overflow raises this failure instead of returning an incomplete completion; output-token
truncation remains an incomplete completion.

An unclassified managed-run exception emits a `LlmAgentWarningEvent` with its runtime type and
managed stack frames, then the failure completion. The warning omits exception messages,
source-file paths and provider payloads. Darkspyre records this warning in the application log,
not in the conversation. The failure completion's diagnostic is the exception's type name and its
message with the connection's credentials removed by `SecretRedactor.Sanitize`.

Grok uses the shared managed session's host compaction and local estimation. The subscription proxy offers no
`responses/compact` route. A too-long refusal teaches the limit, compacts and resends once through the common path.
Search billing does not teach context correction; the context-limit section describes the counted request.
Compaction failures redact selected and effective outgoing header values captured after the send,
including borrowed HttpClient defaults, from provider codes and request IDs. Raw response bodies stay withheld.

`CanCompactHistory` is true when unprotected history exists or a protected function result contains
a text part longer than 2,000 characters or a binary part. `ProtectedFits` is the nullable inverse of
the protected context's measured `NeedsCompaction`. Null means unknown, and compaction availability
does not prove that a particular replacement fits.

Capture defaults to omitting attachment bytes and recording `OmittedAttachments` metadata. For a
tool-output part, the entry's `Index` is the part's position in `Parts` and `ToolCallId` names the
result; the capture drops the binary part or the resource's byte body. Live
compaction must capture with `IncludeAttachmentData = true`; the host may strip bytes only from its
separate persistence projection. Omitted metadata cannot recreate an attachment or become model input.
See the [compiled host-context example](examples/Darkspyre.Inference.Connect.LLM.Examples/HostOwnedAgentContext.cs).

Text-only occupancy is estimated from the actual projected request using the bundled o200k
tokenizer, including instructions and schemas. It is a reasonably close estimate, not an exact current-model token
count, and the accounting adds the 300-token estimate margin to it.
Anthropic and compatible Chat use the same validated plaintext generation mapper without provider I/O.
Actual media, selected resources and their opaque continuation make occupancy unknown.
Responses, direct Codex and Bedrock Converse record the provider's reported input
and output tokens on the continuation state (`LlmContinuationState.InputTokens` and `OutputTokens`). The
Responses and Codex estimates do not use them: a reported count describes the earlier request it answered,
not the one being measured. They tokenize the actual projected request and, on Codex, add the pinned 0.159.3
CLI's coarse encrypted-content length approximation, and report `Estimated`. Omission descriptors are not
replacement attachment input and do not discard the estimate. Compaction-request usage never becomes replacement occupancy. Other providers do not inherit
this heuristic. Codex and Claude CLI estimates count inline attachments and binary tool output with
`AttachmentTokens` (an image 1,600 tokens, a PDF page 4,600, text by content, other bytes/3) and caller-owned
resources by their descriptors (an image 1,600, text and JSON bytes/4, a PDF one page per 100 KB, other bytes/3),
which the caller resolves through `IInferenceResourceAccess.GetAsync` before each measure. Accounting never opens
resource content. The Claude CLI estimate is the local count, uncorrected. Hosts that keep continuation state keep the counts with it. Each
Bedrock model has its own tokenizer, so a Bedrock estimate of the unreported tail is approximate and its
diagnostic says so. Provider context-overflow errors trigger a handoff only for a conversation that reports
`CompactionResponsibility = Host`. Catalog limits
are optional. Limits follow `CompactionLimits.For`: the reserve is the policy's, else the model's output
limit, else 8,000; without a known window or occupancy, `NeedsCompaction` stays null.

Tools execute sequentially by default. Parallel mode is opt-in and bounded; a tool marked
`RequiresSequentialExecution` serializes its whole batch. Schemas are validated before callbacks.
Interrupted or failed handlers produce error results, and all issued calls settle before capture.
Typed resource-operation failures remain operation failures. Parallel settlement preserves every
independent cause, and cleanup failures remain visible to the turn reader and repeated session
disposal callers. Retirement drains ignoring callbacks without waiting for a paused event reader.
A cancelled handler's external effect may be unknown: Darkspyre.Inference.Connect.LLM never automatically replays it.
Model retries are limited to three total attempts before any output, respect retry delays up to
30 seconds, and leave aggregate usage unknown if an attempt's usage was unreported. Streams with
exposed output are not retried. Consume/dispose an enumeration fully before capture or another run.

Device-code sign-in goes through one seam. A provider library registers an `ILlmDeviceLogin` as its
transport's `ILlmAuthenticator`; Codex and Grok implement it. `StartDeviceLoginAsync(transport)`
returns the code to show, and `CompleteDeviceLoginAsync(transport, connectionId, deviceAuthId, userCode,
expectedVersion)` polls once and saves the credential on the connection. A poll that is still pending
throws `LlmAuthPendingException` and saves nothing; its `SlowDown` flag adds five seconds to the polling
interval. Setup's `authorize` action and its continuation, and `SignInAsync`, use the connection's
transport to find the device login. A transport with no registered device login is refused with
`NotSupportedException`.

`LlmDeviceCode.VerificationUrl` is the provider's RFC 8628 `verification_uri_complete` when the device-code
response carries one as an absolute https URL; that page opens with the user code already filled in. Otherwise it is
the plain verification page, where the user types the code. Codex and Grok apply this one rule. ChatGPT's (Codex)
device endpoint returns no complete URI today, so a Codex sign-in shows `https://auth.openai.com/codex/device`.

Interactive completion methods accept the connection version captured before authentication began.
The `SignInAsync` convenience method captures that version internally. In both cases, the final save is
still conditional, so a connection change during the external authentication round trip is rejected.

## Prompt caching

`LlmChatRequest.PromptCache` and Common `LlmInferenceRequest.PromptCache` carry the same advisory
`LlmPromptCacheHint`. `Prefix` asks to cache tools and instructions. `Conversation` also includes
the conversation prefix through its last message. `Default` selects the provider's standard retention;
`Extended` asks for its longer retention, which can increase cache-write charges.

`LlmProviderCapabilities.PromptCache` and Common `LlmCapabilities.PromptCache` declare automatic
caching, hint acceptance and supported retentions. Capability overrides do not replace this declaration.
Unsupported routes ignore the hint. Unsupported retention falls back to `Default`. SelfHost preserves
the loaded engine's declaration and forwards the hint without turning it into a required control.

Managed agent requests automatically include a `Conversation` hint with `Default` retention,
including tool continuations and accounting projections. A hint does not promise a cache hit, token
savings or a billed cache resource. Reported cache reads and writes remain in `CachedInputTokens`
and `CacheWriteInputTokens`.

Anthropic API and Vertex Claude share cache markers in generation and exact-count requests. Prefix
marks the last nonempty system block, or the last tool when no system text exists. Conversation also
sets the top-level automatic marker. Extended requests one hour; Default leaves the provider TTL
unchanged. Signed and opaque continuation blocks are not modified. A supplied hint conflicts with
raw `cache_control` options.

Bedrock Converse and ConverseStream share one exact model-ID cache fact lookup after the existing
geographic-prefix removal. Only documented Claude and Nova IDs receive markers or cache support.
Claude supports system, tool and message points; Nova supports system and message points only.
Nova's conversation checkpoint follows the latest message without tool-use or tool-result blocks.
Tool exchanges remain unchanged. If every message contains tool blocks, Nova receives no message
checkpoint; an existing system checkpoint remains available.
Default omits `ttl` and uses the provider's five-minute behavior. Supplying `ttl`, even `5m`,
activates extended-TTL handling and causes Nova requests to fail. Extended supplies `1h` only for
documented supporting Claude IDs and otherwise retains the default behavior. Supported Claude and
Nova IDs report implicit caching too.
Unknown IDs keep their prompt unchanged. No local token threshold or cache hit is inferred.

OpenAI Responses uses `prompt_cache_options.ttl = "30m"` for Extended on its supported GPT-5.6
and GPT-6 profile rows. Default and Scope add no field. Other OpenAI profiles, Codex, Claude CLI,
Gemini API and Vertex Gemini report automatic-only caching. Responses cache options never flow
into another transport through inheritance.

The recognized OpenRouter Chat Completions endpoint projects documented content markers for Claude,
supported OpenAI profiles, Gemini 2.5/3 and the listed Alibaba explicit-cache models. Claude
Conversation uses the top-level automatic marker; other mapped conversations mark their last text
block. Prefix marks a system/developer text block. A tool-only Prefix leaves the hint unused without
changing the prompt or tools. Claude Extended requests one hour; supported OpenAI Extended requests
30 minutes; other mapped retentions fall back to Default. Known automatic-only routes add no marker.
Raw options cannot override the supported neutral cache control. These declarations describe request
projection, not observed cache hits, access or provider qualification.

## Provider setup and editing

`LlmProviderCatalog.SetupDefinitions` describes providers without resolving connections or
credentials. `LlmProviderService.CreateSettingsManager` returns the manager that opens settings and
runs their actions against actual provider operations. `LlmProviderDefinition.SetupSteps` uses Common's
descriptive `SetupStep`. Render fields and run actions from the settings object, not by interpreting a
descriptive step or switching on a provider ID.

```csharp
LlmSettingsManager manager = llms.CreateSettingsManager(hostCommitTarget);
LlmSettings settings = await manager.OpenAsync(
    LlmTransport.OpenAiResponses,
    new SetupConfigurationIdentity(SetupChangeIntent.Create, connectionId, null),
    cancellationToken: cancellationToken);

// Render semantic field types and call their typed edit methods; edits change only `settings`.
// Inspect each action's declared side effects and obtain host authorization before
// manager.RunAsync(settings, action.Id, progress, cancellationToken).
if (settings.CanSave)
{
    SetupCommitResult saved = await manager.SaveAsync(settings, cancellationToken);
    // Handle Committed, Conflict, Invalid, or Uncertain; never blindly retry Uncertain.
}
```

For editing, supply the exact saved `LlmConnection`, its model selection and an Edit identity whose
expected version matches that connection. The host's `ISetupCommitTarget<LlmSetupChanges>` must
atomically compare that version over configuration and credentials. Authentication uses a draft-local
`ILlmConnectionSource`; neither device completion nor the Claude account sign-in replaces saved
credentials before commit. Normal runtime credential refresh still uses the existing host seam unchanged.
A Claude connection that is not signed in uses the CLI's own login; signing in to a Claude account
marks the draft authenticated.
Every LLM provider's settings have two steps. The connection step names its sign-in action
(`authorize` for ChatGPT, `sign-in-claude` for Claude) and `check` in `SetupStep.ActionIds`; the
model step names `test`. A CLI or Apple helper executable field sits in the connection step's
advanced section, "More options". `LlmProviderDefinition.Icon` and `Group` carry the provider's logo
and add-connection group, and `SetupDefinition` copies both. Claude embeds its own logo; ChatGPT and
the OpenAI API use the shared OpenAI mark in Common (`SetupIcons.OpenAiResource`).
`LlmProviderDefinition.HomeFolder` names the provider's own folder in the user's home folder: `.claude`
for Claude, `.codex` for ChatGPT and `.gemini` for Antigravity. A host lets sandboxed commands read it.
Grok, the OpenAI API and self-hosted providers name none.

The `check` action on a Claude or Antigravity connection asks the CLI for its sign-in. It reports the
CLI as installed or not in `Status.Installed`, and signed in or not in `Status.Authenticated`. A
missing CLI, or a signed-out Antigravity CLI, shows the provider's prerequisite instructions as the
interaction. A signed-out Claude CLI completes the check, so its sign-in action is offered. An edit
marks `Status.Installed` stale. Model choices carry `SetupChoice.Tags`: the context size in
thousands of tokens, "Images" and "Thinking", from the model's listed capabilities. The `test`
action's result carries the reply as `Message` and the request time as `Elapsed`.

Claude and Antigravity executable fields accept blank values. Checks, discovery and Save resolve a
blank executable to the provider's default through the same draft connection projection. The editor
keeps the entered value unchanged. Executable inputs containing carriage returns, newlines or null
characters remain invalid.

Settings objects never expose saved secrets. API-key fields support Keep/Replace/Clear; provider options,
capability overrides and additional headers already on a connection are retained when editing.
Auth interactions, dynamic choices, validation and explicit model tests use one common contract.
`AddLlmConnectBedrock` registers AWS profile/region choices in the SDK-owning assembly.
OpenAI-compatible remains an advanced provider with explicit manual-model support and typed
default/enabled/disabled capability overrides. Disabling the `Streaming` override selects the endpoint's
non-streaming request, as [Streaming, reasoning text and usage](#streaming-reasoning-text-and-usage) describes.
The Chat Completions and Responses adapters each share one protocol implementation. Adapter hooks
select request routing, headers and model-specific sampling validation without replacing the wire model.
The internal complete Chat decoder accepts an absent original request. It maps the actual reported
model, usage and tool calls without inventing request-specific structured-output validation. The xAI
projection retains citation strings and generated file IDs/names for resource materialization, and
its shared body builder accepts Files-prepared native references. These internal mappings do not
enable batch or deferred operations by themselves.
Prompt-image overrides send `image_url` data-URL parts. Prompt-document overrides send PDFs as
`file` parts and valid UTF-8 `text/*` documents as text parts. Other document formats fail before
dispatch. Tool results remain text-only; prompt overrides do not enable binary tool results.

For OpenAI-compatible connections, `LlmConnection.ChatCompletions` stores two non-secret protocol choices. A null `OutputLimitField`
selects `max_completion_tokens` for `api.openai.com` and `max_tokens` elsewhere; an explicit
`MaxTokens` or `MaxCompletionTokens` wins. A null `UsePromptCacheKey` maps a nonempty
`ConversationKey` to `prompt_cache_key` only for `api.openai.com`. Other endpoints require explicit
true; explicit false disables the mapping on any endpoint. The advanced settings expose both
choices and require retesting after edits. Provider options cannot replace a standard field written
by the typed request. Conversation affinity is a hint, not an explicit billed cache resource.
Existing provider JSON, headers and model-specific overrides are retained; the editor does not turn them into
an arbitrary JSON settings payload.

`XaiResponses` defines the Grok OAuth connection and settings contract. Setup fixes the subscription proxy endpoint,
offers the `authorize` sign-in action and has no region, endpoint or API-key field. Save needs a completed sign-in
or a kept saved credential. New drafts do not create `LlmConnection.Xai`; the Grok client ignores any saved Xai options.
`XaiConnectionOptions` remains for the out-of-build STT, Video and Resources adapters.

`GrokAuthenticator` implements `ILlmDeviceLogin`, which supplies the device grant to `StartDeviceLoginAsync`,
`CompleteDeviceLoginAsync` and `SignInAsync`. Completion performs one token poll; interactive sign-in owns the polling loop.
Setup continuation and interactive sign-in respect the device interval and the five-second increase on
RFC 8628 `slow_down`; an expired code ends with the provider's `expired_token`. `LlmDeviceCode.ExpiresAt` is null for a provider
that supplies no expiry. Device-code diagnostic text is redacted. Token saves use the captured
connection version. Facade retirement cancels and drains sign-in and suppresses late results;
it does not dispose the borrowed authenticator or undo a save already committed by the source.
Entered Grok sign-in operations check the synchronous owner fence after awaited work and before
saving. Retirement rejects late results even while linked cancellation callbacks are pending.
Grok authentication status and sign-in saves use the shared OAuth-only credential validator.
Status returns false for incompatible credentials or malformed bearer tokens; sign-in refuses them
before saving. Both paths retain their rejection of a nonempty API-key field, including whitespace.

The internal `GrokAuthenticator` implements the [RFC 8628](https://www.rfc-editor.org/rfc/rfc8628)
device grant against `auth.x.ai` with form-encoded requests and a public client. A successful token
exchange calls the proxy's `GET /v1/user` once and requires a nonempty `user_id` or `userId`. Credentials contain
only OAuth tokens. Expiry comes from `expires_in` or the access-token JWT; account identity comes from
`/v1/user`, and the optional ID-token email is display metadata, not a locally verified identity claim.
The shared OAuth pipeline refreshes Grok within 300 seconds of expiry and other OAuth providers within 60 seconds. It preserves account metadata,
accepts rotated tokens and saves through `LlmConnectionAccess`'s version-conditional source seam.

ConsoleTester qualification uses its separate credential store and the Global/Default route.
Device sign-in completes with the account check, and model discovery returns eight native choices.
One short prompt on the selected `grok-4.20-0309-non-reasoning` model returns `GROK-121-OK`,
`Completed` with finish reason `completed`, and usage of 194 input and 7 output tokens.
These checks use the public facade without retries or app credential access. They do not qualify
token refresh, other models, strict tools, structured output or media.
The same model completes a Global/Default nonstreaming request with `GROK-121-NONSTREAM-OK`,
finish reason `completed`, and usage of 197 input and 10 output tokens. A UnitedStates/Default
streaming request for that model returns the provider's HTTP 404 model/access error before output.
The tester preserves that failure and does not retry or switch routes. UnitedStates discovery
returns two native choices, `grok-4.6` and `grok-4.7`. One UnitedStates/Default streaming request
on the listed `grok-4.6` model completes with `GROK-121-US-OK`, finish reason `completed`, and
reported usage of 648 input and 358 output tokens. This qualifies that selected model and route,
not every US model; a Global catalog entry does not establish access on the US route.
The public tester rechecks UnitedStates/Default model discovery against source `b64c30f` with its
existing OAuth credential. Authentication succeeds and the list again contains `grok-4.6` and
`grok-4.7`, without a region/endpoint mismatch or generation prompt. This checks library catalog
execution, not a consuming application's saved settings or sign-in display.
One genuine UnitedStates/Default nonstreaming request at source `ced2dfd` selects `grok-4.6`,
Low reasoning and 128 requested output tokens. It returns `GROK-121-US-NONSTREAM-OK`,
`Completed` with finish reason `completed`, and reported usage of 654 input and 137 output
tokens, including 126 reasoning output tokens and 512 cached input tokens. The reported output
includes reasoning that the native visible-output cap excludes. This count is not evidence of a
visible-cap violation and does not qualify total-output enforcement. It uses the tester's saved
OAuth without retries, uploads, hosted tools or another route.
A separate Global/Default turn on `grok-4.20-0309-non-reasoning` with a three-iteration cap completes
the existing echo callback and replays its successful text result. The turn completes after two
model steps with final answer `GROK-121-ECHO-OK`. No retry, other tool, upload or media operation
occurs. This qualifies ordinary client-tool replay, not strict functions or hosted tools.
One small UTF-8 reference note sent through the existing inline document attachment path returns
`GROK-121-DOCUMENT-OK` on the Global/Default `grok-4.20-0309-non-reasoning` model. Both streaming
and nonstreaming requests complete with finish reason `completed` and reported usage of 247 input
and 10 output tokens each. The tester uses its separate saved OAuth credential without a Files
upload, retry or alternate route. This qualifies that inline document input, not Files CRUD,
provider file references, cross-provider resource transfer or hosted-search lifecycle observations.

`XaiErrorClassifier` supplies one sanitized HTTP failure policy. It maps 401 and the specified
token-validation 403 message to `AuthenticationRequired`, and 402 or a blocked account to the named
entitlement failure (`AccessDenied`). Unknown 403 responses remain provider errors. Subscription-specific
403 matching is not implemented or live-qualified. Errors never trigger a retry or refresh.
The shared HTTP classifier preserves the response status, observed request ID and delta
`Retry-After` across its resource and authentication routes. Ordinary provider error codes come from
the redacted error envelope, including when a route withholds the diagnostic body. Body-withholding
routes retain only the named JSON error explanation; unrelated fields and unstructured bodies are
not promoted to diagnostics. An absent named explanation keeps the route's fixed rejection label.
Context-compaction rejection uses this body-withholding policy. Its wrapper retains the redacted
named explanation and actual status, code, request ID, retry delay and overflow flag, without exporting
the response body or original exception graph. Malformed successful compaction responses retain
their fixed protocol failure message.
Compaction send failures capture effective headers even when no response returns. Recognized HTTP and
typed provider failures retain the shared send boundary's redacted explanation and observed facts through
the body-withholding compaction wrapper. A missing response does not create HTTP observations or a retry.
Batch and deferred resource failures retain that safe code and explanation. Request IDs use the
same credential redactor as diagnostic bodies. A reported retry delay is diagnostic data, not permission to retry.
Grok catalog and tokenization send failures share the same `XaiErrorClassifier` projection.
They use the selected credential and actual outgoing headers for redaction,
including borrowed-client defaults. Typed failures retain their redacted explanation, provider code,
request ID, status, retry delay and overflow flag. Transport failures expose a redacted explanation;
their original exception remains internal. A failed send does not manufacture a response observation
or trigger another request.
Resource operation wrapping retains the boundary-owned protocol explanation and sanitized HTTP
failure message. It withholds provider response bodies. An unowned provider message without the
selected connection's secret context remains generic.
Batch receipt, request-metadata and pagination strings reject credential or effective-header echoes.
Deferred submission IDs use the same captured request context, including borrowed-client defaults.
Protected native values fail protocol admission without rewriting an identity or publishing a receipt.
Failure observations retain the actual dispatch and response facts.
Rejected Responses generation supplies its live outgoing headers, including borrowed-client defaults,
alongside resolved hosted-authorization secrets to that same classifier.
Files metadata and download failures also redact effective outgoing header values, including headers
added by cross-family request configuration. The existing send owner supplies its actual request to
the shared error boundary. Borrowed streams keep their existing ownership.
Focused coverage checks reflected header values in provider codes and request IDs on ordinary
downloads, configured downloads and configured metadata reads while retaining status and retry facts.
An explicit error-envelope `code` takes precedence over a string `error` message. OAuth envelopes
without a separate code retain their string error code. Flat model/access failures preserve
`not-found` as the provider code and retain the redacted explanatory message separately.
Named authentication and entitlement failures clear the setup device attempt without saving a credential.
Device-grant `access_denied` and `expired_token` failures retain their actual status and code,
the redacted request ID and any observed delta retry delay. These failures end the sign-in attempt;
the retry delay does not restart or extend it.

The shared Responses pipeline retains xAI output as an ordered deep clone, including hosted-tool
items and encrypted metadata. Continuation is bound to the transport, endpoint, account and model;
token rotation on the same account does not break that binding. Grok's replies go through the shared
Responses result mapping: terminal statuses, streamed text and calls, and items reported only on
`response.output_item.done` are read as for every Responses client. A `context_length_exceeded` code in an
HTTP error body or a stream `error` event raises `LlmProviderException.IsContextOverflow`; a terminal reply
that is only `incomplete` with that reason is `Incomplete`, not a too-long refusal.
The named incomplete limits remain `Incomplete` when no recognized overflow code is present.
Two small live requests against source `0dacacd` select Global/Default
`grok-4.20-0309-non-reasoning`, request 32 output tokens and ask for the integers from 1 to 100.
Streaming and nonstreaming both stop after 16, report exactly 32 output tokens and return
`Incomplete` with finish reason `incomplete`; each reports 207 input tokens. No tool executes.
The tester's exit code 1 reflects that incomplete result, not a transport failure. These runs qualify
the selected plain-text cap and incomplete result in both modes. They do not establish the semantics
of hosted-tool aggregate usage or resolve the separate hosted-search output-token discrepancy.
Encrypted context stays opaque; diagnostics redact its named values and
reflections, and malformed JSON does not retain a parser exception containing provider text.
The owning terminal-evidence tests exercise OAuth Grok streaming and nonstreaming through an HTTP
stand-in. Unknown results retain actual text, model, usage and tool calls without continuation. An
empty terminal output discards provisional streamed calls, text and citations from the result and has no
continuation. These checks do not establish live OAuth entitlement or provider qualification.
Terminal annotations replace the citation snapshot without repeating citation events already
observed at the same native item and annotation position.

The internal `GrokResponsesLlmClient` sends OAuth-only streaming or genuine non-streaming requests
to the typed region's fixed endpoint. It rejects mixed static secret headers before dispatch and retains
ordinary nonsecret additional headers. It sends the typed service tier and records the recognized applied
tier in diagnostics. Typed protocol controls cannot be injected through raw provider options, even when
the typed property is absent. The combined wire tools are limited to 350, including deferred MCP
tool search; `web_search` is a reserved hosted name.
The client joins the generic and language listings, offers only listed language models, and reads the
facade's published catalog. Catalog reasoning levels and image modalities override family fallbacks.
Unknown manually entered models still permit a request without fabricated discovery evidence.
Live ConsoleTester discovery at source `987283a` selects UnitedStates/Default and uses its existing
saved Grok sign-in. Authentication check succeeds, and the model list returns `grok-4.6` and
`grok-4.7` with exit code 0. This qualifies selected-route discovery after the diagnostic repair,
not refresh/expiry, other regions or generation on every listed model.
Two small OAuth requests against production source `e3cb082` select the discovered `grok-4.6`
model on UnitedStates/Default, explicit `Low` reasoning and 128 requested output tokens. Both
calculate `17 * 19`, return only `323`, and finish as `Completed` with reason `completed`.
Streaming reports 650 input, 93 output and 92 reasoning output tokens; nonstreaming reports
650 input, 86 output and 85 reasoning output tokens. The tester uses its own saved credential
without retries, tools, app credential access or route fallback. These runs qualify selected-route
acceptance of the Low-effort request in both modes, not every effort level, comparative reasoning
quality, an echoed applied-effort observation or general output-token-cap enforcement.
Image prompts require catalog image support and cannot exceed 20 MiB. Managed agent image tool-result
capabilities use the same selected-model catalog support as chat request projection. Tool-result
restrictions lower image support in both capability views without disabling supported image prompts.
Temperature and top-p follow
the model profile; top-k, seed, penalties and string stops are unsupported on this Responses route.
The discovered `grok-4.20-0309-non-reasoning` ID shares the existing non-reasoning sampling profile
with its `grok-4.20-non-reasoning` alias. Catalog resolution retains temperature/top-p support
without adding selectable reasoning levels or rewriting the requested model ID.
The schema preflight follows schema positions and local references, not literal data. Bounds
above 2048 string characters, 256 array items or 64 properties require explicitly authorized local
validation against the original schema. The same private admission owner distinguishes guaranteed,
outside-guarantee, provider-rejected and invalid basic keyword values. Empty enum/anyOf variants,
boolean property schemas, min/maxContains and Draft-07 tuple items have no native admission.
Explicit boolean `additionalProperties` remains supported. A reference to that boolean from another
position does not inherit its position-specific guarantee. Invalid basic type, enum, required,
numeric and schema-container shapes fail before effects, including with local fallback selected.
Count-keyword admission compares the original number exactly with its integral decimal candidate.
Fractional counts cannot round into admitted integers. Counts outside decimal representation remain
outside native guarantees; admission invents no rejection cutoff for those values.
The [documented formats](https://docs.x.ai/developers/model-capabilities/text/structured-outputs)
`date`, `time`, `date-time`, `email`, `uuid`, `ipv4`, `ipv6` and `uri` retain native admission.
Other formats, unlisted assertions and declared vocabularies remain outside
native guarantees, not invented provider rejections. Known standard annotations retain their literal
values. Invalid keyword shapes refuse before dispatch. The default case does not grant unknown
keywords a native guarantee. Original-schema format evaluation keeps the selected supplier dialect
and vocabulary policy; native admission does not turn invalid output into valid output.
Explicit Draft-07 admission does not interpret `$defs`, newer applicators, anchor declarations, vocabulary
declarations or `deprecated` payloads as that draft's keyword shapes. Explicit Draft 2020-12 does
not interpret retired `additionalItems` or `dependencies` payloads as active keywords. These names
remain outside native guarantees, and their literal payloads are not traversed as schemas.
Active vocabulary names must parse as absolute URIs, and active `prefixItems` arrays must be nonempty,
as specified by [Draft 2020-12 Core](https://json-schema.org/draft/2020-12/json-schema-core).
Invalid shapes use the existing invalid-keyword error before dispatch, including with local validation
selected. This check does not normalize vocabulary names or interpret their declarations.
Active `contentSchema` values enter the same schema-position worklist for shape validation, but they
do not become assertions on the outer instance. Their valid payloads remain outside native guarantees.
Draft-07 retains `contentSchema` as inactive literal data.
Draft-07 `dependencies` validates property-dependency arrays and indexes schema dependencies through
the same position-aware worklist. Dependencies remain outside native guarantees. Unknown declared
dialects remain unproven without applying a known dialect's keyword-container shapes. This boundary
does not select a new dialect for original-schema evaluation or for undeclared default-profile schemas.
The same admission owner scans known regex support differences without compiling or matching a
pattern. It distinguishes escaped operators and character-class contents from active syntax.
Documented backreferences, Unicode property escapes, word boundaries and lookaround/modifier groups
have no native admission. Unescaped dot outside a class remains outside guarantees because
[xAI dot-all matching](https://docs.x.ai/developers/model-capabilities/text/structured-outputs#regex-support-pattern)
can accept a newline rejected by the original schema. Literal escaped dots and class dots keep their
admission. Other alphanumeric escapes and unlisted group introducers remain unproven, not invented
provider rejections. Unmatched active group delimiters fail before dispatch, including with explicit
local fallback. Escaped parentheses and character-class literals do not alter group balance.
Unterminated escapes and classes also remain unproven. No provider regex-length
or repetition cutoff is added. This support scan is
not complete regex syntax validation or proof of full Unicode/dialect matching equivalence.
Response schemas outside native admission retain explicit original-schema local fallback; non-strict
functions stay unchanged. The deferred/batch Chat codec uses this same strict-function entry and
response enforcement owner; its separate wire builder and decoder remain separate.
Its operation-local index resolves pointers against the
selected schema resource, honors embedded `$id` bases, Draft-07 plain fragment IDs and Draft 2020-12
`$anchor` names, and admits only indexed schema targets. Definition containers are inspected without applying unused constraints
to the emitted instance. Shared targets are not cycles. Unknown dialects, unresolved resources,
literal targets, cycles, dynamic references and undefined nonempty `$id` fragment forms remain outside
guarantees. An absolute reference requires an actual declared absolute identity; the internal
resolution base supplies no caller-visible identity. The index never fetches a resource or
registers a schema globally. Draft-07 references ignore sibling assertions. Their own `$ref` values
must be strings; non-string values fail before dispatch even with local fallback selected.
Draft 2020-12 and undeclared default-profile references retain sibling assertions.
Active `$id` values must parse as string URI references. Active `$anchor` values must be strings
with the dialect's ASCII anchor-name syntax. Those failures use the invalid-keyword error before
dispatch, including local fallback. Draft-07 fragment-only IDs use that dialect's letter-first
plain-name syntax, including colons, as defined by [Draft-07 Core](https://json-schema.org/draft-07/draft-handrews-json-schema-01#rfc.section.8.2.3).
Their aliases share the existing anchor map and leave the
containing resource and base URI unchanged. The index keeps undefined nonempty `$id` fragments
and duplicate identities outside native guarantees.
Those unproven identities do not stop the index's keyword and container checks. Malformed values
detected by those checks still fail before dispatch when local fallback is selected.
Schema declarations must parse as absolute URIs. Active ordinary and dynamic references must parse
as string URI references. Active dynamic anchors use the same name check as ordinary anchors.
These checks follow [JSON Schema Core](https://json-schema.org/draft/2020-12/json-schema-core).
Valid dynamic values remain outside native guarantees. Draft-07 keeps its dynamic-keyword payloads
inactive; the preflight does not resolve dynamic references or select a supplier dialect.
The same admission owner treats `not`, conditionals and
multi-branch `allOf` as outside native guarantees. xAI gives `oneOf` the same meaning as `anyOf`;
native admission requires pairwise-disjoint explicit simple types on non-reference branches,
including nullable type arrays. A `$ref` sibling type is not proof of exclusivity in Draft-07.
`number` overlaps `integer`. Unproven exclusivity requires explicit local validation for responses
and refuses explicitly strict tools before effects. Non-strict tools retain their original schema
and strictness. Explicitly strict function roots require a proven object type. A root `$ref` refuses
before dispatch, even when its target proves an object type. Local references below the root remain
subject to the existing guarantee analysis. The same traversal records object proofs for referenced
targets, single-branch intersections and object-only unions. Scalar, nullable/mixed and untyped roots do not acquire an
object proof merely because the schema document is a JSON object or contains `properties`.
Draft-07 `$ref` siblings cannot supply that proof. This is not a general satisfiability analysis.
The local object proof does not establish native strict-tool root acceptance. On UnitedStates/Default
`grok-4.6`, a strict tool rooted at `$ref` receives HTTP 400 with `invalid-argument` and
`invalid_client_tool_schema`: "tool parameter root must be an object type (root schema is a $ref)".
The preflight reports: "Grok does not accept a strict tool schema with a root $ref. Use an explicit object root."
Non-strict tools and response-schema references retain their original handling.
Local validation uses the original schema and reports invalid output without
rewriting it or retrying. Unknown regex semantics add no refusal to an explicitly opted-in local-validation
attempt. The evaluator reports its actual validation result or error, without a full ECMA-equivalence claim.
Complete dialect-specific keyword shapes and complete regex classification
or original-dialect ECMA regex evaluation remain incomplete.
Live UnitedStates/Default qualification at source `4f17955` covers one genuine nonstreaming
`grok-4.6` response with a Draft-07 fragment-ID reference, closed object and required enum marker.
The unchanged original schema returns `{"marker":"FRAGMENT_OK"}`, `Completed/completed`,
`ProviderJsonSchema` enforcement and valid output. Usage reports 751 input, 80 output, 512 cached input
and 72 reasoning output tokens, with reported cost 0.0004540360 USD. The request selects Low reasoning
and asks for 128 output tokens. A separate streaming strict-tool request with a referenced root
receives the rejection above; no callback or tool replay executes. Both calls use the tester's existing
credential without sign-in, authentication probing, retries, schema rewriting or route fallback.
These calls do not qualify arbitrary references, every schema/model, output-cap enforcement or refresh.
Live Global/Default qualification on `grok-4.20-0309-non-reasoning` covers one unchanged object
schema with required string `marker`, enum `GROK-121-SCHEMA-OK` and `additionalProperties:false`.
Streaming and genuine nonstreaming requests both complete with finish reason `completed`,
`ProviderJsonSchema` enforcement and valid output containing that marker. Reported usage is
250 input/18 output tokens for streaming and 250 input/15 output for nonstreaming.
These two requests use only the tester's separate OAuth credential, with no retry or schema/output
rewrite. They do not qualify regex, strict functions, other schemas/models, refresh or media.
Two OAuth requests at production source `e3cb082` use the same selected Global/Default model
with one strictly declared `report_marker` function, a fixed required string enum and closed
object schema, a specific function choice and 128 requested output tokens. Streaming and genuine
nonstreaming each return `RequiresAction` with finish reason `tool_calls`, no ordinary answer,
314 input/18 output/332 total tokens and 192 cached input tokens. The tester exits 1 because the
request needs caller action; this is not a transport failure. No callback or replay executes.
These runs qualify selected-route admission of that strict descriptor and forced choice, not
returned arguments, arbitrary strict schemas, regex guarantees or strict-function replay.
The shared `StructuredOutput` validator builds the unchanged original schema in a fresh local
registry for each validation. Repeated `$id` values across requests do not collide or retain request
graphs in the global registry. Existing supplier default dialect, vocabulary and dialect registries,
format evaluation and global lookup fallback remain unchanged. The pinned supplier's default
dialect is V1, not an implicit Draft 2020-12 selection.
The client does not interpret unqualified xAI headers as OpenAI account-usage evidence. Its usage
header names and units require separate provider qualification; no quota snapshot is fabricated.

The terminal Responses `usage` maps [reported integer cost ticks](https://docs.x.ai/developers/cost-tracking)
exactly to decimal USD at 10^-10 USD per tick. `num_server_side_tools_used` and `num_sources_used`
remain independent total-call and source observations. The documented
[`server_side_tool_usage_details`](https://docs.x.ai/developers/tools/x-search#usage-counts) maps
web, X, code-interpreter, MCP and image-generation call counts to the corresponding hosted categories.
Missing, malformed, negative or unrepresentable observations remain unknown; explicit zero and empty
detail remain observed. The client does not sum named detail into a total or count fetched X items as
calls or sources. It leaves the separate `file_search_calls`/`document_search_calls` pairing unknown
because their neutral aggregation is unqualified. Other Responses providers do not acquire these xAI
metrics. Terminal usage remains authoritative; no interim usage producer or capability is added.
One streaming OAuth CodeExecution request at source `e3cb082` on Global/Default
`grok-4.20-0309-non-reasoning` returns `437` for `19 * 23`, with one observed code call completed
and chat status `Completed`/finish `completed`. The existing tester reports normalized usage:
965 input, 17 output, 982 total, 448 cached input, unknown cache-write and zero reasoning tokens;
provider-reported USD cost `0.0057783500`; one total hosted call and zero sources. Named call
counts report CodeExecution 1 and WebSearch, SocialSearch, RemoteMcp and ImageGeneration 0.
The request supplies 128 output tokens and two hosted turns, uses only the tester's saved OAuth
credential, and performs no retry or account/route fallback. This qualifies these reported
normalized facts on the selected route, not subscription billing, raw-provider equality, every
category's nonzero mapping, headers, document-counter pairing or nonstreaming accounting.
One streaming OAuth image-generation request at production source `0dacacd` on Global/Default
`grok-4.7` reports a native start, `Generating` progress, a completed hosted call and ordinary
answer `OK`. One genuine nonstreaming request at `a6f7b5f` reports a completed hosted call and
108,299 typed image bytes. Both use Low reasoning, 256 requested output tokens, one hosted turn
and explicit Generate action, without input resources or another image request. Both chat results
are `Completed` with finish reason `completed` and report one ImageGeneration call, one total hosted
call, zero other named hosted calls and zero sources. Nonstreaming usage reports 5,493 input,
128 output, 5,621 total, 1,664 cached input and 81 reasoning tokens, unknown cache-write tokens
and provider-reported USD cost `0.0531477200`. The streaming run establishes lifecycle delivery;
its formatter does not report payload presence. The nonstreaming run establishes typed payload
delivery, not decoded pixels, dimensions, streaming byte delivery or Session/Workspace commit.
Neither run writes image bytes to a file or log, retries, changes account/route or establishes
separate subscription billing from the reported cost.

`XaiMultiAgentControls` is the provider-controls type the xAI deferred chat codec reads. Connect metadata keeps the
`xai-multi-agent` discriminator so a saved value still reads. A saved Grok connection's former `MultiAgent` count is
ignored when it is read.

`AddLlmConnect` registers the Grok provider, authenticator, Responses client and managed agent through
the same finite SDK HTTP pipelines. Provider discovery performs no OAuth or model requests.
Referencing `Darkspyre.Inference.Connect.Grok` is what registers them; `AddLlmConnect` discovers the library.
Registration alone does not qualify OAuth refresh or every provider capability. The selected live
qualifications above do not change registration behavior.
`LlmModelListCache` owns generic and lazy listings for all four model families. Agent counts are distinct from reasoning levels.
Grok catalog HTTP and malformed-catalog failures redact reflected credentials and selected/effective
outgoing headers, including borrowed HttpClient defaults, while retaining actual safe request IDs.
The region and tier premiums follow [xAI pricing](https://docs.x.ai/developers/pricing).

`LlmProviderService` implements Connect's `ILlmTextTokenizer`. `TokenizeAsync` accepts a connection
ID, model ID and one plain string. Empty text is valid. Unsupported adapters fail before credential
refresh. The built Responses adapter requires an explicit absolute HTTP or HTTPS
`LlmConnection.LlamaCppTokenizationEndpoint`, such as `http://127.0.0.1:8080/tokenize`. It sends the exact
`model` and plain string `content`, with `add_special=false`, `parse_special=false` and `with_pieces=true`.
It reads native `tokens` entries with `id` and `piece`. String pieces retain exact `Text` and UTF-8 `Bytes`.
Byte-array pieces retain exact `Bytes` and set `Text=null`; consumers count tokens without decoding fragments.
An unset endpoint refuses tokenization without guessing from the hostname or forwarding provider options.
The kept, excluded Grok adapter sends only `model` and `text` to the selected region's
[`/v1/tokenize-text`](https://docs.x.ai/developers/rest-api-reference/inference/other) endpoint.
`LlmTokenization.Tokens` retains provider order and duplicate `uint` IDs. `Text` is nullable for byte-only
pieces. A token's copied bytes need not decode independently as UTF-8. Grok's explicit bytes stay authoritative;
the native llama.cpp string representation supplies UTF-8 bytes only when the piece is a string.
Missing fields and invalid integer/byte representations fail with fixed protocol errors
without retaining raw bodies. `ConnectLlmJson.Tokenization` and `Token` own result serialization.
HTTP rejections and malformed-token errors redact reflected credential, selected-header and effective
outgoing-header values, including borrowed HttpClient defaults, from public diagnostics and request IDs.
Send failures retain the redacted explanation and actual typed failure facts through the same boundary
as catalog sends. The original exception graph stays private; a missing response does not acquire
invented status, request ID or retry observations. No send is retried.
The facade drains active tokenization during retirement and rejects late results through its
synchronous owner fence. Injected access and clients remain borrowed. Cancellation that races a
provider failure retains the actual error as its cause. Tokenization does not count instructions,
tool schemas, media or protocol framing. It does not report request occupancy, generated
usage or billing.
OAuth access to its tokenizer endpoint has not been live-qualified.

`ImageProviderService.SetupDefinitions` and `CreateSettingsManager` provide the same editing surface for
ComfyUI. Supply `IImageWorkflowChoiceSource` for host-owned saved workflows and a typed
`ISetupCommitTarget<ImageSetupChanges>`. Header values remain protected, deletion is explicit, and
health checking is separate from a potentially costly generation test. Reference-required workflows
declare the generic generation test unavailable; their existing reference/upload/job APIs remain
first-class. Setup does not replace durable job handling.

The host retains action tasks and their cancellation sources when work should outlive a dialog.
Dropping a settings object discards its edit; nothing is committed without `SaveAsync`. A settings
object saves once. After a successful or uncertain save, open the entry again for a later edit; an
uncertain outcome requires readback first. The
[generic editor example](../Common/examples/Darkspyre.Inference.Setup.Examples/README.md) compiles the
same renderer against both Connect and Tensor. No UI framework is shipped in these packages.

These contracts use the synchronized source package version 11.16.0-rc.1. The old LLM setup-input/action description
types were replaced by Common setup contracts in the 11.3.0-rc.1 breaking-contract release.
Settings objects carry provider-owned unsaved-change state, the current authentication interaction and
explicit choice capabilities for the shared Settings editor.

### Azure Resource OpenAI v1

`AddLlmConnectAzure()` registers the optional Azure assembly after `AddLlmConnect()`.
`AzureOpenAiResponses` and `AzureOpenAiChat` select distinct wire protocols. Set `Endpoint` to the
HTTPS resource authority, such as `https://resource.openai.azure.com` or
`https://resource.services.ai.azure.com`. Paths, credentials, queries and fragments are rejected.
The adapter constructs `/openai/v1/responses` or `/openai/v1/chat/completions`.

`LlmConnection.Azure` contains the explicit `V1` API mode, `ApiKey` authentication mode,
`DeploymentId` and optional `ModelProfileId`. The request's `ModelId` must match `DeploymentId`.
The underlying profile selects existing model reasoning and sampling validation. It never replaces
the wire deployment or proves Azure availability. Manual deployment entry requires no discovery or
management-plane privileges. Deployment discovery and queried account usage are not implemented.
This adapter does not discover context/output limits or deployment availability.

The shared SDK also exposes the closed `Versioned` and `Entra` selections, explicit cloud/audience
and five identity kinds. `ConnectLlmJson` exports their exact type-name metadata, including managed
identity selectors and protected `LlmAzureSecret` variants. Unknown selections and fields refuse.
Client secrets and PKCS12 bytes/passwords belong only in `LlmCredential.Azure`; their diagnostic
strings are redacted. Metadata never opens a workload file or loads a certificate. These declarations
do not enable Entra or versioned routes: the current Azure adapters reject them before dispatch.
The API-key setter refuses Entra mode. Settings expose an incompatible saved API key only for an
explicit protected edit; it does not become an Entra credential.

Unknown profiles advertise no media, structured-output, reasoning-level or sampling support.
Implemented paths can be selected through supported explicit connection/model overrides; model
overrides take precedence. Tool schema strictness remains Unknown without evidence. The Azure
settings expose the deployment, optional profile and supported explicit capability corrections.
Their local choices also expose API mode/version, authentication, cloud/audience, the five explicit
identity families and all three user-managed identity selectors. Incomplete identity values stay
in a private editor draft and cannot become an effective connection. Selecting V1 removes the
draft API version; selecting API key removes the draft Entra configuration. These mode edits never
delete protected credentials. Workload paths are explicit data; editing does not read a token file.
Client-secret and PKCS12/password fields use independent Keep, Replace and Clear intentions.
They compose into one protected credential transaction. Clearing a certificate removes its
dependent password; password Clear means null and explicit empty Replace preserves an empty
password. Incompatible protected payloads require an explicit edit, not an auth fallback.
The generic settings editor enables empty Replace only for nonrequired secret fields.
Entra and versioned drafts remain invalid for Save and unavailable for checks and inference tests.
Choice enumeration performs no token acquisition, certificate loading, discovery or provider call.
The settings keep credentials separate from configuration and save only through the host's
version-conditional commit target. Checking key presence does not verify deployment invocation.

API-key mode sends only the protected resource key in `api-key`. Other credential schemes and
`Authorization`/`api-key` entries in additional or secret header bags are rejected case-insensitively.
The adapter does not acquire Entra tokens or send legacy versioned routes. Responses and Chat
retain their existing shared codecs; opaque state is bound to authority, protocol, deployment,
configuration and protected-key principal. State from public OpenAI or another scope is rejected.

Azure Chat defaults to `max_completion_tokens`. An explicit Chat output-limit field wins.
Conversation cache keys are omitted by default on Azure Chat and require explicit opt-in.
Prompt/tool binary and schema conversions use the shared matching protocol builders.

The registered Azure HTTP clients have no generation timeout and disable automatic redirects.
The adapter refuses 3xx responses without retry. Replacing the named Azure clients' handlers
transfers redirect-policy responsibility to the caller; response checks cannot undo credential
forwarding by a handler that already followed a redirect. The Azure clients have no public
constructor accepting an arbitrary borrowed `HttpClient`.

## Realtime Voice Contracts

The owning TTS, STT and Realtime test projects drive the production session codecs and lifetimes
with stand-in WebSockets. They check ordered sends, actual completion observations, reusable TTS
and Realtime sessions, bounded feeds with paused readers, resource drain and committed-audio
recovery after cleanup failure. The test-only transport source is shared within Connect.
Each family also drives its public opening route through a real BCL ClientWebSocket with a controlled
HTTP rejection. These checks preserve safe request ID and retry delay without publishing credentials
or a failed-upgrade body. They do not qualify a successful live HTTP upgrade, live OAuth,
provider entitlement, native resumption or actual speech quality.

`AddRealtimeConnect()` registers `RealtimeProviderService` and `XaiRealtimeProvider`.
Its setup catalog contains registered `IRealtimeProvider` definitions. Registration performs no
OAuth or socket request and does not prove provider entitlement or session readiness.
An unregistered transport refuses.

The facade exposes `GetCapabilitiesAsync(connection, modelId?, cancellationToken)` and
`OpenSessionAsync(connection, config, limits, xai?, resources?, cancellationToken)`.
`RealtimeConnection` carries the endpoint, a five-minute library request timeout, copied headers,
data-only xAI selection and optional borrowed authorization. Runtime bindings and resource access
remain explicit operation arguments. `InferenceStreamLimits` contains three positive caller-local
budgets with no implicit defaults. A returned `IRealtimeVoiceSession` owns its socket and tasks;
the caller disposes it. The facade retires a session returned after cancellation before handoff.

The facade implements `IDisposable` and `IAsyncDisposable`. Retirement fences new work, cancels
entered provider callbacks and waits for admitted synchronous and asynchronous calls to finish.
It detaches borrowed providers after drain, including when cancellation callbacks fail. Concurrent
disposal callers observe the same completion and failure. Providers remain caller-owned. A late,
unhanded session is disposed before its open operation exits; cancellation and cleanup failures are
both preserved. A session already returned to its caller is not disposed by facade retirement.
Retained foreign exceptions can still retain their own type and stack metadata.

`RealtimeSessionConfig` retains the complete typed instructions, audio, tools, turn detection,
transcription, speed and explicit scoped resumption settings. Tool schemas and hosted-tool data
are snapshotted before provider entry. `XaiRealtimeOptions.Replace` distinguishes omitted and
explicit empty maps. `XaiRealtimeForceMessageItem` retains text and optional interruptibility;
an inherited client ID refuses because that variant has no declared ID mapping.
`XaiRealtimeOptions.TranscriptionModel` preserves an explicit input-transcription selector;
null omits it. It does not identify the session model or observed model provenance. Setup saves
and edits it through the same session provider-options value. Session snapshots preserve the
actual conversation receipt object; copying configuration does not issue a new native receipt.

Text/audio content and function-call items can represent unknown observed payloads as null.
Required JSON member presence remains distinct from its nullable value. Audio content preserves
unknown versus empty bytes and an actual transcript; text also carries an optional actual
transcript. Audio deltas still require non-null bytes. These observations do not authorize sending
incomplete history: an implemented adapter must reject missing outgoing text/audio or function
name/JSON-object arguments before effects. Transcript text does not substitute for missing audio bytes.
Session events preserve optional observed modalities, and item events preserve raw provider status.
`RealtimeHostedObservationEvent` carries partial hosted observations without fabricating function
call names, arguments, client execution or completion. Its argument-final flag is independent of
tool completion; final snapshots are not appended to earlier deltas.

`LlmWebSearchTool.Location` carries optional country, city, region and timezone without local
normalization. Null differs from a supplied object with omitted members. The existing hosted-tool
snapshot copies the location value. Current built-in chat and Tensor routes reject unsupported
hosted tools before effects; these fields do not enable a realtime or hosted-tool implementation.

`ConnectRealtimeJson` exposes exact data-type getters for connections, settings changes,
configuration, content, conversation items and concrete events. Its complete graphs admit the
fixed xAI options, force-message item and X search tool. Common's metadata does not admit these
Connect-only extensions. Non-null runtime collaborators refuse portable JSON without invoking
them. Audio-content observations use Common's targeted nullable byte policy, while audio deltas
retain the separate required-byte policy. The new hosted-observation event has exact Common and
ConnectRealtimeJson getters, and Common owns the exact WebSearch location getter and nested graph.

Response and error events carry nullable ordered `AudioArtifacts` and raw `ProviderStatus`.
Each `RealtimeAudioArtifact` preserves the actual output identity, committed descriptor,
optional observed audio configuration and actual selection version. Null differs from an
observed empty list. The data contracts alone do not stage audio;
`RealtimeAudioDoneEvent` remains a separate native observation without a resource receipt.

Common exposes `RealtimeOperationException` and `RealtimeOperationCanceledException` for copied
error observations with actual causes. The xAI session uses one cached completion for Events and
every disposer. It settles resource handles, socket cleanup and lifetime cancellation callbacks
before reporting the final failure. A failure with pending committed audio carries its ordered
actual artifacts and terminal provider status. A lone operation cancellation preserves its token;
independent cleanup or publication failures remain faults. Provider execution remains unqualified.

`XaiRealtimeProvider` implements the xAI WebSocket command and observation route registered by
`AddRealtimeConnect()`. It also accepts explicitly borrowed HTTP and the selected same-access binding.
Opening acquires one immutable OAuth connection snapshot and validates controls, headers,
resumption evidence and optional workspace scope before opening the socket. OAuth WebSocket
acceptance and actual provider/model readiness remain unqualified. A returned session owns its
socket, receive task and ordered command sends. Provider retirement does not dispose returned
sessions or borrowed collaborators.

Each command enters the session lifetime and freezes caller bytes and structured data before
waiting for the send gate. Resource and authorization callbacks run in the serialized command
stage. Command completion means local send completion, not provider acknowledgment. Commands
do not wait for Events. The single Events consumer reads a bounded event/payload feed; retirement
interrupts backpressure and drains admitted sends, receive work and resource callbacks.
Dispatched send failure ends unsafe framing without reconnecting or replaying commands.
Explicit disposal prevents late Events delivery and does not wait for a paused reader. Natural
remote completion permits queued observations to drain, including when reading begins afterward.
Caller-canceled enumeration exits without waiting for session cleanup; disposal still reports the
settled session outcome.

Realtime and hosted-tool error observations retain supplied provider explanations through the
existing bounded secret redactor. The session protects opening OAuth credentials, explicit and
HTTP-default header values, and resolved hosted-tool authorization values. Later tool updates add
their protected values before dispatch. Missing explanations retain the generic diagnostic;
error codes, parameters and event identities keep their existing identity policy. This follows the
[xAI voice error event](https://docs.x.ai/developers/rest-api-reference/inference/voice), which carries
a code and message without normally retiring the session.

Explicit `session.update` model selectors follow the published WebSocket schema. An attempted
routing change invalidates the original opening receipt proof before acknowledgment, including
receipts issued afterward. A bounded set of attempted selectors classifies subsequent actual
model observations as caller-requested. It does not establish acknowledgment order or command
success. Requested models never substitute for actual model observations. Original same-issuer
receipt objects can reopen under matching binding/version/endpoint/routing proof with unknown
model observation. Imported or cloned receipts refuse before effects when this owner lacks
actual compatible model/account evidence.

Named tool documents use the shared scoped reader and once-only borrowed-content handling.
Audio outputs use the shared speech writer; terminal publication carries actual committed
descriptors and selection versions. Truncate and delete do not mutate immutable workspace bytes.
The writer retains a validated actual commit receipt before a late scope fence. Session completion
recovers pending artifacts when terminal publication fails, including a late post-commit refusal.
Successful fenced publication releases that recovery snapshot; no session-wide artifact history is
retained. Owned cleanup failures retain their actual refused owners and the same retirement outcome.
Healthy borrowed references detach after drain.

`CreateSettingsManager(target)` borrows the conditional commit target. Setup preserves every typed
session default, separates protected header edits and removes live authorization from saved data.
Capability inspection does not prove authentication or session readiness. Manager retirement
fences new operations, drains admitted work and detaches borrowed collaborators. An admitted Save
retains its actual committed or uncertain outcome. Capability inspection and source compilation
do not qualify OAuth, remote resumption, model mutation or provider execution.

## Account usage

Subscription adapters that report account-level quotas set `SupportsAccountUsage = InferenceSupport.Supported`.
An adapter that can query them implements `ILlmAccountUsageClient`; for one that does not, the facade
returns the quota windows the connection's responses reported (`GetObservedAccountUsageAsync`).
Hosts normally call the provider-neutral facade:

```csharp
LlmAccountUsageSnapshot usage = await llms.GetAccountUsageAsync(connectionId);
```

The snapshot separates proportional or absolute quota windows, credit balances, and aggregate usage
metrics. Reset times are UTC `DateTimeOffset` values; `IsApproximate` identifies estimates. A CLI may
also supply a sanitized `Summary` for display only—hosts must not parse that text as a stable contract.
Credentials, account email, and raw protocol responses are never included. Codex, Antigravity, Grok and
compatible connections whose endpoint host is exactly `openrouter.ai` expose queried usage; Claude
subscription exposes observed usage. OpenRouter reads the authenticated key's `/key` report, not the management-only account balance.
Other compatible endpoints reject account-usage overrides that claim this operation. A missing
`IsUnlimited` value is null, distinct from an explicit false.

Connect reads usage on demand; it owns no startup or polling schedule. Darkspyre's core.inference
`AccountUsage` owner starts an independent read for each connection at activation and after configuration
changes. It refreshes after turns and at reported reset times, without blocking startup or other
connections. Each connection has one in-memory observation, published to all consumers; no session or
disk quota cache owns another copy.

Facade account-usage operations check caller cancellation and facade retirement after capability
callbacks, before queried dispatch and before publishing completed results. Passive polling checks
the same owner fence after connection resolution and after reading the observation store,
before returning the snapshot. Retiring the facade drains admitted operations
without disposing borrowed clients or the borrowed observation store.

`GetObservedAccountUsageAsync(connectionId)` reads passive provider evidence without a provider call.
It returns null until that connection identity reports a quota. OpenAI Responses and compatible Chat
Completions read rate-limit headers, Anthropic Messages reads its rate-limit headers, Codex reads quota
headers, and both Claude CLI lanes record `rate_limit_event`. HTTP observations precede status handling,
including failed responses, and cover streaming and complete response paths. Vertex Claude does not
inherit Anthropic account observations. These measurements are separate from generation-token usage
and are not public stream events.

The store merges each reported bucket by its own observation time. Each observed quota or credit
retains `Observation.ObservedAt` and `Source`; reset and expiry stay unknown unless reported. Raw Claude
utilization uses a fraction and its reset uses Unix seconds. Codex raw utilization uses percent.
Anthropic token remaining counts are approximate. Codex observed IDs match queried meter/window IDs.
Connection version, provider or account-scope replacement clears prior observations and fences late
results from the old binding. A generation-owned reservation is taken before each chat, polling or
native-session host resolution: an older asynchronous completion cannot replace a newer identity,
while late resolutions of the same identity retain valid in-flight bindings. Host versions remain
opaque; the store does not compare their numeric or lexical order. After an OAuth credential save,
chat confirms the persisted provider/account scope and exact refreshed credential before attaching
response evidence to its persisted version. Concurrent replacement or deletion drops that response's
observation without changing generation behavior or clearing a newer bucket. A failed confirmation
read also drops evidence; cancellation still stops the operation. Confirmation and polling
participate in facade cancellation and retirement. The store does not invent fresh balances when a
reset passes.

Standalone composition passes the same `LlmAccountUsageObservations` to the `LlmProviderService` and
`LlmAgentProviderService` constructors. Their injected store remains borrowed; its generation owner
disposes it after the facades retire. A facade without an injected store owns a private store and
disposes it on retirement; that store is not shared with a separately constructed native-agent facade.
Store disposal clears measurements and ignores later emissions. `ConnectLlmJson.AccountUsageSnapshot`
supplies generation-local JSON metadata for the closed result and nullable provenance graph.

## Direct ChatGPT subscription transport and managed agents

`OpenAiCodex` is a direct subscription Responses adapter, separate from the OpenAI API-key
transport. Configure HTTPS and sign in through the existing device-OAuth flow:

```csharp
new LlmConnection
{
    Version = version,
    Transport = LlmTransport.OpenAiCodex,
    Endpoint = "https://chatgpt.com/backend-api/codex",
    Credential = protectedOAuthCredentialJson
};

// Uses the host prompt and protected ILlmConnectionSource, including compare-and-save.
await llms.SignInAsync(connectionId, authPrompt, cancellationToken: cancellationToken);
```

The host owns this OAuth credential. An existing Codex CLI login is separate and is never read,
copied, or used as a fallback. Former executable endpoints (`codex`, `local://codex-cli`, executable
paths) now fail with migration guidance. Direct Codex settings have no endpoint field: they always
use the official endpoint, so saving an edit replaces a former value. Save requires a completed
ChatGPT sign-in, or a saved credential that the edit keeps. Model
discovery uses the direct authenticated catalog. A model whose catalog entry lists
`supported_reasoning_levels` offers exactly those levels; other models offer `Minimal` to `ExtraHigh`.

The adapter targets Codex CLI `0.159.3`, independently of the Inference package version.
Its `User-Agent`, `version` header and catalog `client_version` use that same version for discovery,
generation and account usage. Additional `version` and `User-Agent` headers are reserved on this
transport; other custom headers retain their existing policy. The reviewed upstream baseline is
[`rust-v0.159.3`](https://github.com/openai/codex/releases/tag/rust-v0.159.3), source commit
`01fc69f4026735edfdf6789820549727a4867b11`. A model with `supports_experimental_context` uses its
`max_context_window`. Other catalog limits use `context_window`, falling back to `max_context_window` only when the
former is absent or null. Positive reported limits are physical
context limits, not an invented usable-context or compaction budget. Unknown limits remain null.
Matching the version does not establish full CLI tooling or endpoint parity.
The focused direct-Codex regression verifies the same pin on catalog, generation and account-usage
requests through the public facade. Selected catalog reasoning, summary and input-modality facts
reach model capabilities; admitted reasoning controls reach generation. Its synthetic HTTP responses do not
establish live subscription entitlement or endpoint acceptance.
The existing ConsoleTester at source `3a52055` completes direct device OAuth sign-in and
discovers seven actual models with its independent vault credential. Its selected `gpt-6-luna`
passes all four existing protocol checks: a complete function call retains continuation state,
the document tool result supplies its verification word, multiple assistant states replay without
server-stored history, and an image prompt completes. The tester exits successfully with JSON
reflection disabled and no retries. Installed Codex CLI and the adapter pin both report `0.159.3`.
These results qualify the selected catalog/tool/content/replay routes, not every model, credential
refresh, managed-session compaction, other platforms or complete CLI tooling parity.

Upstream defines the release number in `codex-rs/Cargo.toml` under `[workspace.package]`.
The [main-branch source](https://github.com/openai/codex/blob/2635431edbd43279a4da9c1199456758fbbc8ad3/codex-rs/Cargo.toml#L161-L162)
contains the placeholder `0.0.0`; the
[selected release source](https://github.com/openai/codex/blob/01fc69f4026735edfdf6789820549727a4867b11/codex-rs/Cargo.toml#L160-L161)
contains `0.159.3`. The
[release workflow](https://github.com/openai/codex/blob/2635431edbd43279a4da9c1199456758fbbc8ad3/.github/workflows/rust-release.yml#L11-L55)
runs on `rust-v*.*.*` tags and blocks the build unless the tag without `rust-v` exactly matches
the Cargo package version. Cargo supplies that package metadata to `env!("CARGO_PKG_VERSION")`,
which upstream uses for its
[User-Agent](https://github.com/openai/codex/blob/01fc69f4026735edfdf6789820549727a4867b11/codex-rs/login/src/auth/default_client.rs#L156-L165)
and [version header](https://github.com/openai/codex/blob/01fc69f4026735edfdf6789820549727a4867b11/codex-rs/model-provider-info/src/lib.rs#L532-L535).
There is no separate release-version build override in this path. Connect's compatibility pin
matches the selected release metadata, not main's placeholder or the Inference package version.

Chat and managed agents share SSE request mapping, attachments, tool definitions, reasoning level,
JSON Schema, and ordered opaque response-state replay. Requests use `store: false`; the library
does not retrieve server history or send `previous_response_id`. `ConversationKey` provides cache
affinity only; managed sessions use `ThreadId`. Temperature, top-p and output-limit enforcement
are `Unknown` for this direct endpoint. Explicit values reach the existing Responses fields
unchanged. Unknown admission does not promise endpoint acceptance or output-limit enforcement.
JSON-object mode remains unsupported because the adapter has no mapping for it.
The managed output reserve budgets context independently of `MaxOutputTokens`.

Subscription requests explicitly opt into `reasoning.summary: "auto"`, retaining the selected
effort or leaving effort to the provider when unspecified. Summary deltas flow through
`LlmReasoningDeltaEvent` and managed `LlmAgentReasoningDeltaEvent` with `IsSummary = true`, so hosts
can display progress before the reply. Summaries are provider output and may be absent for a turn;
the library does not manufacture reasoning text. See [OpenAI reasoning summaries](https://developers.openai.com/api/docs/guides/reasoning#reasoning-summaries).

Both `SupportsToolUse` and `SupportsAgentToolUse` is `Supported`. With chat, the caller runs the tool
loop. With `LlmAgentProviderService`, Darkspyre.Inference.Connect.LLM runs it and invokes the host's handlers; do not
run a second Darkspyre loop on top. Start through that facade so every model iteration re-resolves
and refreshes the host connection. Account/endpoint changes reject an existing session.

Account usage reads the same-origin `/backend-api/wham/usage` endpoint: reported quota windows,
balances and available reset counts are mapped without exposing identity. Missing measurements
remain unknown; activity aggregates and reset-credit expiry are unavailable. This is read-only
and never consumes credits. The upstream route is evidenced in
[OpenAI's backend client](https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/client/rate_limit_resets.rs).

| Former Codex runtime behavior | Managed replacement |
| --- | --- |
| App Server dynamic tools | Host-defined tools and the Darkspyre.Inference.Connect.LLM loop |
| Provider resume/fork | Capture complete context; start with saved/new thread identity |
| Native compaction | Compaction inside Inference by `ConversationCompactor`; Codex's own compaction is never requested |
| Immediate steering | Queued safe-boundary steering |
| CLI commands, file edits, web/MCP/subagents | Only tools the host explicitly registers and authorizes |
| Native approval/sandbox enforcement | Host handler policy; no built-in command executor or host sandbox |

The old Codex process/App Server implementation is removed, not retained as a second lane.
Claude and AgentCore still use their native session adapters.

Tools and structured output carry no strictness flag. Codex sends `"strict": false` for every tool and for a
structured answer's JSON schema, because a host schema describes what it accepts and need not meet the
provider's strict rules.

```csharp
LlmAgentProviderService agents = services.GetRequiredService<LlmAgentProviderService>();
var agentWeatherTool = new LlmAgentToolDefinition
{
    Name = "get_weather",
    Description = "Get the current weather for a city.",
    InputSchema = weatherTool.InputSchema
};
await using ILlmAgentSession session = await agents.StartSessionAsync(
    connectionId,
    new LlmAgentSessionOptions
    {
        ModelId = "gpt-5.6-terra",
        Tools = [agentWeatherTool],
        ToolHandler = ExecuteToolAsync
    },
    cancellationToken);

await foreach (LlmAgentEvent agentEvent in session.RunTurnAsync(
                   new LlmAgentTurnRequest
                   {
                       Input = [new LlmAgentTextContent("Inspect this repository and summarize the report.")],
                       Attachments =
                       [
                           new LlmAttachment
                           {
                               Kind = LlmBinaryKind.Document,
                               FileName = "report.pdf",
                               Data = reportBytes
                           }
                       ]
                   },
                   cancellationToken))
{
    Render(agentEvent);
}

// The attachment is not resent. This turn continues on the same headless agent session.
await foreach (LlmAgentEvent agentEvent in session.RunTurnAsync(
                   new LlmAgentTurnRequest
                   {
                       Input =
                       [
                           new LlmAgentTextContent(
                               "What was the second recommendation in the report?")
                       ]
                   },
                   cancellationToken))
{
    Render(agentEvent);
}
```

`LlmAgentSessionOptions.Runtime` (`LlmAgentNativeRuntimeOptions`) controls a provider-owned runtime:
its working directory, `Ephemeral` and `Policy`. The Claude CLI reads only the working directory and
`Ephemeral`; with no `Runtime` it runs in the current directory with a persisted CLI session. It accepts
`Policy` and does not read it, because it runs no tool of its own. A managed session accepts and ignores
`Runtime`.
`Policy` is one of two variants:

- `LlmAgentHostRuntimePolicy` (the default): host-selected writable roots, network access, sandbox,
  approval policy and `AllowRuntimeBackgroundWork`.
- `LlmAgentProviderRuntimePolicy`: explicit adoption of the policy configured in the provider's own
  settings, including its background behavior. Connect does not read, edit or strengthen that policy and
  makes no claim about the filesystem, network or background limits it applies.

`LlmAgentCapabilities.SupportedRuntimePolicies` lists the variants a runtime accepts. The Claude CLI and
AgentCore accept `HostControlled`; the Antigravity CLI accepts only `ProviderConfigured`; managed runtimes
report an empty set. No runtime refuses a policy variant: the Claude CLI runs the default host-controlled
policy when it receives another. Managed Codex has no built-in shell, file editor,
network tool, or approval executor. Darkspyre must enforce authorization, filesystem roots, and network
policy inside each tool handler. Managed sessions are always live/in-memory; persistence is explicit
host capture.

Every Claude CLI process launches with `ClaudeCliConfiguration.IsolationArguments`: `--safe-mode`,
`--restricted`, `--strict-mcp-config`, `--disable-slash-commands`, `--tools ""` and
`--permission-prompts none`. The CLI loads no CLAUDE.md, skills, plugins, hooks, settings files or
configured MCP servers, and runs no built-in tool. AgentCore permits remote Harness-managed tools only with `DangerFullAccess`;
local workspace restrictions cannot constrain remote tools.
Claude agent sessions use `--permission-mode dontAsk`, not planning mode. `--allowedTools` lists only
the registered host MCP tools, so nothing else may run, and permission prompts remain disabled.
The caller's handler owns the effects of its registered tools. Claude's [permission contract](https://code.claude.com/docs/en/agent-sdk/permissions)
separates explicit tool approval from planning mode's write restrictions.
Claude start retires an unsuccessfully initialized session before returning.
Successful cleanup preserves the original opening error. Failed cleanup returns an aggregate
containing the original opening error followed by the cleanup error, without retrying cleanup.

### Headless attachment lifetime

Codex keeps bytes and ordered adapter state in the live managed context and projects them again
on subsequent requests. Claude keeps its CLI conversation open and sends attachment blocks inline.
Neither behavior promises a durable LLM-side file cache.

| Situation | Direct Codex | Claude CLI |
| --- | --- | --- |
| Later turn, same open session | Live context re-sends retained bytes/state | Existing CLI conversation |
| New independent chat request | Send the required history and bytes | Attach again |
| Process restart | Reopen a host snapshot captured with bytes, or reattach | Native resume does not guarantee exact binary access |
| Fork | Start a complete context prefix with new thread identity | Native current-position fork; retain original bytes |
| Session disposal | Live context is gone; host owns any saved snapshot | CLI process closes |
| Compaction | Host replacement decides retained exact bytes | Provider may replace context with summaries |

Default capture omits bytes and adds notices. Use `IncludeAttachmentData = true` when exact
attachment access must survive reopen or live compaction. Keep the original files in host storage;
cache affinity is not file storage.

Managed sessions expose `ThreadId` and `SessionId`, explicit interrupt, safe-boundary steering,
and host context operations. Codex and Claude continue, fork and rewind by starting a new session with the cut
history; `ResumeSessionAsync` refuses on both. Claude supports interrupt and steering. Tools return
text plus provider-neutral image/document attachments. On Codex, an image URL in a turn's input
becomes the text `[Image: {url}]`. Claude receives tool-result resources inline in the bridge's MCP results;
its built-in tools, `Read` included, are off (`--tools ""`). A tool-capable session receives a host tool handler unless it returns its calls
(`ToolHandling = ReturnRequests`); a Claude call with no handler gets an error result. On Claude, returned calls run
without session persistence: the first host tool call of a step interrupts the CLI unanswered, every call of that step
is returned with its argument check, and the next turn opens a new CLI session from the saved history. Codex and Claude
sessions that run their calls add `update_plan` after the Host's tools, and `load_skill` with a skills list in the
instructions when a `SkillFolders` folder holds a `SKILL.md`. Inference answers both (`Plan updated.`, or the
SKILL.md text and the skill's folder); a Host tool of either name is used in its place.

A Claude steer is a user message written to the CLI with `priority: "next"` and `origin: {kind: "human"}`, keyed by a
new uuid. It does not wait for the turn gate, and its echo never starts a host turn. During a tool call the CLI adds
the steer to the tool results of the same turn. When the steer arrives as the model finishes, the CLI ends its turn
with `queued_turn_count` above 0 and then starts its own turn for the steer. The host turn stays open across both CLI
turns and completes at the second result; its usage request ids carry a suffix per CLI turn, and its run totals add
them. The steer's `command_lifecycle` frames, keyed by `command_uuid`, track it: `started` records it in the saved
history at that point of the turn; `cancelled`, `discarded` or `refused` before `started` reports a warning on the
turn. An interrupt first withdraws the turn's unstarted steers with `cancel_async_message`. A steer still unstarted
when the conversation reopens the CLI is recorded in the history the new CLI opens with.

## AWS Bedrock AgentCore Harness transport

Converse sends a tool's `strict` flag only when `LlmToolDefinition.Strict` is true. Models without
strict schemas reject the field even when it is false. The exact base IDs
`amazon.nova-micro-v1:0`, `amazon.nova-lite-v1:0`, `amazon.nova-pro-v1:0` and `amazon.nova-2-lite-v1:0`
report Unsupported strict tool-input schemas and Unsupported native JSON Schema support after geographic-prefix
normalization. Explicit strict tools fail before SDK acquisition through the shared tool validator.
Overrides cannot enable native JSON Schema output for these models. Ordinary tools retain the original
input schema and omit the strict flag in both Converse request modes. This follows the model-card
restrictions for [Micro](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-micro.html),
[Lite](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-lite.html),
[Pro](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-pro.html) and
[Nova 2 Lite](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-2-lite.html), with
[strict tool use included in structured outputs](https://docs.aws.amazon.com/bedrock/latest/userguide/structured-output.html).
Other Nova versions and ARNs do not inherit this restriction merely from their name.

Converse and ConverseStream send native response schemas through the SDK's
`outputConfig.textFormat` with type `json_schema`. The original schema string, name and description
are unchanged. The completion records `ProviderJsonSchema` only when that configuration is sent;
local validation still checks the original response against the original schema. The shared shape
guard rejects an empty name or a non-object schema before resource reads or SDK acquisition.

The exact IDs `openai.gpt-oss-20b-1:0`, `openai.gpt-oss-120b-1:0`,
`anthropic.claude-sonnet-4-6`, `anthropic.claude-opus-4-6-v1`,
`anthropic.claude-sonnet-4-5-20250929-v1:0`, `anthropic.claude-haiku-4-5-20251001-v1:0` and
`anthropic.claude-opus-4-5-20251101-v1:0` advertise AWS-documented native schema support. Existing
geographic-prefix normalization applies. IDs without a pinned support fact and ARNs require an explicit
schema capability override for a native attempt. An override can disable native enforcement on a known model.
Without native support, only an explicit `LocalValidation` fallback permits structured output;
that request carries no native output configuration. JSON-object-only enforcement is unavailable.
Provider schema compilation failures propagate without schema rewriting, retry or automatic fallback.
See [AWS structured output](https://docs.aws.amazon.com/bedrock/latest/userguide/structured-output.html),
[gpt-oss 20B](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-oss-20b.html),
[gpt-oss 120B](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-oss-120b.html)
and [Claude structured output support](https://docs.aws.amazon.com/bedrock/latest/userguide/claude-messages-structured-outputs.html).
The existing SDK marshaller is verified for both request modes. Live gpt-oss 20B and 120B Low
ConverseStream calls in `us-east-1` return text that fails the original schema's JSON validation.
The result retains `IsValid = false`; provider-reported completion is not a validated structured
answer. Native structured-output success remains unqualified on DSA-109. Claude models are not exercised by
these calls. A direct AWS CLI 2.37.2 Converse call on 20B Low
also returns malformed JSON with the same schema in `us-east-1`, without Connect or its stream parser.
That proves a provider-facing nonstreaming failure for that request. It does not establish the cause
of every streaming failure or qualify a native schema guarantee. Responses are not rewritten.
The public ConsoleTester also exercises Connect's real nonstreaming Converse path on 20B Low
against source `612d2b1`, using one short native-schema request with a 256-output-token cap.
The provider reports `Completed`/`stop`, 80 input tokens and 47 output tokens, but its original response
fails JSON validation with `ProviderJsonSchema` and `IsValid = false`. This is a failed structured-output
qualification, not a truncation or successful schema guarantee. Tester exit 0 denotes provider completion;
the separate validation result remains false. No retry, response repair or fallback occurs.

Converse sends a reasoning level through `additionalModelRequestFields` for Claude, Nova Lite 1.5,
Nova 2 and the qualified gpt-oss models, classified from the model or inference-profile id:

| Family | Levels | Fields |
| --- | --- | --- |
| Claude (`anthropic.claude-*`) | The Anthropic Messages set for the same model | `thinking` and `output_config.effort`, or a thinking budget added to `inferenceConfig.maxTokens` (8,192 when the request sets none) |
| Nova Lite 1.5 (`amazon.nova-lite-1-5-v1:0`) | `None`, `Low`, `Medium`, `High` | `reasoningConfig` with `type: "disabled"`, or `type: "enabled"` and `maxReasoningEffort` |
| Nova 2 (`amazon.nova-2-*`) | `None`, `Low`, `Medium`, `High` | `reasoningConfig` with `type: "disabled"`, or `type: "enabled"` and `maxReasoningEffort` |
| gpt-oss (`openai.gpt-oss-20b-1:0`, `openai.gpt-oss-120b-1:0`) | `Low`, `Medium`, `High` | `reasoning_effort` with `low`, `medium` or `high` |

The gpt-oss controls apply to those exact base IDs after geographic-prefix removal. Safeguard and
unqualified gpt-oss versions, DeepSeek-R1, other non-Claude models and ARNs offer no level. A capability
override can narrow a non-Claude family's set but cannot widen it. Claude uses the Messages-native
mapping before sampling and tool-choice validation; a mapped adaptive or budget thinking request
rejects temperature and forced tool choice. A Nova 2 request at `High` rejects temperature and
`MaxOutputTokens` before the call.

Nova Lite 1.5 uses the exact base ID in the [AWS Version 1 reasoning guide](https://docs.aws.amazon.com/nova/latest/userguide/extended-thinking.html)
and its geographic profiles. It retains caller temperature and maximum output tokens at `High`;
Nova 2's control restrictions do not apply to it. The existing reasoning projection merges its
`reasoningConfig` with top-k without changing either field. Nova 1.0 models gain no reasoning levels.
The source mapping does not establish live model availability or successful provider execution.
The configured AWS connection rejects both `amazon.nova-lite-1-5-v1:0` and
`us.amazon.nova-lite-1-5-v1:0` with `ValidationException` and "The provided model identifier is invalid."
Neither identity is live-qualified. This does not establish availability on another connection or region.

Converse streams through ConverseStream. A connection with `SupportsStreaming = InferenceSupport.Unsupported` sends Converse
instead. Its events then arrive after generation ends, in the order reasoning, text, tool calls, then the
completion. Reasoning text arrives as `LlmReasoningDeltaEvent` with `IsSummary = false`. A Claude model
under its default `omitted` reasoning display streams no reasoning text. Usage arrives in the stream's
final metadata event and reaches the host on the completion.
Converse usage includes cache reads and writes in normalized input tokens. A reported `totalTokens`
remains authoritative and is not replaced by that normalized sum. A total-only SDK observation keeps
input and output counts null. Both request modes use the same projection.
Both modes preserve `trace.promptRouter.invokedModelId` as the result's actual model identity when
AWS supplies it. Missing identity remains unknown; the requested ID or resolved inference profile
does not replace it. The shared model-observation boundary rejects invalid or credential-bearing
identity before completion publication. The existing Common projection carries that identity into
provenance. This follows [AWS PromptRouterTrace](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_PromptRouterTrace.html).

Valid tool calls return with `RequiresAction` or `Unknown`. Unknown calls remain observed evidence,
not admitted tool execution. The stop reasons `max_tokens` and
`model_context_window_exceeded` report `Incomplete`, `content_filtered` and `guardrail_intervened` report
`Refused`, and `malformed_model_output` and `malformed_tool_use` report `Failed` with no tool calls and no
continuation state. An unknown or missing stop reason reports `Unknown`.

A `tool_use` reply with null, scalar or array tool input throws `LlmProviderException` in both
Converse modes. Non-streaming mapping rejects it before publishing response events. Empty object
input remains valid, and provider-reported malformed stop reasons keep their `Failed` result.
Streaming preserves the actual argument text. Missing or empty argument text does not become `{}`;
it publishes no completed tool call and a `tool_use` terminal reports the same invalid-object error.
An explicitly streamed `{}` remains a valid empty object. This follows the required
[AWS tool-input delta](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_ToolUseBlockDelta.html).

Managed Converse compaction rejects a change to the provider-visible message prefix through the last
signed assistant or to the top-level system prompt before resource callbacks or context replacement.
Generation and compaction share the system projection: request instructions followed by all
system/developer messages in order, including those after the signed assistant. The validator compares
the shared Converse projection, not context entry IDs or count metadata. Unsigned and counts-only
histories retain their existing compaction behavior. Tool-result reductions after that signed prefix
remain permitted.
An explicit idle `ReplaceContextAsync` can start a fresh context without the old provider state.

Converse model listing returns models the account can attempt, each under an identifier it can
invoke. Listing is not proof of entitlement. It omits a foundation model whose only inference type is
`PROVISIONED`, such as
`amazon.nova-lite-v1:0:300k`. Such a model runs only through purchased provisioned throughput, so a
connection cannot invoke it by model ID.

Inference-profile discovery follows every returned pagination token. Model listing and
foundation-model profile resolution use the complete catalog from the same helper.

For every other foundation model, listing calls `GetFoundationModelAvailability`, at most eight at a
time. A model is callable when its agreement status is `AVAILABLE` and its authorization status is
`AUTHORIZED`. Known agreement statuses `PENDING`, `NOT_AVAILABLE` and `ERROR`, or authorization
`NOT_AUTHORIZED`, keep their existing exclusion. Missing or unrecognized status fields remain unknown;
they do not hide a model unless another known field excludes it. The private availability map retains
that uncertainty. A system profile is omitted only when every named model has a known exclusion.
Unknown or unchecked model availability keeps the profile selectable, without a verified-access claim.
A retained model that supports `ON_DEMAND` is listed by its model ID. A retained model that supports
only `INFERENCE_PROFILE` is not listed by its model ID; it appears
through the region's system inference profiles that include it, such as `us.` and `global.` profiles.
The connection's credentials need `bedrock:GetFoundationModelAvailability`. When any availability
request fails, for example with `AccessDenied`, listing stops admitting more checks and waits for
the checks already in flight. Each failed request logs one warning. A single failure stays a single
provider exception; multiple failures remain together in an aggregate. A failed logging sink does
not skip another request's failure or diagnostic attempt.
Catalog failures and their structured warnings preserve the credential-redacted AWS explanation
alongside actual status, code and request ID. Raw SDK exception graphs remain private evidence.

Bedrock's catalog requests report no model sizes. Each listed model carries the context window and
maximum output of its base model from `BedrockModelLimits`, a table of the limits Anthropic publishes for
Claude on Bedrock and AWS publishes for exact Nova and gpt-oss IDs. An inference profile such as
`us.anthropic.claude-sonnet-4-5-20250929-v1:0` takes its base model's entry. Geographic Nova and gpt-oss
profiles use the same lookup. A model that is not in the table, or an ARN, reports null limits, which
mean unknown. A related name or unlisted version does not inherit another model's limits.

| Exact Bedrock base ID | Published Context Tokens | Published Maximum Output Tokens |
| --- | --- | --- |
| `amazon.nova-micro-v1:0` | 128,000 | 5,000 |
| `amazon.nova-lite-v1:0` | 300,000 | 5,000 |
| `amazon.nova-pro-v1:0` | 300,000 | 5,000 |
| `amazon.nova-2-lite-v1:0` | 1,000,000 | Unknown |
| `openai.gpt-oss-20b-1:0` | 128,000 | 16,000 |
| `openai.gpt-oss-120b-1:0` | 128,000 | 16,000 |

These values follow AWS's model cards for [Micro](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-micro.html),
[Lite](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-lite.html),
[Pro](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-pro.html),
[Nova 2 Lite](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-2-lite.html),
[gpt-oss 20B](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-oss-20b.html) and
[120B](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-oss-120b.html).
The [OpenAI parameter table](https://docs.aws.amazon.com/bedrock/latest/userguide/model-parameters-openai.html)
also states the 128,000-token context window. Nova 2 Lite's output stays null because its model card
states 64K while its [Converse guide](https://docs.aws.amazon.com/nova/latest/nova2-userguide/using-converse-api.html)
states 65,000. These are published catalog facts, not measurements of provider-enforced limits or
claims of account access. Native requests retain their selected model or profile ID.

Explicit `us.`, `us-gov.`, `eu.`, `apac.`, `jp.`, `au.`, `in.` and `global.` inference-profile IDs share one
recognition path. Family-specific reasoning, cache and limit lookup use their base model; native
requests retain the selected profile ID without a foundation-model lookup. Automatic profile-selection
preferences are unchanged. [AWS documents these geographic profile prefixes](https://docs.aws.amazon.com/bedrock/latest/userguide/models-region-compatibility.html).
The [gpt-oss 20B](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-oss-20b.html)
and [120B](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-oss-120b.html)
model cards document the `us-gov.` profile IDs. They retain their base model's request facts without
qualifying live GovCloud access or native structured-output success.
The [India Haiku 4.5 profile](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-anthropic-claude-haiku-4-5.html)
does not support native structured output. Its schema capability is `Unsupported`, and an override cannot
enable it. Explicit original-schema local validation remains available without changing the profile ID.
Its strict tool-input schema capability is Unsupported. An explicit strict tool fails with "The selected
route cannot enforce strict tool input schemas." before SDK acquisition or resource preparation.
[Strict tool use is a structured-output mechanism](https://docs.aws.amazon.com/bedrock/latest/userguide/structured-output.html).
Ordinary tools remain available with their original input schemas and no strict flag. Converse models
without a pinned strict-schema fact report Unknown support.

Converse refuses nonempty connection or request `ProviderOptions` during pure request admission.
The refusal precedes SDK acquisition, discovery, logging-control calls and resource preparation.
Typed supported controls retain their existing Converse projection.
Managed Converse session opening uses the same pure adapter admission before model discovery.
Converse rejects nonempty connection `AdditionalHeaders` during its shared connection admission.
The error states: "Bedrock Converse does not support custom HTTP headers. Remove AdditionalHeaders
from the connection." Generation, catalog discovery, capability lookup, input estimation and managed
session opening use this check before SDK acquisition or resource preparation. Header names and
values are not included in the error. The adapter does not forward custom headers into AWS-signed
requests.

Converse streams use the factory's existing bounded producer. Factory retirement cancels and drains
the producer, closes owned streams and releases clients without waiting for paused callers to resume.
Actual cleanup failures remain visible to retirement callers.
Converse HTTP, AWS service, SDK client and unmodeled event-stream failures retain credential-redacted
explanations through the existing failure classifier. Service failures retain observed status, code
and request IDs. The stream reader unwraps recognized service, client and HTTP causes; HTTP causes
retain their observed status. Raw exception graphs remain private.
Logging-verification refusal keeps its `InvalidOperationException` and exposes its classified safe
cause; no generation request follows that refusal. A transport, SDK client or unmodeled stream failure
supplies no invented status, provider code, request ID or retry delay.

Converse also runs on the host-owned managed agent runtime. `AddLlmConnectBedrock()` registers an
`ILlmAgentClient` for `AwsBedrockConverse`; each model step is one Converse request, and the host runs
every tool. Managed Converse sessions take the same options, context and tool output as managed Codex,
with tool output carried on the Converse route.

`AwsBedrockAgentCore` is a separate transport from `AwsBedrockConverse`. Converse is a stateless chat
API with a host-managed tool loop; AgentCore invokes a deployed
[Harness](https://docs.aws.amazon.com/bedrock-agentcore/latest/devguide/harness.html) and owns the
remote agent session. Both are registered by `AddLlmConnectBedrock()`.

AgentCore chat and native-session calls pass the captured connection to the shared Bedrock failure
classifier. SDK acquisition, Harness invocation and event-stream failures expose credential-redacted
diagnostics for `AwsBedrockAgentCore`. Recognized service, client and HTTP stream causes retain their
safe explanations; observed service status, code and request ID and HTTP status remain available.
Unobserved metadata remains null. Raw exception graphs remain private. Cancellation and independent
owned cleanup failures retain their existing handling.

Configure AgentCore with the Harness ARN as the endpoint and the same protected AWS profile/region
shape used by Bedrock Converse. The AWS SDK resolves the actual credentials from its standard chain:

```csharp
new LlmConnection
{
    Version = version,
    Transport = LlmTransport.AwsBedrockAgentCore,
    Endpoint = "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/my-harness",
    Credential = new LlmCredential
    {
        Aws = new LlmAwsCredentials
        {
            Profile = "application-profile",
            Region = "us-east-1"
        }
    }.Serialize()
};
```

Use `LlmAgentProviderService` for the full Harness contract. Model ID `default` preserves the model
deployed with the Harness; another ID supplies a Bedrock model override. New sessions generate an AWS
runtime session ID, and `ResumeSessionAsync` continues an existing ID. AgentCore does not expose the
portable fork, steer, remote interrupt, compaction, or non-persistent session operations, so those
methods fail explicitly. Canceling `RunTurnAsync` cancels the local streaming request.

The factory-owned AgentCore invocation uses the same bounded producer as Converse. Factory retirement
cancels and drains the SDK invocation and closes its owned response independently of a paused chat
reader. Resuming that reader after retirement reports cancellation.
An invocation failure survives a later iterator or response cleanup failure. Cleanup failures carry
the typed cleanup phase and remain observable through factory retirement without duplicating the
same actual failure.

Direct AgentCore session disposal fences new operational calls and cancels its active SDK request
and host tool callback. Concurrent disposal callers share the same retirement result and failures.
Retirement closes the observation feed and waits for active operations before releasing the turn gate.
An ignoring SDK reader or callback keeps retirement pending until it returns. A bounded turn producer
drains independently of a paused public iterator. Closed resource observations hold no operation
lease across yields. Late provider and tool results are not delivered. The injected invoker remains
borrowed. Local retirement does not claim a remote Harness interrupt or shutdown.

AgentCore chat and native sessions replace usage observations within each actual Harness invocation.
Only `end_turn` and `stop_sequence` report normal completion. Unknown terminal stop reasons,
including a final `tool_result` without a subsequent completed message, remain `Unknown`.
Malformed, interrupted, partial and hook-stopped chat outcomes report `Failed`. Native sessions
preserve their interruption/refusal/failure reasons; a hook-stopped invocation reports provider
failure. Missing message-stop events retain their explicit protocol-failure path.
Native unknown-completion diagnostics retain the actual stop reason.
Chat emits changed known counts before its completion; its final usage can be null. Its running event
cannot represent wholly unavailable usage. A later known observation after unavailable metadata is
emitted again. Native observations share one request ID per invocation and can report unknown usage.
Each native invocation emits authoritative settled usage even when interim observations are disabled.
Turn totals sum distinct completed invocations, including host-tool follow-ups, without adding repeated
metadata. Absent and overflowing counts remain unknown. Input, output and total counts retain the
provider values independently. Reasoning counts are unavailable. Cache-read/write normalization is
unqualified, so the normalized cache subsets remain unavailable; no cache counts are added to input
or total usage. Usage observations do not establish billing or remote-session cumulative totals.

Host tools become AgentCore inline functions. When the Harness stops with `tool_use`, Darkspyre.Inference.Connect.LLM
captures the streamed arguments, invokes `LlmAgentToolHandler`, and performs the required follow-up
with both the assistant `toolUse` and user `toolResult` messages on the same runtime session. Tool
results are text-only. See `ProviderToolExamples.BedrockAgentCoreAsync` for a complete callback.

The generic `WorkingDirectory` value is not sent to AWS; Harness files and commands run in the remote
AgentCore environment, never in the caller's local directory. `ReadOnly` sandbox mode, or any
approval policy other than `Never`, restricts `allowedTools` to the explicitly registered host tools
because AgentCore cannot route approval requests for its remote built-in tools to the host. With a
writable sandbox and `Never`, the deployed Harness tool policy is allowed to operate remotely.

The current Harness wire contract has no image or document message block and no binary tool-result
block. This transport therefore reports all four attachment capabilities as `Unsupported`; callers must not
splice files into text or base64 themselves. It also has no portable model-list endpoint, so hosts
normally expose `default` plus a manually entered Bedrock override.

## Anthropic API-key and Claude CLI transports

The two Claude integrations are separate providers. `AnthropicMessages` is the direct
[Messages API](https://platform.claude.com/docs/en/api/messages) transport and accepts only an API
key or protected `x-api-key` header. It supports chat-style host tools and never launches the Claude
CLI. `AnthropicClaudeCli` is the Claude subscription transport; it launches the official CLI, reports
agent-tool support, and rejects API-key credentials. OAuth is never used to choose between them.
The CLI process starts and ends as [Wrapped CLI processes](#wrapped-cli-processes) describes.

Configure an API-key connection with the API endpoint and a host-owned credential:

```csharp
new LlmConnection
{
    Version = version,
    Transport = LlmTransport.AnthropicMessages,
    Endpoint = "https://api.anthropic.com/v1",
    Credential = protectedApiKeyCredentialJson
};
```

Configure a subscription connection with the CLI executable and no credential:

```csharp
new LlmConnection
{
    Version = version,
    Transport = LlmTransport.AnthropicClaudeCli,
    Endpoint = "claude",
    Credential = null
};
```

For connections created by 0.9.0, migrate Claude subscriptions by changing `Transport` from
`AnthropicMessages` to `AnthropicClaudeCli`, changing `Endpoint` from the Anthropic API URL to
`claude` (or an absolute CLI path), and clearing `Credential`. Leaving the transport as
`AnthropicMessages` now means direct API-key billing and will never launch the CLI.

Install the official Claude CLI and ensure `claude` is on `PATH`. A connection runs as one of two
accounts:

- **The machine's active Claude login**, when the connection has no credential. Complete the CLI's
  interactive sign-in once:

```bash
claude
```

- **A Claude account the connection is signed in to.** The settings action "Sign in to a Claude
  account" (`sign-in-claude`) shows a claude.com sign-in link; the person pastes back the `code#state`
  text the callback page shows. Connect exchanges it for a `user:inference` token with a one-year
  lifetime, the token `claude setup-token` creates, and saves it as the connection's
  `LlmCredential.OAuth`. Every Claude CLI process for the connection (chat, streaming, agent sessions,
  `auth status` and model discovery) then starts with `CLAUDE_CODE_OAUTH_TOKEN` set to that
  token, and without `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN` and `CLAUDE_CODE_OAUTH_SCOPES`, which
  the CLI would prefer to the token or read as its scopes. The machine's login is neither used nor
  changed, so connections on different accounts run at the same time. The token is never refreshed;
  signing in again replaces it.

Subscription prompts run in an empty temporary workspace with safe mode, tools, slash commands,
permission prompts, and session persistence disabled. Darkspyre.Inference.Connect.LLM sends the complete prompt as BOM-free
UTF-8 stream JSON over standard input rather than exposing it in process arguments. It consumes output
line by line and treats the terminal `result` event as authoritative, including when the CLI retries
an intermediate refusal on a fallback model. `IsAuthenticatedAsync` checks only `claude auth status
--json` for a signed-in first-party account, run as the connection's account.

For a connection that is not signed in, model discovery reads the official CLI's `initialize` control
response. It preserves each model's native `value`, `displayName` and complete cloned catalog entry.
It sends no generation prompt and does not read or copy CLI credentials. For a signed-in connection,
discovery uses the Anthropic Models API with the account token.
The installed CLI 2.1.289 discovery path is qualified through the public Connect facade with 12 native
choices. Other versions, refresh, generation and target-platform paths remain separate qualifications.

Claude subscription account usage is the quota the connection's own turns report. Both Claude CLI
lanes read each `rate_limit_event` (`ClaudeRateLimitEvent`): every entry of the `unifiedWindows` object
(`five_hour`, `seven_day` and any other key the CLI sends) is one window, keyed by its name, with its
`utilization` (the used fraction; the remaining fraction is `1 - utilization`) and its `resetsAt`. The
event's top-level `status` is the state of the window named by `rateLimitType`; the other windows are
Available below full utilization and Exhausted at full. An event without `unifiedWindows` yields the
`rateLimitType` window with its status and reset time and no fraction, plus the overage window from
`overageStatus`. The observation store keeps the latest reading of each window.
`ClaudeRateLimitLog` also logs each event's `rate_limit_info` object as received, at Information, once per
connection and `rateLimitType` until the object changes. Nothing else of the stream is logged.
The adapter has no account-usage read of its own, so `GetAccountUsageAsync` returns those windows and
reports `account_usage_not_observed` until a turn has reported one. The CLI's `/usage` command is not
run: from Claude CLI 2.1.302 its headless report carries no quota windows.
API-key connections do not advertise account usage because
Anthropic does not expose the subscription report through the Messages API.

Native chat-style tools are enabled for API-key connections. Claude subscription chat requests remain
isolated one-shot generations, report `SupportsToolUse = InferenceSupport.Unsupported`, and launch the CLI with tools disabled.
Tool history in those requests is carried as text blocks (`ToolHistory = Text`).
For host tools, use `LlmAgentProviderService`: it registers the supplied schemas as an SDK-hosted MCP
server over Claude's streaming stdin/stdout control protocol. MCP `tools/call` requests are converted
to `LlmAgentToolCall`, executed only by the host's `ToolHandler`, and returned to Claude as MCP results.
Each call runs apart from the adapter's stream reader, so the session keeps routing output while a host
tool works. Interrupting a turn makes the CLI cancel its call with `notifications/cancelled`; the adapter
cancels the handler's token and answers the call with an error result. The CLI exiting, or the session
being disposed, cancels calls in flight too. A result that arrives after its turn ended reaches no later
turn.
The adapter launches in restricted mode, disables built-in tools and settings-based MCP servers, and
allows only the explicitly registered Darkspyre.Inference.Connect.LLM tools. Claude subscription connections therefore
report `SupportsAgentSessions = InferenceSupport.Supported` and `SupportsAgentToolUse = InferenceSupport.Supported`.

### Native background resources

A provider-owned runtime can run background work inside its own process. `ILlmAgentSession` reports it:

- `ObserveAsync` is a session-lifetime feed, separate from each turn's events. It carries
  `LlmAgentResourceEvent` (Added, Updated, Ended), `LlmAgentResourceOutputEvent` hints, and turns the
  runtime starts itself: `LlmAgentProviderTurnStartedEvent`, the ordinary message and tool events, then
  `LlmAgentTurnCompletedEvent`. It has one reader at a time and ends when the session is disposed.
- `GetResourcesAsync` returns the current set. `ReadResourceOutputAsync` reads output nondestructively by
  cursor; a null cursor starts at the earliest retained output, and a cursor before it returns Gap.
  `StopResourceAsync` returns Requested; the resource reports Stopped when the runtime confirms.
- `LlmAgentCapabilities.ResourceObservation` is Supported for the Claude CLI, Unknown for AgentCore
  (not measured) and Unsupported for managed Codex, whose tools run in the host.
- The Claude CLI runs no background work of its own: `Task`, `Monitor` and `Bash` are off with every
  other built-in tool. Its session reports no resources, and `StopResourceAsync` returns Unknown.
- `LlmAgentHostRuntimePolicy.AllowRuntimeBackgroundWork` and `ResourceOutputRetentionBytes` are stored
  Host settings. No phase-1 runtime reads them.

Connect launches the Claude CLI with
`--replay-user-messages` and reads it for the session's lifetime: a host turn starts at the CLI's echo of
its message, and output with no echo before it is a provider-started turn on `ObserveAsync`. A host turn
sent during a provider turn is queued by the CLI and starts after it.

### Sampling

The neutral request carries `TopP`, `TopK`, `Seed`, `PresencePenalty`, `FrequencyPenalty` and
`StopSequences`. Null controls leave provider defaults intact. An explicit unsupported control fails
before dispatch. `SupportsTopP`, `SupportsTopK`, `SupportsSeed`, `SupportsPresencePenalty` and
`SupportsFrequencyPenalty` defaults to `Unknown`; `MaxStopSequences` defaults to zero.

Raw provider options cannot carry `temperature`, `top_p`, `top_k`, `seed`, `presence_penalty`,
`frequency_penalty`, `stop`, `stop_sequences`, or Gemini's `topP`, `topK`, `presencePenalty`,
`frequencyPenalty` and `stopSequences`. The guard applies to connection and request options even
when the typed control is absent. The facade checks the resolved connection before credential
refresh, connection preparation or model discovery. Its shared guard throws `InvalidOperationException`,
including for xAI sampling options. Direct Anthropic and Vertex Claude validation retains its
`NotSupportedException` for raw temperature. Other xAI protocol options retain their adapter errors.
Nonstandard extension options retain request-over-connection precedence.

| Route | Sampling mapping |
|---|---|
| OpenAI Responses | `top_p` on sampling-capable profiles; reasoning models require `ReasoningLevel.None` |
| OpenAI-compatible Chat | `top_p`, `seed`, penalties in [-2, 2], up to four `stop` strings; `top_k` requires a known endpoint override |
| Anthropic and Vertex Claude | Profile-owned temperature/top-p/top-k, `stop_sequences` without an invented Connect limit; thinking permits top-p from 0.95 to 1 and no top-k |
| Bedrock Converse | `inferenceConfig.topP` and `stopSequences`; Claude `top_k`, Nova 1 and Nova 2 `inferenceConfig.topK` from 0 to 128, merged with reasoning fields; stop limit 2500, or 10 for DeepSeek R1 |
| AgentCore | `BedrockModelConfig.TopP` on an explicit Bedrock model ID; the harness-default route cannot carry it |
| Gemini API and Vertex Gemini | `generationConfig` sampling fields and penalties, up to five stops; Gemini API reports top-k as Unknown without a catalog response, Unsupported for an observed model without a positive default, and Supported for a positive default; Vertex uses its shared model profile |
| Apple Foundation Models | Top-p or top-k random mode with optional seed; seed alone selects top-p 1; no stops or penalties |
| Codex | Temperature and top-p use the existing Responses fields with Unknown support; top-k, seed and penalties have no adapter mapping |
| Claude CLI and Antigravity CLI | All six controls rejected; capability overrides cannot enable an unmapped control |
| SelfHost | The universal normalizer applies the loaded engine capabilities before sending typed controls to Common |

Claude Opus 4.7 and newer non-sampling profiles reject custom temperature/top-p and all top-k.
First-party Messages accepts the documented defaults described under Sampling Defaults.
Earlier sampling-capable Claude profiles enable these controls by default; overrides can disable them.
Gemini Flash 3.6 and later ignore temperature/top-p/top-k, so Connect rejects those explicit values.
Their penalty support remains Unknown and permits unchanged attempts. Vertex Gemini penalties use
[-2, 2]; the disputed upper boundary remains attemptable. Gemini API requires finite penalties.
Compatible endpoint settings expose typed sampling overrides and a nullable maximum-stop count;
zero disables stops, and blank uses detection. Raw provider options cannot inject these typed fields.

Bedrock recognizes top-k for the exact Nova 1 Micro, Lite, Pro and Premier `v1:0` IDs, Nova Lite 1.5
`amazon.nova-lite-1-5-v1:0`, and their geographic inference profiles. Both Converse transports carry it inside
`additionalModelRequestFields.inferenceConfig`, as the [Nova 1 Converse guide](https://docs.aws.amazon.com/nova/latest/userguide/using-converse-api.html)
specifies. The [Nova request schema](https://docs.aws.amazon.com/nova/latest/userguide/complete-request-schema.html)
permits zero through 128. Nova accepts zero through the shared sampling validator; other adapters
retain its positive top-k requirement. Nova 1.0 classification adds no reasoning levels or fields.

### Reasoning

`AnthropicModelProfiles.MapReasoning` owns explicit Messages-native choice rows. The selected model's
effective native levels select the mapped value before thinking/tool-choice validation and payload
projection. `ExtraHigh` maps down to `high` on the Claude 4.6 profile; `Maximum` maps to `high` on the
effort-only Claude 4.5 profile. Budget models keep their existing token budgets and output allowance
addition. `Minimal` maps to `None` when the profile permits disabling or omitting thinking; `None`
and `Minimal` map to the lowest available positive effort on always-on models. Null leaves defaults
unchanged. The mapping does not change the caller request, selected model or reported native choices.

Missing model/catalog reasoning facts remain Unknown. An unknown positive effort uses the existing
`output_config.effort` field without inventing a thinking mode. An explicit `None` attempts the
existing `thinking.type = disabled` form. This is a native attempt, not a support claim. A catalog
that explicitly reports both thinking and effort unsupported offers only `None`; positive requests
map down to that native no-reasoning form. Missing facts never take that negative path. No shared
rank/distance calculation runs.

Vertex Claude and Bedrock Converse's Claude branch use the same Messages-native mapping. Their
outer admission and sampling checks use the mapped effort before dispatch; their existing Vertex
Messages and Converse field builders send that value. Model-level caller choices use their existing
override precedence on each route. Unknown Claude model facts do not prohibit the existing effort
field mapping. These changes do not alter Gemini, Nova or gpt-oss reasoning admission or fields.

Direct API-key requests map a reasoning level per model. Current models take
`thinking: {type: "adaptive"}` with `output_config.effort` (`low`, `medium`, `high`, `xhigh`, `max`),
and `None` sends `thinking: {type: "disabled"}` where the model accepts it. Sonnet 5.5, Opus 5.5 and
Fable reject disabled thinking and offer no `None`. Opus 4.6 and Sonnet 4.6
offer no `ExtraHigh`, and Opus 4.5 offers effort only up to `High`. Models without effort, such as
Haiku 4.5, take a thinking budget of 1,024, 4,096, 16,384, 32,768 or 64,000 tokens for `Low` through
`Maximum`, added to `max_tokens`.

The levels of an API-key connection come from `GET /v1/models`. A model that reports adaptive thinking
offers the effort levels its entry marks supported; a model that reports only budget thinking offers
`Low` to `Maximum`. `None` is added where the model family accepts disabled or omitted thinking. A model
the catalog lists but the family table does not know therefore gains its reported effort levels without
a library change. Missing catalog facts retain a known family row. A model with neither reports Unknown
and permits the existing native effort attempt without requiring a capability override.

### Sampling Defaults

First-party post-Opus-4.6 Messages models accept explicit `Temperature = 1` and `TopP >= 0.99`
within the API's upper bound of 1. Connect sends those values unchanged, including with default
thinking. Custom temperature, lower top-p and every top-k remain rejected for these known profiles,
as the [Messages reference](https://platform.claude.com/docs/en/api/messages/create) documents.
Temperature and top-p capability states remain `Unknown`, not a promise of controllable sampling.
An explicit caller `Unsupported` override still blocks the field. Vertex and Bedrock do not inherit
this first-party acceptance exception.

### Maximum output tokens

For direct API-key requests, omitting `MaxOutputTokens` sends `max_tokens` of 8192. Adaptive thinking is on by default on current Claude models, and thinking tokens count against `max_tokens`, so a lower ceiling can truncate the response before visible text and return `LlmCompletionStatus.Incomplete`.
Subscription chat requests map an explicit `MaxOutputTokens` to the child process's
[`CLAUDE_CODE_MAX_OUTPUT_TOKENS`](https://code.claude.com/docs/en/env-vars) environment variable.
Both streaming and non-streaming calls use that mapping without changing the parent environment.
Output-limit enforcement remains `Unknown`: the CLI documents the setting for most requests and
applies model-specific caps. An omitted limit leaves the CLI environment unchanged.

### Refusals

A direct Messages API classifier refusal is an HTTP 200 response with `stop_reason: "refusal"`, an empty content list, and a `stop_details` object. Darkspyre.Inference.Connect.LLM emits the explanation as an `LlmRefusalDeltaEvent`, then completes with `LlmCompletionStatus.Refused`, `Refusal` set to `stop_details.explanation`, and `RefusalCategory` set to `stop_details.category`. Both `stop_details` fields are null when the refusal maps to no named category, and `RefusalCategory` is then null.

A refusal can also arrive after partial output. `Text` keeps the streamed partial so the host can log it. That partial is not an answer; hosts branch on `Status`. For subscription prompts, only the CLI's terminal result determines whether the completed request was refused; intermediate fallback attempts do not produce a premature refusal result.

Claude CLI chat and native sessions require a terminal `success` subtype for normal completion.
A successful result with no model stop reason remains completed without inventing that reason.
An explicit unknown stop reason, or a missing/unknown result subtype without another classified
termination, reports `Unknown`. Refusal and token/context-window truncation keep their real terminal
classification. Native unknown diagnostics retain the observed subtype and stop reason. Existing
process errors and interruptions retain their precedence; intermediate fallback attempts do not
classify the completed operation.
Claude CLI chat, native turns and account-usage failures retain the ordered terminal `errors`
details, or the terminal `result` diagnostic when no error list is supplied. The shared Claude
projection uses the existing secret redactor and display bound. Process-exit failures retain their
actual exit code and any observed terminal error detail. A failed native terminal result is failure
evidence, not an ordinary completed answer. Successful result text remains unchanged.

### Structured output

Shared request validation accepts a named object schema or the JSON Schema `true`. The latter
accepts any JSON instance, including a scalar, without changing the original schema. Antigravity
uses this same validator. Native writers carry `true` unchanged when their route has no established
negative; that attempt does not qualify provider acceptance. Explicit local fallback evaluates the
returned JSON against the original schema. Strict OpenAI Responses output retains its documented native
[object-root restriction](https://developers.openai.com/api/docs/guides/structured-outputs#root-objects-must-not-be-anyof-and-must-be-an-object):
`true` uses only a caller-permitted JSON-object or local fallback there. A non-strict native request
retains the original schema and strictness as an attempt, not a guarantee. Anthropic's existing native
subset analysis remains separate. A false schema and non-schema JSON values remain refused by
request admission; this change does not redefine them or weaken host tool schemas.

Direct API-key connections use the Models API's `structured_outputs` evidence for native schema and
strict tool support. A missing entry retains Unknown and permits an unchanged native schema attempt
without advertising qualified support. A catalog rejection cannot be
overridden. JSON-object-only enforcement is not advertised. Supported output schemas use
`output_config.format` with `type: json_schema`; reasoning effort remains in the same output config.

Anthropic Messages and Vertex Claude preflight the same schema subset. Objects must close
`additionalProperties`; unsupported constraints, recursive/external references, complex enums,
unsupported formats and unsupported pattern constructs do not reach native output enforcement.
Unknown keywords and annotations remain unchanged and do not establish native support or inability.
Schema analysis retains an unknown model's support state; passing local checks does not promote it
to supported. Schemas are not rewritten. A request must explicitly permit `LocalValidation` to use a schema
outside that subset or a model without native support. That path omits the native format and
validates the response against the original schema, including its omitted constraints.
Response validation retains the selected JSON Schema dialect and its keyword rules. Admission
of an unfamiliar keyword does not establish that the local evaluator can evaluate it.
When the schema evaluator throws `JsonSchemaException`, the result retains the provider's answer
and reports `WasEvaluated = false`, `IsValid = false`, and the evaluator diagnostic in `Errors`.
This result does not assert that the answer violates the schema. It does not change the dialect,
remove constraints, or retry the provider request. Host tool arguments that cannot be evaluated let the tool
handler run; arguments evaluated as invalid return to the model.

Strict tools use the same subset check and a maximum of 20 strict tools per request. Ordinary tools
do not count toward that limit and retain their original schemas. Unsupported strict schemas are
rejected before dispatch, not simplified or changed to non-strict. Unknown model strictness still
permits an explicit strict attempt; a known unsupported profile rejects it. Provider options cannot
replace the typed output config or supply the retired `output_format` field.

The combined typed strict-tool and native-output budget permits at most 24 optional parameters and
16 union parameters. A property's omission from its object's `required` list makes it optional;
defaults do not make it required. `anyOf` or a type array consumes a union slot. The iterative
analysis counts nested schemas and each use of an internal reference. Unused definitions are
validated but do not spend the budget. Saturated costs prevent reference graphs from expanding
or overflowing the counters. Non-strict tools and locally validated output schemas spend none of
the native budget. An explicitly permitted `LocalValidation` fallback can omit the output format
when it exceeds the combined budget; it cannot weaken over-budget strict tools.

Regex preflight validates syntax and known unsupported features. It invents no numeric cutoff
for unpublished range or compiled-grammar limits. Passing local checks does not guarantee provider
compilation. Provider compilation failures propagate without retries, schema rewriting or an
automatic switch to local validation.

Subscription connections use the Claude CLI's native `--json-schema` contract and accept the `Low`,
`Medium`, `High`, `ExtraHigh` and `Maximum` reasoning levels. Darkspyre.Inference.Connect.LLM uses
the terminal event's canonical `structured_output` value and validates it locally before returning
completion.

`ClaudeCliLlmClient.MapReasoning` owns explicit CLI-native choice rows. Chat sends the mapped value
through `--effort`; native-agent turns send it through `apply_flag_settings.settings.effortLevel`.
Both use the currently selected model's catalog and caller overrides. A model switch resolves effort
for the new model before input is written. `ExtraHigh` maps to `high` for the explicit Claude 4.6
profiles; `Maximum` retains their native `max`. `None` and `Minimal` map to the lowest available CLI
effort because the CLI has no `none` or `minimal` effort spelling. This does not disable thinking.
Null leaves effort unset. Missing effort choices use the CLI's existing native vocabulary and remain
attemptable; an explicit catalog `supportsEffort: false` rejects an effort request before a process
invocation or native turn write. No shared rank/distance calculation runs, and caller intent remains
unchanged.
Explicit `claude-sonnet-4-6` and `claude-opus-4-6` model IDs, including their `[1m]` forms, offer
only `Low`, `Medium`, `High` and `Maximum`, matching the
[Claude Code effort table](https://code.claude.com/docs/en/model-config#adjust-effort-level).
Their shared chat/native-session resolver rejects `ExtraHigh` before sending a prompt or control.
Connection/model overrides can narrow that set but cannot widen it. Aliases and unknown model IDs
without catalog effort facts retain their existing policy; the adapter does not infer their resolved model.
Published native catalog fields `supportsEffort` and `supportedEffortLevels` take precedence over
family defaults. Explicit false offers no selectable effort. A reported list offers only its recognized
levels, including an explicitly empty list. Overrides can narrow but cannot widen these reported facts.
Chat uses the existing connection/version model-list cache, shared with discovery. Native sessions
retain optional `models` from their own initialize reply through the same catalog decoder, without
another process or catalog query. Both lanes use the same effort resolver before prompt/control writes.
The public console tester at source `e66ab01`, installed Claude CLI 2.1.289 and selected `sonnet`
completes one Low-effort chat call with the exact synthetic marker `CONNECT_CLAUDE_CATALOG_121`.
It reports `Completed`, `end_turn`, 567 input, 22 output and 589 total tokens. Cache observations
report zero cached input and 565 cache-write input tokens; reasoning and cost remain unknown.
The existing native resume case passes in 5.64 seconds with two turn attempts and no skips.
Both turns complete, and a replacement process resumes the exact conversation ID and recalls
the synthetic marker. These three turn attempts use the existing CLI sign-in without copying
credentials or retrying. The results qualify the selected alias's catalog-backed chat and native
initialization/resume paths, not every model, effort level, credential refresh or platform.
A successful native schema result with a non-null `structured_output` also completes when its
observed stop reason is `tool_use`. The [CLI structured-output contract](https://code.claude.com/docs/en/agent-sdk/structured-outputs)
identifies that payload as the final structured result. Connect retains the actual stop reason.
JSON in ordinary result text without that native payload does not turn an unresolved tool stop
into completion. Error, refusal, incomplete and unknown-stop handling remains unchanged.
Live qualification against source `0dacacd` uses installed Claude CLI 2.1.289, its existing sign-in,
the `sonnet` alias and explicit Low effort. One unchanged object schema requires a string marker
with one enum value and forbids additional properties. Streaming and nonstreaming both return
`{"marker":"CLAUDE-121-SCHEMA-OK"}`, status `Completed`, finish reason `tool_use` and
`ProviderJsonSchema` validation with `IsValid = true`. Reported usage is 1149 input/69 output
tokens for streaming and 1153 input/69 output tokens for nonstreaming. This qualifies the selected
simple schema and effort request, not every schema, model, applied effort, OAuth refresh or platform.
Connect reads no CLI credentials and performs no fallback or schema rewrite.
The public tester at source `8bb83b8`, installed Claude CLI 2.1.289 and selected `sonnet`
also completes one nonstreaming Low-effort request through chat's catalog-admission path.
The unchanged closed-object schema requires exactly the enum marker
`CONNECT_CLAUDE_CATALOG_SCHEMA_121`. The returned object satisfies that original schema;
the terminal result reports `Completed`, `tool_use` and valid `ProviderJsonSchema` enforcement.
Actual usage is 1179 input, 78 output and 1257 total tokens, with zero cached input and
1177 cache-write input tokens. Reasoning and cost are unreported. One tester call uses the
existing sign-in without a Connect retry, credential copying or schema fallback. This qualifies
the selected catalog-backed complete route, not every model, schema, refresh or platform.
One streaming request at source `fd5e6b2` also passes through that catalog-admission path.
The selected `sonnet` alias and Low effort return the unchanged closed-object schema's exact
`CONNECT_CLAUDE_STREAM_SCHEMA_121` marker, `Completed`, `tool_use` and valid
`ProviderJsonSchema` enforcement. Reported usage is 1173 input, 77 output and 1250 total tokens,
with zero cached input and 1171 cache-write input tokens. Reasoning and cost remain unknown.
The existing tester exits successfully with reflection disabled, no retry and no credential copy.
This qualifies the selected streaming schema route, not other models, refresh or platforms.

### Model discovery

`AnthropicMessages.ListModelsAsync` uses the API key against `GET /v1/models`. Each listed model carries
`max_input_tokens` and `max_tokens` as its limits, and the reasoning levels its entry reports.
`AnthropicClaudeCli` advertises model listing through a no-prompt initialize control exchange with
the signed-in CLI. Selectable IDs and display names come from its actual `models` response;
returned aliases stay aliases, and an empty list stays empty. The owned temporary process is
retired before options are published. Manual model selection remains available. A connection
signed in to a Claude account uses the paged HTTP catalog with its account token instead.
Installed Claude CLI 2.1.289 lists twelve native choices through the public tester with its existing
sign-in, including `default`, `opus`, `fable`, `sonnet` and `haiku`. This qualifies native listing
without a generation prompt or HTTP catalog credential. Refresh, generation on every returned model
and other-platform initialization remain separate qualification requirements.
The focused chat-adapter tests use a process stand-in to check returned alias/metadata preservation,
the no-catalog-OAuth route and cancellation while process retirement drains. These checks use no
installed CLI or provider calls.

## Antigravity CLI subscription transport

The [DSA-74 technical design](docs/antigravity-agent-tech-design.md) holds the evidence and mapping for
native agent sessions. The published 11.15.0-rc.1 adapter includes chat and native agent sessions;
installed CLI and target-platform execution remain separate qualification requirements.

`GoogleAntigravityCli` wraps the official [Antigravity CLI headless mode](https://antigravity.google/docs/cli/headless/). Darkspyre.Inference.Connect.LLM sends prompts over the documented stdin NDJSON protocol, consumes stdout events line by line, and emits model text and activity while the CLI is still running. It does not read, copy, or store Antigravity OAuth credentials. The CLI process starts and ends as [Wrapped CLI processes](#wrapped-cli-processes) describes.
The setup catalog displays `Gemini Subscription (Antigravity)` and retains the provider ID
`gemini-subscription`.

Install the CLI and complete its interactive Google sign-in once:

```bash
curl -fsSL https://antigravity.google/cli/install.sh | bash
agy
```

Configure the host-owned connection without a credential:

```csharp
new LlmConnection
{
    Version = version,
    Transport = LlmTransport.GoogleAntigravityCli,
    Endpoint = "agy"
};
```

`Endpoint` may be `agy`, `local://antigravity-cli`, or an absolute executable path. On macOS and Linux,
bare CLI names also resolve from `~/.local/bin` when a desktop app's inherited PATH omits it.
An installation elsewhere requires its directory on PATH or an explicit executable path.
Model discovery calls `agy models`; model `auto` omits the command-line model override and uses the CLI default.
The installed CLI 1.3.1 discovery path is qualified through the public Connect facade with 14 provider
models plus `auto`. Discovery uses the CLI's existing sign-in without copying its credentials.
That qualification covers listing, not generation on every returned model or every target platform.
On CLI 1.3.1, ordinary GPT-OSS 120B Medium chat completes with actual terminal usage through Connect.
Native-session qualification for that model fails at conversation initialization under the existing
15-second handshake bound. An independent CLI initialization with terminal input emits a valid init
event; that check does not establish the behavior of Connect's pipe-backed launch or explain the timeout.
Native controls and resume for this model remain unqualified. The adapter does not disable the model,
retry a turn or fabricate a conversation identity in response to that failure.

Each request runs in an empty temporary workspace with terminal sandboxing and slash-command expansion disabled. A streamed request sends its prompt as BOM-free UTF-8 through standard input. A request with `SupportsStreaming = InferenceSupport.Unsupported` is the exception: the CLI's only non-streaming form, `agy -p "<prompt>" --output-format json`, takes the prompt as a process argument, because stream-json input requires stream-json output. That prompt is visible in the local process list. It must fit in one argument: at most 131,071 UTF-8 bytes on macOS and Linux, and at most 30,000 characters on Windows, where a `.cmd` or `.bat` launcher cannot carry it at all because the prompt contains line breaks. A prompt that does not fit fails with `NotSupportedException` before the CLI starts. Native JSON Schema and the `Low`, `Medium` and `High` reasoning levels are supported. Only `agent_response` step deltas stream as answer text. A schema-backed request streams no provisional text: it returns the terminal event's canonical `structured_output` value, validated locally. The free-text `response` remains a compatibility fallback for older CLI versions. Temperature, maximum output tokens, headers, and provider options are rejected rather than silently ignored.

Two public ConsoleTester nonstreaming calls at source `cec838c`, installed CLI 1.3.1 and selected
`gemini-3.8-flash-low` with Low effort complete through the CLI's JSON-response path. The plain call
returns exactly `AGY-121-COMPLETE-OK`, `Completed/completed` and 12,657 input/56 output tokens.
The schema call returns the required closed-object enum marker, `Completed/completed`,
`ProviderJsonSchema` enforcement, valid output and 27,459 input/1,536 output tokens.
Both report actual totals and leave unreported cache, reasoning, cost and hosted counts unknown.
Neither call retries or copies CLI credentials. These runs qualify selected nonstreaming plain and
schema output, not all models, caller output caps, provider tool activity, refresh or other platforms.

The same public tester at source `c6edbb8` qualifies one nonstreaming native schema request on
CLI 1.3.1 with selected model `claude-sonnet-4-6`. The unchanged closed-object schema requires
one enum marker. The JSON-response path returns exactly
`{"marker":"CONNECT_AGY_SONNET_JSON_121"}`, `Completed/completed`, `ProviderJsonSchema`
enforcement and `IsValid = true`. Reported usage is 15,527 input, 205 output and 15,732 total tokens;
cache, reasoning, cost and hosted counts remain unknown. No selectable effort is requested for
this model. One call runs without retries or credential copying. This qualifies that schema and
selected CLI route, not independently observed upstream identity, refresh or Windows execution.

Antigravity may emit its own internal tool activity, but the CLI owns that execution. Because
Darkspyre.Inference.Connect.LLM cannot surface the call for host authorization and result submission, this adapter reports
`SupportsToolUse = InferenceSupport.Unsupported`. Its documented headless input protocol accepts only `text` blocks and
terminates on any other content-block type, so `SupportsPromptImages` and
`SupportsPromptDocuments` are also `Unsupported`. Darkspyre.Inference.Connect.LLM does not rely on undocumented `@path` behavior.

Antigravity chat reports `Unknown` for an unrecognized or missing terminal status and retains the
actual response text, usage, conversation ID and observed status. It does not validate that result
as successful structured output. Recognized success and error/interruption handling remain distinct.
Native Unknown diagnostics retain the observed status.
The shared protocol parser retains terminal error detail under the same redacted, single-line policy
as process stderr. Its bound retains a 512-character prefix and appends an ellipsis when truncated.
Chat failures and native failed/interrupted turn completion expose detail through their existing error
contracts. Failures without detail keep their existing generic message. The adapter does not synthesize
success or retry.
After the native terminal result, chat drains the process without projecting trailing text, activity
or usage. The terminal answer stays authoritative. A failed process exit still fails the operation;
the native result does not turn unsuccessful process cleanup into success.

### Antigravity native agent sessions

The shared Antigravity capability resolver restricts the published fixed-effort Gemini 3.8/3.7/3.6
Flash, Gemini 3.1 Pro and GPT-OSS 120B slugs to their pinned level. Discovery, chat and native turn
admission use that resolver. Overrides cannot widen a pinned level. Chat and native turns use
`AntigravityCliLlmClient.MapReasoning` before CLI invocation or native conversation reopening.
Its explicit rows map desired effort down to a native choice, or to the lowest available choice
when none is at or below the request. A fixed-effort slug maps every explicit effort to its pinned
level; the adapter never changes the selected model slug. Null leaves the default unchanged.
The verified `claude-sonnet-4-6` and `claude-opus-4-6-thinking` slugs expose no selectable effort because
the CLI rejects that flag. A requested effort is dropped, which keeps their native default. Overrides cannot enable it.
Other model IDs use the installed CLI's low/medium/high/xhigh/max vocabulary. `None` and `Minimal`
map to the lowest available effort; this does not disable thinking. Missing observations remain
attemptable, including xhigh and max. The installed `agy --help` documents those spellings; the public
headless guide lists only low/medium/high. No live acceptance is inferred from help. Google documents
[pinned model slugs and effort selection](https://www.antigravity.google/docs/cli/headless/).

`LlmAgentProviderService` starts and resumes native AGY conversations. AGY owns the conversation, its
Google sign-in, its native tools and its configured permission policy. Connect owns one local process
per open session, with an open standard input and one output reader.

- `StartSessionAsync` requires `Runtime` with a working directory and an explicit
  `LlmAgentProviderRuntimePolicy`. The runtime rejects `LlmAgentHostRuntimePolicy`, because the CLI cannot
  enforce host roots, network, approval or background limits. The working directory is the process
  working directory; it is not a filesystem boundary. `Ephemeral = true` is rejected: AGY persists every
  conversation.
- Start waits up to 60 seconds for the CLI handshake and returns its conversation ID as both
  `ThreadId` and `SessionId`. The IDs stay fixed when a compaction moves the session to a new AGY conversation. No hidden prompt is sent.
- `ResumeSessionAsync` passes that exact ID with `--conversation` and fails if the handshake reports
  another. Resume without `Runtime` fails, so the host must supply its stored working directory and
  provider policy. `--continue` is never used.
- Host tools reach AGY through the host-tools local service (Darkspyre.Inference.Connect.HostTools). Each AGY
  process registers its session's MCP tool list with the service under a fresh session ID and starts AGY with that ID
  in `DARKSPYRE_HOST_TOOLS_SESSION`. AGY's global MCP entry `darkspyre-host-tools` starts the stdio MCP program
  `darkspyre-host-tools-mcp`, which reads the ID, lists the session's tools and forwards each call to the service.
  The program ships as the flat `hosttools/mcp/` folder beside the Antigravity library, and it installs and connects
  to the service from the sibling `hosttools/service/` package; it carries no service package of its own.
  The service passes the call back to the registering host, which runs it through the session's tool handler as the
  Claude CLI bridge does. The first session with host tools in a process runs `agy mcp add darkspyre-host-tools
  <program>` and adds `mcp(darkspyre-host-tools/*)` to `permissions.allow` in `~/.gemini/antigravity-cli/settings.json`;
  AGY reads both only from that global configuration. An AGY started without the variable gets no host tools.
- A host tool call appears as AGY's `call_mcp_tool` step for the `darkspyre-host-tools` server. The turn reports it as
  an `LlmAgentToolEvent` with the host tool's own name, namespace `darkspyre-host-tools` and the full output the
  handler returned. Returned tool requests (`LlmAgentToolHandling.ReturnRequests`) are refused before any process
  starts. `ThreadId` and `SessionId` on a start are not read: AGY assigns the conversation ID.
- Session `Instructions` and a start's `Context` are sent once, ahead of the first turn's input: the instructions
  as `Instructions for this conversation:\n{text}`, then the history as the `CONVERSATION SO FAR` transcript. That
  turn emits the opening notice as one `LlmAgentWarningEvent`. A resume sends nonempty session instructions the
  same way. Turn instructions, images and attachments follow the universal turn rules above.
- One turn runs at a time. Model, effort (`Low`, `Medium`, `High`) and output schema apply to one turn.
  When they differ from the running process, the session closes that process while idle and reopens the
  same conversation with the new flags before sending the prompt.
- A turn emits `LlmAgentActivityEvent` per step, `LlmAgentTextDeltaEvent` for `agent_response` deltas,
  `LlmAgentToolEvent` for native tools with their reported output, running usage observations and
  `LlmAgentMessageCompletedEvent`. A schema turn streams no text and publishes one
  `LlmAgentStructuredOutputSnapshotEvent` holding the validated canonical result.
- A step in state `ERROR`, such as a tool call AGY refused, is a finished step; its tool event reports the error.
- `SUCCESS` completes the turn. `ERROR` and `INVALID` are provider failures. `CANCELED` and `INTERRUPTED`
  are interruptions. Every other status is Unknown. End of output without a result is a provider failure.
- Usage covers the current turn only. It is the difference between cumulative result counters within
  one process, or the sum of the turn's completed step usage after a start or reopen. Decreasing
  counters make the turn total unavailable. Cache-read and reasoning subsets stay null until their
  inclusion in the input and output totals is established.
  Running and final `LlmAgentUsageEvent` observations share one request ID. The final observation
  replaces the estimate, including when the reconciled total is unknown. The running-usage override
  controls interim observations; the final usage event is always emitted.
- Cancelling or abandoning a turn kills the owned process and faults the session object. Output that
  arrives while no turn is running also faults it. Resume opens a fresh session. Connect does not stop
  work AGY may keep running on its side.
- Disposal closes observation immediately, cancels admitted turns and reopens, and waits for their
  writes and producers to stop using the session before releasing the process. A bounded turn producer
  drains independently of a paused public iterator. Closed resource observations hold no operation
  lease across yields. Late output cannot publish events or update
  usage. All disposal callers share one completion and its cleanup failure. Cancelled turns retain
  cancellation; a failed local termination also faults the session with its PID and remains visible
  to disposal. Failed opening preserves both the opening and cleanup errors when both occur.
- `SupportsAgentToolUse` is `Unsupported`, AGY runs only its own tools, `ResourceObservation` is Unsupported
  and resource operations throw. Interrupt, steering, fork and context capture, replacement and compaction
  handoff are unsupported. `SupportsContextStatus` and `SupportsCompactNow` are Supported: the session the host
  holds measures and compacts its own saved history, as "Compaction on Antigravity" describes.
  `SupportsNativeResume` is Supported until a compaction opens a new AGY conversation.

The ConsoleTester `agy-probe` command runs bounded live qualification cases; see
[Subscription and AWS smoke tests](#subscription-and-aws-smoke-tests).
The resume case requires a completed resumed turn as well as the retained conversation ID and recalled
marker. Matching answer text on a failed or interrupted turn does not qualify resume.
The control case uses an offered effort, or omits effort when the model has no control, and proves that the
same session completes subsequent turns. It checks that a host-controlled runtime policy is refused.
Actual native qualification covers the controls, native-tools and cancellation cases on Antigravity 1.3.1 with
the selected `claude-opus-4-6-thinking` model. The controls case completes three turns and retains
the conversation through schema addition and removal after local unsupported-effort refusal.
The tool case completes one fixture-file read with reported tool output and exact marker recall.
The cancellation case stops after the first text delta, settles the owned local process and verifies
that the cancelled session refuses further turns. It makes one turn attempt without retry.
These cases qualify the selected route, not an independently observed upstream model identity,
all catalog models, credential refresh or other CLI platforms.

`IsAuthenticatedAsync` performs the non-consuming `agy models` check, so an installed but signed-out executable is not reported as authenticated. Run `agy` interactively to sign in or refresh the CLI session. Antigravity owns its credential and session storage in the operating system's native keyring; Darkspyre.Inference.Connect.LLM does not inspect it.

Account usage runs `/usage` as a standalone headless CLI command. Normalized model quota fields are
returned when present; the sanitized CLI report is also retained in `Summary` because Antigravity
documents the slash-command response as human-readable text rather than a versioned JSON schema.
The quota parser recognizes the CLI's tab-separated weekly remaining-limit rows. It preserves each
model-group label, reported remaining percentage and reset timestamp in the normalized quota window.
Account-usage and process-exit failures use the same bounded terminal-error projection as chat and
native sessions. A JSON diagnostic inside the native error string is redacted before composition.
The actual usage or exit error code remains unchanged. Raw terminal envelopes are not used in place
of an available safe terminal error.
The public account-usage path is live-qualified against source `0dacacd` and installed Antigravity
1.3.1 with its existing sign-in. Its standalone `/usage` report returns two weekly model-group
quota windows. Remaining percentages and reset timestamps match the native tab-separated report;
the complementary used fractions are derived from those percentages. Unreported plan, credits,
absolute limits, activity metrics and observation provenance are not manufactured. This read-only
check does not qualify passive generation observations, refresh or other CLI versions/platforms.

## Apple Foundation Models on device

`AddLlmConnect` registers `apple-foundation-models` with self-described capabilities and setup.
Use `LlmTransport.AppleFoundationModels`, endpoint `darkspyre-apple-fm` (or an absolute helper
path), model `apple-system-default` and no credential/headers. The host supplies the connection
through `ILlmConnectionSource` as usual. `Bind` uses the same adapter and semantic tool history.

Setup exposes the helper path, a local readiness check, local model discovery and an explicit
short generation test. It has no API-key, OAuth or CLI-login step and no potential API billing.
Apple Silicon, macOS 26+ and a ready Apple Intelligence system model are required. Provider
metadata remains discoverable on other platforms without loading native code. The runtime has
been exercised on macOS 26.7; oldest-OS and signed clean-machine certification remain release gates.
See the [helper build/test instructions and process limits](tools/AppleFoundationModelsHost/README.md).

Supported: text/instructions, user/assistant semantic history, streaming or one complete
`respond(to:)` response when `SupportsStreaming` is `Unsupported`, temperature in [0,1],
provider-enforced output caps, host-executed chat tools and existing managed agent sessions.
Each model operation owns one helper process, which starts and ends as
[Wrapped CLI processes](#wrapped-cli-processes) describes. Tools and persistence remain with the host.
System/developer messages inside history, prompt images/documents, cloud/PCC, custom weights,
account usage, reasoning controls and native resume/fork are unsupported. Put instructions in
`Instructions`. Full input occupancy and token usage remain unknown.

Tool choice supports Auto and None. Required/Specific and `AllowParallelToolCalls = false` fail
before generation; prompt wording cannot enforce them. None disables active tools while retaining
historical calls/results. Tool results use the existing text projection and omission reports.
Missing, duplicate or mismatched results fail before another generation. No hidden provider state
is needed. Model selection of tools is nondeterministic; multiple successive batches are handled
by the same managed loop and its host execution policy.

Structured output supports closed objects, required/optional nested fields, strings/booleans,
Int64/finite-double values and inclusive bounds, string enum/const, homogeneous arrays/count
bounds, and acyclic local `$defs` references (depth at most 64, at most 16,384 expanded nodes). Unsupported constraints fail explicitly. Explicit
`LocalValidation` fallback allows unconstrained text plus final validation; arbitrary JsonObject
fallback is unavailable. Original schemas always validate final JSON and returned tool arguments.

Structured streaming emits replacement `LlmStructuredOutputSnapshotEvent` objects, not JSON text
deltas. The agent equivalent carries thread/turn/item identity. Final `Text` contains the full JSON.
Apple does not report natural-stop versus truncation, so normal results use terminal
`LlmCompletionStatus.Unknown`; managed agents retain the response and end with reason/status
Unknown. Do not infer completion from valid JSON or automatically continue/retry an Unknown result.
Known tool handoff remains RequiresAction; refusal, cancellation and failures remain distinct.
`Diagnostics.HttpStatusCode = 200` is the existing process-adapter success sentinel, not an HTTP call.

The [design and verification record](docs/apple-foundation-models-design.md) lists runtime evidence
and remaining release qualifications. No helper binary, Apple SDK or upstream C-bridge source is
included in the Connect NuGet.

## SelfHost transport: in-process engines

`LlmTransport.SelfHost` runs a model that the host loads in its own process, such as a Tensor Qwen
engine. The application references `Darkspyre.Inference.Connect.SelfHost` and supplies `ILlmSelfHost`.
`AddLlmConnect()` discovers the included provider:

```csharp
services.AddLlmConnect();
services.AddSingleton<ILlmSelfHost, MyEngineHost>(); // the host's engine map
```

A SelfHost connection's `Endpoint` is an engine ID from `ILlmSelfHost.ListEnginesAsync`, and it has no
credential. Several connections can name one engine. Streaming, model listing and agent sessions go to
the engine returned by `GetInferenceAsync`, which is Common's `ILlmInference`; an engine that is unknown
or not loaded fails with `InferenceException` and `ModelUnavailable`. `GetCapabilities` projects the
loaded engine's capabilities for operations the adapter accepts, and does no I/O, because Connect reads
a transport's capabilities synchronously. Streaming, final streamed usage and running usage flags come
from the engine. Native file and caller-owned resource references remain unsupported; SelfHost reports
`SupportsFileReferences = InferenceSupport.Unsupported` even when the engine declares support. Binary prompt attachments
retain the engine's image/document capability gates. An unloaded engine declares no usage or tool support.

Connect keeps the connection's catalog in its shared model cache. A provider listing refreshes the
selected engine's catalog, so explicit Connect refresh also replaces any list the engine keeps.

Generation and embeddings reject nonempty connection or request `ProviderOptions`, connection
`AdditionalHeaders`, and connection or model capability overrides. SelfHost passes typed Common
inputs to the engine. An engine owns its capabilities; connection overrides do not change them.

Conversations open through `LlmAgentProviderService` and use the same `ManagedLlmAgentClient<TClient>`
opening, request normalization, context accounting and tool loop as Codex and the OpenAI API.
Unsupported sampling and output limits are omitted. Reasoning maps to an offered level. Unsupported
attachments leave the standard notice in the outgoing request. These operations never load an engine.
The caller owns model installation, loading and retirement through Tensor's separate lifecycle contracts.

Agent sessions run on the managed agent lane, which executes host tools; context estimates count the
request text with Connect's local tokenizer. Binary prompt attachments, structured tool content,
non-text tool parts and supported shell tools or history make the local measurement unknown.
Shell admission still uses the engine's capabilities and the existing ordered-history validator;
the local estimator does not project typed shell input through function-only helpers.
Known model limits remain available;
accounting neither runs the engine nor reports a text-only number as complete input usage.
Connect references Common only, never the engine library.
The settings offer an **Engine** choice listed from the host's engines and a **Model** choice listed
from that engine; both are required to save. Checking and testing are local verification.

## Console smoke tester

The OpenAI-compatible ConsoleTester accepts `--endpoint` for a local server:

```bash
echo 'Reply only with OK.' | dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  prompt --provider openai-compatible-api --endpoint http://gpu-box:8080/v1 --model qwen3.8-27b
```

`models` accepts the same provider and endpoint options. The override selects an
anonymous endpoint without reading or writing stored credentials. It is process-local and does
not persist a connection. The default endpoint retains its stored credential.
The invocation parser consumes `--endpoint` and its value before prompt and model option parsing.
The compatible provider does not support the tester's `agent` command.

`tools/Darkspyre.Inference.Connect.ConsoleTester` exercises the public Darkspyre.Inference.Connect.LLM facade directly without a shim or
consuming application. `providers` lists the registered adapters; `setup --provider <id>` displays
their own setup steps, fields, requirements and action effects without running those actions.
Canonical provider IDs and the short aliases `codex|claude|antigravity|bedrock|agentcore|apple`
are accepted. Adapter metadata owns names, endpoints and model defaults. Existing aliases retain
their credential-store identities. Credential entry/sign-in commands retain the subscription/AWS
flows described below; listing another adapter does not provision its credentials.
Codex uses host-owned device OAuth stored in `Darkspyre.SecureStore`; Claude and Antigravity keep
CLI-owned credentials. The tester also stores host-owned AWS profile/region configuration.
Agent completion diagnostics include both status and the actual completion reason. The tester
prints an existing completion error separately; it does not replace it with a generic status.
Prompt and agent diagnostics display actual hosted-call starts, progress phases, terminal status
and individual citation URLs on stderr. Model answer text remains on stdout. Both paths use the
same formatter. Missing call identity and terminal status display as `unknown`; a terminal call
does not imply successful tool execution. Diagnostics do not dump hosted input/output JSON or
generated image bytes.
Image-generation completions also report the actual typed image byte count, or `unknown` when
the payload is absent. This diagnostic reports payload presence, not pixel validity, dimensions
or a resource commit. It writes neither binary content nor base64 to either output stream.
The attachment-tool enum adds primitive string nodes through `JsonArray.Add(JsonNode)`. An explicit
node conversion prevents overload resolution from selecting the generic, trim-unsafe serializer.
The tool's required `kind` property and supported image/document values remain unchanged.
The self-contained macOS ARM64 tester currently reaches assembly trimming but fails with `IL2104`
for `Google.Apis.Core` 1.76.0 and `Newtonsoft.Json` 13.0.4. Warnings remain errors. This result does
not qualify a trimmed executable, NativeAOT, every Connect family or release archives.

### Apple console harness

From `Connect/`, build the separate development helper and inspect its provider-owned setup:

```bash
xcrun swift build --package-path tools/AppleFoundationModelsHost
APPLE_HELPER="$PWD/tools/AppleFoundationModelsHost/.build/debug/darkspyre-apple-fm"
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- providers
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- setup --provider apple
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- status --provider apple --helper "$APPLE_HELPER"
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- models --provider apple --helper "$APPLE_HELPER"
```

Apple requires no login or vault credential. `--helper` accepts an executable name on `PATH` or an
absolute path, and is valid only with Apple. Without it, the adapter's default executable is used.
`status` runs the settings manager's prerequisite check, with actionable helper/model readiness
results. `prompt` and `agent` also check readiness. Apple's model defaults to the adapter's
`apple-system-default`; an explicit `--model` is passed through normally.

```bash
echo 'Reply with hello.' | dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  prompt --provider apple --helper "$APPLE_HELPER"
echo 'Call darkspyre_inference_connect_echo with text hello and report the result.' | \
  dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- agent --provider apple --helper "$APPLE_HELPER"
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  apple-probe --provider apple --helper "$APPLE_HELPER" --repeat 2
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  apple-probe --provider apple --helper "$APPLE_HELPER" --case agent --repeat 5
```

`apple-probe` performs explicit synthetic on-device generation. Select `--case
all|text|structured|tools|agent|cancel` (default `all`), `--repeat 1..20` (default 1), and
`--timeout-seconds 1..600` per case (default 180). Cases execute sequentially; each repetition
starts fresh requests/sessions. There is no retry of a failed case. The managed agent is limited
to four model iterations and exposes only an in-memory synthetic lookup tool.

| Case | Checks |
| --- | --- |
| `text` | Complete and streamed text, terminal `Unknown`, no invented usage/finish reason |
| `structured` | Replacement snapshots without text deltas; final original-schema validation |
| `tools` | Two identical-argument calls with distinct IDs, reversed host results, replay with tools disabled |
| `agent` | One host lookup, settled result retention, no duplicate call-ID execution, final retained response |
| `cancel` | Cancel after a text chunk, await stream cleanup, then complete a new request |

Each case reports `PASS`, `FAIL` or `SKIP`, iteration and elapsed time, followed by totals.
Exit codes are 0 for all passed, 1 for any failure, 3 for unavailable prerequisites, and 130
for caller cancellation. A failed prerequisite operation returns failure rather than a skip.
Unknown termination is expected for Apple; it does not certify a natural stop. Missing snapshots,
unexpected tool selections and native continuation failures remain failures, even if later runs
pass. Reports omit prompts, model output, raw provider errors and credentials; agent failures
include the typed terminal reason and settled tool counts when available. Ctrl+C cancels the
active case and stops subsequent runs. Interactive `prompt` and `agent` commands return failure
on a failed or missing terminal event and accept Apple's normal `Unknown` terminal.

These live checks are separate from deterministic tests. In particular, successful runs do not
resolve the intermittent native continuation issue tracked in DSA-59. The helper's signing and
clean-machine/oldest-OS distribution qualifications remain open.

### Subscription and AWS smoke tests

Sign in to the direct ChatGPT adapter (CLI login alone is insufficient):

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  login --provider codex
```

Check the saved credential or load the provider's models:

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- status --provider codex
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- models --provider codex
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- usage --provider codex
```

The `usage` command formats the existing account snapshot through Connect-owned JSON metadata.
It preserves readable indentation, named quota states and null measurements without default
reflection serialization.

Stream a prompt through Darkspyre.Inference.Connect.LLM. Interactive use reads one line; redirected input can contain the complete prompt:

```bash
echo 'Hello' | dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  prompt --provider codex --model gpt-5.6-terra

echo 'Summarize the attached report.' | dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  prompt --provider claude --model sonnet --attach-document ./report.pdf
```

Smoke-test managed Codex. The tester exposes a harmless `darkspyre_inference_connect_echo` tool; host tool security remains the host's responsibility:

```bash
echo 'Call darkspyre_inference_connect_echo with text Hello, then reply with its result.' | dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  agent --provider codex --model gpt-5.6-terra
```

Run the bounded live scenarios (these consume model usage):

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- codex-probe --provider codex --model gpt-6-luna
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- managed-probe --provider codex --model gpt-6-luna
```

The first checks direct tools, attachments and state replay. The second checks the managed loop,
JSON capture/reopen, fork and low-cap host compaction. Use a model returned by `models`.
The `agent` command accepts `--max-model-iterations` as a positive integer. It passes that cap to
the existing managed execution policy. Omission keeps the library defaults. Other commands reject
the option, and native sessions reject managed execution options. A model iteration includes a
tool request or the model's answer after a tool result; the cap is not a count of all HTTP requests.
The `prompt` command accepts `--non-streaming` to select the provider's complete-response request.
Grok connection commands accept `--xai-region Global|UnitedStates`; the typed choice derives the fixed endpoint
and retains the same tester credential key. Route and streaming controls apply only to the selected
connection and participate in its version. These controls do not save or copy app settings.
`setup` rejects the region option because it renders a separate unsaved settings draft.
The managed tester persists captured contexts through `ConnectLlmJson.AgentContext` metadata.
Its round-trip retains document bytes, tool identities and resource-selection facts without
default reflection serialization.
[Recorded results](docs/managed-agent-verification.md) distinguish live proof from deterministic tests.

Pass `--attach-image path`, `--attach-document path`, `--effort <level>` (a reasoning level name such as `high` or `ExtraHigh`), or
`--schema path/to/schema.json` to exercise those request features. Attachment flags may be repeated.
Prompt text is sent to the tester over standard input and dispatched through the selected provider;
it is not placed in process arguments. Antigravity rejects attachment options because its headless
protocol is text-only.

The tester reports an SDK failure with its typed error code and public message, then exits with code 1.
Probe failures use the same typed SDK/provider error formatting as ordinary commands. They retain
public error codes and explanations. Probes report only the type of other exceptions.
Antigravity turn probes retain the actual non-success terminal status, reason and public error.
They stop that case before another turn is sent. A stream without terminal completion still fails
its existing completion check.
The tester does not print arbitrary inner exception text, response bodies or stack traces. Local request refusals
retain their explanation, including the reasoning levels the selected model accepts.

`grok-probe --provider grok-subscription` explicitly checks `strict`, `files`, `tts`, `batch`, `batch-list`
or `deferred`, or every case with `--case all`. It uses the existing probe model/repeat/deadline
options and typed `--xai-region`.
Strict checks use one original closed object schema, validate the returned required enum argument,
permit at most two managed model iterations and require the successful callback's unique result
as the completed answer. This is not qualification of arbitrary strict schemas or regex guarantees.
Files checks upload one synthetic UTF-8 file with a requested one-hour expiry, get its identity,
check one newest-files page, download and compare its exact original bytes, and delete only the
ID returned by that upload. Cleanup uses a separate wait with the case's selected deadline and
preserves independent operation and cleanup failures. An upload without a returned receipt leaves
remote creation unknown; no guessed ID, filename search deletion, retry or rollback is performed.
The checks use the existing agent and Files facades and do not copy application credentials.
TTS uses the same selected connection access and LLM core through `XaiFamilyBinding`. It lists
actual voices and requests one three-word English WAV utterance with the first returned voice.
It requires nonempty audio and an audio media type. It does not play or persist the result,
or qualify STT, WebSocket speech or Realtime.
Batch creates one text row with a 64-token allowance for its marker reply. It gets the actual
batch, reads one metadata page and one result page, and retains at most one MiB of inline response.
Metadata IDs remain provider IDs; result correlation uses the submitted CustomId. Completed output
must match the marker. An empty result page reports `SKIP` and exit 3, not successful completion
or a fabricated pending state.
`batch-list` reads one page with limit one. It requires an actual receipt, reports whether its
creation instant is known, and reports an empty page as unqualified. It sends no generation or mutation, performs no
cleanup, and does not infer ownership of an existing batch from its name.
The explicit `batch-list` selection prints only the JSON types and string lengths of known receipt
fields from that page and whether a timestamp is a valid calendar date. It prints no field values,
provider body, headers, credential or URL.
Cleanup requests cancellation only for its own receipt when the observed pending count is nonzero
or unknown. A cancellation acknowledgement does not prove terminal cleanup. Cleanup uses the
case's selected timeout and retains both the operation and cancellation failure if both occur.
Deferred submits one short marker request and performs exactly one consuming retrieval. It prints
the actual receipt ID. Pending reports `SKIP` and exit 3 with completion explicitly unqualified;
the tester does not poll or retry. A completed response must retain the exact marker.
Resource failures report actual typed HTTP/request facts and safe typed causes. Arbitrary cause
messages remain omitted. Missing authentication skips the case; provider access failures remain
visible failures and do not become passes. These checks reuse existing facades and add no SDK contract.

Live Global/Default checks at source `1ced77f` use the tester's saved OAuth without another sign-in
or a retry. TTS passes in 16.25 seconds after listing actual voices and returning 61,004 WAV bytes.
Batch creation returns HTTP 200 but fails before a batch receipt is parsed. Remote creation remains
unknown; no repeat creation or guessed cleanup occurs. The deferred check receives an actual receipt
and one retrieval returns typed pending in 1.37 seconds. It reports `SKIP`, not completed generation.
These observations do not qualify batch results, deferred completion, STT or Realtime.
At source `1efcc21`, one Global/Default read-only batch page passes in 0.78 seconds with the saved
credential. Its actual creation and expiry fields are valid calendar dates; the receipt retains
unknown instants. No generation, mutation, cleanup or retry occurs. This qualifies that receipt
decoding, not batch creation/add/results, every timestamp variant or the lost earlier creation payload.
A new one-row Global/Default batch using the same corrected source returns its native receipt,
accepts one add, and completes get/request listing. Its single result page is empty, so the case
reports `SKIP` in 3.57 seconds and acknowledges cancellation of only its returned batch.
No further page, poll or retry occurs. This qualifies those creation/submission/metadata paths,
not completed results or terminal cancellation. The earlier unparsed creation remains unrecovered.

Live Global/Default qualification at source `1418ba1` passes both cases with the tester's
existing OAuth credential and no retries. The strict case selects
`grok-4.20-0309-non-reasoning`, validates its original argument, completes one callback and returns
the exact unique callback result in 3.17 seconds. Two distinct model-step observations total
676 input, 45 output and 721 tokens, with provider-reported cost of 0.0006215000 USD.
That cost observation does not establish the account's actual billing. The Files case completes
in 2.13 seconds, verifies all 89 original bytes and confirms deletion of its exact upload.
These results do not qualify arbitrary strict schemas, regex semantics, public URLs, every page,
expiry enforcement, cross-provider resource transfer or credential refresh.

One public nonstreaming prompt at source `d26da43`, Global/Default and
`grok-4.20-0309-non-reasoning` returns exactly `CONNECT_GROK_COMPLETE_OK` with
`Completed/completed`. Actual usage reports 198 input, 6 output and 204 total tokens, with
128 cached input tokens and provider-reported cost of 0.0001281000 USD. The connection selects
`SupportsStreaming = InferenceSupport.Unsupported`; the adapter sends the native complete Responses request.
This qualifies the selected plain complete route, not native response schemas, all models,
credential refresh or actual account billing. The call does not retry or copy application credentials.

At source `990ae40`, the public request-file command checks one original closed object schema with a
required string enum on the same Global/Default model. Streaming and native nonstreaming each return
the exact `CONNECT_GROK_SCHEMA_121` marker, `Completed/completed` and
`ProviderJsonSchema` with `IsValid = true`. The cap is 128 visible output tokens. Streaming reports
259 input, 15 output and 274 total tokens, with 192 cached input tokens and cost of 0.0001596500 USD.
Nonstreaming reports 259 input, 12 output and 271 total tokens, with 256 cached input tokens and cost
of 0.0000849500 USD. Both report zero reasoning tokens. This qualifies that schema and selected route,
not arbitrary schemas, regex guarantees, cap enforcement, credential refresh or actual billing.
The calls use the tester's saved OAuth without retries or application credential copies.

One streamed hosted web-search request at source `4d7f4eb`, Global/Default and the same model,
uses the existing request-file command with `docs.x.ai` as the only allowed domain. Caller budgets
are two hosted turns and 128 output tokens. Connect surfaces one matching WebSearch start/completion,
five citation observations and `Completed/completed`. Actual usage reports 3874 input, 49 output
and 3923 total tokens, one WebSearch call, one total hosted call and cost of 0.0099650000 USD.
The provider reports zero sources used separately from those five citations; the adapter reads that
native usage field without deriving a count from citations. This qualifies the selected hosted search,
not other hosted tools, domain-filter enforcement, output-cap enforcement, billing or every model.

One streamed hosted code-execution request at source `33ddb0c` uses the same selected model and
Global/Default route to evaluate `sum([2, 3, 5])`. Caller budgets are two hosted turns and 128
visible output tokens. Connect surfaces one matching CodeExecution start/completion, answer `10`
and `Completed/completed`. Actual usage reports 997 input, 21 output and 1018 total tokens,
one CodeExecution call, one total hosted call and cost of 0.0062987500 USD. The request does not
retry, read local files or copy credentials. This qualifies the selected synthetic code-execution
route, not arbitrary remote execution, generated artifacts, output-cap enforcement or billing.

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- grok-probe --provider grok-subscription --case strict --repeat 1 --timeout-seconds 60
```

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- grok-probe --provider grok-subscription --case files --repeat 1 --timeout-seconds 60
```

Claude uses the same commands with `--provider claude`:

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- login --provider claude
```

Claude subscription prompts require the official Claude CLI to be installed and signed in. The
tester creates an `AnthropicClaudeCli` connection with `Credential = null`; Darkspyre.Inference.Connect.LLM does not read
or store the CLI credential. `models --provider claude` discovers native choices through the CLI's
initialization response without an HTTP catalog credential or generation prompt. A prompt accepts
`sonnet`, `opus`, another returned choice, or a manually entered exact model ID.

`claude-probe` uses the shared native CLI probe for `cancel`. Cancellation stops after the first
text delta and requires caller cancellation plus successful owned-session disposal. It does not claim
remote work termination or an unusable session after interruption. The case uses Claude's read-only
host runtime policy and no host tools. Native qualification passes on installed Claude CLI 2.1.289 with
the selected `sonnet` alias. Cancellation reaches the first text delta, surfaces caller cancellation and
completes owned session disposal. This qualifies the named case, not credential refresh, remote work
termination, every model or other CLI platforms.

The Antigravity native resume case at source `ef626b8` passes on Antigravity CLI 1.3.1 with
`claude-sonnet-4-6`. Two completed turns retain the exact conversation ID across process replacement
and recall the synthetic marker. The case passes in 18.23 seconds with two turn attempts, no retry
and no skipped prerequisite. This qualifies that selected model's native resume, not GPT-OSS
capacity, every model, OAuth refresh or other platforms.

The existing `agent` command at source `7a2204d`, installed Claude CLI 2.1.289, `sonnet` and
Low effort completes one document tool-result turn in an empty scratch working directory.
The attachment callback completes with two output parts and no tool error. The final answer is
exactly `Darkspyre.Inference.Connect.LLM attachment smoke test document.` The turn reports
`Completed` and usage of 3621 input, 105 output and 3726 total tokens. The observed tool events
name only the attachment tool. This qualifies that synthetic document result, not strict tools,
Files uploads, logical resource references, cross-provider transfer or other models and platforms.
The tester uses its existing read-only host policy and does not copy CLI credentials or retry.

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- claude-probe --provider claude --model sonnet --case cancel --repeat 1 --timeout-seconds 90
```

Antigravity uses `--provider antigravity` (or `agy`). Run `agy` interactively once to sign in; the
tester does not request or store a Google authorization code. `agy-probe` runs explicit live cases
through the native session: `text`, `structured`, `native-tools`, `session`, `resume`, `controls` and
`cancel`. It works in a temporary workspace with one fixture, reports the CLI version and the number
of turn attempts, and never retries. The attempt counter increments before dispatch and does not
prove that a prompt reaches the provider. The `usage` command is available for all three subscription providers:

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- usage --provider claude
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- usage --provider antigravity
```

Configure Bedrock with a named AWS profile and region. Only those identifiers are stored in SecureStore; AWS credentials remain in the standard AWS credential chain:

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  login --provider bedrock --profile brandon-cli --region us-east-1
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- models --provider bedrock
echo 'Hello' | dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  prompt --provider bedrock --model amazon.nova-micro-v1:0
```

AgentCore uses the same profile/region storage. Pass the deployed Harness ARN on every console
invocation because this small tester deliberately does not own a connection database:

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  login --provider agentcore --harness-arn "$HARNESS_ARN" \
  --profile brandon-cli --region us-east-1

echo 'Call darkspyre_inference_connect_echo with text Hello, then reply with its result.' | \
  dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  agent --provider agentcore --harness-arn "$HARNESS_ARN" --model default
```

The AgentCore tester exposes only `darkspyre_inference_connect_echo`: Harness prompt and tool-result attachments are
unsupported by the current AWS wire contract.

## Verification

```bash
dotnet restore Darkspyre.Inference.Connect.slnx
dotnet build Darkspyre.Inference.Connect.slnx --no-restore
dotnet test Darkspyre.Inference.Connect.slnx --no-build --no-restore
dotnet pack Darkspyre.Inference.Connect.slnx -c Release --no-restore
dotnet list Darkspyre.Inference.Connect.slnx package --vulnerable --include-transitive
```

Formatting is optional reviewed cleanup, not a development or release gate:

```bash
dotnet format Darkspyre.Inference.Connect.slnx --no-restore
```

Releases are immutable tags such as `v11.3.1-rc.1`. `Common`, `Connect`, `Tensor`, and `UI.Settings` are built and
published in tandem from the approved local release environment to the private GitHub Packages feed;
GitHub Actions is not the release pipeline.
