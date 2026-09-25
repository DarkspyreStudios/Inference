# Inference async public-contract technical design

Status: **Implementation approved; migration in progress on `feature/11.10-async-boundaries`.**

Target: **11.10.x**, breaking-contract migration. No package publication is authorized.

Baseline: source `1544cb5`, published family `11.9.2-rc.1`; audited 2026-09-25.

## 1. Approved boundary

The owner clarified the original “every public method” requirement: **crossing to another service is
asynchronous; working on an object already in hand is synchronous**. The initial proposal to turn
every helper and property into an asynchronous method was too broad and is superseded.

- Request objects, snapshots, service instances and live observations asynchronously.
- Read and modify detached local objects normally. A snapshot's name, a request's model ID,
  validation of local values, pure projections and value factories do not require Task wrappers.
- Submit changes through the owning provider/session asynchronously. Local edits never mutate
  authoritative provider state through hidden synchronous callbacks.
- Operations that perform inference, authentication, discovery, installation, loading, saving or
  shared-state mutation use Task/Task<T>. A completed Task is valid for a no-op/in-memory operation.
- No gratuitous Task.Run, sync-over-async, compatibility aliases or duplicate authority.
- Connect remains out-of-process access; Tensor remains in-process execution. Hosts still own
  applications, persistence, API translation and lifecycle scheduling.

The user explicitly approved implementation on a new branch. Subsequent confirmation settled
settings timing: **submit local field edits to the session immediately to refresh dependent controls;
Save is a separate persistence operation**. No new human decision is outstanding.

Related authorities:

- [Repository laws](../.roles/laws.md) and [package rules](../.roles/design-rules.md).
- [Connect integration](../Connect/README.md) and [managed agents](../Connect/docs/enhancements.md).
- [Setup/audio](provider-setup-and-audio-technical-design.md) and
  [settings editor](settings.ui.tech-design.md).
- [Tensor integration](../Tensor/README.md), [model locations](tensor-model-locations.tech-design.md)
  and [Laya runtime](laya-onnx-runtime.tech-design.md).

## 2. Resolved decisions

| Boundary | Approved behavior |
|---|---|
| Streaming | Acquire with Task<IAsyncEnumerable<T>>, then consume with await foreach. Never buffer the whole result to manufacture a Task return. |
| DTOs and snapshots | Ordinary properties, local edits, pure helpers and construction remain synchronous. Fetching/replacing authoritative state is asynchronous. |
| Construction | Operational objects are obtained through asynchronous factories. Tensor creation, Start and Load remain separate; no implicit download or load. |
| Framework plumbing | Preserve IDisposable, IAsyncDisposable, ToString and generated object/record signatures. Preserve await using and secret-safe formatting. |
| Notifications | Hand off without waiting. Observer latency/failure must not delay, roll back or change the reported result of a completed operation. |
| Required-result callbacks | Await them: tool execution and source-credential resolution return Task<T>, carry cancellation and remain part of the owning operation. |
| Settings timing | Apply edits asynchronously to the session for dependent metadata; persist only on the separate Save/CommitAsync call. |

Non-blocking notifications are invalidation hints, not an ordered change log or another state owner.
Observers fetch the latest snapshot. Required-result callbacks and stream backpressure are not
notifications and must not become fire-and-forget.

## 3. Settings transaction

Target Common boundary:

```csharp
Task<SetupSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
Task<SetupEditResult> ApplySnapshotAsync(
    SetupSnapshot snapshot, CancellationToken cancellationToken = default);
Task<SetupValidation> ValidateAsync(CancellationToken cancellationToken = default);
Task<SetupCommitResult> CommitAsync(CancellationToken cancellationToken = default);
```

The concrete implementation must preserve typed fields rather than introduce an untyped property bag.
A fetched snapshot contains local editable values and the originating session/revision identity.
SetValue/Add/Remove are local edits; they do not synchronously invoke the provider or save storage.
The submit boundary validates typed edits, ownership, revision, visibility and availability before
publishing changes. Omission never means delete: repeatable-row removal remains explicit.

Apply updates only the session draft and returns after publication. A successful submission advances
revision once and invalidates dependent choices, validation and readiness. Busy/stale/closed/invalid
submissions do not partially mutate the draft. Cancellation before publication prevents application;
once published, a later cancellation does not pretend to roll back it.

The editor submits the edited snapshot, awaits the result and fetches the refreshed snapshot.
Changing repository therefore refreshes revision/model choices without persisting the configuration.
Unsaved input must not be silently discarded on a notification or stale apply. Existing parsing-error
Save blocking, pending secret edits, callback retention, dirty-close signals and latest-choice handling
remain mandatory.

Provider-owned computation and publication remain serialized. Do not await host callbacks under the
state monitor. Implementation hooks which only compute local values can remain synchronous.
There is no need to make every protected hook asynchronous solely because it is extensible.

Commit validates the current session draft, freezes conflicting mutation and awaits the host's
existing typed commit target once. Preserve expected-version comparison and uncertain-commit
semantics. Observer failure is unrelated to persistence success. Disposal closes admission,
cancels/joins owned work and clears secret state; it never stops a host-owned Tensor instance.

## 4. Streaming and callback implementation

All public inference/agent stream methods return Task<IAsyncEnumerable<T>>:

