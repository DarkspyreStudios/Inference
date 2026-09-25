# Inference async public-contract technical design

Status: **Draft for review — mapping in progress, not approved for implementation.**

Target: **11.10.x**, breaking-contract migration.

Baseline: source `1544cb5`, published family `11.9.2-rc.1`; audited 2026-09-25.

## 1. Authority and scope

The user requires every public-facing method across Common, Connect and Tensor to be asynchronous.
Public helpers, validators, factories, registration methods and capability queries are in scope;
being fast, in-memory or currently synchronous does not exempt an operation.
The user selected 11.10.x because this breaks contracts. Creating this design does not authorize
implementation, a version bump, packaging or publication.

This document maps the migration of:
- `Common/src/Darkspyre.Inference.Common`.
- Every shipped capability assembly in `Connect/src`.
- Every shipped capability assembly in `Tensor/src`.
- Their in-repository consumers, tests, examples, tools and release verification.
- UI.Settings as a consumer of Common's changed contracts, not as an expansion of the audit to every
  UI component method.

Connect still owns external connections; Tensor still owns in-process execution. Hosts own app
startup, API translation, storage, consent and application/session orchestration. No new transport,
agent scheduler, persistence owner or capability is introduced.

Read this alongside the current behavior authorities:
- [Repository laws](../.roles/laws.md) and [package rules](../.roles/design-rules.md).
- [Connect integration](../Connect/README.md) and [managed-agent design](../Connect/docs/enhancements.md).
- [Setup/audio design](provider-setup-and-audio-technical-design.md).
- [Settings editor design](settings.ui.tech-design.md).
- [Tensor integration](../Tensor/README.md), [model locations](tensor-model-locations.tech-design.md)
  and [Laya runtime](laya-onnx-runtime.tech-design.md).

These authorities describe current behavior, including synchronous and ValueTask signatures.
They must be updated together with implementation; their old signatures do not override the user's
new rule. This draft is not a claim that production already follows the target.

## 2. Audited baseline and counting rules

| Family | Synchronous methods | ValueTask methods | Async streams | Task methods |
|---|---:|---:|---:|---:|
| Common | 11 | 1 | 2 | 16 |
| Connect | 30 | 0 | 5 | 61 |
| Tensor | 25 | 7 | 3 | 81 |
| Total | **66** | **8** | **10** | **158** |

These are explicitly authored public method declarations on externally accessible types. Interface
and public implementation declarations count separately. Public members of internal/private types
are excluded; inherited methods are not multiplied by each consuming class.
The 158 Task-returning declarations already have an async return contract, even where their
implementation returns a completed Task without an `async` modifier.

Supplementary surface:
- 47 synchronous protected declarations: Common 14, Connect 22, Tensor 11. Many are overrides on sealed
  implementations; the extensible roots are the 14 setup-base helpers/hooks and 2 resolver-base hooks.
- Two named public ValueTask delegates: `LlmAgentToolHandler` and `TensorSourceAccess`.
- 613 explicitly declared public properties: Common 192, Connect 362, Tensor 59. This includes DTO
  properties, not just operational getters; compiler-generated positional record properties are extra.
- Constructors, synchronous callback parameters, events, inherited disposal and compiler-generated
  record/Object members require explicit treatment below. None is hidden by the method count.

Appendices map every audited synchronous/ValueTask/stream declaration and protected hook. Source
line numbers are baseline navigation aids, not stable identities. Re-run the audit against the
implementation starting commit before claiming the mapping is exhaustive for a later checkout.

## 3. Target operation rules

Proposed ordinary-operation shape:

```csharp
Task OperationAsync(..., CancellationToken cancellationToken = default);
Task<TResult> OperationAsync(..., CancellationToken cancellationToken = default);
```

- Keep the existing typed inputs, results, provider selection and error meanings unless a mapped
  boundary explicitly needs redesign.
