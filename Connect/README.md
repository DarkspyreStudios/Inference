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
[TangoFlux integration](#tangoflux-audio). All five capability assemblies ship in Connect **11.11.0-rc.1**.

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
Connect-only data: `LlmChatMessage` and `LlmChatResult` with response state and diagnostics,
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
`AddLlmConnect` registers once for the application. A list stays until the connection's `Version` changes,
which every connection or credential edit does, or until `RefreshModelsAsync` lists again and replaces it.
Concurrent requests for one connection share a single listing, request accounting and managed sessions
read the same list, and a failed listing is not kept. Common's `ILlmInference` carries the same pair:
`ListModelsAsync` may answer from a kept list, and `RefreshModelsAsync` lists again. A service built by
hand without the shared cache keeps its own.

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
                    ResponseState = result.ResponseState
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

`LlmChatResult.ResponseState` is opaque, versioned adapter state. Preserve it unchanged on that
response's `LlmChatMessage.ResponseState`, alongside its visible text and tool calls, on ordinary
turns as well as tool turns. Keep every assistant record, not just the latest one. Responses adapters
retain ordered reasoning/text/calls; Anthropic retains thinking/signatures/content. The adapter
replays state instead of duplicating the message's visible text/calls. State is bound to transport,
model and account/connection scope; incompatible state fails before sending unless the request opts
in to omission, as described below. The former request-level `ContinuationToken` has been removed,
with no compatibility shim. Bedrock Converse state carries only the reply's reported token counts and
no replayable output; a Converse request accepts only state bound to its own connection and model.
Chat Completions, AgentCore and CLI chat do not import this opaque state.

### Carrying history to another model or provider

A conversation that moves to another model or provider starts a new provider-side conversation from
the host's retained history. Set `UnboundResponseState = LlmUnboundResponseState.Omit` on those
requests. Each message whose state does not bind to the target transport, model and account is sent
without it; its visible text, tool calls, tool results and attachments are kept. The result's
`OmittedResponseStateCount` reports how many messages were sent without state. The default,
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

### Host-owned managed agent runtime

**Accounting extension (11.11.0-rc.1):** the library includes
`LlmProviderService.MeasureRequestAsync(connectionId, request, policy, ct)` and
`ILlmAgentSession.MeasureContextAsync(proposedContext, ct)`, both returning `LlmRequestAccounting`.
These APIs are introduced in the coordinated `11.11.0-rc.1` contract release.
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
and output tokens on the response state (`LlmProviderResponseState.InputTokens` and `OutputTokens`), and
the estimate counts the history up to the last such state as their sum, tokenizing only what follows.
That estimate reports `Source = Mixed`. Hosts that keep response state keep the counts with it. Each
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
default/enabled/disabled capability overrides. Existing provider JSON, headers and model-specific
overrides are retained; the editor does not turn them into an arbitrary JSON settings payload.

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

These contracts are included in the 11.11.0-rc.1 family. The old LLM setup-input/action description
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
discovery uses the direct authenticated catalog.

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
its working directory, writable roots, network access, sandbox, approval policy and `Ephemeral`. The
Claude CLI and AgentCore require it; managed sessions reject it. A resume without `Runtime` uses the
current directory as working directory and only writable root, `WorkspaceWrite`, network allowed and
`OnRequest`; a supplied `Runtime` is used whole. Managed Codex has no built-in shell, file editor,
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

Converse sends no reasoning setting. It reports no reasoning levels, a capability override cannot add
one, and a request with `ReasoningLevel` fails before the call.

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
- `LlmAgentSessionOptions.AllowRuntimeBackgroundWork` (off by default) lets the Claude CLI start
  subagents and monitors. A background Bash command is part of the Bash tool and is observed either
  way. `ResourceOutputRetentionBytes` (1 MiB by default) caps the subagent output Connect retains.

The Claude CLI reports each task's start, status and end. Command output is read from the CLI's output
file, and the exit code from the trailer it appends. A subagent's output is the text of its own messages,
which stay out of the main turn. Terminal attach is unsupported.

The Claude CLI starts a turn of its own when background work reports. Connect launches it with
`--replay-user-messages` and reads it for the session's lifetime: a host turn starts at the CLI's echo of
its message, and output with no echo before it is a provider-started turn on `ObserveAsync`. A host turn
sent during a provider turn is queued by the CLI and starts after it.

### Temperature

`SupportsTemperature` defaults to `false`. Claude Opus 4.7 and later reject any non-default `temperature`, `top_p`, or `top_k` with HTTP 400, so Darkspyre.Inference.Connect.LLM throws `NotSupportedException` before the request leaves the process. An API-key host targeting Claude Opus 4.6, Claude Sonnet 4.6, or an earlier model can opt back in per model:

```csharp
new LlmConnection
{
    Version = version,
    Transport = LlmTransport.AnthropicMessages,
    Endpoint = "https://api.anthropic.com/v1",
    ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
    {
        ["claude-opus-4-6"] = new() { SupportsTemperature = true }
    }
};
```

### Reasoning

Direct API-key requests map a reasoning level per model. Current models take
`thinking: {type: "adaptive"}` with `output_config.effort` (`low`, `medium`, `high`, `xhigh`, `max`),
and `None` sends `thinking: {type: "disabled"}` where the model accepts it. Opus 4.6 and Sonnet 4.6
offer no `ExtraHigh`, and Opus 4.5 offers effort only up to `High`. Models without effort, such as
Haiku 4.5, take a thinking budget of 1,024, 4,096, 16,384, 32,768 or 64,000 tokens for `Low` through
`Maximum`, added to `max_tokens`. A model the adapter does not recognise accepts no level unless a
capability override supplies the set.

### Maximum output tokens

For direct API-key requests, omitting `MaxOutputTokens` sends `max_tokens` of 8192. Adaptive thinking is on by default on current Claude models, and thinking tokens count against `max_tokens`, so a lower ceiling can truncate the response before visible text and return `LlmCompletionStatus.Incomplete`. The official CLI does not expose a maximum-output-token option, so subscription requests reject `MaxOutputTokens` rather than silently ignoring it.

### Refusals

A direct Messages API classifier refusal is an HTTP 200 response with `stop_reason: "refusal"`, an empty content list, and a `stop_details` object. Darkspyre.Inference.Connect.LLM emits the explanation as an `LlmRefusalDeltaEvent`, then completes with `LlmCompletionStatus.Refused`, `Refusal` set to `stop_details.explanation`, and `RefusalCategory` set to `stop_details.category`. Both `stop_details` fields are null when the refusal maps to no named category, and `RefusalCategory` is then null.

A refusal can also arrive after partial output. `Text` keeps the streamed partial so the host can log it. That partial is not an answer; hosts branch on `Status`. For subscription prompts, only the CLI's terminal result determines whether the completed request was refused; intermediate fallback attempts do not produce a premature refusal result.

### Structured output

Direct API-key connections report `SupportsJsonSchema = false` and `SupportsJsonObject = false`; schema-backed requests must permit `LlmStructuredOutputFallback.LocalValidation`. Subscription connections use the Claude CLI's native `--json-schema` contract and accept the `Low`, `Medium`, `High`, `ExtraHigh` and `Maximum` reasoning levels. Darkspyre.Inference.Connect.LLM uses the terminal event's canonical `structured_output` value and validates it locally before returning completion.

### Model discovery

`AnthropicMessages.ListModelsAsync` uses the API key against `GET /v1/models`. The Claude CLI does not
expose an equivalent machine-readable catalog. A subscription connection therefore reports
`SupportsModelListing = false` by default and the host may accept a manually entered model ID. A host
can explicitly attach the optional browser-authorized catalog OAuth credential to the
`AnthropicClaudeCli` connection to enable the same paged HTTP catalog. That credential affects only
`SupportsModelListing` and `ListModelsAsync`; prompts, CLI readiness, usage, and agent tools ignore it.

## Antigravity CLI subscription transport

`GoogleAntigravityCli` wraps the official [Antigravity CLI headless mode](https://antigravity.google/docs/cli/headless/). Darkspyre.Inference.Connect.LLM sends prompts over the documented stdin NDJSON protocol, consumes stdout events line by line, and emits model text and activity while the CLI is still running. It does not read, copy, or store Antigravity OAuth credentials.

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

Each request runs in an empty temporary workspace with terminal sandboxing and slash-command expansion disabled. Prompts are sent as BOM-free UTF-8 through standard input instead of process arguments. Native JSON Schema and the `Low`, `Medium` and `High` reasoning levels are supported; schema-backed requests use the terminal event's canonical `structured_output` value and are also validated locally before being returned. The free-text `response` remains a compatibility fallback for older CLI versions. Temperature, maximum output tokens, headers, and provider options are rejected rather than silently ignored.

Antigravity may emit its own internal tool activity, but the CLI owns that execution. Because
Darkspyre.Inference.Connect.LLM cannot surface the call for host authorization and result submission, this adapter reports
`SupportsToolUse = false`. Its documented headless input protocol accepts only `text` blocks and
terminates on any other content-block type, so `SupportsPromptImages` and
`SupportsPromptDocuments` are also false. Darkspyre.Inference.Connect.LLM does not rely on undocumented `@path` behavior.

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

Supported: text/instructions, user/assistant semantic history, streaming, temperature in [0,1],
provider-enforced output caps, host-executed chat tools and existing managed agent sessions.
Each model operation owns one helper process. Tools and persistence remain with the host.
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
capabilities synchronously. An engine that is not loaded reports streaming only.

Agent sessions run on the managed agent lane, which executes host tools; context estimates count the
request text with Connect's local tokenizer. Connect references Common only, never the engine library.
The settings offer an **Engine** choice listed from the host's engines and a **Model** choice listed
from that engine; both are required to save. Checking and testing are local verification.

## Console smoke tester

`tools/Darkspyre.Inference.Connect.ConsoleTester` exercises the public Darkspyre.Inference.Connect.LLM facade directly without a shim or
consuming application. Select ChatGPT Codex, Claude subscription, Google Antigravity, Bedrock
Converse, or AgentCore Harness with `--provider codex|claude|antigravity|bedrock|agentcore`.
Codex uses host-owned device OAuth stored in `Darkspyre.SecureStore`; Claude and Antigravity keep
CLI-owned credentials. The tester also stores host-owned AWS profile/region configuration.

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
tester does not request or store a Google authorization code. The `usage` command is available for all
three subscription providers:

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