- Common ILlmInference.StreamAsync and CompleteAsStreamAsync.
- Connect ILlmClient/LlmProviderService.StreamAsync.
- Connect ILlmAgentSession.RunTurnAsync, ContinueTurnAsync and ObserveAsync.
- Tensor Qwen StreamAsync and Whisper TranscribeStreamAsync.

```csharp
var events = await inference.StreamAsync(request, cancellationToken);
await foreach (var item in events.WithCancellation(cancellationToken))
{
    // Read the returned event's properties normally.
}
```

Acquisition obtains a lazy sequence; it does not mean the prompt has completed or that a live
observation sequence has ended. Current lazy execution remains on enumeration: no extra inference,
buffering, provider fallback or speculative work is introduced. Enumerator cancellation and disposal
continue to govern in-flight execution and native-memory lifetime. Internal process/native iterators
can remain IAsyncEnumerable; they are not alternate public routes.

LlmAgentToolHandler becomes Task<LlmToolOutput>. TensorSourceAccess becomes
Task<TensorSourceCredential?>. Hosts return completed Tasks for immediate results or await real work.
Framework ValueTask disposal is unchanged. Source credentials retain exact-origin/redirect restrictions;
tool ordering, approvals, pause/continue accounting and error handling retain existing semantics.

## 5. Remaining provider and Tensor migration

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

The appendices retain the original audit as historical inventory, not as an instruction to async-wrap
local helpers. Their initial proposed names are superseded by the approved ownership boundary above.

## 6. Migration and verification

Implement coherent, compiling units; update all in-repository implementations, tests, examples,
tools and UI.Settings consumers for each changed seam. No parallel legacy signature remains.
External application repositories are not authorized for modification.

- Stream acquisition: progressive output, no eager generation, backpressure, terminal events,
  cancellation, partial failure and abandoned-enumerator cleanup.
- Required-result callbacks: await completion/fault/cancellation; preserve tool and credential policy.
- Snapshots: independent local edits, typed submission, one revision per batch, stale/busy/closed
  rejection, dependent choices, secret redaction, no persistence on Apply.
- Notifications: blocked/throwing observers cannot block operations; reentrancy does not deadlock;
  queued invalidations do not create a second state owner.
- Lifecycle: failed/cancelled load, urgent/graceful stop, repeated cleanup, gate/lease ownership.
- Public API inventory: classify remaining methods against the approved boundary, not a blanket
  “all non-Task methods fail” rule. Compile external implementations of changed contracts.
- All three solutions: zero-warning Debug/Release builds and deterministic tests; format and
  dependency-vulnerability checks. No paid provider calls or downloaded models as gates.

Four packages remain synchronized through Directory.Build.props. The target is 11.10.x, with the
repository's existing prerelease convention. Do not create packages or publish until separately
authorized, and do not advertise an incomplete branch as a released API.

## 7. Implementation progress

- [x] Original public surface audited.
- [x] Owner decisions resolved, including immediate draft submission versus durable Save.
- [ ] Stream acquisition and required-result callbacks migrated, verified and committed.
- [ ] Detached snapshot/apply boundary, non-blocking notifications and all settings consumers.
- [ ] Remaining provider factories, system queries and Tensor operations.
- [ ] Final API audit, examples, documentation and complete Debug/Release verification.
- [ ] Coordinated version update; publish only with separate authorization.

## Historical audit inventory

The following tables preserve the baseline audit and initial proposals for traceability.
**Initial async-everything proposals are not the approved implementation scope.** Use sections 1–6
above to distinguish system access from local object work; source links refer to the audited baseline.

## Appendix A. Every synchronous public method

The initial proposals below are historical, not instructions to append Async or CancellationToken
to local operations. Explicit corrections identify confirmed synchronous methods; sections 1–5
govern all remaining classifications. Constructors and property getters are not counted here.

### Common

| Existing signature | Proposed operation | Source |
|---|---|---|
| `InferenceCapability InferenceTaskExtensions.GetCapability(this InferenceTask task)` | `GetCapabilityAsync → Task<InferenceCapability>` | [Inference.cs:29](../Common/src/Darkspyre.Inference.Common/Inference.cs) |
| `bool LlmToolOutputResource.IsTextMediaType(string mediaType)` | `IsTextMediaTypeAsync → Task<bool>` | [LlmToolOutput.cs:53](../Common/src/Darkspyre.Inference.Common/LLM/LlmToolOutput.cs) |
| `LlmToolOutput LlmToolOutput.FromText(string text, bool isError = false)` | `FromTextAsync → Task<LlmToolOutput>` | [LlmToolOutput.cs:67](../Common/src/Darkspyre.Inference.Common/LLM/LlmToolOutput.cs) |
| `void SetupConfigurationIdentity.Validate()` | **Corrected: retain synchronous `Validate`; validates local data.** | [SetupModels.cs:43](../Common/src/Darkspyre.Inference.Common/Setup/SetupModels.cs) |
| `SetupObservation SetupObservation.Known(bool value, string? reason = null)` | `KnownAsync → Task<SetupObservation>` | [SetupModels.cs:70](../Common/src/Darkspyre.Inference.Common/Setup/SetupModels.cs) |
| `SetupEditResult SetupValueField.SetValue(T value)` | `SetValueAsync → Task<SetupEditResult>` | [SetupFields.cs:26](../Common/src/Darkspyre.Inference.Common/Setup/SetupFields.cs) |
| `SetupSecretEdit SetupSecretEdit.Replace(string value)` | `ReplaceAsync → Task<SetupSecretEdit>` | [SetupFields.cs:83](../Common/src/Darkspyre.Inference.Common/Setup/SetupFields.cs) |
| `string SetupSecretEdit.ToString()` | D5: fixed CLR override; preserve formatting/redaction | [SetupFields.cs:89](../Common/src/Darkspyre.Inference.Common/Setup/SetupFields.cs) |
| `SetupEditResult SetupSecretField.SetValue(SetupSecretEdit value)` | `SetValueAsync → Task<SetupEditResult>` | [SetupFields.cs:98](../Common/src/Darkspyre.Inference.Common/Setup/SetupFields.cs) |
| `SetupEditResult SetupRepeatableGroupField.Add()` | `AddAsync → Task<SetupEditResult>` | [SetupFields.cs:151](../Common/src/Darkspyre.Inference.Common/Setup/SetupFields.cs) |
| `SetupEditResult SetupRepeatableGroupField.Remove(string rowId)` | `RemoveAsync → Task<SetupEditResult>` | [SetupFields.cs:152](../Common/src/Darkspyre.Inference.Common/Setup/SetupFields.cs) |

