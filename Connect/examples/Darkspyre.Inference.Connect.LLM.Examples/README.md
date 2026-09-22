# Provider tool-lane examples

This project contains compile-checked examples for every Darkspyre.Inference.Connect.LLM transport that can invoke a
host-defined tool. Start with the provider capability, not the provider name:

| Capability | Service | Example | Who completes the tool call? |
|---|---|---|---|
| `SupportsToolUse` | `LlmProviderService` | [`ChatToolLane.cs`](ChatToolLane.cs) | The host executes each returned call and sends the result in a later chat request. |
| `SupportsAgentToolUse` | `LlmAgentProviderService` | [`AgentToolLane.cs`](AgentToolLane.cs) | The selected agent engine invokes the host callback within the same turn. |
| Neither | None | Do not attach host tools. | The transport cannot expose host-authorized calls. |

These are separate contracts. Direct Codex supports both; choose the agent lane when Darkspyre.Inference.Connect.LLM
should own the loop. Claude subscription rejects chat tools and requires the agent lane. Do not run
the host's chat loop on top of an agent session.

## Provider map

| Provider connection | Supported lane | Concrete entry point |
|---|---|---|
| OpenAI Responses | Chat | `ProviderToolExamples.OpenAiResponsesAsync` |
| OpenAI-compatible Chat Completions | Chat | `ProviderToolExamples.OpenAiCompatibleChatAsync` |
| Anthropic Messages with API key | Chat | `ProviderToolExamples.AnthropicApiKeyAsync` |
| AWS Bedrock Converse | Chat | `ProviderToolExamples.BedrockConverseAsync` |
| AWS Bedrock AgentCore Harness | Agent | `ProviderToolExamples.BedrockAgentCoreAsync` |
| ChatGPT Codex subscription | Chat or managed agent | `CodexSubscriptionChatAsync` / `CodexSubscriptionAsync` |
| Claude subscription | Agent | `ProviderToolExamples.ClaudeSubscriptionAsync` |
| Antigravity CLI | None | The CLI may use its own internal tools, but it cannot call a host callback. |

All eight entry points use the same deterministic `get_weather` fixture from
[`WeatherTool.cs`](WeatherTool.cs). That file shows both public tool-definition types, strict
argument validation, cancellation, success results, and error results.

[`AttachmentToolResults.cs`](AttachmentToolResults.cs) shows the shared attachment contract for both
lanes. A tool supplies only `LlmAttachmentKind.Image` or `LlmAttachmentKind.Document`, a filename,
and bytes. It does not construct media types, base64, data URLs, provider file IDs, temporary paths,
MCP resources, or provider-specific content blocks.

## Credential ownership and OAuth

There are two credential owners:

- Direct transports, including ChatGPT subscription OAuth, are authenticated by the host. The host retrieves protected credential JSON in
  its `ILlmConnectionSource` and Darkspyre.Inference.Connect.LLM sends the appropriate API authentication.
- Wrapped CLI transports are authenticated by their official CLI. Claude and Antigravity
  keep their own sessions in their own credential stores. Do not copy those CLI
  tokens into `LlmConnection.Credential` or make the application vault their owner.

The console tester demonstrates a host vault with `Darkspyre.SecureStore`: its
[`Program.cs`](../../tools/Darkspyre.Inference.Connect.ConsoleTester/Program.cs) registers the vault and Darkspyre.Inference.Connect.LLM,
and [`SecureConnectionSource.cs`](../../tools/Darkspyre.Inference.Connect.ConsoleTester/SecureConnectionSource.cs)
adapts `ICredentialStore` to `ILlmConnectionSource`. That source stores only credentials and
configuration that the host actually owns. A production connection source should use the same
boundary and must preserve Darkspyre.Inference.Connect.LLM's versioned compare-and-save contract.

| Connection | `LlmConnection.Credential` | Authentication owner | When OAuth is needed |
|---|---|---|---|
| OpenAI Responses | Protected `LlmCredential` containing the API key | Host/vault | Not for the standard API-key path. |
| OpenAI-compatible Chat Completions | Protected API key or secret-header credential required by that endpoint | Host/vault | Only if that custom endpoint independently requires OAuth; Darkspyre.Inference.Connect.LLM does not define a general compatible-endpoint OAuth flow. |
| Anthropic Messages API | Protected `LlmCredential` containing the Anthropic API key | Host/vault | No. This is direct `x-api-key` HTTP authentication. |
| AWS Bedrock Converse | Usually protected `LlmAwsCredentials` containing only profile and region | AWS SDK credential chain | No. The named profile, environment, workload identity, or other AWS SDK source owns the actual AWS credentials. |
| AWS Bedrock AgentCore Harness | Usually protected `LlmAwsCredentials` containing only profile and region | AWS SDK credential chain | No. The connection endpoint is the deployed Harness ARN; AWS credentials still come from the standard SDK chain. |
| ChatGPT Codex subscription | Protected ChatGPT OAuth credential | Host/vault | Device OAuth through `SignInAsync` is required for direct generation, models and usage. CLI login is not reused. |
| Claude subscription | `null` | Official `claude` CLI | No OAuth is required for prompts, tools, usage, or CLI readiness. Optional catalog OAuth is used only when the host explicitly enables HTTP model discovery. |
| Antigravity subscription | `null` | Official `agy` CLI | No host OAuth. Run `agy` interactively; the CLI owns its Google session. |

