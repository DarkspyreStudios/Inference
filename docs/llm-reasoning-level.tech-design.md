# Provider-neutral reasoning level: technical design

Date: 2026-09-23

Status: **Implemented and published privately in 11.5.0-rc.1.** Decisions are in section 9.

## 1. Problem

`LlmInferenceRequest.ReasoningEffort` is a free-form string, and `LlmCapabilities` reports only
`SupportsReasoningEffort`. Each adapter reads the string in its own provider's vocabulary, so a caller
must know which provider is behind a connection to choose a value. That breaks the provider-neutral
seam (M-a): the wire vocabulary leaks into the request.

Current adapter behaviour:

| Adapter | Declares support | Wire use of the string | Accepted values |
|---|---|---|---|
| `OpenAiResponsesLlmClient`, `OpenAiCodexLlmClient` (`ResponsesLlmClientBase`) | yes | `reasoning.effort` | any string |
| `OpenAiCompatibleChatLlmClient` | no | `reasoning_effort` | any string |
| `ClaudeCliLlmClient` | yes | `--effort` | low, medium, high, xhigh, max |
| `ClaudeAgentClient` | via its session | `settings.effortLevel` | low, medium, high, xhigh, max |
| `AntigravityCliLlmClient` | yes | `--effort` | low, medium, high |
| `AnthropicMessagesLlmClient`, `BedrockLlmClient` | no | rejected by `LlmClientBase` | none |
| `BedrockAgentCoreLlmClient`, `BedrockAgentCoreSession` | no | rejected | none |
| Tensor `QwenLlmInference` | yes | TensorSharp `enableThinking` | none, high |

`OpenAiCompatibleChatLlmClient` declares no support and still writes the field. The same neutral
intent ("reason more") needs a different string per adapter, and nothing tells a caller which strings a
model accepts.

## 2. Scope and ownership

- The Inference contract owns a provider-neutral **reasoning level**: how much reasoning a request asks
  of the model that runs it.
- Each adapter owns the mapping from a level to its provider's wire form, and reports the levels each
  model honours.
- Capability tiers, meaning which model is strong enough for which work, are not an Inference concern.
  The Agent DS model selector owns them. Inference makes no claim that a level costs the same compute on
  two different models.

Out of scope: choosing models, cost estimation, and new provider features beyond the mappings in
section 5.

## 3. Contract

`Darkspyre.Inference.Common.LLM` gains an ordered level:

```csharp
public enum LlmReasoningLevel
{
    None,       // no reasoning, where the model can switch it off
    Minimal,
    Low,
    Medium,
    High,
    ExtraHigh,
    Maximum
}
```

- The order is the only cross-provider guarantee: on one model, each level asks for at least as much
  reasoning as the level below it.
- `LlmInferenceRequest.ReasoningEffort` (string) is replaced by `LlmReasoningLevel? ReasoningLevel`.
  Null means the provider's default, and the adapter sends no reasoning control.
- `LlmCapabilities.SupportsReasoningEffort` (bool) is replaced by
  `IReadOnlySet<LlmReasoningLevel> ReasoningLevels`, the levels the model accepts. An empty set means
  the model accepts no reasoning control. `LlmModelInfo.Capabilities` reports the set per model, so two
  models on one connection can differ.
- `LlmCapabilities.SupportsReasoning` is true when `ReasoningLevels` contains a level above `None`. It
  is computed from the set, never stored beside it. An application offers reasoning controls only when
  it is true.
- The same replacement applies to `LlmChat.ReasoningEffort`, `LlmAgent.ReasoningEffort` and the managed
  agent session's turn setting, and to `LlmProviderCapabilities` and its override record.

## 4. Validation

- A request whose level is not in the resolved model's `ReasoningLevels` fails with
  `InferenceException` and `InvalidRequest` before any provider call. The adapter never substitutes a
  nearby level: a silent substitution would record a decision the caller did not make.
- `LlmClientBase` performs this check once for every Connect adapter, so no adapter repeats ad hoc
  string validation (L14). Tensor engines perform the same check in their request validation.

## 5. Adapter mappings

| Adapter | Levels offered | Wire form |
|---|---|---|
| OpenAI Responses, OpenAI Codex | per connection: Minimal, Low, Medium, High, ExtraHigh; a connection's per-model capability overrides narrow the set for one model | `reasoning.effort`: `minimal`, `low`, `medium`, `high`, `xhigh` |
| OpenAI-compatible chat | per connection, from the capability override (section 6) | `reasoning_effort`: the lower-case value for Low, Medium or High |
| Claude CLI, Claude agent | Low, Medium, High, ExtraHigh, Maximum | `--effort` / `effortLevel`: `low`, `medium`, `high`, `xhigh`, `max` |
| Antigravity CLI | Low, Medium, High | `--effort`: `low`, `medium`, `high` |
| Anthropic Messages, adaptive-thinking models | per model: `None` where the model accepts disabled thinking, then Low to Maximum as the model's effort values allow | `None`: `thinking: {type: "disabled"}`. Otherwise `thinking: {type: "adaptive"}` and `output_config.effort`: `low`, `medium`, `high`, `xhigh`, `max` |
| Anthropic Messages, budget-only models (Haiku 4.5, Sonnet 4.5) | None, Low, Medium, High, ExtraHigh, Maximum | `None`: no `thinking`. Otherwise `thinking: {type: "enabled", budget_tokens}` with 1,024, 4,096, 16,384, 32,768 or 64,000 |
| Bedrock, Bedrock AgentCore | none | — |
| Tensor Qwen | None, High | `None`: `enableThinking: false`. `High`: `enableThinking: true` |