- Convert public synchronous operation names to their Async form. Remove the old public sync form
  in the migration; do not leave dual APIs or compatibility aliases.
- Immediate computations can return completed Tasks. Do not add Task.Run merely to rename a method.
- Do not introduce sync-over-async in constructors, DI registration, getters or compatibility code.
- Callers await results; no fire-and-forget edits, cleanup, registration or authentication work.
- Cancellation is forwarded to work the library owns. An awaited result reports completion, not
  merely that work was queued.
- Existing internal synchronous pure implementation helpers may remain internal. Public/protected
  methods are not reclassified as internal solely to hide an operation the host needs.
- Argument validation, cancellation, fault observation and cleanup must be specified consistently
  for the final signatures; using the async keyword on every implementation is not the contract.

This is a draft mapping. The following language/framework boundaries still need a resolved technical
shape, not assumed exceptions.

## 4. Decisions required before implementation approval

| ID | Boundary | Required behavior / unresolved shape |
|---|---|---|
| D1 | Streaming | Preserve incremental delivery, cancellation, backpressure, terminal result events and enumerator cleanup. Existing IAsyncEnumerable methods are asynchronous but not Task-returning. Resolve whether they remain the async stream contract or acquisition becomes Task<IAsyncEnumerable<T>>. A Task of a buffered list is not acceptable. |
| D2 | Framework disposal | IAsyncDisposable requires ValueTask, IDisposable requires void. Design Task-facing cleanup and decide how unavoidable framework integration is exposed. Explicit interface implementation is still externally callable; it cannot be described as eliminating the ValueTask contract. |
| D3 | Properties and DTOs | Replace operational getters with Task-returning queries. Explicitly settle the treatment of data-only DTO properties, positional record members, immutable identities and configuration properties; do not silently classify all 613 as exempt. |
| D4 | Construction / registration | Constructors cannot be async. Map operational construction to awaited factories and keep host composition viable. Settle data-only construction separately. DI registration can be Task-returning but the container's synchronous factories must not block awaiting initialization. |
| D5 | Fixed CLR methods | Three authored ToString overrides and inherited/generated Object/record methods cannot return Task while retaining their CLR signatures. Define the boundary between library operations and unavoidable CLR mechanics explicitly. Preserve secret-safe formatting. |
| D6 | Notifications / progress | Changed uses EventHandler; IProgress<T>.Report is synchronous. Decide Task-based delivery semantics and framework adaptation without async-void handlers, lost exceptions, or duplicate authoritative state. |
| D7 | Settings atomicity | Async edit delegates and subclass hooks cannot be awaited inside existing monitor locks. Specify immutable/staged draft computation and revision-checked publication, or another approved owner-only protocol, before converting the setup base. |

No option may drop streaming, framework cleanup safety, redaction, settings atomicity or host composition.
These decisions are not invitations to retain arbitrary synchronous public operations. They identify
the shapes that cannot be implemented correctly through a mechanical return-type replacement.

## 5. Common mapping and settings transaction boundary

### 5.1 Public operations

Appendix A lists all 11 current synchronous declarations. In particular:
- `SetValueAsync`, `AddAsync` and `RemoveAsync` return Task<SetupEditResult>.
- Observation/output/secret-edit factories return Task of their existing typed value.
- Public validation returns Task; it does not gain network or storage side effects.
- Public extension helpers follow the same Async/Task rule.
- `SetupSecretEdit.ToString` is tracked under D5, not blindly renamed as an override.

### 5.2 Callback migration

Ten public setup-field type declarations accept synchronous edit/add/remove callbacks, including the
generic base; the repeatable group has both add and remove callbacks. Convert operation delegates
to Task-returning, cancellation-aware contracts in every concrete field constructor/factory and
provider binding. Choice retrieval already returns Task and must retain its revision/latest-result
semantics.

The public `Changed` contract appears on the setup interface and implementation. Its current
observer-exception isolation must survive D6. Progress parameters on setup actions and image/model
installation likewise require an explicit callback policy.