### Why Claude has two authentication dependencies

A Claude subscription uses the distinct `AnthropicClaudeCli` transport. Its normal connection has
`Credential = null`; the CLI owns authentication for prompts, tools, usage, and readiness checks.
It is never selected by inspecting a stored credential.

There is one optional, independent integration:

1. Anthropic's `GET /v1/models` is authenticated HTTP and is not exposed anonymously or by the
   Claude CLI. A host that wants that catalog can explicitly store a browser-authorized catalog OAuth
   credential through `ILlmConnectionSource`.
2. That catalog token affects only `SupportsModelListing` and `ListModelsAsync`. It never enables the
   subscription transport, prompts, agent sessions, tools, usage, or `IsAuthenticatedAsync`.

`AnthropicMessages` is a separate API-key transport. It never launches the CLI and cannot be used
with `LlmAgentProviderService`. Conversely, `AnthropicClaudeCli` rejects API-key credentials and never
uses them for CLI operations.

[`ConnectionExamples.cs`](ConnectionExamples.cs) shows the exact connection shapes. Notice that
Claude subscription and Antigravity use `Credential = null`; direct Codex takes the host's protected OAuth credential and an HTTPS endpoint.

## Host setup

Register the host's connection source and the provider services once:

```csharp
services.AddSecureStore(options =>
{
    options.ServiceName = "com.example.product";
    options.Label = "Example Product";
});
services.AddSingleton<MySecureConnectionSource>();
services.AddSingleton<ILlmConnectionSource>(services =>
    services.GetRequiredService<MySecureConnectionSource>());

services.AddLlmConnect();
services.AddLlmConnectBedrock(); // Bedrock is included in the Connect product package.
```

`MySecureConnectionSource` owns application API keys, direct Codex device OAuth, optional Claude catalog OAuth, and optionally
the Bedrock profile/region record. It still returns connection metadata for CLI connections, but
their normal `Credential` is `null`. Use `SetApiKeyAsync`,
`SetBedrockCredentialsAsync`, `SignInAsync` for Codex, or the optional Claude catalog sign-in APIs to save host-owned values through the
source instead of placing secrets in source code. AgentCore uses the same AWS credential record; its
connection endpoint is the deployed Harness ARN.

Resolve both facades where tools may use either lane:

```csharp
LlmProviderService llms = services.GetRequiredService<LlmProviderService>();
LlmAgentProviderService agents = services.GetRequiredService<LlmAgentProviderService>();
```

## Chat tool lane

These calls all run the complete loop in [`ChatToolLane.cs`](ChatToolLane.cs):

```csharp
LlmChatResult openAi = await ProviderToolExamples.OpenAiResponsesAsync(
    llms, "openai", "gpt-5.4", cancellationToken);

LlmChatResult codexChat = await ProviderToolExamples.CodexSubscriptionChatAsync(
    llms, "codex-subscription", "gpt-5.5", cancellationToken);

LlmChatResult compatible = await ProviderToolExamples.OpenAiCompatibleChatAsync(
    llms, "openrouter", "provider/model", cancellationToken);

LlmChatResult anthropic = await ProviderToolExamples.AnthropicApiKeyAsync(
    llms, "anthropic-api", "claude-sonnet-4-6", cancellationToken);

LlmChatResult bedrock = await ProviderToolExamples.BedrockConverseAsync(
    llms, "bedrock", "amazon.nova-micro-v1:0", cancellationToken);
```

For every round, the example:

1. Sends the conversation and tool schema through `CompleteAsync`.
2. Returns immediately when the status is terminal.
3. On `RequiresAction`, validates and executes every `LlmToolCall` in the host.
4. Appends the assistant's exact calls and a user message containing matching `LlmToolResult` IDs.
5. Preserves `ResponseState` unchanged on each assistant message for the same connection and model.

Per-message response state preserves OpenAI Responses reasoning and Anthropic thinking/signatures.
It is normally null for Chat Completions and Bedrock. Preserve it on ordinary assistant turns too,
and never decode it or move it between providers, account scopes, or models. There is no longer a
request-level continuation token.

## Managed agent context (10.0.0)

