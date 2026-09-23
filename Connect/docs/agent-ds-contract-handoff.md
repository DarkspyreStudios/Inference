# Agent DS: managed-agent contract handoff

Status: **version 10.1.0**, 2026-09-22. Direct Codex and the managed engine retain the behavior
live-tested for 3.0.0 under the new package and namespace. Version 2.2.0 does not contain these changes.
Direct Codex now uses host-owned device OAuth; Claude/Antigravity remain CLI-owned.
The public contracts are now distributed through the coordinated three-package release.

Agent DS can now implement its data mappings, ownership selection, event handling and host compactor
against these exact types. Consume `Darkspyre.Inference.Connect` through the private NuGet 10.1.0 package;
do not copy library source into Agent DS. Keep the new path capability-gated.
The [README](../README.md) is the canonical integration contract; the
[technical design](enhancements.md) defines the execution semantics.

## Types and operations

All names below are in `Darkspyre.Inference.Connect.LLM`. Existing session methods remain on `ILlmAgentSession`; no new
managed-session subtype or host storage interface is required.

| Host integration | Library contract |
| --- | --- |
| Select host-owned versus provider-owned memory | `LlmProviderCapabilities.Agent.ContextOwnership` |
| Query the selected session's implemented operations | `await session.GetCapabilitiesAsync(ct)` |
| Save/reopen/fork model context | `LlmAgentContext` → stable `LlmAgentContextEntry(Id, TurnId, Message)` records |
| Start from host context and chosen identities | `LlmAgentSessionOptions.Context`, `ThreadId`, `SessionId` |
| Set iteration/tool/budget policy | `LlmAgentSessionOptions.ExecutionOptions` |
| Apply plugin-composed instructions for one logical run | `LlmAgentTurnRequest.Instructions`, gated by `SupportsAgentTurnInstructions` |
| Inspect current or prospective input occupancy | `await session.GetContextStatusAsync(prospectiveTurn, ct)` |
| Capture settled context | `await session.CaptureContextAsync(options, ct)` |
| Compact between runs | `await session.ReplaceContextAsync(replacement, expectedRevision, ct)` |
| Compact within a run | `LlmAgentCompactionRequiredEvent` → `ContinueTurnAsync(continuation, ct)` |
| Preserve reasoning and ordered tool outputs | `LlmChatMessage.ResponseState`, copied from `LlmChatResult.ResponseState` |

Session I/O/control operations use `Task`, streaming uses `IAsyncEnumerable`, handlers/disposal use
`ValueTask`. Identity properties and model records are ordinary data, not asynchronous lookups.

### Retained context

`LlmAgentContext` has `FormatVersion` (currently 1), `Revision` (`long`, initially 0), and `Entries`.
Entries reuse `LlmChatMessage`, including `Attachments`, `ToolCalls`, `ToolResults`, `ResponseState`
and `OmittedAttachments`. Store these model records separately from the existing display transcript.

Persist the whole response-state value unchanged. It has `FormatVersion`, `Transport`, `ModelId`,
opaque non-secret `ScopeId`, and opaque `Payload`. Do not parse encrypted reasoning or reconstruct
state from visible assistant text. The old request/result `ContinuationToken` no longer exists.
Each ordinary or tool-producing assistant response has its own state, even if its visible text is
empty. Adapters replay state **instead of** duplicating that message's visible text and calls.

Responses and Anthropic currently implement this state mapping. The codec allows only known output
fields, validates version/transport/model/account binding and assistant roles, and checks that
visible tool calls match the encoded calls. Unknown state fields cannot become request options,
instructions or credentials. API-key state is conservatively bound to the configured credential and
endpoint/header scope; key rotation requires explicit neutralization of old opaque records. OAuth
account identity, when available, survives token refresh. Scope is compatibility, not authenticity.

The managed engine validates imports/tool groups and makes private copies of live data. Public records
are serialization DTOs: `IReadOnlyList` does not make a caller's underlying list immutable. Do not
mutate submitted values while an operation is using them. Native adapters reject host snapshots now.

