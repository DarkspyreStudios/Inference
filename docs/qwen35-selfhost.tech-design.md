# Qwen 3.5 family, tools, thinking and self-hosted connections

This design covers three parts:

1. The Tensor Qwen adapter runs the Qwen 3.5 family with tools, thinking and a memory-fitted context.
   It changes no public contract and ships as a point release.
2. A Connect `SelfHost` transport runs an in-process `ILlmInference` as an agent session. It adds
   public contract and ships as its own release.
3. A host, such as Agent DS, configures Tensor entries and owns one engine per entry.

The motivating model is `ukisai/Swift-1.5-Qwen3.8-27B-GGUF`. Its GGUF header reports
`general.architecture = "qwen35"`, a 262,144-token context and a hybrid layer stack.

## 1. Tensor Qwen adapter

### Scope

The Tensor library exposes the existing contracts: `ITensorLlmInference`, which is Common's
`ILlmInference` plus `ITensorModelInstance`. It gains no agent-session code. Image input stays off.

### Architectures

TensorSharp's `ModelService` selects the model class from the GGUF architecture. `qwen35`,
`qwen35moe` and `qwen3next` load through `Qwen35Model`; `qwen3` loads through the Qwen 3 class. The
adapter accepts these four architectures. `qwen4exp` (Qwen 3.8 Flash Next) is out of scope.

`QwenModelCompatibility` validates each architecture from its own keys, prefixed by the
architecture name:

- every architecture: `block_count`, `embedding_length`, `attention.head_count`,
  `attention.head_count_kv`, `context_length`, a GPT-2 BPE tokenizer with merges, and a chat
  template;
- `qwen3`: the existing dense attention and tensor-shape checks;
- the Qwen 3.5 family: `ssm.inner_size`, `ssm.state_size`, `ssm.group_count`,
  `ssm.time_step_rank` and `ssm.conv_kernel`, plus the attention layer layout from `layer_types`
  or `full_attention_interval` (default 4).

The shard-set, truncation and definition checks apply to every architecture. TensorSharp's own load
validates the per-layer tensors of the Qwen 3.5 family; a failure there reports Failed through
`EngineStatusChanged`. `QwenModelResolver` uses the same validation, so Inspect reports a Supported
Qwen 3.5 GGUF and Save accepts it. The pack's `RuntimeArchitecture` stays
`TensorLlmModels.QwenRuntimeArchitecture`.

### Chat protocol

The adapter follows the protocol TensorSharp registers for the architecture
(`ChatProtocolRegistry.For(architecture)`). Each request creates that protocol's output parser and
calls `Init(enableThinking, tools)`. Each non-terminal stream piece passes through `Add(piece, false)`
and the terminal update through `Add(string.Empty, true)`. A `RawGenerationSuffix` on an update
primes the parser through `SetGenerationPromptSuffix` before the next piece. An update that
TensorSharp already parsed (`IsParsed`) carries its content, thinking and tool calls directly.

The parser's output maps to Common stream events:

| `ParsedOutput` | Common event |
|---|---|
| `Content` | `LlmTextDeltaEvent` |
| `Thinking` | `LlmReasoningDeltaEvent(delta, IsSummary: false)` |
| a completed `ToolCall` | `LlmToolCallStartedEvent`, one `LlmToolCallArgumentsDeltaEvent` with the JSON arguments, `LlmToolCallCompletedEvent` |

A tool call without a source ID gets a generated ID, unique within the response.

### Tools

`SupportsToolUse` is true. The request's tool definitions convert to TensorSharp `ToolFunction`
values through TensorSharp's JSON-schema tool parser. History messages carry their tool calls
(`ChatMessage.ToolCalls`) and tool results (role `tool` with `ToolCallId`). `ToolChoice` and
`AllowParallelToolCalls` are refused as unsupported features, because the chat template has no
control for either.

### Thinking

`ReasoningLevels` is `{ None, High }`. `None` passes `enableThinking: false`; `High` passes
`enableThinking: true`. Any other level fails as an invalid request.

### Context

TensorSharp resolves the context to the model's declared maximum and grows the attention cache on
demand while spare memory allows. The adapter reports how much of that context fits, so a host
compacts before memory runs out:

```text
usable = min(declared context, spare bytes / cache bytes per token)
```

- `spare bytes` is measured once after the weights are resident: the GC's available memory, minus
  the process working set, minus a reserve of the larger of 512 MiB and one sixteenth of available
  memory. This matches TensorSharp's own host estimate.
- `cache bytes per token` is `full-attention layers x head_count_kv x (key_length + value_length) x 2`
  bytes. Recurrent layers keep a fixed state and do not count per token. Qwen3.8-27B has 16 full-attention layers of
  64, 4 KV heads and a key length of 256: 64 KiB per token, 16 GiB at 262,144 tokens.

The result is `LlmModelInfo.MaxContextTokens` for the loaded model. The adapter estimates a request's
prompt by rendering it with the protocol's `Render` and counting it with the model's tokenizer. A
request whose estimated prompt plus `MaxOutputTokens` exceeds the usable context fails with
`CapacityExhausted` before TensorSharp runs. The
value is a snapshot: memory another engine or application takes later does not lower it.

## 2. Connect SelfHost transport

This part adds public contract and needs its own release.

- `LlmTransport.SelfHost` names a connection whose model runs in the host's process.
- `ILlmSelfHost` is the host seam: `Task<ILlmInference> GetInferenceAsync(string connectionId,
  CancellationToken)`. The host resolves a connection ID to its loaded model. Several connections can
  be loaded at once.
- The SelfHost client lists models and capabilities from that `ILlmInference`, maps Connect's
  `LlmChatRequest` to Common's `LlmInferenceRequest`, and maps the stream back.
- Agent sessions run on the managed agent lane (`ManagedAgentSession`), which executes the tool
  loop. Connect references Common only.
- A SelfHost connection has no endpoint and no credential. Connect's settings do not offer it; the
  host creates the connection.

## 3. Host responsibilities

- Settings add Tensor entries through `QwenSetup.CreateSettingsManager`, with a
  `HuggingFaceModelSource` for browsing. Packs use the library's default folder.
- Each entry owns one engine. The host creates it from the saved `TensorSetupConfiguration`, loads and
  unloads it, and disposes it.
- A Tensor entry is also its connection. Saving the entry writes an `LlmConnection` with
  `Transport = SelfHost` under the same ID.
- The host implements `ILlmSelfHost` over its entry-to-engine map.