### 5.3 State ownership and concurrency requirements

The session remains the only owner of mutable draft, revision, operation status and published view.
Currently Snapshot builds fields/actions under a monitor, and Edit invokes an Action under that
monitor. Changing Action to Func<Task> and awaiting it there is not a valid migration.

The completed design must map these exact invariants:
1. Capture expected revision, active/closed state and immutable inputs before async computation.
2. No externally supplied callback mutates live owner state across an await.
3. Publication validates the relevant revision/operation identity before changing authoritative state.
4. Invalid/stale/busy/cancelled edits do not partially mutate the draft or increment revision as success.
5. A successful edit changes revision once and invalidates dependent choices/status once.
6. Snapshot fields/actions correspond to one revision, including async step/action construction.
7. Notify only after publication; observer failure cannot undo a successful host commit or suppress
   another observer. Reentrancy and notification ordering must be specified under D6.
8. Disposal closes admission, settles owned work and clears sensitive state. Cancellation after an
   uncertain external commit must not fabricate a rollback or a successful retry.

Changing a returned Task alone is not permission to add optimistic races or two owners.
Detailed staging/serialization mechanics remain D7; implementation is blocked on that decision.

Proposed operational getter replacement: `GetSnapshotAsync(ct)`. Returned SetupSnapshot remains a
coherent typed view; D3 settles its data-property representation. Base protected hooks and all five
provider setup subclasses are mapped in Appendix D.

## 6. Connect mapping

### 6.1 Composition and provider selection

Map every Bind/CreateSetupSession and the five Add*Connect extension methods in Appendix A.
Await registration before the host builds/uses its service provider. Do not add a sync wrapper around
an async factory. Preserve explicit provider choice, exact transport matching and no automatic fallback.

`ILlmClient.GetCapabilitiesAsync` propagates into every implementation, adapter validation/projection,
model-specific capability resolution, setup discovery and service/portable adapter caller. Methods on
internal implementation classes may be excluded from the public audit count but still implement
changed public interfaces and therefore must migrate.

`ILlmEmbeddingClient.SupportsAsync` similarly requires awaited selection. Image transport preparation
becomes `PrepareAsync` and remains side-effect-equivalent to today's preparation contract.

### 6.2 Authentication

Both StartClaudeLogin overloads and IClaudePkceLogin.Start become Task-returning operations. Preserve
PKCE identity/state, callback validation and the existing credential-owner boundary.

LoopbackRedirectListener currently opens a listener in its public constructor. Move operational
startup behind awaited creation/start according to D4; preserve its redirect result and cleanup.
Do not make a constructor call GetAwaiter().GetResult on an async factory.

Credential Serialize/Parse and pasted-code parsing remain typed operations but become Task-facing.
Credential material must remain absent from logs, diagnostics and generated documentation examples.

### 6.3 Agent and chat callers

The existing nonmutating accounting operations already return Task and keep their no-generation,
no-refresh/persistence, nullable-limit and provenance semantics. No compactor moves into Connect.
Model/run configuration, pause/revision validation, tool ordering, native approvals and safe teardown
must remain unchanged when their dependencies become awaitable.

`LlmAgentToolHandler`: ValueTask<LlmToolOutput> -> Task<LlmToolOutput>, preserving its existing
cancellation token and typed result. Update managed, Claude/native and AgentCore dispatch paths,
examples and host fixtures together. Native synchronous process internals are not public exceptions;
they may remain private behind the awaited boundary when required by the underlying runtime.

Streaming and framework agent-session disposal depend on D1/D2.

## 7. Tensor mapping

### 7.1 Catalog, packs, resolution and setup

Map the catalog and pack-manager Register/GetRequired* methods, definition validation, lease artifact
lookup, resolved-pack builders and source credential factories to Task operations (Appendix A).
Preserve explicit install/import versus load; no query or async factory implicitly downloads weights.

