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
- No sync-over-async, compatibility aliases or duplicate authority.
- Independent work runs concurrently wherever it can: through async calls, or on separate threads
  with Task.Run.
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
| Construction | A factory that acquires a service or opens a resource is asynchronous. `Bind` stays synchronous: it wraps a supplied connection around the service in hand, and the returned adapter's calls are async. Tensor creation, Start and Load remain separate; no implicit download or load. |
| Framework plumbing | Preserve IDisposable, IAsyncDisposable, ToString and generated object/record signatures. Preserve await using and secret-safe formatting. |
| Required-result callbacks | Await them: tool execution and source-credential resolution return Task<T>, carry cancellation and remain part of the owning operation. |
| Settings timing | Edits stay in the local settings object. Actions and model queries run against the local values. Only Save returns settings to the app. |

## 3. Settings editor flow

The settings editor works on a plain settings object. The settings manager is the service that
opens, acts on and saves it. Only Save returns settings to the app.

1. **Open.** The user picks a provider type. `settingsManager.OpenAsync` returns the settings object
   for that type: the wizard steps and the entry's current settings, or blank settings for a new
   entry.
2. **Edit.** A field edit changes only the local object. It makes no provider call and writes no
   storage. Show/hide and enable rules between fields run on the local object synchronously.
3. **Act.** Sign-in, tests, checks and model queries are async calls on the settings manager. Each
   call takes the settings object with its current values, including unsaved ones. Its result,
   including a sign-in credential, goes into the settings object.
4. **Save.** The Save button is enabled when every required field has a value, such as the
   endpoint URL, the API key and the model where the provider needs one. A provider with sign-in
   also requires a successful sign-in. Save is `settingsManager.SaveAsync(settings)`. It
   validates the settings object, hands the whole object to the app through the host's typed commit
   target once, and closes the dialog. Closing the dialog without Save discards the object, and the
   app receives nothing.

There is no setup session and no change event. Each button's handler receives its call's result
and updates the page. Sign-in reports its progress through the call's progress callback.

The settings object carries no revision number. Only the editor changes it, so no edit, action or
query can be stale. A model query result that arrives after the user changed a field it depends on
is dropped by the page: the click handler compares the values it queried with the current ones.

Blazor pages are event based. A field's change handler edits the local object synchronously. A
button's click handler is async: it awaits its connector calls in order, then updates the page
state with the result.

The implementation keeps typed fields and does not introduce an untyped property bag. A row leaves
the settings object only through Remove. A secret field starts as Keep: Save leaves a kept secret
unchanged, stores a replaced one and deletes a cleared one. A parse error blocks Save. The editor
reports unsaved changes so the host can warn before closing. Save keeps expected-version comparison
against the app's stored entry and uncertain-commit semantics. The settings object is plain data
and is not disposable. Every settings-manager call takes a cancellation token, and closing the
dialog cancels running calls.

Section 5.3 covers the Tensor setup buttons that change files or a running instance.

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
MoveNextAsync; subsequent moves on prompt and turn streams preserve incremental delivery and
backpressure. Async iterator
implementations use [EnumeratorCancellation] to combine method and enumeration cancellation.
Callers dispose abandoned enumerators. Native resources remain owned until execution settles and
the enumerator releases them. No Task wrapper, eager generation or full-result buffering is needed.
Framework MoveNextAsync and DisposeAsync retain their standard ValueTask signatures.

A stream method never throws from the call itself. Every error, including an unsupported
operation and a disposed session, surfaces on the first MoveNextAsync. A caller wraps the
`await foreach` in one `try`.

Each read of a returned stream is a new call. Reading one StreamAsync stream twice sends the
prompt twice. An agent session runs one turn at a time. A RunTurnAsync read that starts while a turn
on the same session is running or paused fails on its first read with "The session is running or
paused." ContinueTurnAsync resumes the paused turn itself. It succeeds when its turn ID and pause
ID match the paused turn and that turn is not already running. Any other continuation fails with
"The run or pause is stale, consumed, or active." The session refuses in both cases and does not
queue.

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
| Common | Open, act on and save settings through the settings manager; inference requests | DTO construction, field values, typed local validation, secret-edit/value factories, formatting |
| Connect | Provider/service acquisition, live capabilities, setup creation, authentication start (including `StartClaudeLoginAsync`), operational listener creation | `Bind` adapters over a supplied connection, credential serialization/parsing, capability-record projection, host-local DI collection composition |
| Tensor | Engine/setup acquisition, shared catalog registration/query, pack operations, live observations, lifecycle | Pack-definition construction/validation, immutable artifact lookup, credential factories, local metadata calculations |