### Connect

| Existing signature | Proposed operation | Source |
|---|---|---|
| `IServiceCollection ServiceCollectionExtensions.AddLlmConnectBedrock(this IServiceCollection services)` | `AddLlmConnectBedrockAsync → Task<IServiceCollection>` | [ServiceCollectionExtensions.cs:7](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Bedrock/ServiceCollectionExtensions.cs) |
| `string LlmCredential.Serialize()` | `SerializeAsync → Task<string>` | [LlmCredential.cs:24](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmCredential.cs) |
| `LlmCredential? LlmCredential.Parse(string? raw)` | `ParseAsync → Task<LlmCredential?>` | [LlmCredential.cs:26](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmCredential.cs) |
| `LlmRedirectResult LlmRedirectResult.ParsePastedCode(string pasted)` | `ParsePastedCodeAsync → Task<LlmRedirectResult>` | [LlmAuth.cs:22](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmAuth.cs) |
| `LlmProviderCapabilities LlmProviderCapabilities.Apply(LlmCapabilityOverrides? overrides)` | `ApplyAsync → Task<LlmProviderCapabilities>` | [LlmProviderCapabilities.cs:70](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmProviderCapabilities.cs) |
| `IServiceCollection ServiceCollectionExtensions.AddLlmConnect(this IServiceCollection services)` | `AddLlmConnectAsync → Task<IServiceCollection>` | [ServiceCollectionExtensions.cs:12](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/ServiceCollectionExtensions.cs) |
| `LlmProviderCapabilities ILlmClient.GetCapabilities(LlmConnection connection, string? modelId = null)` | `GetCapabilitiesAsync → Task<LlmProviderCapabilities>` | [ILlmClient.cs:16](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/ILlmClient.cs) |
| `void LoopbackRedirectListener.Dispose()` | D2: Task-facing cleanup; framework disposition required | [LoopbackRedirectListener.cs:52](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LoopbackRedirectListener.cs) |
| `bool ILlmEmbeddingClient.Supports(LlmTransport transport)` | `SupportsAsync → Task<bool>` | [ILlmEmbeddingClient.cs:5](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/ILlmEmbeddingClient.cs) |
| `LlmAuthorizationSession IClaudePkceLogin.Start(string redirectUri)` | `StartAsync → Task<LlmAuthorizationSession>` | [ILlmAuthenticator.cs:24](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/ILlmAuthenticator.cs) |
| `SetupValidation ILlmSetupEnvironment.Validate(LlmConnection connection)` | `ValidateAsync → Task<SetupValidation>` | [LlmSetupSession.cs:20](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmSetupSession.cs) |
| `LlmSetupSession LlmProviderService.CreateSetupSession(LlmTransport transport, Common.Setup.SetupConfigurationIdentity identity, Common.Setup.ISetupCommitTarget<LlmSetupChanges> target, LlmConnection? existing = null, string? modelId = null)` | `CreateSetupSessionAsync → Task<LlmSetupSession>` | [LlmProviderService.cs:89](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmProviderService.cs) |
| `ILlmInference LlmProviderService.Bind(string connectionId)` | `BindAsync → Task<ILlmInference>` | [LlmProviderService.cs:103](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmProviderService.cs) |
| `LlmAuthorizationSession LlmProviderService.StartClaudeLogin()` | `StartClaudeLoginAsync → Task<LlmAuthorizationSession>` | [LlmProviderService.cs:178](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmProviderService.cs) |
| `LlmAuthorizationSession LlmProviderService.StartClaudeLogin(string redirectUri)` | `StartClaudeLoginAsync → Task<LlmAuthorizationSession>` | [LlmProviderService.cs:182](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmProviderService.cs) |
| `ImageSetupSession ImageProviderService.CreateSetupSession( ImageTransport transport, Common.Setup.SetupConfigurationIdentity identity, Common.Setup.ISetupCommitTarget<ImageSetupChanges> target, IImageWorkflowChoiceSource? workflows = null, ImageConnection? existing = null, string? workflowId = null)` | `CreateSetupSessionAsync → Task<ImageSetupSession>` | [ImageProviderService.cs:35](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageProviderService.cs) |
| `Darkspyre.Inference.Common.Image.IImageInference ImageProviderService.Bind( ImageConnection connection, ImageWorkflow workflow, TimeSpan? pollInterval = null)` | `BindAsync → Task<Darkspyre.Inference.Common.Image.IImageInference>` | [ImageProviderService.cs:43](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageProviderService.cs) |
| `IImageTransport ImageProviderService.GetRequiredTransport(ImageTransport transport)` | `GetRequiredTransportAsync → Task<IImageTransport>` | [ImageProviderService.cs:62](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageProviderService.cs) |
| `PreparedImageTransportRequest IImageTransport.Prepare(ImageTransportPreparationRequest request)` | `PrepareAsync → Task<PreparedImageTransportRequest>` | [ImageTransportContracts.cs:75](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageTransportContracts.cs) |
| `IServiceCollection ServiceCollectionExtensions.AddImageConnect(this IServiceCollection services)` | `AddImageConnectAsync → Task<IServiceCollection>` | [ServiceCollectionExtensions.cs:9](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ServiceCollectionExtensions.cs) |
| `IAudioGenerationClient IAudioProvider.Bind(AudioConnection connection)` | `BindAsync → Task<IAudioGenerationClient>` | [AudioContracts.cs:58](../Connect/src/Audio/Darkspyre.Inference.Connect.Audio/AudioContracts.cs) |
| `IAudioGenerationClient AudioProviderService.Bind(AudioConnection connection)` | `BindAsync → Task<IAudioGenerationClient>` | [AudioProviderService.cs:15](../Connect/src/Audio/Darkspyre.Inference.Connect.Audio/AudioProviderService.cs) |
| `AudioSetupSession AudioProviderService.CreateSetupSession(AudioTransport transport, SetupConfigurationIdentity identity, ISetupCommitTarget<AudioSetupChanges> target, AudioSetupConfiguration? existing = null)` | `CreateSetupSessionAsync → Task<AudioSetupSession>` | [AudioProviderService.cs:20](../Connect/src/Audio/Darkspyre.Inference.Connect.Audio/AudioProviderService.cs) |
| `IServiceCollection ServiceCollectionExtensions.AddAudioConnect(this IServiceCollection services)` | `AddAudioConnectAsync → Task<IServiceCollection>` | [AudioProviderService.cs:27](../Connect/src/Audio/Darkspyre.Inference.Connect.Audio/AudioProviderService.cs) |
| `IAudioGenerationClient TangoFluxHttpProvider.Bind(AudioConnection connection)` | `BindAsync → Task<IAudioGenerationClient>` | [TangoFluxHttpProvider.cs:24](../Connect/src/Audio/Darkspyre.Inference.Connect.Audio/TangoFluxHttpProvider.cs) |
| `ITtsClient TtsProviderService.Bind(TtsConnection connection)` | `BindAsync → Task<ITtsClient>` | [TtsProviderService.cs:15](../Connect/src/TTS/Darkspyre.Inference.Connect.TTS/TtsProviderService.cs) |
| `TtsSetupSession TtsProviderService.CreateSetupSession(TtsTransport transport, SetupConfigurationIdentity identity, ISetupCommitTarget<TtsSetupChanges> target, TtsSetupConfiguration? existing = null)` | `CreateSetupSessionAsync → Task<TtsSetupSession>` | [TtsProviderService.cs:20](../Connect/src/TTS/Darkspyre.Inference.Connect.TTS/TtsProviderService.cs) |
| `IServiceCollection ServiceCollectionExtensions.AddTtsConnect(this IServiceCollection services)` | `AddTtsConnectAsync → Task<IServiceCollection>` | [TtsProviderService.cs:27](../Connect/src/TTS/Darkspyre.Inference.Connect.TTS/TtsProviderService.cs) |
| `ITtsClient PocketTtsHttpProvider.Bind(TtsConnection connection)` | `BindAsync → Task<ITtsClient>` | [PocketTtsHttpProvider.cs:24](../Connect/src/TTS/Darkspyre.Inference.Connect.TTS/PocketTtsHttpProvider.cs) |
| `ITtsClient ITtsProvider.Bind(TtsConnection connection)` | `BindAsync → Task<ITtsClient>` | [TtsContracts.cs:54](../Connect/src/TTS/Darkspyre.Inference.Connect.TTS/TtsContracts.cs) |