Operational query candidates under D3:
- Catalog/pack-manager Definitions -> GetDefinitionsAsync.
- ITensorModelInstance.Observation -> GetObservationAsync; state remains owned by model lifetime.
- RequestedBackend -> GetRequestedBackendAsync if retained as a service query.
- Resolver Requirements / AdditionalArtifactRolePrefix / Source -> named async accessors or an
  approved immutable descriptor. Do not turn them into a second authoritative registry.
- Setup Definition and implementation Descriptor -> async discovery at the owning service boundary.

All four setup factories become CreateSessionAsync, including their host-provided pack manager,
model choices, reader/source and optional instance inputs.

TensorModelResolver's CompanionPath/SelectArtifacts hooks become awaited hooks and its orchestration
must await them. The synchronous metadata-parser callback in TensorMetadataReader.ReadAsync also
needs mapping; the method's Task return does not make Func<Stream,T> an async external contract.
Preserve bounded range reads and compatibility inspection without loading tensors. Synchronous
third-party parsers may remain internal behind an adapter; exposing an async callback must not break
the existing stream lifetime or permit a retained stream after the callback settles.

TensorSourceAccess becomes Task<TensorSourceCredential?> with the existing exact-origin cancellation
contract. Retain origin/redirect boundaries; credentials are not broadened by this change.
TensorBackendProbes.CpuCuda and its Func<bool> callback both need Task-facing forms.

### 7.2 Lifecycle and cleanup

StartAsync, LoadAsync, UnloadAsync, StopAsync and execution methods are already async contracts.
Do not add another lifecycle, force flags or a process boundary during this migration.

MarkLoaded/MarkUnloaded become awaited mutations. Preserve load publication only after successful
initialization, coherent observations and unload's release order.
TensorExecutionCoordinator.AcquireAsync changes ValueTask<IAsyncDisposable> under D2; the acquisition
must still be cancellation-safe and must not leak or double-release a gate.

Uncancelled Unload/Stop protects active requests. Cancellation requests urgent teardown but must
still wait until native work stops using memory. Stop remains terminal as already documented.
Dispose of a lease exactly once, and make repeated/in-flight cleanup safe under the final D2 shape.
Do not translate framework cleanup to a completed Task while native cleanup is still running.

## 8. Constructors, helpers and generated members

The method appendix alone is not the entire reachable CLR surface. Constructors, implicit default
constructors, positional records, synthesized equality/Deconstruct and property accessors exist too.

The final API review must distinguish and explicitly document:
- Host-invoked operations, which must become async.
- Operational construction/accessors, which must move to awaited entry points.
- Framework-required dispatch and value representation, whose signatures C# fixes.

This distinction is unresolved design work, not an approved exception list. Do not invent Task-returning
ToString overrides, awaitable constructors, or Task DisposeAsync implementations of IAsyncDisposable.
Do not remove secret-safe ToString behavior from SetupSecretEdit or TensorSourceCredential without
an equally safe approved replacement.

Public methods may be internalized only when their capability is no longer needed externally and
the approved public route preserves all existing required functionality.

## 9. Consumer migration / dependency order

| Unit | Contract owner | Required simultaneous callers |
|---|---|---|
| Task callbacks, fields, setup snapshots/hooks | Common | All Connect/Tensor setup providers, UI.Settings renderer, generic editor example and tests |
| Provider binding, registration, capabilities, preparation | Connect | Concrete adapters (including internal ones), provider services, portable bindings, console tester, examples and controlled test doubles |
| Tool callbacks / agent lifetime / streams | Connect + Common | Managed/native/AgentCore sessions, host callback fixtures, stream consumers and integration guidance |
| Model catalog, source access, resolution, lifecycle helpers | Tensor | Qwen/Supra/Laya/Whisper, model pack manager, setup sessions, resolver tests and examples |
| Shared discovery/getter contracts | Common + product owners | Both portable implementations, setup renderer/composition and package-only consumer |
| Final release surface | Entire family | Exact Common dependency, all four packages, XML documentation and release consumer |

