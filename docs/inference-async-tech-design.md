# Inference async public-contract technical design

Target contract: **11.10.x**.

## 1. Service boundary

**Crossing to another service is asynchronous; working on an object already in hand is synchronous.**

- Request objects, snapshots, service instances and live observations asynchronously.
- Read and modify detached local objects normally. A snapshot's name, a request's model ID,
  validation of local values, pure projections and value factories do not require Task wrappers.
- Submit changes through the owning provider/session asynchronously. Local edits never mutate
  authoritative provider state through hidden synchronous callbacks.
- Single-result service operations, including inference, authentication, discovery, installation,
  loading, saving and publication, use Task/Task<T>. Streams use IAsyncEnumerable<T> directly.
  A completed Task is valid for a no-op/in-memory implementation of a service operation.
- No gratuitous Task.Run, sync-over-async, compatibility aliases or duplicate authority.
- Connect remains out-of-process access; Tensor remains in-process execution. Hosts still own
  applications, persistence, API translation and lifecycle scheduling.

The settings editor keeps every edit in a local settings object. Only Save returns settings to the
app.

Related authorities:

- [Repository laws](../.roles/laws.md) and [package rules](../.roles/design-rules.md).
- [Connect integration](../Connect/README.md) and [managed agents](../Connect/docs/enhancements.md).
- [Setup/audio](provider-setup-and-audio-technical-design.md) and
  [settings editor](settings.ui.tech-design.md).
- [Tensor integration](../Tensor/README.md), [model locations](tensor-model-locations.tech-design.md)
  and [Laya runtime](laya-onnx-runtime.tech-design.md).

## 2. Contract rules

| Boundary | Behavior |
|---|---|
| Streaming | Return IAsyncEnumerable<T> directly and consume with await foreach. Perform setup lazily during enumeration; use EnumeratorCancellation on async iterator implementations. |
| DTOs and snapshots | Ordinary properties, local edits, pure helpers and construction remain synchronous. Fetching/replacing authoritative state is asynchronous. |
| Construction | Operational objects are obtained through asynchronous factories. Tensor creation, Start and Load remain separate; no implicit download or load. |
| Framework plumbing | Preserve IDisposable, IAsyncDisposable, ToString and generated object/record signatures. Preserve await using and secret-safe formatting. |
| Required-result callbacks | Await them: tool execution and source-credential resolution return Task<T>, carry cancellation and remain part of the owning operation. |
| Settings timing | Edits stay in the local settings object. Actions and model queries run against the local values. Only Save returns settings to the app. |

## 3. Settings editor flow

The settings editor works on a local settings object. Only Save returns settings to the app.

1. **Open.** The user picks a provider type. An async call returns the wizard for that type: its
   steps and the entry's current settings, or blank settings for a new entry.
2. **Edit.** A field edit changes only the local object. It makes no provider call and writes no
   storage. Show/hide and enable rules between fields run on the local object synchronously.
3. **Act.** Sign-in, tests, checks and model queries are async connector calls. Each call takes the
   current local values, including unsaved ones. Its result, including a sign-in credential, goes
   into the local object.
4. **Save.** The Save button is enabled when every required field has a value, such as the
   endpoint URL, the API key and the model where the provider needs one. A provider with sign-in
   also requires a successful sign-in. Save is one async call. It
   validates the local object, hands the whole object to the app through the host's typed commit
   target once, and closes the dialog. Closing the dialog without Save discards the object, and the
   app receives nothing.

The session has no change event. Each button's handler receives its call's result and updates the
page. Sign-in reports its progress through the action's progress callback.

Blazor pages are event based. A field's change handler edits the local object synchronously. A
button's click handler is async: it awaits its connector calls in order, then updates the page
state with the result.

The implementation keeps typed fields and does not introduce an untyped property bag. Omission
never means delete: repeatable-row removal stays explicit. A parse error blocks Save. The editor
reports unsaved changes so the host can warn before closing. Save keeps expected-version comparison
and uncertain-commit semantics. Disposal cancels and joins owned work and clears secret state; it
never stops a host-owned Tensor instance.

This section covers Connect providers and Settings.UI. Tensor setup has its own requirements,
which this section does not cover.

## 4. Streaming and callback implementation

Public inference/agent stream methods return IAsyncEnumerable<T> directly:

- Common ILlmInference.StreamAsync and CompleteAsStreamAsync.
- Connect ILlmClient/LlmProviderService.StreamAsync.
- Connect ILlmAgentSession.RunTurnAsync, ContinueTurnAsync and ObserveAsync.
- Tensor Qwen StreamAsync and Whisper TranscribeStreamAsync.

```csharp
await foreach (var item in inference.StreamAsync(request, cancellationToken))
{
    // Read the returned event's properties normally.
}
```