On managed reopen, pass base instructions, tool definitions and delegates again. They are not part
of the snapshot. Preserve `ThreadId` on reopen; assign a new thread ID for a host-selected prefix
fork, preserving copied entry IDs and the root `SessionId`. Choose a complete assistant/tool-result
group boundary, not a display-message count. Host-owned sessions use start-from-context, not native
resume/fork by provider ID. Native providers keep their existing handles and native resume behavior.

### Attachments

The caller still chooses `Image` or `Document` and supplies bytes in `LlmAttachment`. The adapter
owns sniffing, MIME types, encoding, upload/staging and request projection. No application base64 or
document-to-prompt concatenation is needed.

`LlmAgentContextCaptureOptions.IncludeAttachmentData` defaults false. The managed capture operation
omits bytes from both prompt and nested tool-result attachments without changing live context.
`LlmOmittedAttachment(Kind, FileName, Index, ToolCallId)` identifies the unavailable attachment:
the containing message owns it; null tool ID means prompt attachment, otherwise the matching tool
result owns it. `Index` is the original zero-based index in that owner's attachment list. Duplicate
filenames are safe because filename is not identity. Descriptors are notices, never model input.

Use `IncludeAttachmentData = true` for live compaction. Agent DS can separately omit bytes from
its stored projection. Never send that stripped projection back into the live run by accident.
Darkspyre.Inference.Connect.LLM will not read application paths to reconstruct missing attachments after reopen.

## Context pressure and compaction

`LlmAgentContextStatus` exposes `ModelId`, `ContextRevision`, `MaxContextTokens`, `UsedContextTokens`,
`ReservedOutputTokens`, `AvailableInputTokens`, nullable `NeedsCompaction`, `IsEstimated`, and
`Source` (`Unknown`, `Estimated`, `ProviderReported`, `Mixed`). All token values are nullable `long`.

Unknown is not zero, false, unlimited, or a request to compact. Used context is the projected next
input (including cached input, tools, instructions, attachments and opaque state), not summed usage
or subscription quota. While idle, pass a prospective turn to preview it without appending input;
null measures base context. Running/null returns the last published status; paused/null measures
the pending run. Prospective-turn preview is idle-only.

`LlmModel.MaxContextTokens` and `MaxOutputTokens` are nullable discovery metadata. Session policy
can set a smaller/manual limit; it cannot increase a known hard limit. No model catalog limits are
invented. Text occupancy is estimated using the bundled o200k tokenizer over the actual request
projection. It remains unknown when attachments or encrypted reasoning cannot be counted. If the
catalog lacks an output limit, configure a host output reserve for threshold admission.

`LlmAgentExecutionOptions` defaults: 32 model iterations, sequential tools, maximum 4 concurrent
tools when parallel mode is explicitly selected. `LlmAgentToolDefinition.RequiresSequentialExecution`
forces the entire containing batch to be sequential. Host handlers still enforce authorization,
filesystem/network policy and sandboxing themselves. Native sandbox options are not a host sandbox.

`LlmAgentContextPolicy` defaults to ratio 0.85 and safety margin 4096; host context cap/output reserve
are optional. Runtime admission validates positive limits, positive concurrency/iteration counts,
nonnegative margin, finite ratio in `(0, 1]`, and cap precedence before effects. With known quantities:

```text
inputBudget = max(0, MaxContextTokens - ReservedOutputTokens - SafetyMarginTokens)
available = max(0, inputBudget - UsedContextTokens)
needsCompaction = UsedContextTokens >= floor(inputBudget * CompactionThresholdRatio)
```

### Same-run pause protocol

1. Consume `RunTurnAsync` until its enumeration finishes. A `LlmAgentCompactionRequiredEvent`
   carries thread/turn/pause IDs, status, reason (`Threshold` or `ProviderOverflow`),
   `CanCompactHistory` and `ProtectedEntryIds`. It is **not** a completed turn.
2. Capture with attachment bytes. All issued tools have settled; retain protected records intact.
   Summarize only earlier complete groups using Agent DS's existing compactor. Its own model input
   must be bounded too. If `CanCompactHistory` is false, interrupt and surface oversized fixed input.
3. Persist the replacement through the existing `ISessionStore` under Agent DS's existing policy,
   retaining the original durable context for rollback. If persistence fails, do not continue.