Implement dependency-first, but do not publish half-migrated packages or keep compatibility aliases.
Each compiling implementation unit updates its affected canonical docs and tests. The final release
contains a coherent contract set, not independent Common/Connect/Tensor version bumps.

UI.Settings remains a consumer migration: await SetValue/Add/Remove before processing SetupEditResult,
and replace Snapshot reads/notification handling according to D3/D6. Preserve parsing-error Save
blocking, dirty state, uncertain-commit reporting and latest-choice-result behavior. No redesign of
UI layout, no Lumeo fork and no new application ownership.

External consumer repositories (Agent DS, Desktop apps, other hosts) receive explicit migration
guidance and the final package version; do not edit them without separate authorization.
Public extension interfaces/delegates can break host implementations even when all our concrete
adapters are internal. Document those breaks, not just caller renames.

## 10. Verification and acceptance

Required before implementation is considered complete:
- Repeat source API inventory; every remaining non-Task public operation has a disposition backed
  by an explicitly approved decision, not a private test allowlist invented during implementation.
- Add a public-surface contract test/analyzer covering implicit interface members, external type
  visibility, inherited owned interfaces, delegates, public operations on generic types and nested
  accessible types. Track fixed-framework/generated surface explicitly.
- Compile controlled external implementations of changed interfaces/hooks/delegates, not only
  concrete product classes.
- Assert awaited completion, cancellation propagation and faults; no fire-and-forget mutation,
  sync-over-async, unnecessary Task.Run, duplicate operation or silent fallback.
- Setup concurrency tests: two edits, stale revision, cancellation before/during publication,
  dependent choice refresh, commit uncertainty, observer failures/reentrancy and disposal races.
- Streaming tests: first result before full completion, backpressure, cancellation, partial failure,
  tool events, single terminal completion, abandoned-enumerator cleanup and native memory lifetime.
- Tensor tests: repeated cleanup, failed/cancelled acquisition/load, urgent stop, request drain,
  observations, gates and lease ownership; no download/load during discovery.
- Secret-redaction and credential-origin regressions remain green.
- Zero-warning Debug/Release builds and deterministic tests for Connect, Tensor and UI; format and
  transitive vulnerability checks; compile examples and migrate package-only release consumer.
- No paid provider calls or model downloads as gates. Pack/private-feed validation only after an
  explicit publish request.

## 11. Release and documentation

The user selected **11.10.x**. The expected first contract release is 11.10.0 with the repository's
then-required prerelease suffix; do not change framework generation or invent a suffix policy.
Leave Directory.Build.props unchanged while drafting. All four packages still release together,
including UI.Settings's necessary Common-consumer updates.

Before publication, update Common/Connect/Tensor/UI entry points, setup and managed-agent designs,
Tensor model-source/lifecycle guidance, examples, XML docs, Agent DS handoff, project map and release
consumer. Preserve old release notes as historical evidence. Do not tell consumers that a draft API
is already in their floating NuGet resolution.

## 12. Draft completion checklist

- [x] Baseline public methods inventoried.
- [x] All synchronous, ValueTask and stream declarations mapped below.
- [x] Protected-hook and consumer impact identified.
- [x] Version line recorded as 11.10.x.
- [ ] Resolve D1–D7 with exact final signatures and state transitions.
- [ ] Complete member-level operational getter/factory dispositions; classify DTO/CLR mechanics.
- [ ] Approve complete technical design.
- [ ] Implement, verify and commit production migration.
- [ ] Publish only when explicitly authorized.

## Appendix A. Every synchronous public method

Proposals below are mappings, not already implemented API. Unless a decision row says otherwise,
append a CancellationToken parameter and preserve existing parameters/generic constraints.
Constructors and property getters are covered in sections 4, 7 and 8, not counted here.

### Common