Each adapter keeps its level set and mapping table beside its capability declaration. A model the
adapter cannot classify reports an empty set.

Anthropic rules that the per-model sets encode:

- Minimal has no Anthropic equivalent, so no Anthropic model offers it.
- ExtraHigh (`xhigh`) exists on Opus 4.7 and later, Sonnet 5 and Fable; Maximum (`max`) on Opus 4.6 and
  later. Opus 4.5 offers Low, Medium and High only.
- `None` appears only where the model accepts disabled thinking. Fable and Opus 5.5 reject it. Opus 5
  accepts it only at effort `high` or below, which a request carrying `None` satisfies because it sends
  no effort.
- `budget_tokens` is rejected on current models and is used only for the budget-only models. There it
  must be less than `max_tokens`, so the adapter sends `max_tokens` as the request's output allowance
  plus the budget. It never shrinks either one.

## 6. Setup and capability overrides

`LlmSettings` offers a boolean "Reasoning effort" override for connections whose capability
the adapter cannot infer. It becomes a level-set override: the host selects which levels the endpoint
accepts. The override record carries `IReadOnlySet<LlmReasoningLevel>?` in place of `bool?`.

## 7. Consumers and release

This is a breaking public contract change, so the coordinated family advances its release component
(11.5.0). Per L1, every caller in this repository moves in the same unit, with no parallel string
property or dual reader.

Consumers outside this repository migrate in their own units after the release:

- Agent DS: request and session settings, and `BackgroundWorkerRequest.ThinkingLevel`, which moves to
  the same level type.
- ChatEngine: model assignments persist a reasoning setting. Its migration includes a schema change.
- The RightSize plugin's selector maps RightSize's reasoning demand to a level from the chosen model's
  `ReasoningLevels`.

## 8. Verification

- Each adapter has a mapping test for every level it offers, and a rejection test for a level it does
  not.
- `LlmClientBase` validation has a test for null, for an offered level and for an unoffered level.
- The setup session has a test for the level-set override and its effect on the resolved capabilities.
- Live provider calls are not gates (L7).

## 9. Decisions

1. **Anthropic Messages maps levels to effort, and to budgets only where effort does not exist.**
   Current Claude models take a named effort with adaptive thinking; token budgets are rejected there.
   The budget column in section 5 applies only to the budget-only models. It starts at the API minimum
   of 1,024 tokens and grows about fourfold per level.
2. **Tensor Qwen takes the enum and offers only `None`.** Any other level fails validation, and its
   `SupportsReasoning` is false, so applications do not offer reasoning for it.
3. **The full seven-level set.** It covers the union of today's provider vocabularies, and each model
   offers only the levels it honours.

## 10. Implementation record

### Reasoning level unit (2026-09-23)

- Common adds `LlmReasoningLevel`, `LlmInferenceRequest.ReasoningLevel`, and
  `LlmCapabilities.ReasoningLevels` with the computed `SupportsReasoning`. Connect's
  `LlmProviderCapabilities`, `LlmCapabilityOverrides`, `LlmChatRequest` and `LlmAgentTurnRequest` carry
  the same types. The string and boolean members are removed.
- `LlmReasoningEffort` holds the shared lower-case effort words and the single level check, which fails
  with `InvalidRequest`. `LlmClientBase` runs it for every Connect adapter; Antigravity and the
  AgentCore lanes, which validate on their own paths, call the same check.
- OpenAI Responses and Codex offer Minimal to ExtraHigh per connection. The chat-completions adapter
  offers levels only through a capability override. The Claude CLI and its agent lane share one level
  set. `AnthropicReasoning` holds the Anthropic model-family table and wire mapping; an explicit level
  override replaces its table for a connection or one model. Bedrock and AgentCore accept no level.
  Qwen accepts only `None`.
- The setup editor's boolean "Reasoning effort" override is one Default/Enabled/Disabled choice per
  level. Enabling or disabling a level makes the override explicit: the endpoint then accepts exactly
  the enabled levels. Returning the last enabled level to Default restores the adapter's detection.
  Common has no multi-select setup field, so the per-level choice reuses the existing override field.
- Tests cover every Responses level, a rejected Responses level, the chat override, each Anthropic
  model family's wire form and budget arithmetic, Anthropic rejections, override precedence, the
  Claude CLI and Antigravity rejections, the setup override and Qwen. The console tester parses
  `--effort` as a level name.

Phase exit: Connect, Tensor and UI build with zero warnings in Debug and Release. Connect passes 327
tests (Connect.LLM 165), Tensor 292 (Qwen 42) and UI 49. Format verification passes for all three,
and the Connect and Tensor transitive vulnerability audits report no vulnerable packages. No version
change, package or publication is part of this unit; section 7's 11.5.0 release is a separate step.

The subsequent coordinated source-version update selects `11.5.0-rc.1`. The complete implementation
and version update are committed together at the user's request. All four packages were subsequently
published from `500a572` after release verification; `.roles/project.md` records the evidence.
