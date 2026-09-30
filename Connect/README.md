# Darkspyre.Inference.Connect

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

The package also includes `Darkspyre.Inference.Connect.TTS` for external
PocketTTS HTTP synthesis and `Darkspyre.Inference.Connect.Audio` for external TangoFlux generation.
They are included in the Connect product project, not separate NuGets. See
[PocketTTS integration](#pockettts-speech) and
[TangoFlux integration](#tangoflux-audio). The coordinated package version is **11.14.0-rc.1**.

The packages target .NET 11. Credentials
for direct API transports remain owned by the host through `ILlmConnectionSource`; the connector
never owns an application database or secret store. Wrapped CLI transports instead use the official
CLI's existing login, which Darkspyre.Inference.Connect.LLM never reads or copies. Optional Claude catalog OAuth is a
separate model-discovery credential; it never selects or enables the Claude CLI transport.

Every returned `LlmConnection` includes a required opaque `Version`. The version must cover both
connection metadata and credential state. `SaveCredentialAsync` must atomically compare
`expectedVersion` and replace only the credential when it still matches; it must reject changed,
replaced, or deleted connections and must never upsert. A separate read/check/write sequence is not
sufficient because delayed OAuth completion or refresh could otherwise overwrite a concurrent provider
change.

## Packages

The coordinated release contains four NuGets. They always use the same version from the repository
`Directory.Build.props` and publish together. Version numbers use
`<net>.<release>.<minor>`: .NET generation, breaking-contract release, and compatible tweak/fix.
The direct Codex and host-owned managed-agent behavior from the former `Mathis.LlmConnect` 3.0.0 is
retained; Claude and AgentCore retain provider-owned sessions.
See the [Agent DS handoff](docs/agent-ds-contract-handoff.md),
[technical design](docs/enhancements.md), and [verification record](docs/managed-agent-verification.md).

The Apple on-device adapter is registered by `AddLlmConnect`. Its separately built Swift
helper and release gates are described in the [connector design](docs/apple-foundation-models-design.md).
This source implementation has not been published as a signed helper release.

Google Vertex AI support for Gemini and Claude is implemented in source under DSA-61.
Register `AddLlmConnectVertex()` alongside `AddLlmConnect()` and supply a typed `LlmConnection.Vertex`
with project, location and authentication mode. The adapter assembly is included by the Connect
package project. See the [technical mapping](docs/google-vertex-ai-tech-design.md) for reviewed model
profiles and limits. Live cloud qualification remains; the shared contract requires host migration.

The Gemini API (Google AI Studio) adapter is registered by `AddLlmConnect` under the provider id
`gemini-api` and transport `GoogleGeminiApi`. It needs only an API key, sent in the `x-goog-api-key`
header, and a fixed endpoint. It shares the Gemini request, stream, schema and continuation code with the
Vertex Gemini route, so both send the same generateContent body. See the
[Gemini API design](docs/gemini-api-tech-design.md) for model discovery, capabilities and limits. Live
qualification remains.

### Contract migration

Continuation state is now Common's opaque `LlmContinuationState`, carried by both portable and
Connect messages/results as `ContinuationState`. Captures and envelopes use format 2; old
`ResponseState` JSON members and unversioned captures are rejected. Hosts must migrate stored records
before adoption; see [DSA-68 handoff](docs/agent-ds-contract-handoff.md). Explicit state omission is
reported by `OmittedContinuationStateCount`; default handling rejects unbound state.

`LlmToolCapabilities.StrictInputSchema` reports Supported, Unsupported or Unknown. Unknown allows an
explicit `Strict=true` request unchanged so the host can try it and handle the provider result.
Known Unsupported rejects that request. Ordinary tools default to `Strict=false` independently.
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
| `Darkspyre.Inference.UI.Settings` | Host-placed Blazor/Lumeo editor using only Common setup contracts |

Connect, Tensor and UI.Settings depend on the exact synchronized Common NuGet; none carries
another product's DLLs. UI.Settings does not depend on Connect or Tensor. The release-candidate
suffix reflects the .NET 11 RC ASP.NET dependency, including on the private package feed.

There is no `Darkspyre.Inference.Connect.Common` package or assembly. Connector-only endpoint,
authentication, provider-management, and job contracts remain in their capability assemblies. Only
semantic inference contracts that are also implemented by Tensor belong in
`Darkspyre.Inference.Common`.

Connect's chat lane uses Common's LLM primitives directly: the stream events, tool definitions, tool
choice, tool calls, tool results with `LlmToolOutput`, attachments, structured-output types, completion
status and `LlmUsage`, all in `Darkspyre.Inference.Common.LLM`. Connect adds only what carries
Connect-specific data: `LlmChatMessage` and `LlmChatResult` with provider diagnostics and the shared continuation envelope,
`LlmChatCompletionEvent` for the final result, and `LlmModel` with provider capabilities. An attachment's
`MediaType` is used as given; without one, the adapter detects the type from the bytes.

## Portable inference lane

Bind a configured Connect implementation when an application wants to select Connect or Tensor
without changing its request and result types:

```csharp
ILlmInference llm = providerService.Bind(connectionId);
LlmInferenceResult result = await llm.CompleteAsync(new LlmInferenceRequest
{
    ModelId = modelId,
    Messages = [new LlmMessage("user", prompt)]
}, cancellationToken);
```

Image workflows expose the same boundary through `ImageProviderService.Bind(connection, workflow)`.
Binding is explicit and retains exactly one backend. Neither adapter retries through a different
provider nor falls back from Connect to Tensor.

### Reasoning levels

`LlmInferenceRequest.ReasoningLevel` takes an `LlmReasoningLevel`: `None`, `Minimal`, `Low`, `Medium`,
`High`, `ExtraHigh` or `Maximum`. On one model, each level asks for at least as much reasoning as the
level below it; a level does not cost the same on two different models. Null leaves the provider's
default in place. Each model reports the levels it accepts in `LlmCapabilities.ReasoningLevels`, and
`SupportsReasoning` is true when that set contains a level above `None`. A request carrying a level
the model does not accept fails with `InferenceFailureCode.InvalidRequest` before any provider call;
no adapter substitutes a nearby level. Each adapter owns the mapping to its provider's wire form.
`LlmCapabilityOverrides.ReasoningLevels` replaces a transport's set for a connection or one model,
and the setup editor exposes it as one Default/Enabled/Disabled choice per level. The design is
`docs/llm-reasoning-level.tech-design.md` in the repository root.

Anthropic API-key, Gemini API and ChatGPT subscription connections read each model's levels from the
provider's model catalog, which `LlmModelListCache` keeps. The first capability query or generation on a
new connection version therefore lists the connection's models once. When that listing fails, the adapter
uses its own model-family table and the operation proceeds.

OpenAI Responses levels are per model, from a table of verified model pages, because the OpenAI Models
API reports no capabilities. `None` sends `reasoning.effort: "none"` and `Maximum` sends `"max"`. A
verified reasoning model accepts `temperature` only with `ReasoningLevel = None`; a request without a
level counts as reasoning, because these models default to effort `medium`. A model without a table row
offers `Minimal` to `ExtraHigh` and accepts temperature at any level.

## PocketTTS speech

`ITtsClient` accepts `TtsRequest` and returns `TtsResult` with WAV bytes and provenance. Register
`services.AddTtsConnect()`, then bind a host-supplied `TtsConnection` with `TtsProviderService`:

```csharp
ITtsClient speech = ttsProviders.Bind(new TtsConnection
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
`voice`, `speaker`, `speed` and `response_format`. Initially only WAV is advertised and accepted.
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

## TangoFlux audio

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

Call the provider facade with an opaque connection ID and a model selected by the host:

```csharp
var result = await provider.CompleteAsync(
    connectionId,
    new LlmChatRequest
    {
        ModelId = modelId,
        Instructions = "Return a concise answer.",
        Messages = [new LlmChatMessage("user", prompt)]
    },
    cancellationToken);
```

### Model lists

`LlmProviderService.ListModelsAsync` keeps each connection's model list in `LlmModelListCache`, which
`AddLlmConnect` registers once for the registration owner's lifetime. A list stays until the connection's `Version` changes,
which every connection or credential edit does, or until `RefreshModelsAsync` lists again and replaces it.
Concurrent requests for one connection share a single listing, request accounting and managed sessions
read the same list, and a failed listing is not kept. Common's `ILlmInference` carries the same pair:
`ListModelsAsync` may answer from a kept list, and `RefreshModelsAsync` lists again. A service built by
hand without the shared cache keeps its own.

`LlmProviderService` and `LlmModelListCache` implement `IDisposable` and `IAsyncDisposable`. Facade
retirement fences new calls, cancels active calls and waits for them to leave. It retires a privately
created cache, but not an injected shared cache. Clients, authenticators and the connection source
remain borrowed and are not disposed by the facade.

Cache retirement clears kept models, cancels provider listings through an owner token, and waits
for every listing, including entries replaced by an edit or refresh. A cancelled caller only stops
its own wait. A provider that ignores owner cancellation keeps retirement pending until its call
ends; its late result is not published. Abandoned provider faults are observed.

Use asynchronous disposal while provider work is active. Consume or dispose active stream
enumerators so their owned operations can leave. Disposal does not report completion while an
operation remains active. A registration owner disposes its shared cache; sharing a cache across
containers does not transfer that responsibility to a scoped facade.

### Embedding lifetime

`LlmEmbeddingProviderService` implements `IDisposable` and `IAsyncDisposable`. Its registration owner
retires the facade. Retirement fences new requests, cancels connection lookup and provider calls,
and awaits all active requests before releasing its adapter references. Supplied adapters and the
connection source remain borrowed and are not disposed by the facade. Caller cancellation affects
only that request. A provider that ignores cancellation keeps retirement pending; its late result
is rejected. Use asynchronous scope disposal while embedding requests are active.

### Agent-session lifetime

`LlmAgentProviderService` implements `IDisposable` and `IAsyncDisposable`. It owns sessions returned
by start, resume and fork. Facade retirement fences new openings, cancels active openings and
retires every returned session. Existing sessions retire without waiting for another opening to
finish. A session that finishes opening after cancellation is disposed before its opening call
ends. Supplied clients and the connection source remain borrowed.

Each returned session fences its methods on retirement, cancels and drains active calls and stream
enumerations, then disposes its adapter session. Concurrent callers share one retirement completion.
Disposing one session leaves its siblings and the facade usable. Caller cancellation affects only
the named operation. Late results and events from cancelled work are rejected. A failed session
retirement remains visible when its owning facade retires; another session is not skipped.

Use asynchronous scope disposal while sessions are active. Consume or dispose active enumerators
so their owned calls can leave; an enumerator suspended at a yielded event still owns a call.
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

Every SDK call holds an owner lease. Streaming leases cover response reading and disposal, not
just response creation. Owner retirement fences new acquisition, cancels active calls, waits for
their leases, and disposes every cached client and explicitly resolved disposable credential.
Response cleanup failures remain visible to the SDK owner. One disposal failure does not skip
another resource. Logging verification also fences and drains its lookups and clears its cache.
Use asynchronous container disposal while work is active.

Clients use a private default AWS credential resolver and SDK-client-local HTTP caches. Retiring
the SDK owner disposes the resolver and the SDK pipelines' HTTP pools. Runtime and AgentCore HTTP
clients have infinite generation timeouts; control-plane clients retain their SDK timeout.
An adapter that ignores cancellation keeps retirement pending until its call leaves. Consume or
dispose open streaming responses and enumerators so their leases can leave.

### Streaming, reasoning text and usage

`LlmProviderCapabilities.SupportsStreaming` says whether the adapter streams provider output as the model
generates it. `LlmCapabilityOverrides.SupportsStreaming = false` selects the provider's non-streaming request:
the adapter sends it and emits the complete response as events after it arrives. A transport whose provider
has no non-streaming request rejects every request while streaming is off. Common's
`LlmCapabilities.SupportsStreaming` carries the same flag to `ILlmInference` hosts.

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
when display is enabled. The file is removed with the call's working directory on completion,
cancellation, failure or reader disposal. Authentication probes and non-streaming calls receive no
summary settings file. The setting requests summaries; it does not guarantee that the CLI returns them.

Anthropic Messages and Vertex Claude share model profiles. Positive adaptive reasoning requests
`thinking.display = summarized` on families whose default display is omitted. A null level requests
only that display with adaptive thinking on the named default-thinking Opus 5, Sonnet 5, Fable and
Mythos families; it invents no effort or budget. Explicit `None`, budget models, effort-only models
and older adaptive models retain their existing controls. Disabling display omits the summary request
without changing explicit reasoning controls. Catalog evidence remains authoritative for thinking
support. The qualified Vertex Sonnet 4.6 request body receives no new display field.

Claude profiles reject temperature and forced tool choice while adaptive or budget thinking is
active, including a profile's default thinking. Profiles that prohibit forced tool choice reject it
at every level. Explicitly disabled thinking and effort-only reasoning keep the model's supported
sampling/tool controls. Anthropic Messages, Vertex Claude and Bedrock Claude use this validation.

Anthropic Messages and Vertex Claude report `pause_turn` as `Incomplete` with its native reason and
opaque continuation state. Ordered server-tool calls, their result blocks, caller metadata and
citations remain replay data; they do not become host tool actions. Streaming retains partial
server-tool argument deltas before constructing that state. Calls from native code execution retain
their `caller` metadata, including client calls that still require a tool result. A caller replays a
paused assistant message with its unchanged text and continuation state. Vertex accepts that opaque replay without
allowing ordinary assistant prefilling. Unknown stop reasons stay Unknown;
`model_context_window_exceeded` and `max_tokens` are Incomplete without paused-turn state.

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
provider counting. Typed tool choice and parallel controls apply to the combined names. Raw `tools`
and `tool_choice` options fail even when the typed fields are absent. Vertex Gemini rejects this option.

Native definitions accept the documented web search/fetch, code execution, tool search and advisor
versions. Unknown types, client-executed native built-ins, duplicate names and native schema/strict
overrides fail locally. Other native configuration stays opaque and unchanged; provider errors
determine route/model availability and tool-specific parameter support. The adapter never rewrites
definitions, retries an invalid configuration or switches tools. Advisor results, including encrypted
advice, remain opaque ordered continuation content, not public reasoning or host tool events.
Beta tools require the caller's provider beta header; the adapter does not enable paid tools implicitly.

Client `allowed_callers` accepts nonempty unique lists of `direct`, `code_execution_20260120` and
`code_execution_20260521`. A programmatic client requires a declared code-execution tool at either
of these two versions. It cannot be strict or disable parallel tool use. A forced client choice
must allow direct calling. Deferred clients require a declared tool-search tool. These checks do
not establish live tool provisioning, complete tool-specific compiler validation or correct timing
of programmatic continuation results. Provider restrictions still apply to retained-container
continuation and the content of tool-result messages. See the
[programmatic calling contract](https://platform.claude.com/docs/en/agents-and-tools/tool-use/programmatic-tool-calling).

Managed sessions retain an Incomplete reply that carries continuation state and no host tool calls.
They emit its message, finish the turn with Truncation, and preserve the state through capture/import
for a caller-directed later turn. They do not automatically retry or execute server tools.

`SupportsStreamingUsage` describes usage reported by completion. `SupportsUsageDeltas` describes
running `LlmUsageDeltaEvent` observations and is mirrored on Common's capabilities. Each observation
replaces the previous value for the entire call. Null counts remain unknown. Duplicate observations
are suppressed. Only completion usage is authoritative, including when it corrects an earlier value.
Anthropic and Vertex Claude report observations from `message_start` and `message_delta`. Gemini
and Vertex Gemini report normalized `usageMetadata`, including reasoning output tokens.
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

| Transport | Streams | With `SupportsStreaming = false` |
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

Cancellation and disposal send `SIGKILL` to the process group. Every descendant that stays in the group
ends with the process. A descendant that calls `setsid` or `setpgid` leaves the group, and the kill does
not reach it. On Windows, cancellation and disposal kill the process tree.

After the kill, Connect waits at most 5 seconds for the runtime to report the exit. When the bound
expires, the operation fails with a message that names the process ID:

| Lane | Failure |
| --- | --- |
| Claude CLI chat | `LlmProviderException` with `ProviderCode` `claude_cli_exit_timeout` |
| Antigravity CLI chat | `LlmProviderException` with `ProviderCode` `antigravity_cli_exit_timeout` |
| Antigravity native agent session | The cancelled operation keeps its own outcome, and the session faults. Later calls throw `InvalidOperationException` with the reason. |
| Apple helper | `InferenceException` with `InferenceFailureCode.ExecutionFailed` |

Claude CLI chat owns the process during stdin writes as well as output reads. Cancellation during a
blocked stdin write and early disposal of its output stream both terminate the owned process.
Its stderr reader enforces a 16 MiB character limit while reading. A stderr read failure interrupts
blocked stdin/stdout operations. Retirement cancels and observes that reader with a 5-second bound.

Claude and Antigravity share one launcher for chat and native sessions. Their default commands are
`claude` and `agy`. On Windows the launcher searches absolute PATH directories in order for `.exe`,
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

Launcher tests exercise planning with filesystem fixtures and actual managed argument round trips.
Windows batch execution, cancellation and installed-Node round trips are conditional tests. A
non-Windows run reports them as skipped; it does not qualify Windows invocation.

## Image provider integration

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

PreparedImageTransportRequest prepared = transport.Prepare(new ImageTransportPreparationRequest
{
    Workflow = workflow,
    PositivePrompt = prompt,
    NegativePrompt = negativePrompt,
    Width = 1216,
    Height = 832,
    Seed = seed,
    CacheKey = cacheKey,
    Reference = reference
});

string providerJobId = await transport.SubmitAsync(connection, prepared, cancellationToken);
ImageTransportJobSnapshot status = await transport.GetJobAsync(
    connection,
    providerJobId,
    workflow,
    cancellationToken);
```

`ImageWorkflow.TransportConfiguration`, `PreparedImageTransportRequest.ProviderRequest`, and
`ImageTransportOutput.ProviderState` are opaque adapter-owned data. Consumers may round-trip them for
durable recovery but must not interpret them as ComfyUI contracts. The package does not own an image
database, media library, workflow catalog, cache, or background worker.

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
| OpenAI Responses | Yes | No | Yes | Yes |
| OpenAI-compatible Chat Completions | Yes | No | No | No |
| ChatGPT Codex subscription | Yes | Yes | Yes | Yes |
| Anthropic Messages with API key | Yes | No | Yes | Yes |
| Claude subscription via official CLI | No | Yes | Yes | Yes |
| AWS Bedrock Converse | Yes | Yes | Yes | Yes |
| AWS Bedrock AgentCore Harness | No | Yes | No | No |
| Google Vertex AI (Gemini and Claude) | Yes | Yes | Yes | Yes |
| Gemini API (Google AI Studio) | Yes | Yes | Yes | Yes |
| Antigravity CLI | No | No | No | No |

Tool-result content support is declared per route and lane; see [Tool output](#tool-output).

Define tools on the request and branch on `RequiresAction`:

```csharp
var weatherTool = new LlmToolDefinition
{
    Name = "get_weather",
    Description = "Get the current weather for a city.",
    InputSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            city = new { type = "string" }
        },
        required = new[] { "city" },
        additionalProperties = false
    })
};

LlmChatResult result = await provider.CompleteAsync(
    connectionId,
    new LlmChatRequest
    {
        ModelId = modelId,
        Messages = [new LlmChatMessage("user", "What is the weather in Chicago?")],
        Tools = [weatherTool]
    },
    cancellationToken);

if (result.Status == LlmCompletionStatus.RequiresAction)
{
    LlmToolCall call = AssertSingleAuthorizedCall(result.ToolCalls);
    string toolOutput = await ExecuteWeatherToolAsync(call.ArgumentsJson, cancellationToken);

    result = await provider.CompleteAsync(
        connectionId,
        new LlmChatRequest
        {
            ModelId = modelId,
            Messages =
            [
                new LlmChatMessage("user", "What is the weather in Chicago?"),
                new LlmChatMessage("assistant", result.Text)
                {
                    ToolCalls = result.ToolCalls,
                    ContinuationState = result.ContinuationState
                },
                new LlmChatMessage("user", string.Empty)
                {
                    ToolResults = [new LlmToolResult(call.Id, LlmToolOutput.FromText(toolOutput))]
                }
            ],
            Tools = [weatherTool]
        },
        cancellationToken);
}
```

During streaming, tool calls arrive as `LlmToolCallStartedEvent`, one or more `LlmToolCallArgumentsDeltaEvent` values, and `LlmToolCallCompletedEvent`. The terminal `LlmChatCompletionEvent` repeats the complete calls in `LlmChatResult.ToolCalls`. `ArgumentsJson` is deliberately raw JSON; the host should deserialize it into its own validated input type before execution.

### 2.0 attachment migration

Version 2.0 replaces `LlmToolAttachment` with `LlmAttachment`, removes caller-supplied
`MediaType`, and adds `Attachments` to `LlmChatMessage` and `LlmAgentTurnRequest`. Replace the old
type name, delete `MediaType` initializers, and gate prompt inputs with `SupportsPromptImages` or
`SupportsPromptDocuments`.

Prompt messages carry attachments as a provider-neutral binary value. The host supplies only the
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

### Tool output

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
with `SupportsToolResultImages` or `SupportsToolResultDocuments` set to `false` lowers those carriages.
An override never raises a carriage.

| Route and lane | Images | PDF | Text docs | Audio | Links | Embedded text | Structured | Error flag |
|---|---|---|---|---|---|---|---|---|
| Direct Codex (chat and agent), OpenAI Responses | Native | Native | Native | Unsupported | Text | Text | Text | Text |
| Anthropic Messages | Native | Native | Native | Unsupported | Text | Text | Text | Native |
| Bedrock Converse | Native | Native | Native | Unsupported | Text | Text | Native | Native |
| Claude CLI agent | Native | Unsupported | Text | Unsupported | Native | Native | Text | Native |
| AgentCore Harness | Unsupported | Unsupported | Text | Unsupported | Text | Text | Text | Native |
| OpenAI-compatible, Claude CLI chat, Antigravity CLI | Unsupported | Unsupported | Text | Unsupported | Text | Text | Text | Text |

Every route omits other binary content and keeps the supplied order. Direct Codex and the Claude CLI
agent rows are measured; the other rows follow each provider's protocol and are not live-verified.

The text renderings are fixed. A resource link becomes `[Resource link: name] uri`. An embedded text
resource becomes `[Resource: uri]` followed by its text. A text document becomes `[File: name]`
followed by its text, or an embedded text resource where the route carries those natively. Structured
content becomes its JSON as the final text part. A converted error flag becomes a leading
`[Tool error]` text part.

`LlmProviderService.ProjectToolOutputAsync` reports what one output sends for a connection, model and
lane, without a network call. Each part is `Delivered`, `Converted` to text, or `Omitted` with a
reason: `UnsupportedByRoute`, `UnsupportedByModel`, `InvalidContent` or `Empty`. Invalid content is
structured content that is not a JSON object, a resource with both a text and a byte body, text that is
not UTF-8, or a part over `MaxBinaryBytes`. Adapters serialize through the same projection, so the
report matches the request. An omitted part never fails the request, the turn or the tool, and never
changes `IsError`. Connect adds no notice for an omitted part. A host that wants one adds a text part
and projects again.

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

### Carrying history to another model or provider

A conversation that moves to another model or provider starts a new provider-side conversation from
the host's retained history. Set `UnboundContinuationState = LlmUnboundContinuationState.Omit` on those
requests. Each message whose state does not bind to the target transport, model and account is sent
without it; its visible text, tool calls, tool results and attachments are kept. The result's
`OmittedContinuationStateCount` reports how many messages were sent without state. The default,
`Reject`, fails before any request. The caller's messages are not modified, so the original state
stays in host storage.

Claude CLI chat and Antigravity CLI chat report `LlmProviderCapabilities.ToolHistory = Text`. Tool
calls and results already in the history reach their prompt as `[TOOL CALL id=… name=…]` and
`[TOOL RESULT id=… status=ok|error]` blocks. Offering tools to these routes still fails.

`LlmAgentCapabilities.ContextImport` reports how a session start takes `LlmAgentSessionOptions.Context`:

- Managed Codex and managed Bedrock Converse import it as host-owned context (`Structured`). With `Omit`, unbound state is removed
  and the first turn emits one `LlmAgentWarningEvent` with the count.
- Claude CLI and AgentCore sessions report `OpeningContext`. A start with `Omit` begins a new provider
  conversation and sends the history once, as a `CONVERSATION SO FAR` transcript ahead of the first
  turn's input. The first turn emits one warning that names what was not carried; AgentCore carries
  no history attachments. A start context with `Reject` fails on these runtimes.

`MeasureRequestAsync` measures the request as it would be sent. Claude CLI chat and Antigravity CLI
chat estimate it with the bundled `o200k_base` tokenizer and say so in the diagnostic; the estimate
excludes each CLI's own framing.

### Provider input-token counting

`LlmProviderService.CountInputTokensAsync(connectionId, request, ct)` explicitly calls the selected
provider's count API. `ILlmProviderTokenCounter` is the optional adapter capability. Anthropic
Messages, Gemini API, Vertex Gemini and Vertex Claude implement it. A route without it throws
`NotSupportedException`; counting never substitutes a local estimate or creates a completion.
`MeasureRequestAsync` remains local nonmutating accounting and does not call a count API.

Counting refreshes and prepares the selected credential, binds continuation state, and attaches the
same catalog as generation. It uses the canonical generation request builder. Anthropic excludes
generation-only controls but preserves system/messages, tools, tool choice, thinking and output
format. Gemini API wraps the complete request in `generateContentRequest`. Vertex Gemini sends
the count API's input fields and generation config without `toolConfig`. Fields a count API cannot
represent are rejected, not silently stripped from the prompt.

`LlmProviderInputTokenCount` carries the resolved model, provider-reported input count, nullable
cached-input and billable-character counts, modality breakdowns, and omitted continuation-state
count. Unknown modalities retain their counts as `Unknown`. Missing optional values stay null;
malformed, negative and overflowing counts fail. These are preflight provider counts, not actual
generation usage, billing records or guaranteed admission. No output-token usage is invented.

Each count HTTP operation has a 100-second deadline through response parsing. Caller cancellation
remains cancellation. Service disposal cancels and drains active counts and rejects late results.
Failures expose safe status/retry information without response bodies or nested transport diagnostics.
Vertex Claude uses `count-tokens:rawPredict` with the bare model ID. Its count regions differ from
generation regions; an unsupported selected location fails without rerouting or changing the model.
The reviewed Vertex model allowlist remains unchanged.

### Host-owned managed agent runtime

**Accounting extension (11.14.0-rc.1):** the library includes
`LlmProviderService.MeasureRequestAsync(connectionId, request, policy, ct)` and
`ILlmAgentSession.MeasureContextAsync(proposedContext, ct)`, both returning `LlmRequestAccounting`.
These APIs are introduced in the coordinated `11.14.0-rc.1` contract release.
Standalone accounting resolves the selected connection read-only and may read its model catalog,
but never refreshes/persists credentials or runs inference. Session accounting uses base settings
while idle and the frozen executing model/instructions/tools while paused, including queued steering
without consuming it. Active execution is rejected. It never replaces context or consumes a pause.

The result distinguishes `IsSupported = false` from supported-but-unknown measurement, exposes raw
nullable model limits separately from the effective host ceiling, reserve, safety margin and usable
input budget, and preserves `LlmContextMeasurementSource`. Neither `IsSupported`, an estimate, nor
`NeedsCompaction = false` proves fit. Native-owned context accounting is unsupported. See the
[accounting design](docs/enhancements.md#public-nonmutating-accounting) for failure and concurrency semantics.

`OutputLimitEnforcement` is exposed by both `LlmProviderCapabilities` and Common's `LlmCapabilities`.
OpenAI Responses, Anthropic Messages and Bedrock Converse advertise `ProviderEnforced`; direct Codex,
Claude CLI and Antigravity advertise `Unsupported`. Generic compatible endpoints and the custom
AgentCore harness remain `Unknown`: forwarding a field is not evidence of enforcement. A reserve
does not enforce `MaxOutputTokens`. Bounded summarization must reject an unsupported or unverifiable
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
versioned list of stable `LlmAgentContextEntry` records, not a durable run scheduler. Agent DS owns
its existing session store, restore/fork decisions and compactor; Darkspyre.Inference.Connect.LLM adds no storage seam.

`LlmAgentContextStatus.NeedsCompaction` and token counts are nullable: unknown is not zero/false.
`MaxContextTokens` is effective context capacity; model discovery also exposes nullable context
and output limits. Context occupancy includes cached input and differs from accumulated usage or
account quota. A `LlmAgentCompactionRequiredEvent` ends an execution segment, **not the logical run**.
After host compaction, `ContinueTurnAsync` uses the same turn ID, pause ID and expected revision,
without resubmitting the prompt or re-executing settled tools. Only direct Codex currently enables this engine.

Capture defaults to omitting attachment bytes and recording `OmittedAttachments` metadata. For a
tool-output part, the entry's `Index` is the part's position in `Parts` and `ToolCallId` names the
result; the capture drops the binary part or the resource's byte body. Live
compaction must capture with `IncludeAttachmentData = true`; the host may strip bytes only from its
separate persistence projection. Omitted metadata cannot recreate an attachment or become model input.
See the [compiled host-context example](examples/Darkspyre.Inference.Connect.LLM.Examples/HostOwnedAgentContext.cs).

Text-only occupancy is estimated from the actual projected request using the bundled o200k
tokenizer, including instructions and schemas. It is not an exact current-model token count.
Binary attachments and encrypted reasoning make occupancy unknown; these return null rather than
a fabricated count, with one exception. Responses, direct Codex and Bedrock Converse record the provider's reported input
and output tokens on the continuation state (`LlmContinuationState.InputTokens` and `OutputTokens`), and
the estimate counts the history up to the last such state as their sum, tokenizing only what follows.
That estimate reports `Source = Mixed`. Hosts that keep continuation state keep the counts with it. Each
Bedrock model has its own tokenizer, so a Bedrock estimate of the unreported tail is approximate and its
diagnostic says so. Provider context-overflow errors still trigger a host handoff. Catalog limits
are optional. Configure an explicit output reserve when the catalog provides none; without a
known limit, reserve and occupancy, `NeedsCompaction` stays null.

Tools execute sequentially by default. Parallel mode is opt-in and bounded; a tool marked
`RequiresSequentialExecution` serializes its whole batch. Schemas are validated before callbacks.
Interrupted or failed handlers produce error results, and all issued calls settle before capture.
A cancelled handler's external effect may be unknown: Darkspyre.Inference.Connect.LLM never automatically replays it.
Model retries are limited to three total attempts before any output, respect retry delays up to
30 seconds, and leave aggregate usage unknown if an attempt's usage was unreported. Streams with
exposed output are not retried. Consume/dispose an enumeration fully before capture or another run.

Interactive completion methods accept the connection version captured before authentication began.
The `SignInAsync` convenience method captures that version internally. In both cases, the final save is
still conditional, so a connection change during the external authentication round trip is rejected.

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
`ILlmConnectionSource`; neither device completion nor catalog OAuth replaces saved credentials
before commit. Normal runtime credential refresh still uses the existing host seam unchanged.
CLI-owned authentication remains external; optional Claude catalog OAuth does not establish CLI login.

Settings objects never expose saved secrets. API-key fields support Keep/Replace/Clear; provider options,
capability overrides and additional headers already on a connection are retained when editing.
Auth interactions, dynamic choices, validation and explicit model tests use one common contract.
`AddLlmConnectBedrock` registers AWS profile/region choices in the SDK-owning assembly.
OpenAI-compatible remains an advanced provider with explicit manual-model support and typed
default/enabled/disabled capability overrides. Disabling the `Streaming` override selects the endpoint's
non-streaming request, as [Streaming, reasoning text and usage](#streaming-reasoning-text-and-usage) describes.
Existing provider JSON, headers and model-specific overrides are retained; the editor does not turn them into
an arbitrary JSON settings payload.

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

These contracts are included in the 11.14.0-rc.1 family. The old LLM setup-input/action description
types were replaced by Common setup contracts in the 11.3.0-rc.1 breaking-contract release.
Settings objects carry provider-owned unsaved-change state, the current authentication interaction and
explicit choice capabilities for the shared Settings editor.

## Account usage

Subscription adapters that can read account-level quotas report `SupportsAccountUsage = true` and
implement `ILlmAccountUsageClient`. Hosts normally call the provider-neutral facade:

```csharp
LlmAccountUsageSnapshot usage = await llms.GetAccountUsageAsync(connectionId);
```

The snapshot separates proportional or absolute quota windows, credit balances, and aggregate usage
metrics. Reset times are UTC `DateTimeOffset` values; `IsApproximate` identifies estimates. A CLI may
also supply a sanitized `Summary` for display only—hosts must not parse that text as a stable contract.
Credentials, account email, and raw protocol responses are never included. Codex, Claude subscription,
and Antigravity expose this capability; Anthropic API-key connections and other transports do not.

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

Chat and managed agents share SSE request mapping, attachments, tool definitions, reasoning level,
JSON Schema, and ordered opaque response-state replay. Requests use `store: false`; the library
does not retrieve server history or send `previous_response_id`. `ConversationKey` provides cache
affinity only; managed sessions use `ThreadId`. Temperature, JSON-object mode, and
`MaxOutputTokens` are unsupported; the managed output reserve budgets context without sending
that unsupported parameter.

Subscription requests explicitly opt into `reasoning.summary: "auto"`, retaining the selected
effort or leaving effort to the provider when unspecified. Summary deltas flow through
`LlmReasoningDeltaEvent` and managed `LlmAgentReasoningDeltaEvent` with `IsSummary = true`, so hosts
can display progress before the reply. Summaries are provider output and may be absent for a turn;
the library does not manufacture reasoning text. See [OpenAI reasoning summaries](https://developers.openai.com/api/docs/guides/reasoning#reasoning-summaries).

Both `SupportsToolUse` and `SupportsAgentToolUse` are true. With chat, the caller runs the tool
loop. With `LlmAgentProviderService`, Darkspyre.Inference.Connect.LLM runs it and invokes the host's handlers; do not
run a second Agent DS loop on top. Start through that facade so every model iteration re-resolves
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
| Native compaction | Agent DS compactor and same-run pause/continue |
| Immediate steering | Queued safe-boundary steering |
| CLI commands, file edits, web/MCP/subagents | Only tools the host explicitly registers and authorizes |
| Native approval/sandbox enforcement | Host handler policy; no built-in command executor or host sandbox |

The old Codex process/App Server implementation is removed, not retained as a second lane.
Claude and AgentCore still use their native session adapters.

Tools are sent non-strict unless their definition sets `Strict`: `LlmToolDefinition` and
`LlmAgentToolDefinition` both default to false, because a host schema describes what the tool accepts.
Structured output keeps `Strict = true`, because its schema describes the reply the host requires. A strict
tool's schema must meet the provider's strict rules; for OpenAI, every object closes
`additionalProperties` and lists every property as required, and any other schema fails the whole
request.

OpenAI Responses and Codex send the flag as given. Anthropic Messages, OpenAI-compatible Chat Completions and Bedrock Converse send `strict` only
for a strict tool, because some models and compatible servers reject the field even when it is false.

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
its working directory, `Ephemeral` and `Policy`. Native runtimes require it; managed sessions reject it.
`Policy` is one of two variants:

- `LlmAgentHostRuntimePolicy` (the default): host-selected writable roots, network access, sandbox,
  approval policy and `AllowRuntimeBackgroundWork`. The runtime enforces these limits.
- `LlmAgentProviderRuntimePolicy`: explicit adoption of the policy configured in the provider's own
  settings, including its background behavior. Connect does not read, edit or strengthen that policy and
  makes no claim about the filesystem, network or background limits it applies.

`LlmAgentCapabilities.SupportedRuntimePolicies` lists the variants a runtime accepts. The Claude CLI and
AgentCore accept `HostControlled`; the Antigravity CLI accepts only `ProviderConfigured`; managed runtimes
report an empty set. A runtime rejects any other
variant before effects. A resume without `Runtime` uses the current directory as working directory and a
host-controlled policy with that directory as its only writable root, `WorkspaceWrite`, network allowed,
`OnRequest` and no runtime background work. A runtime that accepts only `ProviderConfigured` therefore
rejects a resume without `Runtime`. A supplied `Runtime` is used whole. Managed Codex has no built-in shell, file editor,
network tool, or approval executor. Agent DS must enforce authorization, filesystem roots, and network
policy inside each tool handler. Managed sessions are always live/in-memory; persistence is explicit
host capture.

Claude uses restricted mode and `--add-dir` for read-only/workspace sessions. A workspace session
with `NetworkAllowed = false` is rejected because the CLI cannot independently constrain Bash
network access. AgentCore permits remote Harness-managed tools only with `DangerFullAccess`;
local workspace restrictions cannot constrain remote tools.

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
and host context operations. Resume and fork are optional: a client that implements
`ILlmAgentResumeClient` or `ILlmAgentForkClient` supports them, and `LlmAgentProviderService` throws
`NotSupportedException` for one that does not. Native `ResumeSessionAsync` and `ForkSessionAsync` are
unsupported for managed Codex and managed Bedrock Converse; AgentCore resumes but does not fork. Claude retains native resume/current-position fork and interrupt; its steering method
is unsupported. Tools return
text plus provider-neutral image/document attachments. Managed tools reject URL-based image/audio
content; use `Attachments`. Claude materializes MCP document resources through its read-only
`Read` tool. Every tool-capable session must receive a host tool handler.

## AWS Bedrock AgentCore Harness transport

Converse sends a tool's `strict` flag only when `LlmToolDefinition.Strict` is true. Models without
strict schemas, such as Amazon Nova, reject the field even when it is false; a strict tool on such a
model fails with Bedrock's own error.

Converse sends a reasoning level through `additionalModelRequestFields` for Claude and Nova 2 models,
classified from the model or inference-profile id:

| Family | Levels | Fields |
| --- | --- | --- |
| Claude (`anthropic.claude-*`) | The Anthropic Messages set for the same model | `thinking` and `output_config.effort`, or a thinking budget added to `inferenceConfig.maxTokens` (8,192 when the request sets none) |
| Nova 2 (`amazon.nova-2-*`) | `None`, `Low`, `Medium`, `High` | `reasoningConfig` with `type: "disabled"`, or `type: "enabled"` and `maxReasoningEffort` |

gpt-oss, DeepSeek-R1, other models and ARNs offer no level. A capability override can narrow a family's
set but cannot widen it. A Claude request with a level above `None` rejects temperature and forced tool
choice, and a Nova 2 request at `High` rejects temperature and `MaxOutputTokens`, before the call.

Converse streams through ConverseStream. A connection with `SupportsStreaming = false` sends Converse
instead. Its events then arrive after generation ends, in the order reasoning, text, tool calls, then the
completion. Reasoning text arrives as `LlmReasoningDeltaEvent` with `IsSummary = false`. A Claude model
under its default `omitted` reasoning display streams no reasoning text. Usage arrives in the stream's
final metadata event and reaches the host on the completion.

Tool calls return only with `RequiresAction`. The stop reasons `max_tokens` and
`model_context_window_exceeded` report `Incomplete`, `content_filtered` and `guardrail_intervened` report
`Refused`, and `malformed_model_output` and `malformed_tool_use` report `Failed` with no tool calls and no
continuation state. An unknown or missing stop reason reports `Unknown`.

Converse model listing returns the region's inference profiles and its foundation models. It omits a
foundation model whose only inference type is `PROVISIONED`, such as `amazon.nova-lite-v1:0:300k`.
Such a model runs only through purchased provisioned throughput, so a connection cannot invoke it by
model ID.

Converse also runs on the host-owned managed agent runtime. `AddLlmConnectBedrock()` registers an
`ILlmAgentClient` for `AwsBedrockConverse`; each model step is one Converse request, and the host runs
every tool. Managed Converse sessions take the same options, context and tool output as managed Codex,
with tool output carried on the Converse route.

`AwsBedrockAgentCore` is a separate transport from `AwsBedrockConverse`. Converse is a stateless chat
API with a host-managed tool loop; AgentCore invokes a deployed
[Harness](https://docs.aws.amazon.com/bedrock-agentcore/latest/devguide/harness.html) and owns the
remote agent session. Both are registered by `AddLlmConnectBedrock()`.

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
block. This transport therefore reports all four attachment capabilities as `false`; callers must not
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
`claude` (or an absolute CLI path), and clearing `Credential` unless the host intentionally retains
the optional model-catalog OAuth. Leaving the transport as `AnthropicMessages` now means direct
API-key billing and will never launch the CLI.

Install the official Claude CLI, ensure `claude` is on `PATH`, and complete its interactive sign-in once:

```bash
claude
```

Subscription prompts run in an empty temporary workspace with safe mode, tools, slash commands,
permission prompts, and session persistence disabled. Darkspyre.Inference.Connect.LLM sends the complete prompt as BOM-free
UTF-8 stream JSON over standard input rather than exposing it in process arguments. It consumes output
line by line and treats the terminal `result` event as authoritative, including when the CLI retries
an intermediate refusal on a fallback model. `IsAuthenticatedAsync` checks only `claude auth status
--json` for a signed-in first-party account; it does not inspect or require a stored credential.

Claude subscription account usage runs the CLI-owned `/usage` command separately and normalizes its
quota windows plus approximate local request/session activity. It only needs the CLI login, not the
separate model-catalog OAuth credential. API-key connections do not advertise account usage because
Anthropic does not expose the subscription report through the Messages API.

Native chat-style tools are enabled for API-key connections. Claude subscription chat requests remain
isolated one-shot generations, report `SupportsToolUse = false`, and launch the CLI with tools disabled.
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
report `SupportsAgentSessions = true` and `SupportsAgentToolUse = true`.

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
- `LlmAgentHostRuntimePolicy.AllowRuntimeBackgroundWork` (off by default) lets the Claude CLI start
  subagents and monitors. A background Bash command is part of the Bash tool and is observed either
  way. `ResourceOutputRetentionBytes` (1 MiB by default) caps the subagent output Connect retains.

The Claude CLI reports each task's start, status and end. Command output is read from the CLI's output
file, and the exit code from the trailer it appends. A subagent's output is the text of its own messages,
which stay out of the main turn. Terminal attach is unsupported.

The Claude CLI starts a turn of its own when background work reports. Connect launches it with
`--replay-user-messages` and reads it for the session's lifetime: a host turn starts at the CLI's echo of
its message, and output with no echo before it is a provider-started turn on `ObserveAsync`. A host turn
sent during a provider turn is queued by the CLI and starts after it.

### Sampling

The neutral request carries `TopP`, `TopK`, `Seed`, `PresencePenalty`, `FrequencyPenalty` and
`StopSequences`. Null controls leave provider defaults intact. An explicit unsupported control fails
before dispatch. `SupportsTopP`, `SupportsTopK`, `SupportsSeed`, `SupportsPresencePenalty` and
`SupportsFrequencyPenalty` default to false; `MaxStopSequences` defaults to zero.

| Route | Sampling mapping |
|---|---|
| OpenAI Responses | `top_p` on sampling-capable profiles; reasoning models require `ReasoningLevel.None` |
| OpenAI-compatible Chat | `top_p`, `seed`, penalties in [-2, 2], up to four `stop` strings; `top_k` requires a known endpoint override |
| Anthropic and Vertex Claude | Profile-owned temperature/top-p/top-k, `stop_sequences` without an invented Connect limit; thinking permits top-p from 0.95 to 1 and no top-k |
| Bedrock Converse | `inferenceConfig.topP` and `stopSequences`; Claude `top_k`, Nova 2 `inferenceConfig.topK` up to 128, merged with reasoning fields; stop limit 2500, or 10 for DeepSeek R1 |
| AgentCore | `BedrockModelConfig.TopP` on an explicit Bedrock model ID; the harness-default route cannot carry it |
| Gemini API and Vertex Gemini | `generationConfig` sampling fields and penalties, up to five stops; Gemini API top-k requires a catalog default, Vertex uses its qualified profile |
| Apple Foundation Models | Top-p or top-k random mode with optional seed; seed alone selects top-p 1; no stops or penalties |
| Claude CLI, Antigravity CLI and Codex | All six controls rejected; capability overrides cannot enable an unmapped control |
| SelfHost | All six controls and support flags pass to the selected Common engine; unsupported engine controls fail before its stream starts |

Claude Opus 4.7 and newer non-sampling profiles reject temperature/top-p/top-k even with an override.
Earlier sampling-capable Claude profiles enable these controls by default; overrides can disable them.
Gemini Flash 3.6 and later ignore temperature/top-p/top-k and reject penalties, so Connect rejects
explicit values. Vertex Gemini penalties use [-2, 2); Gemini API requires finite penalties.
Compatible endpoint settings expose typed sampling overrides and a nullable maximum-stop count;
zero disables stops, and blank uses detection. Raw provider options cannot inject these typed fields.

### Reasoning

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
a library change. Without a catalog entry, the family table alone applies. A model with neither accepts
no level unless a capability override supplies the set.

### Maximum output tokens

For direct API-key requests, omitting `MaxOutputTokens` sends `max_tokens` of 8192. Adaptive thinking is on by default on current Claude models, and thinking tokens count against `max_tokens`, so a lower ceiling can truncate the response before visible text and return `LlmCompletionStatus.Incomplete`. The official CLI does not expose a maximum-output-token option, so subscription requests reject `MaxOutputTokens` rather than silently ignoring it.

### Refusals

A direct Messages API classifier refusal is an HTTP 200 response with `stop_reason: "refusal"`, an empty content list, and a `stop_details` object. Darkspyre.Inference.Connect.LLM emits the explanation as an `LlmRefusalDeltaEvent`, then completes with `LlmCompletionStatus.Refused`, `Refusal` set to `stop_details.explanation`, and `RefusalCategory` set to `stop_details.category`. Both `stop_details` fields are null when the refusal maps to no named category, and `RefusalCategory` is then null.

A refusal can also arrive after partial output. `Text` keeps the streamed partial so the host can log it. That partial is not an answer; hosts branch on `Status`. For subscription prompts, only the CLI's terminal result determines whether the completed request was refused; intermediate fallback attempts do not produce a premature refusal result.

### Structured output

Direct API-key connections use the Models API's `structured_outputs` evidence for native schema and
strict tool support. A missing entry does not advertise native output enforcement; a host can
explicitly enable schema support when the catalog is unknown. A catalog rejection cannot be
overridden. JSON-object-only enforcement is not advertised. Supported output schemas use
`output_config.format` with `type: json_schema`; reasoning effort remains in the same output config.

Anthropic Messages and Vertex Claude preflight the same schema subset. Objects must close
`additionalProperties`; unsupported constraints, recursive/external references, complex enums,
unsupported formats and unsupported pattern constructs do not reach native output enforcement.
Schemas are not rewritten. A request must explicitly permit `LocalValidation` to use a schema
outside that subset or a model without native support. That path omits the native format and
validates the response against the original schema, including its omitted constraints.

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

### Model discovery

`AnthropicMessages.ListModelsAsync` uses the API key against `GET /v1/models`. Each listed model carries
`max_input_tokens` and `max_tokens` as its limits, and the reasoning levels its entry reports. The Claude CLI does not
expose an equivalent machine-readable catalog. A subscription connection therefore reports
`SupportsModelListing = false` by default and the host may accept a manually entered model ID. A host
can explicitly attach the optional browser-authorized catalog OAuth credential to the
`AnthropicClaudeCli` connection to enable the same paged HTTP catalog. That credential affects only
`SupportsModelListing` and `ListModelsAsync`; prompts, CLI readiness, usage, and agent tools ignore it.

## Antigravity CLI subscription transport

The [DSA-74 technical design](docs/antigravity-agent-tech-design.md) holds the evidence and mapping for
native agent sessions. They are unreleased: the published 11.14.0-rc.1 adapter is chat-only.

`GoogleAntigravityCli` wraps the official [Antigravity CLI headless mode](https://antigravity.google/docs/cli/headless/). Darkspyre.Inference.Connect.LLM sends prompts over the documented stdin NDJSON protocol, consumes stdout events line by line, and emits model text and activity while the CLI is still running. It does not read, copy, or store Antigravity OAuth credentials. The CLI process starts and ends as [Wrapped CLI processes](#wrapped-cli-processes) describes.

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

`Endpoint` may be `agy`, `local://antigravity-cli`, or an absolute executable path. Desktop apps launched outside a login shell should prefer the absolute path because their `PATH` can differ from Terminal or PowerShell. Model discovery calls `agy models`; model `auto` omits the command-line model override and uses the CLI default.

Each request runs in an empty temporary workspace with terminal sandboxing and slash-command expansion disabled. A streamed request sends its prompt as BOM-free UTF-8 through standard input. A request with `SupportsStreaming = false` is the exception: the CLI's only non-streaming form, `agy -p "<prompt>" --output-format json`, takes the prompt as a process argument, because stream-json input requires stream-json output. That prompt is visible in the local process list. It must fit in one argument: at most 131,071 UTF-8 bytes on macOS and Linux, and at most 30,000 characters on Windows, where a `.cmd` or `.bat` launcher cannot carry it at all because the prompt contains line breaks. A prompt that does not fit fails with `NotSupportedException` before the CLI starts. Native JSON Schema and the `Low`, `Medium` and `High` reasoning levels are supported. Only `agent_response` step deltas stream as answer text. A schema-backed request streams no provisional text: it returns the terminal event's canonical `structured_output` value, validated locally. The free-text `response` remains a compatibility fallback for older CLI versions. Temperature, maximum output tokens, headers, and provider options are rejected rather than silently ignored.

Antigravity may emit its own internal tool activity, but the CLI owns that execution. Because
Darkspyre.Inference.Connect.LLM cannot surface the call for host authorization and result submission, this adapter reports
`SupportsToolUse = false`. Its documented headless input protocol accepts only `text` blocks and
terminates on any other content-block type, so `SupportsPromptImages` and
`SupportsPromptDocuments` are also false. Darkspyre.Inference.Connect.LLM does not rely on undocumented `@path` behavior.

### Antigravity native agent sessions

`LlmAgentProviderService` starts and resumes native AGY conversations. AGY owns the conversation, its
Google sign-in, its native tools and its configured permission policy. Connect owns one local process
per open session, with an open standard input and one output reader.

- `StartSessionAsync` requires `Runtime` with a working directory and an explicit
  `LlmAgentProviderRuntimePolicy`. The runtime rejects `LlmAgentHostRuntimePolicy`, because the CLI cannot
  enforce host roots, network, approval or background limits. The working directory is the process
  working directory; it is not a filesystem boundary. `Ephemeral = true` is rejected: AGY persists every
  conversation.
- Start waits up to 15 seconds for the CLI handshake and returns its conversation ID as both
  `ThreadId` and `SessionId`. No hidden prompt is sent.
- `ResumeSessionAsync` passes that exact ID with `--conversation` and fails if the handshake reports
  another. Resume without `Runtime` fails, so the host must supply its stored working directory and
  provider policy. `--continue` is never used.
- Host tools, a tool handler, host context, host identities, session instructions, per-turn instructions,
  images and attachments are rejected before any process starts or any prompt is written.
- One turn runs at a time. Model, effort (`Low`, `Medium`, `High`) and output schema apply to one turn.
  When they differ from the running process, the session closes that process while idle and reopens the
  same conversation with the new flags before sending the prompt.
- A turn emits `LlmAgentActivityEvent` per step, `LlmAgentTextDeltaEvent` for `agent_response` deltas,
  `LlmAgentToolEvent` for native tools with their reported output, running usage observations and
  `LlmAgentMessageCompletedEvent`. A schema turn streams no text and publishes one
  `LlmAgentStructuredOutputSnapshotEvent` holding the validated canonical result.
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
- `SupportsAgentToolUse` is false, `ToolExecutionOwnership` is Provider, `ResourceObservation` is
  Unsupported and resource operations throw. Interrupt, steering, fork and context capture, status,
  replacement and compaction handoff are unsupported.

The ConsoleTester `agy-probe` command runs bounded live qualification cases; see
[Subscription and AWS smoke tests](#subscription-and-aws-smoke-tests).

`IsAuthenticatedAsync` performs the non-consuming `agy models` check, so an installed but signed-out executable is not reported as authenticated. Run `agy` interactively to sign in or refresh the CLI session. Antigravity owns its credential and session storage in the operating system's native keyring; Darkspyre.Inference.Connect.LLM does not inspect it.

Account usage runs `/usage` as a standalone headless CLI command. Normalized model quota fields are
returned when present; the sanitized CLI report is also retained in `Summary` because Antigravity
documents the slash-command response as human-readable text rather than a versioned JSON schema.

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
`respond(to:)` response when `SupportsStreaming` is false, temperature in [0,1],
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
engine. The host implements `ILlmSelfHost` and registers it with `AddLlmConnectSelfHost()`:

```csharp
services.AddLlmConnect();
services.AddLlmConnectSelfHost();
services.AddSingleton<ILlmSelfHost, MyEngineHost>(); // the host's engine map
```

A SelfHost connection's `Endpoint` is an engine ID from `ILlmSelfHost.ListEnginesAsync`, and it has no
credential. Several connections can name one engine. Streaming, model listing and agent sessions go to
the engine returned by `GetInferenceAsync`, which is Common's `ILlmInference`; an engine that is unknown
or not loaded fails with `InferenceException` and `ModelUnavailable`. `GetCapabilities` returns what the
engine declared when the host loaded it, and does no I/O, because Connect reads a transport's
capabilities synchronously. Streaming, final streamed usage and running usage flags come from the
engine. An engine that is not loaded declares no usage or tool support.

Generation and request accounting reject nonempty connection or request `ProviderOptions`, connection
`AdditionalHeaders`, and connection or model capability overrides. SelfHost passes only typed Common
request controls to the engine. An engine owns its capabilities; connection overrides do not change
them. Settings cannot save these unsupported options. Model and capability discovery remain available
so an invalid saved connection can be inspected and repaired.

Agent sessions run on the managed agent lane, which executes host tools; context estimates count the
request text with Connect's local tokenizer. Connect references Common only, never the engine library.
The settings offer an **Engine** choice listed from the host's engines and a **Model** choice listed
from that engine; both are required to save. Checking and testing are local verification.

## Console smoke tester

`tools/Darkspyre.Inference.Connect.ConsoleTester` exercises the public Darkspyre.Inference.Connect.LLM facade directly without a shim or
consuming application. `providers` lists the registered adapters; `setup --provider <id>` displays
their own setup steps, fields, requirements and action effects without running those actions.
Canonical provider IDs and the short aliases `codex|claude|antigravity|bedrock|agentcore|apple`
are accepted. Adapter metadata owns names, endpoints and model defaults. Existing aliases retain
their credential-store identities. Credential entry/sign-in commands retain the subscription/AWS
flows described below; listing another adapter does not provision its credentials.
Codex uses host-owned device OAuth stored in `Darkspyre.SecureStore`; Claude and Antigravity keep
CLI-owned credentials. The tester also stores host-owned AWS profile/region configuration.

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
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- codex-probe --provider codex --model gpt-5.5
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- managed-probe --provider codex --model gpt-5.5
```

The first checks direct tools, attachments and state replay. The second checks the managed loop,
JSON capture/reopen, fork and low-cap host compaction. Use a model returned by `models`.
[Recorded results](docs/managed-agent-verification.md) distinguish live proof from deterministic tests.

Pass `--attach-image path`, `--attach-document path`, `--effort <level>` (a reasoning level name such as `high` or `ExtraHigh`), or
`--schema path/to/schema.json` to exercise those request features. Attachment flags may be repeated.
Prompt text is sent to the tester over standard input and dispatched through the selected provider;
it is not placed in process arguments. Antigravity rejects attachment options because its headless
protocol is text-only.

Claude uses the same commands with `--provider claude`:

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- login --provider claude
```

Claude subscription prompts require the official Claude CLI to be installed and signed in. The
tester creates an `AnthropicClaudeCli` connection with `Credential = null`; Darkspyre.Inference.Connect.LLM does not read
or store the CLI credential. Enter `sonnet`, `opus`, another alias, or an exact model ID manually
because the tester does not configure the optional HTTP catalog credential.

Antigravity uses `--provider antigravity` (or `agy`). Run `agy` interactively once to sign in; the
tester does not request or store a Google authorization code. `agy-probe` runs explicit live cases
through the native session: `text`, `structured`, `native-tools`, `session`, `resume`, `controls` and
`cancel`. It works in a temporary workspace with one fixture, reports the CLI version and the number
of turns sent, and never retries. The `usage` command is available for all three subscription providers:

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
dotnet format Darkspyre.Inference.Connect.slnx --verify-no-changes --no-restore
dotnet pack Darkspyre.Inference.Connect.slnx -c Release --no-restore
dotnet list Darkspyre.Inference.Connect.slnx package --vulnerable --include-transitive
```

Releases are immutable tags such as `v11.3.1-rc.1`. `Common`, `Connect`, `Tensor`, and `UI.Settings` are built and
published in tandem from the approved local release environment to the private GitHub Packages feed;
GitHub Actions is not the release pipeline.