| Existing signature | Proposed operation | Source |
|---|---|---|
| `InferenceCapability InferenceTaskExtensions.GetCapability(this InferenceTask task)` | `GetCapabilityAsync → Task<InferenceCapability>` | [Inference.cs:29](../Common/src/Darkspyre.Inference.Common/Inference.cs) |
| `bool LlmToolOutputResource.IsTextMediaType(string mediaType)` | `IsTextMediaTypeAsync → Task<bool>` | [LlmToolOutput.cs:53](../Common/src/Darkspyre.Inference.Common/LLM/LlmToolOutput.cs) |
| `LlmToolOutput LlmToolOutput.FromText(string text, bool isError = false)` | `FromTextAsync → Task<LlmToolOutput>` | [LlmToolOutput.cs:67](../Common/src/Darkspyre.Inference.Common/LLM/LlmToolOutput.cs) |
| `void SetupConfigurationIdentity.Validate()` | `ValidateAsync → Task` | [SetupModels.cs:43](../Common/src/Darkspyre.Inference.Common/Setup/SetupModels.cs) |
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
| `PersistenceFileReference TensorModelPackLease.GetRequiredArtifact(string role)` | `GetRequiredArtifactAsync → Task<PersistenceFileReference>` | [TensorModelPackLease.cs:23](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackLease.cs) |
| `string TensorModelKey.ToString()` | D5: fixed CLR override; preserve formatting/redaction | [TensorModels.cs:28](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModels.cs) |
| `TensorModelPackDefinition TensorModelCatalog.Register(TensorModelPackDefinition definition)` | `RegisterAsync → Task<TensorModelPackDefinition>` | [TensorModels.cs:84](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModels.cs) |
| `TensorModelPackDefinition TensorModelCatalog.GetRequired(TensorModelKey key)` | `GetRequiredAsync → Task<TensorModelPackDefinition>` | [TensorModels.cs:107](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModels.cs) |
| `void TensorModelDefinitionValidator.Validate(TensorModelPackDefinition definition)` | `ValidateAsync → Task` | [TensorModels.cs:129](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModels.cs) |
| `TensorModelPackDefinition ITensorModelPackManager.Register(TensorModelPackDefinition definition)` | `RegisterAsync → Task<TensorModelPackDefinition>` | [TensorModelPackManager.cs:44](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackManager.cs) |
| `TensorModelPackDefinition ITensorModelPackManager.GetRequiredDefinition(TensorModelKey key)` | `GetRequiredDefinitionAsync → Task<TensorModelPackDefinition>` | [TensorModelPackManager.cs:46](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackManager.cs) |
| `TensorModelPackDefinition TensorModelPackManager.Register(TensorModelPackDefinition definition)` | `RegisterAsync → Task<TensorModelPackDefinition>` | [TensorModelPackManager.cs:104](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackManager.cs) |
| `TensorModelPackDefinition TensorModelPackManager.GetRequiredDefinition(TensorModelKey key)` | `GetRequiredDefinitionAsync → Task<TensorModelPackDefinition>` | [TensorModelPackManager.cs:110](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackManager.cs) |
| `void TensorModelPackManager.Dispose()` | D2: Task-facing cleanup; framework disposition required | [TensorModelPackManager.cs:425](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackManager.cs) |
| `void HuggingFaceModelSource.Dispose()` | D2: Task-facing cleanup; framework disposition required | [HuggingFaceModelSource.cs:234](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/HuggingFaceModelSource.cs) |
| `TensorModelPackDefinition TensorResolvedPack.Create(TensorModelCandidate candidate, TensorCapability capability, string architecture, IReadOnlySet<TensorBackendKind> backends, IReadOnlyList<TensorModelArtifactDefinition> artifacts, TensorModelPackDefinition? builtIn = null)` | `CreateAsync → Task<TensorModelPackDefinition>` | [TensorModelResolution.cs:39](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelResolution.cs) |
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