4. Enumerate `session.ContinueTurnAsync(new LlmAgentTurnContinuation(pause.TurnId, pause.PauseId,
   captured.Revision, replacement), ct)`. No second prompt. The engine validates and installs
   atomically, assigns its next revision, and consumes the pause ID once on success.
5. A `LlmAgentTurnContinuedEvent` confirms acceptance. Validation failure leaves the original live
   context/pause intact: Agent DS restores/retains its original durable state. A later provider
   failure after acceptance does **not** roll the installed context back. Persist a fresh settled
   capture after completion, another pause, or settled cancellation.

The [compiled example](../examples/Darkspyre.Inference.Connect.LLM.Examples/HostOwnedAgentContext.cs) demonstrates
open, capture and continue calls without creating a competing store or compactor seam.
Serialize Agent DS edits during this sequence. There is no cross-system transaction and no claim
of exactly-once recovery. Queued steering may make a replacement insufficient; a rejected replacement
can be corrected against the same live pause. The engine, not a supplied `replacement.Revision`,
chooses the new revision.

Instructions, model, reasoning level, schemas, tool definitions, usage and iteration counts stay
fixed across the logical run's pauses. `Instructions = null` uses the session base, `""` clears it
for this run, and nonempty replaces it. Completion, failure, interruption or cancellation ends that
override. Agent DS owns plugin instruction composition; Darkspyre.Inference.Connect.LLM does not persist it as user text.

`LlmAgentUsageEvent` contains a model-step `RequestId`, per-request `Usage`, and `RunTotals` including
earlier continued segments. Null means unreported. `LlmAgentTurnCompletedEvent.Reason` distinguishes
completed, iteration limit, refusal, truncation, provider failure and interruption; existing status
values remain. `LlmReasoningDeltaEvent` carries exposed reasoning deltas; encrypted state is never
displayed. Pre-output transient retries are bounded to three total attempts; exposed streams are not retried.

Breaking enumeration or cancellation must settle active work before capturing. A cancelled consumer
may not receive a final event. Deliberately ending at a compaction pause preserves the live run.
After process death/disposal, the pause is not resumable: snapshots are context data, not executable
checkpoints. Agent DS can reopen its stored context and explicitly initiate new work under its own
recovery policy. No durable harness, scheduler, journal or automatic tool replay is added here.

## Implemented availability

| Adapter | Context | Capture/status/replace/handoff | Native resume/fork | Explicit compact/interrupt |
| --- | --- | --- | --- | --- |
| Claude CLI | Provider | Unsupported | Resume / current-position fork | Compact unsupported; interrupt supported |
| AgentCore Harness | Provider | Unsupported | Resume / no fork | Both unsupported; cancel enumeration to cancel local work |
| Antigravity CLI | No agent session | Unsupported | Neither | Neither |
| Direct Codex managed engine | Host | Supported | Host snapshot instead | Host compaction; explicit interrupt supported |

Native adapters reject `Context`, host IDs, managed `ExecutionOptions`, managed tool scheduling
flags, and non-null per-run `Instructions` before effects. Overrides cannot enable unimplemented
agent features. Direct Codex uses the existing device-OAuth flow and protected host connection store;
it never scrapes CLI credentials. Replace executable endpoints with the direct HTTPS endpoint.
Native Codex shell/file/web/MCP/subagent execution is not implicitly registered in the managed
engine: Agent DS supplies and secures those tools. Start managed sessions through
`LlmAgentProviderService` so every iteration resolves/refreshes the host connection.

## Verification and consumer handoff

The [verification record](managed-agent-verification.md) lists actual live console commands and
their results, separately from deterministic tests. Live proof includes direct model discovery and
usage, chat/agent attachment tools, ordered state replay, JSON save/reopen, independent fork, and
same-run host compaction with retained instructions and no duplicate prompt or settled tool replay.

Agent DS can now integrate its existing store and compactor against this implementation. Keep
capability gating and its existing durability/security responsibilities. The library adds no
storage seam, durable harness, scheduler, command executor, or automatic replay of external effects.
The core and Bedrock assemblies ship together in `Darkspyre.Inference.Connect` 10.1.0, which depends
on `Darkspyre.Inference.Common` 10.1.0.