Review concrete constructors and properties individually. Fixed descriptors and identity values already
owned by a returned object are not asynchronous lookups. Construction that opens a resource, such as the
loopback redirect listener, moves to an async factory. DI must not block on asynchronous initialization. No metadata query implicitly installs or loads weights.

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

Load, Unload and Stop return once the instance accepts the request (section 5.3). Graceful teardown waits for requests; urgency
cancels requests but must still join native execution before freeing memory. Stop remains terminal.
Keep explicit framework cleanup.

### 5.2 Common, Connect and Settings.UI public members

This list covers every public synchronous member in `Common/src`, `Connect/src` and
`UI/src/Settings` that touches a service or live state. Section 5.3 covers Tensor.

**Becomes async**

| Member | Change |
|---|---|
| `CreateSetupSession` on `LlmProviderService`, `ImageProviderService`, `AudioProviderService`, `TtsProviderService` | Replaced by `settingsManager.OpenAsync`, which returns the settings object: steps plus current or blank settings (section 3). |
| `LoopbackRedirectListener` constructor | Opens sockets. Moves to an async factory. |
| `LlmProviderService.StartClaudeLogin` (both overloads) | Becomes `StartClaudeLoginAsync`. Starting a sign-in is authentication start. |

**Removed by the settings editor flow**

| Member | Replacement |
|---|---|
| `IInferenceSetupSession` and `InferenceSetupSession<T>` | The settings object and the settings manager. |
| `IInferenceSetupSession.Snapshot`, `SetupSnapshot` | The settings object returned by `OpenAsync`. |
| `IInferenceSetupSession.Changed` | None. Button handlers receive their call's result. |
| `IInferenceSetupSession.ValidateAsync` | Local validation on the settings object, and validation inside `SaveAsync`. |
| `IInferenceSetupSession.CommitAsync()` | `settingsManager.SaveAsync(settings)`. |
| `SetupSnapshot.Revision`, `IsBusy`, `IsClosed` | None. The settings object has no revision. |
| Busy, Stale and Closed states of `SetupEditResult` | None. A local edit cannot be busy, stale or closed. |
| Revision binding of setup actions (`BindAction(revision, …)`) | Actions are settings-manager calls that take the settings object. |
| `SetupChoiceQuery.ExpectedRevision`, Stale state of `SetupChoicePage` | The page drops a query result whose input values changed. |

**Changes behaviour, signature unchanged**

| Member | Change |
|---|---|
| `SetupValueField<T>.SetValue`, `SetupSecretField.SetValue`, `SetupRepeatableGroupField.Add`/`Remove` | Stay synchronous. They edit the local settings object and no longer call the session's `Edit`. |
| Bedrock and Claude `ContinueTurnAsync`; Bedrock and managed `ObserveAsync` | Report "not supported" and "disposed" on the first read, not on the call. |
| Claude and Bedrock `RunTurnAsync` | Refuse a second turn on a running session. They no longer wait on the turn gate. |

**Stays synchronous**

| Member | Reason |
|---|---|
| `ILlmAgentSession.ThreadId`, `SessionId` | Set in the constructor and read-only in every provider. |
| `SupportedProviders`, `SupportedTransports`, `SetupDefinitions`, `Transport`, `ProviderDefinition`, `DisplayName` | Fixed definitions. |
| `ILlmClient.GetCapabilities` | Reads the supplied connection. |
| `Bind` on the four provider services, `ImageProviderService.GetRequiredTransport` | Wraps a supplied connection around the service in hand. The adapter's calls are async. |
| `LlmCredential.Serialize`/`Parse`, `LlmRedirectResult.ParsePastedCode`, `SetupConfigurationIdentity.Validate`, `LlmToolOutput.FromText` | Work on values in hand. |
| `AddLlmConnect`, `AddLlmConnectBedrock`, `AddImageConnect`, `AddAudioConnect`, `AddTtsConnect`, service constructors | DI registration and construction with no I/O. |
| Bedrock AWS profile reads inside `GetProfilesAsync` and credential resolution | The AWS SDK has no async form. The async method calls them directly. |

### 5.3 Tensor native work

Tensor runs models through three native libraries. Qwen and Supra use TensorSharp. Laya uses ONNX
Runtime. Whisper uses Whisper.net. Supra and Laya tokenize through ML.Tokenizers. Inference uses
TensorSharp and ML.Tokenizers through the public API they already provide and requires no change
to either.