[HostOwnedAgentContext.cs](HostOwnedAgentContext.cs) demonstrates the implemented managed Codex
contract: open host context, capture with bytes, and continue after host compaction. Claude and
AgentCore remain provider-owned and reject these operations.

The [Agent DS handoff](../../docs/agent-ds-contract-handoff.md) defines persistence, context pressure,
same-run continuation, attachment ownership, and rollback responsibilities. Agent DS keeps its
existing session store. The [live console scenario](../../tools/Darkspyre.Inference.Connect.ConsoleTester/ManagedAgentProbe.cs)
uses in-memory JSON only, exercising real capture/reopen/fork and a low context cap.

Textual context occupancy uses an explicitly approximate tokenizer projection. With attachments or
encrypted reasoning it is unknown; do not turn null into zero. Set a host output reserve when model
discovery has none. Provider overflow is another handoff trigger.

```text
RunTurnAsync -> model -> host tools -> next model
                   |
         context pressure / overflow
                   v
     CompactionRequired (enumeration ends)
                   |
 Agent DS: capture bytes -> compact older groups -> persist
                   |
 ContinueTurnAsync(same turn, pause ID, captured revision)
                   v
          resumed model -> completion
```

Keep protected entries unchanged. A rejected replacement does not consume the pause or mutate live
context. Queued steering is added atomically to the accepted replacement and participates in its
budget check. A pause is live process state, not a durable executable checkpoint.

## Tool-result attachments

Chat and agent tool results use the same provider-neutral value:

```csharp
LlmAttachment report = new()
{
    Kind = LlmAttachmentKind.Document,
    FileName = "report.pdf",
    Data = await File.ReadAllBytesAsync(reportPath, cancellationToken)
};

LlmToolResult chatResult = new(call.Id, "Report attached.")
{
    Attachments = [report]
};

LlmAgentToolResult agentResult = new()
{
    Content = [new LlmAgentTextContent("Report attached.")],
    Attachments = [report]
};
```

Use `SupportsToolResultImages` and `SupportsToolResultDocuments` to gate tool behavior. OpenAI
Responses, Anthropic Messages, Codex and Claude agent sessions, and Bedrock Converse accept both
kinds. AgentCore Harness host-tool results are text-only because the Harness tool-result wire shape
does not accept image or document blocks.
OpenAI-compatible Chat Completions remains text-only because no portable attachment result shape
exists across compatible endpoints. Claude uses MCP embedded resources. Direct Codex uses native
Responses image/file blocks in both prompt and tool output. Unsupported content fails explicitly.

## Prompt attachments

The same value is accepted on chat user messages and agent turns:

```csharp
LlmChatMessage chatPrompt = new("user", "Summarize the report.")
{
    Attachments = [report]
};

LlmAgentTurnRequest agentPrompt = new()
{
    Input = [new LlmAgentTextContent("Summarize the report.")],
    Attachments = [report]
};
```

Gate those paths with `SupportsPromptImages` and `SupportsPromptDocuments`. OpenAI Responses,
Anthropic Messages, Bedrock Converse, Codex subscription, and Claude subscription support both kinds.
OpenAI-compatible Chat Completions, AgentCore Harness, and Antigravity do not. The AgentCore Harness
message union currently exposes text, reasoning, tool-use, and tool-result blocks, but no image or
document block. Antigravity's documented stdin protocol accepts text blocks only. Darkspyre.Inference.Connect.LLM does not
emulate either limitation by splicing extracted text into the prompt.

### Headless multi-turn attachment use

Managed Codex retains and reprojects its host-owned context; Claude keeps its CLI conversation open.
Attach the document on the first turn, then continue through the same `ILlmAgentSession`:

```csharp
await using ILlmAgentSession session = await agents.StartSessionAsync(
    connectionId,
    new LlmAgentSessionOptions
    {
        ModelId = modelId,
        WorkingDirectory = repositoryPath,
        Sandbox = LlmAgentSandboxMode.ReadOnly,
        ApprovalPolicy = LlmAgentApprovalPolicy.Never
    },
    cancellationToken);

await foreach (LlmAgentEvent agentEvent in session.RunTurnAsync(
                   new LlmAgentTurnRequest
                   {
                       Input = [new LlmAgentTextContent("Analyze this document.")],
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

// No attachment is needed while this same live session still owns the conversation.
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

Codex retains bytes in its live model context and sends the required history on each request.
Claude relies on its native CLI conversation. Neither is a durable LLM-side attachment cache.

| Situation | Direct Codex | Claude CLI |
|---|---|---|
| Later turn, same session | Reprojects live context including bytes | Existing native conversation |
| Independent chat request | Send relevant history and attachments | Attach again |
| Process restart | Start from a snapshot captured with bytes, or reattach | Native resume does not guarantee exact binary access |
| Fork | Start a complete context prefix with new thread identity | Native fork; keep original bytes |
| Session disposal | Only host-saved context survives | CLI process closes |
| Context compaction | Host replacement controls exact retention | Exact contents may become a summary |

`CaptureContextAsync()` omits bytes by default and returns omission notices. For exact replay use
`CaptureContextAsync(new() { IncludeAttachmentData = true })` and persist through the host's
existing store. Always retain bytes for live compaction; a separate persistence projection may omit
them. The library does not read host paths to recreate missing attachments.

## Agent tool lane

Codex, Claude, and AgentCore tools are registered on a session and execute through the callback in
that same turn:

```csharp
AgentToolExampleResult codex = await ProviderToolExamples.CodexSubscriptionAsync(
    agents,
    "codex-subscription",
    "gpt-5.6-terra",
    repositoryPath,
    cancellationToken);