Returning a sequence does not execute or complete the prompt. Setup runs lazily on the first
MoveNextAsync; subsequent moves preserve incremental delivery and backpressure. Async iterator
implementations use [EnumeratorCancellation] to combine method and enumeration cancellation.
Callers dispose abandoned enumerators. Native resources remain owned until execution settles and
the enumerator releases them. No Task wrapper, eager generation or full-result buffering is needed.
Framework MoveNextAsync and DisposeAsync retain their standard ValueTask signatures.

Each agent session's `ObserveAsync` feed never slows the provider. Events wait in that session's
queue until its reader takes them. A session's feed allows one reader at a time and ends when that
session is disposed. The host is that reader and distributes the events to its own consumers.

LlmAgentToolHandler returns Task<LlmToolOutput>. TensorSourceAccess returns
Task<TensorSourceCredential?>. Hosts return completed Tasks for immediate results or await real work.
Framework ValueTask disposal is unchanged. Source credentials retain exact-origin/redirect restrictions;
tool ordering, approvals, pause/continue accounting and error handling retain existing semantics.

## 5. Provider and Tensor boundaries

Classify by ownership, not by how quickly an implementation currently returns:

| Area | System-facing work | Ordinary local work |
|---|---|---|
| Common | Fetch/apply setup snapshots and inference requests | DTO construction, field values, typed local validation, secret-edit/value factories, formatting |
| Connect | Provider/service acquisition, live capabilities, setup creation, authentication start, operational listener creation | Credential serialization/parsing, capability-record projection, host-local DI collection composition |
| Tensor | Engine/setup acquisition, shared catalog registration/query, pack operations, live observations, lifecycle | Pack-definition construction/validation, immutable artifact lookup, credential factories, local metadata calculations |

Review concrete constructors and properties individually. Fixed descriptors and identity values already
owned by a returned object are not asynchronous lookups. Operational construction moves to factories;
DI must not block on asynchronous initialization. No metadata query implicitly installs or loads weights.

### 5.1 Lease acquisition, artifact lookup and file access

These are three distinct operations, not one asynchronous category:

| Operation | Contract | Reason |
|---|---|---|
| Ask the pack manager to acquire a model-pack lease | `AcquireAsync` | Crosses to the manager that owns access and the lease lifetime. |
| Find the tokenizer/model reference in an acquired lease | `GetRequiredArtifact` stays synchronous | Reads the lease's existing `Artifacts` dictionary; it neither opens a file nor calls a store. |
| Open or read the referenced artifact through storage | Asynchronous storage operation | Crosses to the store/file-access service to obtain contents. |

A lease containing a file reference does not necessarily contain the file's bytes. Returning that
already-held reference is ordinary object access. If an operation instead resolves an artifact by
calling a store or another service, that service-facing operation must be asynchronous; do not hide
such work inside a synchronous getter or the local lookup method.

The same rule keeps `TensorModelDefinitionValidator.Validate`, `TensorResolvedPack.Create` and
`SetupConfigurationIdentity.Validate` synchronous: they validate or construct values already in hand.
By contrast, registering a pack with its manager remains an asynchronous publication boundary.
Classification depends on responsibility and service access, not execution speed, an in-memory
implementation, or whether the method name contains “Get”, “Create” or “Validate”.

Start/Load/request/Unload/Stop semantics do not change. Graceful teardown waits for requests; urgency
cancels requests but must still join native execution before freeing memory. Stop remains terminal.
Keep explicit framework cleanup and never report completed cleanup while native work still owns memory.

## 6. Verification

All in-repository implementations, tests, examples, tools and UI.Settings consumers use the same
public contracts. No parallel legacy signature is exposed.

- Streaming: progressive output, no eager generation, backpressure, terminal events,
  cancellation, partial failure and abandoned-enumerator cleanup.
- Required-result callbacks: await completion/fault/cancellation; preserve tool and credential policy.
- Settings editor: edits and show/hide rules make no provider call; actions and model queries use
  unsaved values; Save enables once required fields are filled and any sign-in succeeds,
  then saves and closes the dialog;
  nothing reaches the app without Save; closing without Save discards everything;
  secret redaction.
- Lifecycle: failed/cancelled load, urgent/graceful stop, repeated cleanup, gate/lease ownership.
- Public API inventory: classify remaining methods against the approved boundary, not a blanket
  “all non-Task methods fail” rule. Compile external implementations of changed contracts.
- All three solutions: zero-warning Debug/Release builds and deterministic tests; format and
  dependency-vulnerability checks. No paid provider calls or downloaded models as gates.

Common, Connect, Tensor and UI.Settings share the version in Directory.Build.props and exact
Common package dependency. These boundaries do not introduce application-owned persistence,
provider selection or lifecycle scheduling into the libraries.
