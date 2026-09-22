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
isolated in the `Darkspyre.Inference.Connect.LLM.Bedrock` assembly. All three assemblies ship in the
one Connect NuGet.

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

The coordinated release contains three NuGets. They always use the same version from the repository
`Directory.Build.props` and publish together. Version numbers use
`<net>.<release>.<minor>`: .NET generation, breaking-contract release, and compatible tweak/fix.
The direct Codex and host-owned managed-agent behavior from the former `Mathis.LlmConnect` 3.0.0 is
retained; Claude and AgentCore retain provider-owned sessions.
See the [Agent DS handoff](docs/agent-ds-contract-handoff.md),
[technical design](docs/enhancements.md), and [verification record](docs/managed-agent-verification.md).

| Package | Purpose |
|---|---|
| `Darkspyre.Inference.Common` | Typed LLM, image, and decision requests, results, capabilities, streaming events, provenance, and failures shared by Connect and Tensor |
| `Darkspyre.Inference.Connect` | Core LLM transports, Bedrock/AgentCore, and ComfyUI image connection assemblies |
| `Darkspyre.Inference.Tensor` | In-process LLM, image, and typed-decision inference |

There is no `Darkspyre.Inference.Connect.Common` package or assembly. Connector-only endpoint,
authentication, provider-management, and job contracts remain in their capability assemblies. Only
semantic inference contracts that are also implemented by Tensor belong in
`Darkspyre.Inference.Common`.

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

| Transport | Chat tools | Agent tools | Prompt images | Prompt documents | Result images | Result documents |
|---|---:|---:|---:|---:|---:|---:|
| OpenAI Responses | Yes | No | Yes | Yes | Yes | Yes |
| OpenAI-compatible Chat Completions | Yes | No | No | No | No | No |
| ChatGPT Codex subscription | Yes | Yes | Yes | Yes | Yes | Yes |
| Anthropic Messages with API key | Yes | No | Yes | Yes | Yes | Yes |
| Claude subscription via official CLI | No | Yes | Yes | Yes | Yes | Yes |
| AWS Bedrock Converse | Yes | No | Yes | Yes | Yes | Yes |
| AWS Bedrock AgentCore Harness | No | Yes | No | No | No | No |
| Antigravity CLI | No | No | No | No | No | No |

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
                    ToolResults = [new LlmToolResult(call.Id, toolOutput)]
                }
            ],
            Tools = [weatherTool]
        },
        cancellationToken);
}
```

During streaming, tool calls arrive as `LlmToolCallStartedEvent`, one or more `LlmToolCallArgumentsDeltaEvent` values, and `LlmToolCallCompletedEvent`. The terminal `LlmCompletionEvent` repeats the complete calls in `LlmChatResult.ToolCalls`. `ArgumentsJson` is deliberately raw JSON; the host should deserialize it into its own validated input type before execution.

### 2.0 attachment migration

Version 2.0 replaces `LlmToolAttachment` with `LlmAttachment`, removes caller-supplied
`MediaType`, and adds `Attachments` to `LlmChatMessage` and `LlmAgentTurnRequest`. Replace the old
type name, delete `MediaType` initializers, and gate prompt inputs with `SupportsPromptImages` or
`SupportsPromptDocuments`. Tool-result capability checks are unchanged.

Prompt messages and tool results use the same provider-neutral binary value. The host supplies only
the high-level kind, filename, and bytes; Darkspyre.Inference.Connect.LLM detects the media type and owns base64 encoding,
temporary-file staging, sandbox roots, and every provider wire shape:

```csharp
var resultWithFiles = new LlmToolResult(call.Id, "The chart and report are attached.")
{
    Attachments =
    [
        new LlmAttachment
        {
            Kind = LlmAttachmentKind.Image,
            FileName = "chart.png",
            Data = chartBytes
        },
        new LlmAttachment
        {
            Kind = LlmAttachmentKind.Document,
            FileName = "report.pdf",
            Data = reportBytes
        }
    ]
};