AgentToolExampleResult claude = await ProviderToolExamples.ClaudeSubscriptionAsync(
    agents,
    "claude-subscription",
    "sonnet",
    repositoryPath,
    cancellationToken);

AgentToolExampleResult agentCore = await ProviderToolExamples.BedrockAgentCoreAsync(
    agents,
    "agentcore-harness",
    "default",
    repositoryPath,
    cancellationToken);
```

[`AgentToolLane.cs`](AgentToolLane.cs) requests read-only native execution and disables native approvals,
registers `WeatherTool.AgentDefinition`, supplies `WeatherTool.ExecuteAsync` as the
`LlmAgentToolHandler`, streams events, and verifies that the callback completed. There is no second
`CompleteAsync` call from the consumer: managed Codex runs the model/tool loop internally, and Claude's MCP bridge returns the callback result to its native agent. AgentCore maps the definition to an inline
function, captures the streamed `toolUse`, executes the same callback, then sends the assistant
`toolUse` and user `toolResult` blocks back on the same runtime session. Codex lives in memory and
Claude uses ephemeral mode in this example; AgentCore rejects ephemeral mode because AWS persists the runtime
session and expires it according to AgentCore's session lifecycle.

Managed Codex does not enforce native sandbox settings on host callbacks. Your handler must
enforce tool authorization, path access and network policy. No shell or filesystem tools are
registered implicitly.

The console tester uses the same contract with `darkspyre_inference_connect_echo`. Sign in to direct Codex with
`login --provider codex`; sign in to Claude through its CLI. Then run:

```bash
echo 'Call darkspyre_inference_connect_echo with text Hello, then reply with its result.' | \
  dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  agent --provider codex --model gpt-5.6-terra

echo 'Call darkspyre_inference_connect_echo with text Hello, then reply with its result.' | \
  dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  agent --provider claude --model sonnet
```

For AgentCore, configure the AWS profile and pass the deployed Harness ARN. The console tester stores
only profile/region and requires the ARN on each invocation because it has no connection database:

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  login --provider agentcore --harness-arn "$HARNESS_ARN" \
  --profile application-profile --region us-east-1

echo 'Call darkspyre_inference_connect_echo with text Hello, then reply with its result.' | \
  dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  agent --provider agentcore --harness-arn "$HARNESS_ARN" --model default
```

The implementation is in
[`Program.RunAgentAsync`](../../tools/Darkspyre.Inference.Connect.ConsoleTester/Program.cs). The console app is a smoke
tester, not a credential shim: direct Codex uses the tester's secure device-OAuth credential,
Claude uses its CLI login, and AgentCore uses the AWS SDK credential chain.

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- codex-probe --provider codex --model gpt-5.5
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- managed-probe --provider codex --model gpt-5.5
```

These live scenarios consume usage. See the [verification record](../../docs/managed-agent-verification.md).

## Common mistakes

- Do not attach chat tools to Claude subscription calls. Codex supports either lane; use
  `LlmAgentToolDefinition` when the library should run the tool loop.
- Do not send AgentCore host tools through the chat lane. Use the AgentCore agent session so
  Darkspyre.Inference.Connect.LLM can execute inline functions and complete the tool-result loop.
- Do not treat CLI-internal activity as a host tool call. Antigravity has no host callback lane.
- Do not execute arguments before validating the tool name, JSON shape, authorization, and intended
  side effects. A schema guides the model; it is not the host's security boundary.
- Do not omit the assistant tool-call message, mismatch result IDs, or drop per-message response state in
  a multi-round chat tool exchange.
- Do not base64-encode attachments in application code. Supply bytes through `LlmAttachment` and
  let the selected adapter detect the media type and construct its native representation.
- Do not copy CLI tokens into SecureStore. Claude and Antigravity own their CLI sessions. Direct
  Codex instead requires a host-owned device-OAuth credential obtained through Darkspyre.Inference.Connect.LLM.
- Do not restore Codex by an old App Server thread ID; open a captured host context. Native commands,
  approval handling, web search and subagents now require explicit host tool implementations.