### Tensor

| Existing signature | Proposed operation | Source |
|---|---|---|
| `TensorSetupSession QwenSetup.CreateSession(SetupConfigurationIdentity identity, ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs, TensorSetupConfiguration? existing = null, ITensorLlmInference? instance = null, IReadOnlyList<TensorModelPackDefinition>? offeredModels = null, ITensorModelSource? source = null, ITensorArtifactReader? reader = null)` | `CreateSessionAsync → Task<TensorSetupSession>` | [QwenSetup.cs:13](../Tensor/src/LLM/Darkspyre.Inference.Tensor.LLM.Qwen/QwenSetup.cs) |
| `TensorSetupSession SupraSetup.CreateSession(SetupConfigurationIdentity identity, ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs, TensorSetupConfiguration? existing = null, ITensorImageInference? instance = null, IReadOnlyList<TensorModelPackDefinition>? offeredModels = null, ITensorModelSource? source = null, ITensorArtifactReader? reader = null)` | `CreateSessionAsync → Task<TensorSetupSession>` | [SupraSetup.cs:13](../Tensor/src/Image/Darkspyre.Inference.Tensor.Image.Supra/SupraSetup.cs) |
| `PersistenceFileReference TensorModelPackLease.GetRequiredArtifact(string role)` | **Corrected: retain synchronous `GetRequiredArtifact`; local dictionary lookup (section 5.1).** | [TensorModelPackLease.cs:23](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackLease.cs) |
| `string TensorModelKey.ToString()` | D5: fixed CLR override; preserve formatting/redaction | [TensorModels.cs:28](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModels.cs) |
| `TensorModelPackDefinition TensorModelCatalog.Register(TensorModelPackDefinition definition)` | `RegisterAsync → Task<TensorModelPackDefinition>` | [TensorModels.cs:84](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModels.cs) |
| `TensorModelPackDefinition TensorModelCatalog.GetRequired(TensorModelKey key)` | `GetRequiredAsync → Task<TensorModelPackDefinition>` | [TensorModels.cs:107](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModels.cs) |
| `void TensorModelDefinitionValidator.Validate(TensorModelPackDefinition definition)` | **Corrected: retain synchronous `Validate`; validates a definition in hand.** | [TensorModels.cs:129](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModels.cs) |
| `TensorModelPackDefinition ITensorModelPackManager.Register(TensorModelPackDefinition definition)` | `RegisterAsync → Task<TensorModelPackDefinition>` | [TensorModelPackManager.cs:44](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackManager.cs) |
| `TensorModelPackDefinition ITensorModelPackManager.GetRequiredDefinition(TensorModelKey key)` | `GetRequiredDefinitionAsync → Task<TensorModelPackDefinition>` | [TensorModelPackManager.cs:46](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackManager.cs) |
| `TensorModelPackDefinition TensorModelPackManager.Register(TensorModelPackDefinition definition)` | `RegisterAsync → Task<TensorModelPackDefinition>` | [TensorModelPackManager.cs:104](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackManager.cs) |
| `TensorModelPackDefinition TensorModelPackManager.GetRequiredDefinition(TensorModelKey key)` | `GetRequiredDefinitionAsync → Task<TensorModelPackDefinition>` | [TensorModelPackManager.cs:110](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackManager.cs) |
| `void TensorModelPackManager.Dispose()` | D2: Task-facing cleanup; framework disposition required | [TensorModelPackManager.cs:425](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackManager.cs) |
| `void HuggingFaceModelSource.Dispose()` | D2: Task-facing cleanup; framework disposition required | [HuggingFaceModelSource.cs:234](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/HuggingFaceModelSource.cs) |
| `TensorModelPackDefinition TensorResolvedPack.Create(TensorModelCandidate candidate, TensorCapability capability, string architecture, IReadOnlySet<TensorBackendKind> backends, IReadOnlyList<TensorModelArtifactDefinition> artifacts, TensorModelPackDefinition? builtIn = null)` | **Corrected: retain synchronous `Create`; constructs a local definition.** | [TensorModelResolution.cs:39](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelResolution.cs) |
| `TensorModelArtifactDefinition TensorResolvedPack.Artifact(TensorSourceArtifact source, string role, string relativePath)` | `ArtifactAsync → Task<TensorModelArtifactDefinition>` | [TensorModelResolution.cs:100](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelResolution.cs) |
| `void TensorModelLifetime.MarkLoaded(TensorModelKey model, TensorBackendKind backend)` | `MarkLoadedAsync → Task` | [TensorModelLifetime.cs:27](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelLifetime.cs) |
| `void TensorModelLifetime.MarkUnloaded()` | `MarkUnloadedAsync → Task` | [TensorModelLifetime.cs:88](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelLifetime.cs) |
| `void TensorModelLifetime.Dispose()` | D2: Task-facing cleanup; framework disposition required | [TensorModelLifetime.cs:95](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelLifetime.cs) |
| `ITensorBackendProbe TensorBackendProbes.CpuCuda(Func<bool> isCudaAvailable)` | `CpuCudaAsync → Task<ITensorBackendProbe>` | [TensorBackendProbes.cs:6](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorBackendProbes.cs) |
| `TensorSourceCredential TensorSourceCredential.Bearer(string token)` | `BearerAsync → Task<TensorSourceCredential>` | [TensorModelDiscovery.cs:61](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelDiscovery.cs) |
| `TensorSourceCredential TensorSourceCredential.Basic(string username, string password)` | `BasicAsync → Task<TensorSourceCredential>` | [TensorModelDiscovery.cs:69](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelDiscovery.cs) |
| `string TensorSourceCredential.ToString()` | D5: fixed CLR override; preserve formatting/redaction | [TensorModelDiscovery.cs:81](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelDiscovery.cs) |
| `void TensorExecutionCoordinator.Dispose()` | D2: Task-facing cleanup; framework disposition required | [TensorExecutionCoordinator.cs:24](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorExecutionCoordinator.cs) |
| `TensorSetupSession LayaSetup.CreateSession(SetupConfigurationIdentity identity, ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs, IReadOnlyList<TensorModelPackDefinition> offeredModels, TensorSetupConfiguration? existing = null, ITensorDecisionInference? instance = null, ITensorModelSource? source = null, ITensorArtifactReader? reader = null)` | `CreateSessionAsync → Task<TensorSetupSession>` | [LayaSetup.cs:18](../Tensor/src/Decision/Darkspyre.Inference.Tensor.Decision.Laya/LayaSetup.cs) |
| `TensorSetupSession WhisperSetup.CreateSession(SetupConfigurationIdentity identity, ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs, TensorSetupConfiguration? existing = null, ITensorSpeechToTextInference? instance = null, IReadOnlyList<TensorModelPackDefinition>? offeredModels = null, ITensorModelSource? source = null, ITensorArtifactReader? reader = null)` | `CreateSessionAsync → Task<TensorSetupSession>` | [WhisperSetup.cs:13](../Tensor/src/STT/Darkspyre.Inference.Tensor.STT.Whisper/WhisperSetup.cs) |