**Native work runs on a background thread.** GPU and CPU compute has no async form. This covers
backend and device initialization, CUDA kernel compilation, weight upload, model steps, ONNX
session creation and `Run`, and native teardown. TensorSharp and ML.Tokenizers also read model,
weight and tokenizer files synchronously. Every public Tensor method that performs this work runs
it with `Task.Run`, so no Tensor method blocks its caller. This applies to load, generate, decide,
measure, unload, stop and dispose. Cancellation takes effect between steps: a native step that has
started finishes before the method observes cancellation. TensorSharp stops checking cancellation
early in a Qwen load, so a cancelled load finishes on its background thread and is then unloaded.
Laya runs synchronous `InferenceSession.Run` on the background thread and cancels it through
`RunOptions.Terminate`.

**Metadata reads.** TensorSharp parses GGUF, safetensors and torch headers from a synchronous
`Stream`. The Inference metadata reader downloads the header bytes asynchronously first, up to the
parser's 16 MB limit, and passes the parser an in-memory stream. No blocking wait remains.

**Backend checks.** The prerequisites check confirms that a backend is present without starting
it. It calls `GgmlBasicOps.CanInitializeBackend` for GGML backends, `MlxBackend.IsAvailable` for
MLX and `CudaBackend.IsAvailable` for direct CUDA. A device that is present but broken fails on
Load. The first GGML backend type a process initializes stays fixed until the process exits. Stop,
Dispose, a new instance and `GgmlBasicOps.RecreateBackend` keep that type. Changing from one GGML
type to another requires a new process. Changing from GGML to MLX, direct CUDA or managed CPU uses
Stop and a new instance.

**Instance lifecycle.** `ITensorModelInstance` raises `EngineStatusChanged` with the model, the
backend, the state, the time and, for Failed, the error. The states are Starting, Started,
Loading, Loaded, Unloading, Unloaded, Stopping, Stopped and Failed. The instance raises the event
and moves on. A listener with async work posts it with `_ = ...` and returns. The instance catches
a listener's exception.

- `StartAsync`, `LoadAsync`, `UnloadAsync` and `StopAsync` validate their input, return once the
  engine accepts the request, and report the outcome through `EngineStatusChanged`. A model that
  is not installed or not supported fails on the call.
- `GetObservationAsync` returns the instance's current model, backend and state. It replaces the
  synchronous `Observation` property.
- The instance reports Unloaded or Stopped only after native work has released model memory.

**Tensor setup buttons.** Import and Install write model files to the pack store. The files stay
when the settings dialog closes without Save. Load, Unload and Stop act on the host's running
instance in real time. The inspect, check and verify buttons read files and headers only.

**Qwen streams.** Disposing a Qwen stream before it completes aborts its engine request, the same
as cancellation.

**Concurrency limits.** GGML runs one compute step at a time under the model's GPU compute lock. A
Supra model uses one CUDA stream. Concurrent work comes from file reading, tokenization and CPU
work running beside GPU work.

## 6. Verification

All in-repository implementations, tests, examples, tools and UI.Settings consumers use the same
public contracts. No parallel legacy signature is exposed.

- Streaming: progressive output, no eager generation, backpressure on prompt and turn streams,
  a non-blocking ObserveAsync feed, terminal events, every
  error on the first read, a second turn on a running session is refused,
  cancellation, partial failure and abandoned-enumerator cleanup.
- Required-result callbacks: await completion/fault/cancellation; preserve tool and credential policy.
- Settings editor: edits and show/hide rules make no provider call; actions and model queries use
  unsaved values; Save enables once required fields are filled and any sign-in succeeds,
  then saves and closes the dialog;
  nothing reaches the app without Save; closing without Save discards everything;
  secret redaction.
- Lifecycle: failed/cancelled load, urgent/graceful stop, repeated cleanup, gate/lease ownership.
- Tensor: no public method runs native work on the caller's thread; cancellation between steps;
  Load, Unload and Stop return on acceptance and report through `EngineStatusChanged`; Failed
  carries the error; the prerequisites check starts no backend; disposing an unfinished Qwen
  stream aborts its engine request; the metadata reader holds no blocking wait.
- Public API: every member in section 5.2 has its listed signature and behaviour. Compile external
  implementations of changed contracts.
- All three solutions: zero-warning Debug/Release builds and deterministic tests; format and
  dependency-vulnerability checks. No paid provider calls or downloaded models as gates.

Common, Connect, Tensor and UI.Settings share the version in Directory.Build.props and exact
Common package dependency. These boundaries do not introduce application-owned persistence,
provider selection or lifecycle scheduling into the libraries.