var promptWithFiles = new LlmChatMessage("user", "Compare the chart with the report.")
{
    Attachments = resultWithFiles.Attachments
};
```

Check `SupportsPromptImages` and `SupportsPromptDocuments` before attaching prompt input, and check
`SupportsToolResultImages` and `SupportsToolResultDocuments` before returning an attachment. Prompt
attachments belong on user messages only. Adapters reject an unsupported kind, unrecognized content,
path-bearing filename, or empty payload instead of silently turning it into text.

OpenAI Responses, direct Codex, Anthropic Messages, Claude CLI, and Bedrock use native
image/document content blocks. Direct Codex sends binary prompt and tool-result attachments
inline through Responses, with no CLI staging directory. The caller supplies kind, filename,
and bytes; adapters own MIME detection, encoding, and wire representation.

`LlmChatResult.ResponseState` is opaque, versioned adapter state. Preserve it unchanged on that
response's `LlmChatMessage.ResponseState`, alongside its visible text and tool calls, on ordinary
turns as well as tool turns. Keep every assistant record, not just the latest one. Responses adapters
retain ordered reasoning/text/calls; Anthropic retains thinking/signatures/content. The adapter
replays state instead of duplicating the message's visible text/calls. State is bound to transport,
model and account/connection scope; incompatible state fails before sending. The former
request-level `ContinuationToken` has been removed, with no compatibility shim. Chat Completions,
Bedrock, AgentCore and CLI chat do not import this opaque state.

### Host-owned managed agent runtime

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

Capture defaults to omitting attachment bytes and recording `OmittedAttachments` metadata. Live
compaction must capture with `IncludeAttachmentData = true`; the host may strip bytes only from its
separate persistence projection. Omitted metadata cannot recreate an attachment or become model input.
See the [compiled host-context example](examples/Darkspyre.Inference.Connect.LLM.Examples/HostOwnedAgentContext.cs).

Text-only occupancy is estimated from the actual projected request using the bundled o200k
tokenizer, including instructions and schemas. It is not an exact current-model token count.
Binary attachments and encrypted reasoning make occupancy unknown; these return null rather than
a fabricated count. Provider context-overflow errors still trigger a host handoff. Catalog limits
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

## Guided setup metadata

`LlmProviderCatalog` exposes canonical provider metadata without resolving configured connections,
credentials, HTTP clients, or scoped runtime facades. A host can inject the singleton catalog into a
settings surface and build a provider picker and ordered wizard without maintaining its own provider
text:

```csharp
foreach (LlmProviderDefinition providerDefinition in catalog.SupportedProviders)
{
    if (!providerDefinition.IsGuidedSetupOption)
    {
        continue;
    }

    RenderProviderChoice(providerDefinition.DisplayName, providerDefinition.Description);
    foreach (LlmSetupStep step in providerDefinition.SetupSteps)
    {
        RenderStep(step.Title, step.Instructions, step.Action);
    }
}
```

Runtime clients expose the same `LlmProviderDefinition` instances through
`LlmProviderService.SupportedProviders`, so catalog and execution metadata cannot drift.

Each step describes its required inputs, secret handling, current-platform instructions, completion
requirement, and troubleshooting guidance. The contract is UI-framework-neutral: the host renders the
controls, opens browsers or terminals when requested, and stores credentials through its protected
`ILlmConnectionSource` implementation.

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
paths) now fail with migration guidance. Model discovery uses the direct authenticated catalog.

Chat and managed agents share SSE request mapping, attachments, tool definitions, reasoning effort,
JSON Schema, and ordered opaque response-state replay. Requests use `store: false`; the library
does not retrieve server history or send `previous_response_id`. `ConversationKey` provides cache
affinity only; managed sessions use `ThreadId`. Temperature, JSON-object mode, and
`MaxOutputTokens` are unsupported; the managed output reserve budgets context without sending
that unsupported parameter.

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
        WorkingDirectory = repositoryPath,
        Sandbox = LlmAgentSandboxMode.WorkspaceWrite,
        WritableRoots = [repositoryPath, artifactDirectory],
        NetworkAllowed = true,
        Tools = [agentWeatherTool],
        Handlers = new LlmAgentSessionHandlers
        {
            ToolHandler = ExecuteToolAsync,
            ApprovalHandler = ReviewApprovalAsync
        }
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
                               Kind = LlmAttachmentKind.Document,
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

`Sandbox`, `WritableRoots`, `WorkingDirectory`, `NetworkAllowed`, and approval settings control
provider-owned runtimes, not managed host callbacks. Managed Codex has no built-in shell, file
editor, network tool, or approval executor. Agent DS must enforce authorization, filesystem roots,
and network policy inside each tool handler. Managed sessions are always live/in-memory; persistence
is explicit host capture regardless of `Ephemeral`.

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
and host context operations. Native `ResumeSessionAsync`, `ForkSessionAsync`, and
`CompactAsync` are unsupported for managed Codex. Claude retains native resume/current-position
fork and interrupt; its explicit compaction and steering methods are unsupported. Tools return
text plus provider-neutral image/document attachments. Managed tools reject URL-based image/audio
content; use `Attachments`. Claude materializes MCP document resources through its read-only
`Read` tool. Every tool-capable session must receive a host tool handler.

## AWS Bedrock AgentCore Harness transport

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
because AgentCore cannot route approval requests for its remote built-in tools to
`LlmAgentApprovalHandler`. With a writable sandbox and `Never`, the deployed Harness tool policy is
allowed to operate remotely. Supplying an approval handler is rejected rather than ignored.

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
For host tools, use `LlmAgentProviderService`: it registers the supplied schemas as an SDK-hosted MCP
server over Claude's streaming stdin/stdout control protocol. MCP `tools/call` requests are converted
to `LlmAgentToolCall`, executed only by the host's `ToolHandler`, and returned to Claude as MCP results.
The adapter launches in restricted mode, disables built-in tools and settings-based MCP servers, and
allows only the explicitly registered Darkspyre.Inference.Connect.LLM tools. Claude subscription connections therefore
report `SupportsAgentSessions = true` and `SupportsAgentToolUse = true`.

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

### Maximum output tokens

For direct API-key requests, omitting `MaxOutputTokens` sends `max_tokens` of 8192. Adaptive thinking is on by default on current Claude models, and thinking tokens count against `max_tokens`, so a lower ceiling can truncate the response before visible text and return `LlmCompletionStatus.Incomplete`. The official CLI does not expose a maximum-output-token option, so subscription requests reject `MaxOutputTokens` rather than silently ignoring it.

### Refusals

A direct Messages API classifier refusal is an HTTP 200 response with `stop_reason: "refusal"`, an empty content list, and a `stop_details` object. Darkspyre.Inference.Connect.LLM emits the explanation as an `LlmRefusalDeltaEvent`, then completes with `LlmCompletionStatus.Refused`, `Refusal` set to `stop_details.explanation`, and `RefusalCategory` set to `stop_details.category`. Both `stop_details` fields are null when the refusal maps to no named category, and `RefusalCategory` is then null.

A refusal can also arrive after partial output. `Text` keeps the streamed partial so the host can log it. That partial is not an answer; hosts branch on `Status`. For subscription prompts, only the CLI's terminal result determines whether the completed request was refused; intermediate fallback attempts do not produce a premature refusal result.

### Structured output

Direct API-key connections report `SupportsJsonSchema = false` and `SupportsJsonObject = false`; schema-backed requests must permit `LlmStructuredOutputFallback.LocalValidation`. Subscription connections use the Claude CLI's native `--json-schema` contract and expose low, medium, high, xhigh, and max reasoning effort. Darkspyre.Inference.Connect.LLM uses the terminal event's canonical `structured_output` value and validates it locally before returning completion.

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

Each request runs in an empty temporary workspace with terminal sandboxing and slash-command expansion disabled. Prompts are sent as BOM-free UTF-8 through standard input instead of process arguments. Native JSON Schema and low, medium, or high reasoning effort are supported; schema-backed requests use the terminal event's canonical `structured_output` value and are also validated locally before being returned. The free-text `response` remains a compatibility fallback for older CLI versions. Temperature, maximum output tokens, headers, and provider options are rejected rather than silently ignored.

Antigravity may emit its own internal tool activity, but the CLI owns that execution. Because
Darkspyre.Inference.Connect.LLM cannot surface the call for host authorization and result submission, this adapter reports
`SupportsToolUse = false`. Its documented headless input protocol accepts only `text` blocks and
terminates on any other content-block type, so `SupportsPromptImages` and
`SupportsPromptDocuments` are also false. Darkspyre.Inference.Connect.LLM does not rely on undocumented `@path` behavior.

`IsAuthenticatedAsync` performs the non-consuming `agy models` check, so an installed but signed-out executable is not reported as authenticated. Run `agy` interactively to sign in or refresh the CLI session. Antigravity owns its credential and session storage in the operating system's native keyring; Darkspyre.Inference.Connect.LLM does not inspect it.

Account usage runs `/usage` as a standalone headless CLI command. Normalized model quota fields are
returned when present; the sanitized CLI report is also retained in `Summary` because Antigravity
documents the slash-command response as human-readable text rather than a versioned JSON schema.

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

Pass `--attach-image path`, `--attach-document path`, `--effort low|medium|high|xhigh|max`, or
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

Releases are immutable tags such as `v11.1.0`. `Common`, `Connect`, and `Tensor` are built and
published in tandem from the approved local release environment to the private GitHub Packages feed;
GitHub Actions is not the release pipeline.