## Appendix B. ValueTask methods and callbacks

| Existing method | Required mapping | Source |
|---|---|---|
| `ValueTask InferenceSetupSession.DisposeAsync()` | D2: Task cleanup with explicit framework-disposal design | [InferenceSetupSession.cs:301](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `ValueTask QwenLlmInference.DisposeAsync()` | D2: Task cleanup with explicit framework-disposal design | [QwenLlmInference.cs:241](../Tensor/src/LLM/Darkspyre.Inference.Tensor.LLM.Qwen/QwenLlmInference.cs) |
| `ValueTask SupraImageInference.DisposeAsync()` | D2: Task cleanup with explicit framework-disposal design | [SupraImageInference.cs:192](../Tensor/src/Image/Darkspyre.Inference.Tensor.Image.Supra/SupraImageInference.cs) |
| `ValueTask TensorModelPackLease.DisposeAsync()` | D2: Task cleanup with explicit framework-disposal design | [TensorModelPackLease.cs:28](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackLease.cs) |
| `ValueTask TensorModelLifetime.ExecutionLease.DisposeAsync()` | D2: Task cleanup with explicit framework-disposal design | [TensorModelLifetime.cs:132](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelLifetime.cs) |
| `ValueTask<IAsyncDisposable> TensorExecutionCoordinator.AcquireAsync( TensorModelKey key, TensorBackendKind backend, CancellationToken cancellationToken = default)` | Task acquisition; D2 also settles returned lease cleanup | [TensorExecutionCoordinator.cs:11](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorExecutionCoordinator.cs) |
| `ValueTask LayaDecisionInference.DisposeAsync()` | D2: Task cleanup with explicit framework-disposal design | [LayaDecisionInference.cs:208](../Tensor/src/Decision/Darkspyre.Inference.Tensor.Decision.Laya/LayaDecisionInference.cs) |
| `ValueTask WhisperSpeechToTextInference.DisposeAsync()` | D2: Task cleanup with explicit framework-disposal design | [WhisperSpeechToTextInference.cs:185](../Tensor/src/STT/Darkspyre.Inference.Tensor.STT.Whisper/WhisperSpeechToTextInference.cs) |

Named delegates: `LlmAgentToolHandler` -> Task<LlmToolOutput>; `TensorSourceAccess` -> Task<TensorSourceCredential?>. Both retain existing cancellation parameters. Public setup delegates, EventHandler and IProgress are mapped in sections 4, 5 and 7.

## Appendix C. Streaming signatures

All ten declarations are already asynchronous. D1 must preserve their streaming behavior, including inherited consumers; merely wrapping a fully buffered result is not a valid migration.

| Declaration | Source |
|---|---|
| `IAsyncEnumerable<LlmStreamEvent> ILlmInference.StreamAsync( LlmInferenceRequest request, CancellationToken cancellationToken = default)` | [LlmInference.cs:103](../Common/src/Darkspyre.Inference.Common/LLM/LlmInference.cs) |
| `IAsyncEnumerable<LlmStreamEvent> LlmInferenceExtensions.CompleteAsStreamAsync( LlmInferenceResult result, [EnumeratorCancellation] CancellationToken cancellationToken = default)` | [LlmInference.cs:137](../Common/src/Darkspyre.Inference.Common/LLM/LlmInference.cs) |
| `IAsyncEnumerable<LlmStreamEvent> ILlmClient.StreamAsync( LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default)` | [ILlmClient.cs:18](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/ILlmClient.cs) |
| `IAsyncEnumerable<LlmStreamEvent> LlmProviderService.StreamAsync( string connectionId, LlmChatRequest request, CancellationToken cancellationToken = default)` | [LlmProviderService.cs:306](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmProviderService.cs) |
| `IAsyncEnumerable<LlmAgentEvent> ILlmAgentSession.ContinueTurnAsync( LlmAgentTurnContinuation continuation, CancellationToken cancellationToken = default)` | [LlmAgent.cs:322](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmAgent.cs) |
| `IAsyncEnumerable<LlmAgentEvent> ILlmAgentSession.RunTurnAsync( LlmAgentTurnRequest request, CancellationToken cancellationToken = default)` | [LlmAgent.cs:326](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmAgent.cs) |
| `IAsyncEnumerable<LlmAgentEvent> ILlmAgentSession.ObserveAsync(CancellationToken cancellationToken = default)` | [LlmAgent.cs:341](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmAgent.cs) |
| `IAsyncEnumerable<LlmStreamEvent> QwenLlmInference.StreamAsync( LlmInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)` | [QwenLlmInference.cs:94](../Tensor/src/LLM/Darkspyre.Inference.Tensor.LLM.Qwen/QwenLlmInference.cs) |
| `IAsyncEnumerable<SpeechWindowTranscription> ITensorSpeechToTextInference.TranscribeStreamAsync(IAsyncEnumerable<SpeechAudioWindow> windows, SpeechToTextOptions options, CancellationToken cancellationToken = default)` | [SpeechTranscription.cs:39](../Tensor/src/STT/Darkspyre.Inference.Tensor.STT/SpeechTranscription.cs) |
| `IAsyncEnumerable<SpeechWindowTranscription> WhisperSpeechToTextInference.TranscribeStreamAsync(IAsyncEnumerable<SpeechAudioWindow> windows, SpeechToTextOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)` | [WhisperSpeechToTextInference.cs:110](../Tensor/src/STT/Darkspyre.Inference.Tensor.STT.Whisper/WhisperSpeechToTextInference.cs) |

## Appendix D. Protected synchronous hooks and helpers

These include overrides on sealed types; they are migration dependencies, not all independently callable public APIs. Map each to an awaited hook/helper or an approved internal-only implementation detail. Resolve D7 before changing Action/Func-based state mutation helpers.

| Declaration | Proposed mapping / dependency | Source |
|---|---|---|
| `IReadOnlyList<SetupStep> InferenceSetupSession.CreateSteps(long revision)` | `CreateStepsAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:47](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `IReadOnlyList<ISetupAction> InferenceSetupSession.CreateActions(long revision)` | `CreateActionsAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:48](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `SetupValidation InferenceSetupSession.ValidateDraft()` | `ValidateDraftAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:49](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `TChanges InferenceSetupSession.CreateChanges()` | `CreateChangesAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:50](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `void InferenceSetupSession.ClearSensitiveState()` | `ClearSensitiveStateAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:52](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `SetupStatus InferenceSetupSession.GetStatus()` | `GetStatusAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:53](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `SetupInteraction? InferenceSetupSession.GetInteraction()` | `GetInteractionAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:54](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `void InferenceSetupSession.OnDraftEdited()` | `OnDraftEditedAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:56](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `SetupObservation InferenceSetupSession.Stale(SetupObservation observation)` | `StaleAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:66](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `SetupEditResult InferenceSetupSession.Edit(long expectedRevision, Action change)` | `EditAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:70](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `void InferenceSetupSession.Publish(Action change)` | `PublishAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:87](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `TResult InferenceSetupSession.Read<TResult>(Func<TResult> read)` | `ReadAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:93](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `bool InferenceSetupSession.PublishIfCurrent(long revision, Action change)` | `PublishIfCurrentAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:98](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `ISetupAction InferenceSetupSession.BindAction(long revision, SetupActionDescriptor descriptor, Func<IProgress<SetupProgress>?, CancellationToken, Task<SetupActionResult>> execute)` | `BindActionAsync`; D7: setup owner/hook atomicity | [InferenceSetupSession.cs:110](../Common/src/Darkspyre.Inference.Common/Setup/InferenceSetupSession.cs) |
| `IReadOnlyList<SetupStep> LlmSetupSession.CreateSteps(long revision)` | `CreateStepsAsync`; D7: setup owner/hook atomicity | [LlmSetupSession.cs:90](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmSetupSession.cs) |
| `IReadOnlyList<ISetupAction> LlmSetupSession.CreateActions(long revision)` | `CreateActionsAsync`; D7: setup owner/hook atomicity | [LlmSetupSession.cs:241](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmSetupSession.cs) |
| `SetupInteraction? LlmSetupSession.GetInteraction()` | `GetInteractionAsync`; D7: setup owner/hook atomicity | [LlmSetupSession.cs:295](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmSetupSession.cs) |
| `SetupValidation LlmSetupSession.ValidateDraft()` | `ValidateDraftAsync`; D7: setup owner/hook atomicity | [LlmSetupSession.cs:402](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmSetupSession.cs) |
| `void LlmSetupSession.OnDraftEdited()` | `OnDraftEditedAsync`; D7: setup owner/hook atomicity | [LlmSetupSession.cs:432](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmSetupSession.cs) |
| `LlmSetupChanges LlmSetupSession.CreateChanges()` | `CreateChangesAsync`; D7: setup owner/hook atomicity | [LlmSetupSession.cs:441](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmSetupSession.cs) |
| `void LlmSetupSession.ClearSensitiveState()` | `ClearSensitiveStateAsync`; D7: setup owner/hook atomicity | [LlmSetupSession.cs:455](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmSetupSession.cs) |
| `IReadOnlyList<SetupStep> ImageSetupSession.CreateSteps(long revision)` | `CreateStepsAsync`; D7: setup owner/hook atomicity | [ImageSetup.cs:65](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageSetup.cs) |
| `IReadOnlyList<ISetupAction> ImageSetupSession.CreateActions(long revision)` | `CreateActionsAsync`; D7: setup owner/hook atomicity | [ImageSetup.cs:107](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageSetup.cs) |
| `SetupValidation ImageSetupSession.ValidateDraft()` | `ValidateDraftAsync`; D7: setup owner/hook atomicity | [ImageSetup.cs:158](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageSetup.cs) |
| `ImageSetupChanges ImageSetupSession.CreateChanges()` | `CreateChangesAsync`; D7: setup owner/hook atomicity | [ImageSetup.cs:206](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageSetup.cs) |
| `void ImageSetupSession.ClearSensitiveState()` | `ClearSensitiveStateAsync`; D7: setup owner/hook atomicity | [ImageSetup.cs:222](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageSetup.cs) |
| `IReadOnlyList<SetupStep> AudioSetupSession.CreateSteps(long revision)` | `CreateStepsAsync`; D7: setup owner/hook atomicity | [AudioSetupSession.cs:55](../Connect/src/Audio/Darkspyre.Inference.Connect.Audio/AudioSetupSession.cs) |
| `IReadOnlyList<ISetupAction> AudioSetupSession.CreateActions(long revision)` | `CreateActionsAsync`; D7: setup owner/hook atomicity | [AudioSetupSession.cs:115](../Connect/src/Audio/Darkspyre.Inference.Connect.Audio/AudioSetupSession.cs) |
| `SetupValidation AudioSetupSession.ValidateDraft()` | `ValidateDraftAsync`; D7: setup owner/hook atomicity | [AudioSetupSession.cs:156](../Connect/src/Audio/Darkspyre.Inference.Connect.Audio/AudioSetupSession.cs) |
| `AudioSetupChanges AudioSetupSession.CreateChanges()` | `CreateChangesAsync`; D7: setup owner/hook atomicity | [AudioSetupSession.cs:174](../Connect/src/Audio/Darkspyre.Inference.Connect.Audio/AudioSetupSession.cs) |
| `void AudioSetupSession.ClearSensitiveState()` | `ClearSensitiveStateAsync`; D7: setup owner/hook atomicity | [AudioSetupSession.cs:189](../Connect/src/Audio/Darkspyre.Inference.Connect.Audio/AudioSetupSession.cs) |
| `IReadOnlyList<SetupStep> TtsSetupSession.CreateSteps(long revision)` | `CreateStepsAsync`; D7: setup owner/hook atomicity | [TtsSetupSession.cs:42](../Connect/src/TTS/Darkspyre.Inference.Connect.TTS/TtsSetupSession.cs) |
| `IReadOnlyList<ISetupAction> TtsSetupSession.CreateActions(long revision)` | `CreateActionsAsync`; D7: setup owner/hook atomicity | [TtsSetupSession.cs:118](../Connect/src/TTS/Darkspyre.Inference.Connect.TTS/TtsSetupSession.cs) |
| `SetupValidation TtsSetupSession.ValidateDraft()` | `ValidateDraftAsync`; D7: setup owner/hook atomicity | [TtsSetupSession.cs:151](../Connect/src/TTS/Darkspyre.Inference.Connect.TTS/TtsSetupSession.cs) |
| `TtsSetupChanges TtsSetupSession.CreateChanges()` | `CreateChangesAsync`; D7: setup owner/hook atomicity | [TtsSetupSession.cs:169](../Connect/src/TTS/Darkspyre.Inference.Connect.TTS/TtsSetupSession.cs) |
| `void TtsSetupSession.ClearSensitiveState()` | `ClearSensitiveStateAsync`; D7: setup owner/hook atomicity | [TtsSetupSession.cs:183](../Connect/src/TTS/Darkspyre.Inference.Connect.TTS/TtsSetupSession.cs) |
| `string QwenModelResolver.CompanionPath(string role, string path, string primary)` | `CompanionPathAsync`; await from resolver orchestration | [QwenModelResolver.cs:14](../Tensor/src/LLM/Darkspyre.Inference.Tensor.LLM.Qwen/QwenModelResolver.cs) |
| `IReadOnlyList<TensorSourceArtifact> QwenModelResolver.SelectArtifacts(TensorModelCandidate candidate)` | `SelectArtifactsAsync`; await from resolver orchestration | [QwenModelResolver.cs:16](../Tensor/src/LLM/Darkspyre.Inference.Tensor.LLM.Qwen/QwenModelResolver.cs) |
| `IReadOnlyList<SetupStep> TensorSetupSession.CreateSteps(long revision)` | `CreateStepsAsync`; D7: setup owner/hook atomicity | [TensorSetupSession.cs:68](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorSetupSession.cs) |
| `IReadOnlyList<ISetupAction> TensorSetupSession.CreateActions(long revision)` | `CreateActionsAsync`; D7: setup owner/hook atomicity | [TensorSetupSession.cs:266](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorSetupSession.cs) |
| `SetupStatus TensorSetupSession.GetStatus()` | `GetStatusAsync`; D7: setup owner/hook atomicity | [TensorSetupSession.cs:408](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorSetupSession.cs) |
| `void TensorSetupSession.OnDraftEdited()` | `OnDraftEditedAsync`; D7: setup owner/hook atomicity | [TensorSetupSession.cs:420](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorSetupSession.cs) |
| `SetupValidation TensorSetupSession.ValidateDraft()` | `ValidateDraftAsync`; D7: setup owner/hook atomicity | [TensorSetupSession.cs:427](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorSetupSession.cs) |
| `TensorSetupChanges TensorSetupSession.CreateChanges()` | `CreateChangesAsync`; D7: setup owner/hook atomicity | [TensorSetupSession.cs:438](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorSetupSession.cs) |
| `void TensorSetupSession.ClearSensitiveState()` | `ClearSensitiveStateAsync`; D7: setup owner/hook atomicity | [TensorSetupSession.cs:442](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorSetupSession.cs) |
| `string TensorModelResolver.CompanionPath(string role, string path, string primary)` | `CompanionPathAsync`; await from resolver orchestration | [TensorModelResolver.cs:14](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelResolver.cs) |
| `IReadOnlyList<TensorSourceArtifact> TensorModelResolver.SelectArtifacts(TensorModelCandidate candidate)` | `SelectArtifactsAsync`; await from resolver orchestration | [TensorModelResolver.cs:30](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelResolver.cs) |
