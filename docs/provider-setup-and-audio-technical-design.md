# Provider setup and audio: technical design

Date: 2026-09-22

Phase: **Implementation complete — technical design approved and source verified on 2026-09-22.**
All eight runtime/settings rows are complete; see the final unit 5 acceptance record. The user
separately authorized coordinated 11.3.0-rc.1 publication on 2026-09-23; see the release record in
`.roles/project.md`. Consuming-application integration remains separate.

Baseline: Common / Connect / Tensor 11.2.0. No version change, package creation, publication,
consumer-application migration, or model download is part of this design unit.

## 1. Scope and completion checklist

The primary scope is the **Summary by capability and implementation** table in
[the consumer gap assessment](provider-settings-contract-support-and-gaps.md#summary-by-capability-and-implementation).
Its detailed sections explain those rows; they do not authorize a separate application framework.
The user has selected the initial audio implementations: external PocketTTS and TangoFlux in
Connect, and in-process Whisper.net in Tensor.

| Row | Concrete change | Completion evidence |
|---|---|---|
| Common | Shared setup discovery, typed editable fields, validation, callable actions, status | One provider-independent example/editor driver exercises both families |
| Connect Text | Bind the existing providers' setup descriptions to real edit/auth/model/test operations | Every existing provider passes the same setup conformance tests plus its protocol tests |
| Connect Image / ComfyUI | Connection/workflow editor, validation and explicit health/test actions | Setup uses the existing granular transport; durable job APIs remain intact |
| Tensor Text / Qwen | Compatible pack discovery/selection, setup, backend availability, readiness; load selected pack | Resolver and loader accept the same supported definitions; no pinned-key-only guard |
| Tensor Image / Supra | Complete compatible multi-artifact pack selection and setup | Missing/incompatible companion artifacts fail; selected pack reaches the real loader |
| Connect Audio | TTS/PocketTTS and Audio/TangoFlux typed contracts, implementations and setup | Deterministic endpoint, voice, request, failure and cancellation tests |
| Tensor Audio | STT/Whisper typed contracts, explicit lifetime, packs and setup | Batch/window transcription and lifecycle tested through a controlled native boundary |
| Tensor Decision / Laya | Decision classification plus host-selected compatible pack loading/setup (subsequently authorized amendment below) | One instance unloads and loads another host-supplied Laya pack; no new CE section or remote model-search project |

Out of scope: CE UI implementation, GMTools changes, hosted products, process supervision,
microphone capture/VAD, soundscape orchestration, Chroma, ComfyUI-style Tensor workflows, a general
machine-diagnostics library, and new facade NuGets. A Whisper bridge is buildable example source only.

The gap assessment now also records a separate `Darkspyre.Inference.UI.Settings` Blazor/Lumeo
library for CE and Agent-DS, in the separately scoped UI project. It is a consumer of these forthcoming
contracts, not part of this runtime implementation.
This design adds no UI-framework dependency; that library's theming, extension points and packaging
remain its own technical-design work. Its separately approved design now places UI.Settings in the
same repository as a fourth synchronized package; this runtime work does not implement or publish it.

## 2. Assembly ownership

This runtime work owns three NuGets, with their one shared version and exact Common dependency.
The separately approved UI.Settings package is outside this implementation scope.

| Location | Owned types and behavior |
|---|---|
| `Common/.../Setup/` | UI-neutral setup descriptions, typed fields, action/interaction contracts, validation, observations, typed host commit interface |
| Existing Common inference types | Existing request/result contracts; implementation descriptor gains task classification |
| `Connect/.../LLM/` and `.LLM.Bedrock` | LLM settings and managers, typed LLM edits, authentication and provider-specific protocol bindings |
| `Connect/.../Image/` | Image settings and managers, typed image edits, host workflow-choice seam |
| `Connect/src/TTS/Darkspyre.Inference.Connect.TTS/` | TTS contract/models, provider service, PocketTTS HTTP adapter and setup |
| `Connect/src/Audio/Darkspyre.Inference.Connect.Audio/` | Generated-audio contract/models, provider service, TangoFlux HTTP adapter and setup |
| `Tensor/.../Common/` | Pack discovery/source contracts, immutable registration, installation integration and shared readiness observations |
| Existing Qwen and Supra assemblies | Engine-specific compatibility, complete pack resolution, selected-pack loading and setup |
| `Tensor/src/STT/Darkspyre.Inference.Tensor.STT/` | STT interface and transcription models |
| `Tensor/src/STT/Darkspyre.Inference.Tensor.STT.Whisper/` | Whisper.net, PCM normalization, native-resource ownership, Whisper compatibility, packs and setup |
| `Tensor/examples/Darkspyre.Inference.Tensor.STT.Bridge/` | Non-packable source example: host startup, explicit load, request mapping, shutdown |

Tests mirror these capability folders. No empty Connect.STT, Tensor.TTS or Tensor.Audio projects
are created. Audio is the discovery group; TTS, generated audio, and STT remain separate contracts.
Task-specific models stay in their owning assemblies until genuinely reused across families; they
are not moved into Common just to make the directory trees symmetrical.

## 3. Shared discovery and editing contract

### Discovery without activation

Extend `InferenceImplementationDescriptor` with supported `InferenceTask` values:
`TextGeneration`, `ImageGeneration`, `SpeechSynthesis`, `AudioGeneration`, `SpeechTranscription`,
and `Decision`. Common supplies the single task-to-Text/Image/Audio/Decision grouping rule.
Existing implementation identities remain stable; a configured instance has a separate host ID.

`InferenceSetupDefinition` contains the descriptor, description, display order, guided/advanced
visibility, supported create/edit operations, and prerequisite descriptions. Each capability's
existing catalog/service exposes its definitions and creates its settings manager. A host composes
these definitions at its composition root; there is no global service locator or inference wrapper.
Listing definitions does not resolve secrets, start CLIs, probe devices, connect, or load weights.
Actual availability is an observation obtained by an explicit check, not a declaration inferred
from a registered implementation or backend enum.

### Typed fields and drafts

Each provider owns a typed settings class. A settings object exposes a safe projection containing:

- Ordered `SetupStep` and `SetupSection` records, including advanced sections and help text.
- `SetupField` variants for text, URI, executable reference, integer, decimal, Boolean, choice,
  secret, and repeatable groups (needed for headers). Each variant exposes its corresponding typed
  edit method; there is no `object`, arbitrary JSON or property dictionary as the editing API.
- Stable field IDs, labels, defaults, constraints, required/read-only flags and evaluated
  visible/enabled state. Providers compute dependent state; the renderer does not evaluate scripts
  or derive behavior from labels, IDs or provider names.
- Secret fields expose only `HasValue` and explicit Keep/Replace/Clear operations. They never return
  the current secret. Replace accepts the typed secret input without retaining it in the safe view.
- Choices for a choice field come from `IInferenceSettingsManager.QueryChoicesAsync(settings, fieldId,
  SetupChoiceQuery, CancellationToken)`. Queries contain search text, an opaque continuation token and
  a bounded page size. Results contain stable value/label pairs and a continuation. Manual entry is
  explicitly enabled or disabled by the field, never guessed by the host.
- `SetupChoiceCapabilities` explicitly declares search, paging and refresh independently of manual
  entry. Bounded local lists (protocol, format, capability overrides, backend and cache policy) do
  not request search/refresh chrome. Catalog/discovery fields opt in to the supported operations.
- Decimal fields expose a positive `Step` alongside nullable bounds. UI numeric controls must keep
  `long`/`decimal` precision and report invalid visible input rather than silently rounding or
  clamping it into a different typed value.

The renderer branches on these shared field types only. The provider binds each edit method to its
typed settings property. A field edit changes only the settings object and marks dependent readiness
evidence stale. It never calls a provider. A choice query never changes the settings; the renderer
drops a page that returns after a later edit. Choice selection is revalidated by the provider; a
host-supplied choice ID is not trusted evidence.

### Settings and manager signatures

The shared callable surface is:

```csharp
public abstract class InferenceSettings
{
    InferenceSetupDefinition Definition { get; }
    SetupConfigurationIdentity Identity { get; }
    IReadOnlyList<SetupStep> Steps { get; }
    IReadOnlyList<SetupActionDescriptor> Actions { get; }
    SetupStatus Status { get; set; }
    SetupInteraction? Interaction { get; set; }
    bool HasUnsavedChanges { get; }
    bool CanSave { get; }
    bool IsSaved { get; }
    bool SaveOutcomeUncertain { get; }
    SetupValidation Validate();
}

public interface IInferenceSettingsManager
{
    Task<SetupActionResult> RunAsync(InferenceSettings settings, string actionId,
        IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<SetupChoicePage> QueryChoicesAsync(InferenceSettings settings, string fieldId,
        SetupChoiceQuery query, CancellationToken cancellationToken = default);
    Task<SetupCommitResult> SaveAsync(InferenceSettings settings, CancellationToken cancellationToken = default);
}

public interface IInferenceSettingsStatusSource
{
    event EventHandler? StatusChanged;
    Task RefreshStatusAsync(InferenceSettings settings, CancellationToken cancellationToken = default);
}

public interface ISetupCommitTarget<TChanges>
{
    Task<SetupCommitResult> CommitAsync(
        TChanges changes,
        CancellationToken cancellationToken = default);
}
```

Each provider has a typed manager with an `OpenAsync` that returns its settings for a provider type,
blank or from a saved entry. `Steps` and `Actions` are rebuilt from the current values on each read,
so show/hide and enable rules run locally. Actions are descriptors, not bound delegates; the manager
runs one by ID against the settings' current values. A disabled or unknown action ID returns
Unavailable. They are not string commands for the host to dispatch to providers.

`HasUnsavedChanges` is a provider-owned comparison against the opened values. Every concrete
settings class implements `IsDraftDirty`: changes to editable settings, selected models and explicit
secret mutations count; checks, validation, progress and runtime observations alone do not.
Reverting editable values restores clean state. A successful save marks the object saved and clears
its dirty signal. `SaveOutcomeUncertain` is separate and remains explicit even if the object had no
edits. `CanSave` requires every visible required field to have a value and any required sign-in to
have succeeded. Required fields are the values Save stores, such as an endpoint, an API key or a
model; inputs that only feed an action are not required. The UI combines these signals with transient
invalid or unapplied input for host dirty-close behavior; that transient text is not a second copy of
the settings.

Actions declare their semantic purpose and side effects: network access, potential billing,
browser interaction, CLI execution, download, local verification, model load/unload or terminal stop.
Execution returns Completed, NeedsInteraction, Failed, Cancelled or Unavailable, with structured
issues and retryability. `SetupValidation` associates errors/warnings with fields or steps; warnings do
not silently become errors. An unknown or disabled action is an explicit Unavailable outcome.

Interactions are typed variants: browser/device authorization with URI/code/expiry, callback input,
or external prerequisite instructions followed by a recheck action. Continuation, cancel and recheck
are action descriptors that the manager runs by ID. Authentication protocol fields such as OAuth verifier/state remain
inside the provider, not a host-interpreted payload. The host opens a browser or presents instructions
only after its own consent checks. A provider cannot execute shell text from remote metadata.

`InferenceSettings.Interaction` is authoritative for the current interaction. An action may also
return its interaction as an immediate result, but renderers read controls from the settings. Failed
CLI authentication sets prerequisite instructions with a recheck action; a successful recheck clears
them. An edit to a value the sign-in depends on clears a pending authorization. Browser callback text
remains one-way: the settings accept it but never echo it in a field value. Each authorization attempt owns a unique callback field identity,
stable across callback edits/status updates and replaced on restart. Renderers retain the transient
masked text for that identity and clear it when the interaction ends or the editor is replaced.

### Commit, cancellation and concurrency

At composition, the host creates a typed settings manager with a capability-specific commit target.
The generic renderer calls `SaveAsync`; the manager passes a typed change set to that target:
`LlmSetupChanges`, `ImageSetupChanges`, `TtsSetupChanges`, `AudioSetupChanges`, or
`TensorSetupChanges`. Each contains create/edit intent, configuration ID, expected opaque version,
the complete typed proposed configuration, and that family's typed secret changes where applicable.
Common defines no universal configuration or secret-storage format.

Save validates the settings, checks `CanSave`, computes the typed changes from the current values, and
makes one host call. One settings object saves once.
The host atomically compares the configuration version and publishes configuration plus protected
secret changes, or returns Conflict/Failed without exposing a partial update. When vault and settings
storage cannot share a transaction, the host stages a new secret and atomically switches the saved
reference; its implementation owns cleanup. No distributed transaction is promised by the library.
Creates require absence; edits require the same existing version. Neither silently upserts.

LLM interactive setup uses a settings-local `ILlmConnectionSource`, so authentication completion updates
the settings rather than saved credentials. `LlmConnectionAccess` owns runtime refresh for chat and
embeddings using the existing host `ILlmConnectionSource.SaveCredentialAsync` compare-and-replace seam
unchanged. The final setup commit
compares the original saved version, including credentials; a concurrent refresh can therefore cause
a real conflict. Do not solve conflicts by overwriting newer credentials or replaying authorization.

Dropping a settings object without Save discards its changes and does not save or clear existing
credentials.
Already-performed external effects (authorization, download or paid test) are not rolled back.
Commit cancellation before invocation prevents the write; once invoked, the host must report the
actual outcome. An uncertain outcome requires readback, not automatic retry or a false Cancelled.

The editor runs one action at a time and disables edits and Save while it runs. Choice queries may
overlap; the renderer drops a page that returns after a later edit. The host retains the task,
progress sink and cancellation source for any action that should survive a dialog closing. Closing a
view is not cancellation. Neither the settings nor the manager stops an application-owned inference
instance. There is no library background hosted service or
durable operation scheduler. Retrying inference that may have billed is never automatic setup policy.

### Status and invalidation

`SetupStatus` reports separate Configured, Authenticated, Installed, Verified, Loaded and Ready
observations. Each carries applicability, nullable Boolean value (null = unknown), observation time,
stale flag and reason. Missing credentials/weights are not successful readiness. Configure/Save never
implicitly authenticates, tests, installs or loads. Provider actions invalidate affected observations;
editing endpoint, credential, model or backend invalidates dependent evidence.

For Tensor, loaded state is obtained only from an explicitly supplied live instance observation;
absence of an instance means unknown, not unloaded. Installation state comes from the pack manager,
not a second setup cache. Lifecycle observations are published by the existing model lifetime owner.
Backend checks report known prerequisites and unknowns; they do not become the parked general
memory-diagnostics project, predict exact load memory, or guarantee an allocation will succeed.

## 4. Connect Text and Image bindings

Retain `LlmProviderCatalog` as the owner of LLM definitions and project its existing setup guidance
through the shared descriptions. Replace the old input/action-description mechanism in repository
callers as one unit; do not keep two authoritative setup catalogs. Execution and authentication
services stay first-class and callable independently of a wizard.

| Provider | Fields/choices | Bound operations and special rules |
|---|---|---|
| OpenAI Responses | Endpoint, secret API key, model | Validate, discover models, explicit potentially billed test |
| OpenAI-compatible | Endpoint, optional key, model, supported advanced options | Remains advanced; explicit discovery/manual-entry policy |
| Anthropic Messages | Endpoint, secret API key, model | Preserve API-key-only credential policy; discover and explicit test |
| Direct Codex | Device authorization, model | Start/poll/cancel device auth with expiry; draft-local credential completion. No endpoint field: the official endpoint is fixed (owner decision, 2026-09-23), and a saved former value is replaced on the next save |
| Claude CLI | Executable, model; separate optional catalog authorization | Verify CLI/auth without copying CLI tokens; manual model entry without catalog OAuth |
| Antigravity CLI | Executable, model | Verify installed/authenticated CLI; discover via current adapter; no token scraping |
| Bedrock Converse | AWS profile and region choices, model | SDK-owned choices/checks in Bedrock assembly; standard credential chain remains owner |
| AgentCore Harness | Profile, region, Harness ARN, optional model override | Validate ARN/region; default/manual override semantics; explicit session-based test |

`XaiResponses` has an unregistered connection/setup contract tracked by DSA-127. Region and service-tier
fields are bounded choices on `XaiConnectionOptions`. Setup derives the endpoint from the region
and has no API-key field. A saved OAuth token or completed draft sign-in satisfies Save. Both setup
continuation and facade interactive sign-in use the same expiry/interval/slow-down helper. The internal
`XaiAuthenticator` implements the device grant, token refresh and one `/v1/me` check. The shared
`XaiErrorClassifier` sanitizes errors; subscription-specific 403 matching remains unqualified and is
not implemented. No xAI client or authenticator is registered by `AddLlmConnect`, so this contract
does not add an enabled provider to the table above. The shared Responses pipeline retains ordered
xAI output verbatim, binds replay to the account/endpoint/model, uses terminal output for function
completion and maps context exhaustion to an incomplete result. Encrypted replay data remains opaque
and is redacted from diagnostics. This shared handling is not a concrete xAI chat adapter.

ComfyUI setup edits endpoint, timeout, protected headers and workflow selection. An
`IImageWorkflowChoiceSource` supplied by the host provides paged/searchable workflow identities and
resolves the selected identity to the existing `ImageWorkflow`. Missing host workflow support is an
explicit unavailable field/action, not a fake empty successful choice list. Workflow persistence,
import UI and durable jobs stay host-owned. Header names/values are validated; existing secret values
are masked. HTTP status/protocol health and a potentially costly generation test are separate actions.
Preparation/upload/submission/status/progress/download/cancel APIs are not replaced or hidden.

## 5. Tensor discovery, compatible selection and installation

The approved location extension is defined by
[`tensor-model-locations.tech-design.md`](tensor-model-locations.tech-design.md). It adds local
files/folders and independent URLs alongside repository discovery, source-neutral artifact access,
three-artifact ONNX Laya resolution and explicit staged Import for unknown integrity. The historical
implementation records below describe their original release scope, not a repository-only restriction.

### Source discovery versus engine compatibility

`ITensorModelSource` belongs in Tensor.Common. Its cancellable `SearchAsync`, `ListRevisionsAsync`
and `GetCandidateAsync` methods use typed queries and paged results. The initial implementation is
Hugging Face; candidates carry source/repository/revision/variant identities, artifact metadata,
access/license state and nullable size/integrity values. Search is not an installable catalog.

Each engine owns an `ITensorModelResolver` with `EvaluateAsync(candidate, backend, token)` and
`ResolveAsync(selection, token)`. Selection now carries a typed `TensorModelLocation`; repository
browsing is optional and artifact reading is separated through `ITensorArtifactReader`.
Compatibility is Supported, Unsupported or Unverified with reasons
and evidence. Missing metadata is Unverified, not proof of compatibility. Evaluation may explicitly
fetch bounded metadata/header ranges, but cannot silently download full weights or run repository
code. Unsupported range requests do not fall back to downloading the entire file during browsing.

Supported means the adapter recognizes a complete artifact schema that its current loader accepts;
it does not claim successful inference on this machine. Resolution pins revisions and returns the
existing `TensorModelPackDefinition` plus source provenance, per-artifact license data and compatibility
evidence. Every installable artifact still requires a known length and SHA-256. Unknown license or
integrity produces a structured unresolved result rather than fabricated values. A Git object ID is
not substituted for a file SHA-256. Small metadata artifacts may be fetched and hashed explicitly;
missing weight integrity cannot be repaired by an unannounced full download.

Pack identity must distinguish variants and companion sets. Preserve built-in keys; new resolved
keys include variant identity and a deterministic manifest fingerprint in their revision identity.
Each artifact separately retains its source host, repository, immutable revision and relative path.
Never reuse one pack key for a different artifact manifest.

### Registration, storage and protected access

Extend `TensorModelCatalog` with validated, thread-safe `Register(definition)` returning an immutable
snapshot: registering the identical definition is idempotent; a different manifest at the same key
fails. Deep-copy mutable collections on entry. The catalog remains the one definition owner read by
the pack manager; discovery does not create a parallel downloader/catalog. Host persistence of chosen
definitions is explicit and registrations are reconstructed by the host after restart.

Installation still uses `ITensorModelPackManager`, which installs into its local pack directory. Add a source-access seam to the existing manager for protected
artifact reads; do not put credentials into definitions/URLs. The Hugging Face source obtains a
short-lived credential through a host callback bound to the exact origin. Follow redirects with
explicit origin checks, never forwarding authorization to another origin. Gated/license-required,
denied, missing and transient-network results remain distinct. Remote repository text is data only.

Preserve manager serialization, leases, hash verification and removal rules. Expose artifact and
overall install progress with nullable totals where appropriate, terminal failure/cancelled state,
and actual resume support (initially no resumability claim). An explicit Install action registers the
resolved immutable pack and calls the manager. Save selection does not install. Duplicate installs
use the existing serialized manager behavior; an active lease prevents removal. A failed or partial
install is never verified/ready. Host-retained action lifetime keeps status observable outside a view.

### Qwen and Supra changes

Qwen targets the Qwen 3 and Qwen 3.5 family GGUF architectures, not every TensorSharp architecture.
Validate GGUF architecture/version, tokenizer/chat metadata, dimensions, tensor names/shapes,
quantization types, complete shard set and selected backend against the loader's supported schema.
Do not treat repository tags or a `.gguf` suffix as sufficient. Model limits come from validated
metadata rather than copying the current 4B limits to every candidate.

Supra resolution requires the DiT, text encoder, tokenizer and VAE roles and pinned companions.
Validate their formats, architecture/configuration and tensor schema against the current Supra2 +
Flan-T5 + VAE pipeline. Keep the restricted checkpoint reader; no arbitrary pickle execution.
Same-schema revisions/variants can be selected; unrelated diffusion models remain unsupported.
Current 256x256, negative-prompt and sampler limits remain honestly exposed.

Both engines replace their constructor-selected constant with the definition selected by
`LoadAsync(key)`. Capability checks, request model validation, coordination and provenance use that
selected definition. The resolver and loader share one engine-owned compatibility policy. Load
revalidates installed artifacts and actual backend suitability; a stale discovery result is not a
load bypass. Other registered supported models can be loaded after Unload, not between prompts.

All Tensor implementations retain Start / Load / typed request / Unload / Stop. Start may be a no-op;
Stop is terminal. Normal Unload/Stop protects active requests; a cancelled teardown token requests
their earliest safe cancellation and still waits before freeing native resources. No artificial
Start guard, implicit download/load, host supervisor, or new cancellation Boolean is introduced.

## 6. Audio contracts and implementations

### Connect TTS / PocketTTS

`ITtsClient` exposes `Descriptor`, `GetCapabilitiesAsync`, `ListVoicesAsync` and
`SpeakAsync(TtsRequest, CancellationToken)`. `TtsRequest` contains Text, VoiceId, optional Speed and
Format; `TtsResult` contains ContentType, audio bytes and provenance. `TtsVoice` contains stable ID,
display name and optional locale/description. No voice asset storage or playback is included.

`TtsProviderService` discovers adapters and binds a typed `TtsConnection` (transport, endpoint,
timeout, protected headers and typed PocketTTS endpoint options). Setup supplies endpoint, protocol
mode, path options, voices and defaults; the host owns saved configuration and protected headers.
Provider-specific protocol options are interpreted only by the adapter, not by the renderer.

Retain the donor's external `/voices`, empty-list `/health` fallback, and `/tts` behavior, including
voice-option and voice-name response shapes. Preserve both documented donor protocol modes through
a typed mode: multipart (`text`, `output_format`, `voice_url`) and bridge JSON
(`text`, `voice`, `speaker`, `speed`, `response_format`). Multipart does not implement speed:
capabilities mark it unsupported and a non-default speed fails rather than being silently ignored.
Format capabilities must match protocol evidence; do not promise arbitrary codecs. Endpoint probes
and voice listing are distinct from an explicit potentially billed synthesis test.

No sidecar launch, stdin/stdout worker, model install, virtual environment, restart loop, certificate
validation bypass or native PocketTTS engine is included. Localhost is allowed as an external endpoint.

### Connect Audio / TangoFlux

`IAudioGenerationClient` exposes `Descriptor`, `GetCapabilitiesAsync` and
`GenerateAsync(AudioGenerationRequest, CancellationToken)`. The request carries Prompt,
Duration, optional Steps, GuidanceScale, Seed and provider-cache preference. The result carries
ContentType, audio bytes, nullable measured duration and provenance; requested duration is not
misrepresented as measured output duration.

`AudioProviderService` binds typed `AudioConnection` values and exposes real adapter/setup discovery.
TangoFlux maps these fields to the donor `/generate` JSON protocol. The initial constraints are
duration 1–30 seconds, steps 10–100 and guidance 0.1–20. Reject out-of-range values instead of
silently clamping. Preserve explicit `useCache` and seed semantics; the library does not generate a
random seed because of an application's cache policy. WAV responses must be nonempty and validated.
Do not invent a health or model-list endpoint: advertise unavailable discovery where the actual
server protocol has none. A real generation test is an explicit side-effecting action.

Both connectors honor cancellation and timeouts, dispose HTTP resources, reject malformed results,
and return typed failures through `InferenceException` rather than swallowed errors/null. Extend
failure codes only for shared semantics needed here (authentication/access, timeout and protocol
failure); caller cancellation remains `OperationCanceledException`. Log neither prompts, secret
headers nor raw provider responses. No automatic retry after potentially accepted generation.

### Tensor STT / Whisper.net

`ITensorSpeechToTextInference : ITensorModelInstance` exposes `Descriptor`,
`GetCapabilitiesAsync`, `ListModelsAsync`, `TranscribeAsync(SpeechTranscriptionRequest, token)` and
`TranscribeStreamAsync(IAsyncEnumerable<SpeechAudioWindow>, SpeechToTextOptions, token)`.

`SpeechAudio` is interleaved float PCM with sample rate and channel count. Requests include audio,
optional language and TranslateToEnglish. Batch results contain text, timestamped segments,
nullable detected language and provenance. Windows carry a caller identity; streamed results carry
that identity and window-relative timestamps, so time does not ambiguously reset between windows.
The host supplies windows; this is not microphone capture, VAD, overlapping-window deduplication,
speaker diarization or continuous partial-token recognition.

Convert the donor's channel normalization/WDL resampling to validated mono 16 kHz input using
NAudio.Core 2.2.1. Validate sample layout, finite values and supported rates/channels before native
execution. Keep Whisper.net and Whisper.net.Runtime 1.9.1 as the initial dependency baseline,
centrally pinned and subject to the normal audit. Advertise only the CPU runtime actually supplied;
do not promise CUDA/CoreML from an enum. A native availability probe is explicit, not part of listing.

Preserve all twelve donor model variants: tiny/tiny.en, base/base.en, small/small.en,
medium/medium.en, large-v1/v2/v3 and large-v3-turbo. Definitions/resolution require actual immutable
artifact lengths/hashes, not donor approximate sizes. Validate language/translation capabilities
per model. Discovery defaults do not cause automatic selection, download or load.

Load explicitly acquires a verified pack lease and creates one resident Whisper factory. Requests
create/dispose processors against that factory and use the existing Tensor lifetime coordination;
one instance serializes native execution. A streaming call holds its request lease until enumeration
finishes or is disposed. Normal Unload can therefore wait for an open stream; cancelling teardown
cancels its input/processing and still awaits native completion. Unload disposes processors/factory
before releasing storage; Stop adds any remaining instance cleanup.

**Approved persistence adaptation:** the pinned Whisper.net version exposes model-path and
model-buffer factories, but no model-stream factory. Read the pack artifact through its persistence
reference. For a returned FileStream, retain the stream and load its real path. For another store,
the Whisper adapter creates a private temporary file projection and owns its cleanup, using bounded
stream copying rather than a whole-model managed byte array. Retain it until native load/use is
finished. This is native-adapter compatibility only: no host raw-path contract, second durable model
cache, or alternate downloader. A future upstream stream API can replace the internal projection
without changing the public contract; this design does not authorize a new Whisper fork.

Source evidence: [Whisper.net 1.9.1 factory](https://github.com/sandrohanea/whisper.net/blob/1.9.1/Whisper.net/WhisperFactory.cs)
and the installed 1.9.1 package XML; do not design against unreleased main-branch APIs.

### Source-only bridge example

The non-packable example uses ASP.NET hosting solely to demonstrate host-owned lifecycle. The host
supplies store/configuration, starts and loads an already-installed chosen model, maps a documented
PCM transcription request to the typed library request, and unloads/stops during shutdown. It does
not claim OpenAI compatibility, auto-download, expose administrative model operations, or implement
production authentication. Bind loopback by default and document that deployment/auth belongs to a
real host. It is source in the solution, never a fourth product or runtime inside Tensor.STT.

## 7. Extraction, migration and verification

| Donor area | Classification | Treatment |
|---|---|---|
| TTS voice/request/result concepts | Convert | Typed provider-neutral library models; remove app provider base |
| PocketTTS HTTP encoding/parsing | Convert | Preserve evidenced protocols; fix capability reporting and failure handling |
| NativePocketTtsClient / bridge process host | Exclude | No supervised provider in Connect |
| TangoFlux HTTP generation | Convert | Strip cue/cache/mixer authority; preserve reusable request parameters |
| Whisper processing and resampling | Convert | Explicit Tensor lifecycle; no model-per-request lazy loading |
| Whisper file catalog/downloader | Rewrite | Existing pack manager, persistence and compatible source resolution |
| App settings, Scribe, microphone, voice assets, caches and playback | Exclude | Remain in donor/consuming applications |
| Historical Whisper HTTP bridge | Rewrite as example | Minimal source-only host, not recovered as a product |

Approved implementation units:

1. Shared setup contract plus real LLM/Image and Tensor projections; typed draft/commit boundaries,
   full provider action bindings, generic driver and conformance tests. No placeholder successful actions.
2. Connect TTS and Audio with setup, deterministic HTTP tests, examples and product inclusion.
3. Tensor model-source/resolver and registration integration; Qwen/Supra selected-pack loading,
   setup and compatibility fixtures, protected-source and installer tests.
4. Tensor STT/Whisper with setup, all model variants, lifecycle tests and source-only bridge.
5. Cross-row acceptance pass, integration documentation and full verification. No release step
   until separately authorized; determine coordinated version impact from the actual approved API diff.

Tests must demonstrate generic add/edit flows across every definition, not merely count fields.
Cover conditional/dependent fields, paging, secret masking/Keep/Replace/Clear, unsaved settings,
concurrent credential changes, late choice pages, commit conflicts/uncertainty, side-effect declarations,
and status invalidation. Use a host example with typed registration/commit bindings and one generic
field/action driver; no provider-name dispatch is permitted inside that driver.

Model fixtures cover mismatched architecture/quantization/tensor shape, missing shards/companions,
unverified metadata, immutable-key conflicts, private/gated access, redirects, corrupt/partial installs,
duplicate installs, active leases, unavailable backends and selected-key propagation into real loader
adapters. Whisper tests isolate native execution to verify cancellation and disposal ordering without
requiring full weights; protocol tests exercise the real HTTP mappers. No paid or full-weight run is
a mandatory development gate, and skipped optional live tests are reported honestly.

Each implementation unit updates canonical integration docs and all in-repository callers, verifies
zero-warning Debug/Release builds, deterministic tests and formatting, and commits whole files.
Run the transitive vulnerability audit after dependency changes and at the final acceptance pass.
Do not modify CE/GMTools or copy library source into them. Do not run `dotnet pack` or change the
shared version as routine development verification.

## 8. Phase exit

The user approved this technical mapping and its ownership choices on 2026-09-22. In particular,
the host commit boundary, dynamic immutable pack registration, selected-pack loader changes, and
Whisper's internal non-file-store projection are explicit design decisions, not incidental refactors.
Approval permits implementation, not package publication or a version bump.

## 9. Implementation record

These entries are chronological checkpoints. Earlier pending-work statements describe their
checkpoint only; the final unit 5 record is the current completion status.

### Unit 1 — shared setup and existing-provider bindings (2026-09-22)

Implemented Common task classification, typed fields/drafts, revision-checked actions and choices,
explicit secret edits, typed host commits and separate status observations. The session owns its
operation lifetime; disposal waits for actions and queries. Observer exceptions cannot change an
operation outcome. Commit conflicts remain editable; an uncertain commit closes the draft and
requires host readback.

Existing LLM providers now expose real setup sessions. Device/browser authorization updates a
draft-local connection source, CLI verification uses the existing adapters, and AWS profile/region
bindings stay in the Bedrock assembly. Optional catalog authorization is not CLI authentication.
Compatible endpoints expose typed capability overrides; arbitrary provider JSON is preserved from
the supplied configuration, not exposed as a generic editor payload. Model/endpoint checks and
potentially billed tests remain explicit. ComfyUI supplies protected-header edits and host-owned
workflow selection, with health and generation kept separate.

Qwen/Supra have shared setup against the existing built-in packs and host-supplied live instances.
Loaded/readiness observations read the existing lifetime owner; setup disposal never stops that
instance. Laya is classified as Decision. This unit does **not** claim remote discovery or arbitrary
pack loading: those remain unit 3. TTS, generated audio and Whisper remain units 2 and 4.

The non-packable `Common/examples/Darkspyre.Inference.Setup.Examples` composes real sessions and
provides one typed field/action renderer with no provider-name dispatch. Both focused solutions
compile it. Integration READMEs distinguish these source changes from published 11.2.0.

Verification: both focused solutions pass zero-warning Debug/Release builds, deterministic tests
(Connect 223, Tensor 56; Common's 22 occur in both), formatting verification and transitive
vulnerability audits. No paid calls, full-weight inference, model downloads, package creation,
version changes or publication are part of this unit. Implementation remains active for units 2–5.

### Unit 2a — external PocketTTS checkpoint (2026-09-22)

Implemented `Connect.TTS`: request-only client, provider/service registration, typed requests,
voice/capability discovery, WAV results and host-owned setup. Preserves the donor's multipart and
bridge-JSON modes and empty-voice-list health fallback. Multipart explicitly rejects non-default
speed; only WAV is advertised. Protected header changes are explicit and separate from configuration.
Save has no network effects; voice checks and synthesis tests remain distinct. Timeout, caller
cancellation, HTTP access errors and malformed responses are not collapsed into null results or
automatic retries. No native PocketTTS engine or bridge process is included.

This is a separate safe checkpoint at the user's request to discuss host-supplied Laya packs.
TangoFlux, remote discovery/resolution and Whisper have not been implemented yet. Laya's new request
adds scope beyond the original classification-only row; its setup/pack-selection mapping will be
reviewed separately before implementation. The user's custom Laya checkpoint has already run
successfully through the Tensor engine; no new architecture support is inferred or needed.

Verification for this checkpoint: both focused solutions pass zero-warning Debug/Release builds
and deterministic tests (Connect 240, including 17 PocketTTS protocol/setup tests; Tensor 56).
Formatting verification and the Connect transitive dependency vulnerability audit pass. No new
third-party dependency, package artifact, version bump, publication, live endpoint request or
model download was required. Audio implementation is paused here for the Laya discussion.

### Laya amendment — shared Tensor selection/lifecycle (2026-09-22)

The user supplied a caller-defined-pack handoff, confirmed their custom-trained checkpoint already
runs through Tensor, specified that implementation names identify **model families**, and explicitly
required Laya to follow the broader Tensor design. That supersedes the handoff's constructor-bound
single-pack proposal. General and technical direction are settled; this amendment is implemented.

- Keep the Laya family implementation and current constructor. No checkpoint parameter or alternate
  inference path: `LoadAsync(key)` obtains the host-registered definition from the existing manager.
- Expose `TensorDecisionModels.LayaRuntimeArchitecture`. A single engine-owned policy validates common
  metadata, Decision capability, the Laya family, required artifact roles and CPU/CUDA declarations
  for both offered setup definitions and Load. The native loader retains its tensor-schema checks.
- Use the acquired pack key for execution coordination, model-ID checks and provenance. Acquire the
  request lifetime before checking the resident model identity so a model switch cannot race a request.
  Normal Unload protects requests; urgent teardown still waits for native execution to finish.
- `LayaSetup` binds the existing `TensorSetupSession` to host-offered compatible packs. Defaults may
  offer the built-in pack; the host can instead offer private/custom packs. Save persists the key/backend
  using `TensorSetupChanges`; no new settings payload, renderer dispatch, downloader or credential store.
- Unload frees native resources and the pack lease, then another registered compatible pack can load
  on that same instance. Stop remains terminal. A failed/cancelled load releases acquired resources
  and does not publish a loaded model. Settings disposal never owns the engine's lifetime.
- Tests use the real engine lifecycle, pack manager and persistence with controlled HTTP/native
  boundaries; fixtures exercise two distinct custom packs, provenance, wrong-family rejection,
  failure/cancellation cleanup, graceful/urgent unload and the generic selection/save flow. They do
  not claim a new full-weight accuracy or calibration experiment. Formatting/calibration algorithms
  and architecture support did not change at this historical checkpoint; the serving-parity unit
  below supersedes the tokenization, calibration and sequence-limit behavior and requires agent-config.

No version change, package creation/publication, UI implementation or consumer migration is authorized
by this amendment. TangoFlux, remote Qwen/Supra discovery and Whisper remain pending.

Phase exit: Laya implementation and integration documentation complete. Both focused solutions pass
zero-warning Debug/Release builds and tests (Tensor 66, including 17 Laya tests; Connect 240).
Formatting verification and the Tensor transitive vulnerability audit pass. The ten new Laya tests
use controlled native execution, not a new full-weight run. The generic model-selection example
compiles in both solutions. No packages were created or published and the shared version is unchanged.

### Unit 2b — external TangoFlux and audio settings (2026-09-22)

Implementation resumed after the completed Laya amendment. `Connect.Audio` now supplies the typed
generation client, connection/request/result/capability contracts, adapter registration and shared
settings session. The HTTP mapper preserves the donor's `/generate` protocol without application
cache, cue or playback ownership. Duration/steps/guidance are validated, not clamped; optional seeds
and cache preference are preserved without generating a seed. WAV framing and content type are
validated, and unmeasured duration stays null. Failures are typed and sanitized, HTTP resources are
disposed, caller cancellation remains cancellation, and potentially accepted work is never retried.

Settings expose the real connection and generation fields, optional values, paged cache choices,
protected-header Keep/Replace/Clear and explicit removal/rename mutations. Saving only calls the
typed, versioned host commit target. There is no invented health/model-list action; the real generation
test declares network/potential-billing effects. Editing invalidates readiness. Session disposal
cancels and waits for an active test, not for an externally hosted provider process.

The Connect product includes its Audio assembly/XML; no new NuGet or third-party dependency was
introduced. A compiled host-composition example offers both PocketTTS and TangoFlux to the same
generic renderer and maps saved typed audio defaults plus separately resolved secrets to a request.
The separate UI implementation and consumer applications are unchanged by this unit.

Phase exit: the Connect audio implementation unit is complete. Both focused solutions pass
zero-warning Debug/Release builds and tests (Connect 291, including 51 new TangoFlux protocol/setup
tests; Tensor 66). Both formatting checks and transitive dependency vulnerability audits pass.
One intermediate Release run failed the existing LLM test
`InterruptSettlesIssuedToolsAndRejectsConcurrentOwnership`; it passed an isolated rerun and then
the full Release suite. This intermittent failure is recorded, not suppressed or claimed fixed;
no LLM implementation/test or shared runtime code changed in this unit.

Implementation remains active for remote Qwen/Supra discovery/selection, Whisper and final
cross-row acceptance. No version bump, package creation, publication or live model call occurred.

### Unit 3a — source metadata, registration and protected installation (2026-09-22)

Implemented the shared `ITensorModelSource` contracts and bounded Hugging Face metadata adapter:
paged search/revision listing, immutable candidate revision, typed artifact/provenance/license data,
and nullable integrity/size fields. Git object IDs are not treated as file hashes. Repository data is
inert; discovery reads JSON only, never loads a remote model or executes its code. The user's
clarification is explicit: discovery examines metadata for compatibility; only an installed pack is
passed to an engine's Load. The already-tested Supra runtime is not blocked by remote inspection.

`TensorModelCatalog.Register` now snapshots its collections, is thread-safe/idempotent, and rejects
different definitions at an existing key. The manager forwards registration and publishes the same
catalog's definitions. The shared source-access callback supplies exact-origin short-lived bearer
credentials to metadata and installation. Redirects are bounded, HTTPS cannot downgrade, and a
credential never crosses origins or returns after the chain has left its origin. Caller-supplied
HTTP clients must disable automatic redirects/default credentials and carry no default headers.
Sanitized source failures distinguish gated, authentication-required, denied, missing/private,
transient and unverified outcomes without exposing response bodies or transient URLs.

Installation retains persistence, serialization and verified leases. Progress adds overall totals,
explicit failure/cancellation terminals and an honest no-resume declaration. Oversized artifacts
are rejected while copying; observers cannot alter installation outcomes. Repair cannot overwrite
a leased pack, and an interrupted repair cannot retain a manifest advertising installation.
The first full test pass exposed a direct-file-store absent-directory case in manifest invalidation;
the repair path now deletes only an existing invalid manifest, and all lifecycle tests pass again.

Verification: zero-warning Debug/Release builds and tests for both focused solutions (Tensor 88,
including 22 new source/registration/installer cases; Connect 291), formatting verification and
transitive dependency audits. Tests use controlled HTTP/persistence, not model downloads. No package
creation, version change, publication, UI edit or new TensorSharp dependency was required.

This is a completed shared-foundation checkpoint, not completion of the settings request. Engine
resolvers, Qwen/Supra selected-pack loading and remote setup binding, Whisper, and the final
cross-row acceptance remain active. The source contract is independently callable; no successful
engine compatibility result or UI readiness is fabricated for its candidates.

### Unit 3b — selected Qwen/Supra packs; dependency checkpoint (2026-09-22)

Qwen and Supra now obtain the selected registered definition in `LoadAsync(key)`, use that key for
execution coordination/request checks/provenance, and release it during Unload. Their setup entry
points accept host-offered compatible definitions through the same shared typed session. The
compiled generic example offers the manager-owned registered definitions without provider-specific
renderer controls. Selection and Save do not install or load; setup does not own engine lifetime.

Each engine owns one pack compatibility policy. Supra requires the complete four-role pipeline and
retains its restricted checkpoint reader/component schema validation and generation algorithm.
Qwen checks installed GGUF architecture/version, tokenizer/chat metadata, dimensions, required
tensor names/shapes, storage encoding/ranges and shard declarations before model allocation.
Qwen listing no longer copies a fixed 4B context limit onto arbitrary packs: limits remain unknown
until validated loaded-runtime metadata is available. Controlled native boundaries test selected-key
propagation, repeated model switching, failure/cancellation cleanup and graceful/urgent teardown.
The Qwen metadata tests use small generated GGUF fixtures, including multiple layers and incompatible
architecture, missing metadata/tensors, wrong shapes, unknown quantization and incomplete shards.

Verification for this checkpoint: zero-warning Debug/Release builds and tests for both focused
solutions (Tensor 111, including 23 new selection/metadata cases; Connect 291) and formatting checks.
The dependency audits already passed in unit 3a; no dependency version changed here. No full-weight
run is claimed. No packages, version changes, UI changes, model downloads or publication occurred.

**Historical dependency gate (resolved by the following unit):** TensorSharp 2.8.6.3 `PersistenceFileLease.AcquireAsync`
projects a non-FileStream artifact to one generated filename. `GgufFile.OpenSiblingShards` resolves
the rest of a split checkpoint from filenames beside that file. `ModelService.LoadModelAsync`
accepts only the model reference (plus an optional vision projector), not a complete shard set.
Consequently a complete split Qwen pack cannot load from an arbitrary non-file persistence store
through the current dependency. Single-file Qwen and Supra are unaffected; this is not a remote
model-loading requirement or a claim that Supra has not already worked.

The proposed focused follow-up is to extend the existing TensorSharp fork's persistence-backed
loading to retain/project a complete named artifact set for the model's lifetime. This keeps the
host on IPersistenceStore and avoids an Inference-owned second materialization path. Verification
requires building local-only fork dependency packages, an exception to the present no-package-
creation development rule; no publication or Inference version bump is proposed. Implementation
pauses at this committed checkpoint for that direction. Remote engine resolution/setup binding,
Whisper and final cross-row acceptance are still outstanding, not reported as complete.

### GGUF persistence and Laya serving-parity corrections (2026-09-22)

Implementation phase: the user authorized the focused GGUF fork fix, including local-only
dependency packages, and supplied the Laya serving-parity handoff plus golden cases. No UI work,
Inference version bump, publication or new model architecture is included.

- TensorSharp's `darkspyre` commit `a1ce7780` adds a named immutable `PersistenceFileSet`, consumed
  by both its GGUF reader and model service. One lease implementation retains an in-place file set
  or projects the complete named set to a private temporary directory. Unlisted siblings are never
  read by persistence loading. Failure/cancellation/unload clean up projections and source handles.
  Shard counts, filenames, numbering and duplicate tensor names are validated before allocation.
- Qwen maps the selected definition's relative paths to every acquired pack artifact and passes the
  same complete set to compatibility validation and model loading. The locally built nine-package
  dependency closure is pinned as `2.8.6.4-local.gguf.a1ce7780`; build/restore instructions are in
  `Tensor/README.md`. This is the explicitly authorized package-creation exception, not a release.
- Laya requires the selected pack's `agent-config` artifact in setup and load compatibility. Load
  parses `max_len`, `head_max_len`, `temperature` and `temperature_by_options` before native work.
  Configured option buckets take precedence; absent buckets use the configured question-type
  temperature. Values must be finite and positive and are not clamped. No upstream defaults remain.
  Invalid configuration is `ModelCorrupt`; absent required roles fail compatibility. Failed loads
  release their acquired pack lease. Capabilities expose loaded `max_len`, otherwise null.
- Laya explicitly validates the supported ByteLevel pre-tokenizer configuration and supplies its
  GPT-2 regex. Every added token is registered, including ordinary whitespace runs and placeholders.
  Sequence framing remains unchanged but truncation/head budgeting use the loaded parameters.
  All three Laya corrections form one completed unit, with no host API/lifecycle redesign.
- The supplied 24 HF reference rows and pinned, Apache-2.0 tokenizer fixture are offline regression
  inputs. Controlled-model tests verify a custom pack's served probability, head budget/window,
  missing/invalid configuration and resource cleanup. They do not claim the RightSize full-weight
  `coding-v7-s42` probability comparison; that acceptance run belongs to RightSize.

Remote engine resolution/setup binding, Whisper and final settings cross-row acceptance remain
outstanding. These fixes do not mark that broader work complete.

The pinned built-in `laya-typed-decisions` agent configuration actually declares 1024/256, unlike
the handoff's `laya` reference configuration (512/192). Both are retained as offline regression
fixtures. Reading the selected pack resolves this distinction without imposing new fixed limits.

Phase exit: these fixes pass zero-warning Debug/Release builds and deterministic tests
(Tensor 165, Connect 291, TensorSharp persistence 22). All 24 tokenizer golden rows pass.
Both Inference solution format checks and transitive vulnerability audits pass; the fork's
changed-file formatting and Chat dependency audit pass. The nine local dependency archives have
matching local-version references and were consumed through NuGet by the Inference verification.
There was no full-weight/native-device experiment, Inference package build, publication or version
bump. UI source and the other worker's pending documentation remain untouched. The shared
`Directory.Packages.props` whole-file commit preserves that worker's already-staged Lumeo,
ASP.NET Components and bunit declarations alongside this unit's TensorSharp dependency pin.

### Unit 3c — bounded source inspection, generic resolution editing and backend observations (2026-09-22)

Implementation continues under section 5. The shared source contract now lists artifact variants and
supports explicit bounded metadata reads: maximum 16 MiB per header range or 4 MiB for a complete
small metadata file. The Hugging Face adapter validates pinned provenance, range/content lengths and
body bounds, rejects an ignored weight range without reading the whole body, and retains the existing
origin-scoped credential rules through redirects. Repository contents are never executed.

`ITensorModelResolver` exposes the approved evaluation/resolution contract with Supported,
Unsupported and Unverified states, structured issues, evidence and source-access state.
`TensorResolvedPack` snapshots canonical complete manifests and fingerprints variant, capability,
backend set, artifact integrity and per-artifact provenance. Source/artifact ordering is immaterial;
changed companions or pinned provenance change identity. Qwen/Supra built-ins now carry their
per-artifact provenance/license declarations while retaining established keys and content hashes.

The existing `TensorSetupSession`, not a second editor, optionally binds an engine resolver to
repository/revision/variant choices and an explicit Resolve action. Supported complete results become
the draft selection. Unsupported, unverified and cancelled resolution cannot enable Install or Save.
Edits invalidate dependent selection/evidence; old query completions return Stale. Save carries the
selected immutable `Definition` with the typed configuration for host persistence/re-registration.
Only explicit Install registers a resolved definition and calls the existing downloader; selection,
browsing, resolution and Save never download/load/register. Unregistered selections report not installed.

Qwen, Supra and Laya setup now bind explicit backend prerequisite probes. Qwen uses its loader's Auto
policy and checks native GGML/MLX device availability; direct CUDA checks its driver/device. Supra/Laya
CPU and Auto report the managed fallback. Missing native support is unavailable; inconclusive checks
remain unknown. The read-only observation is invalidated on edits. No model allocation guarantee,
mandatory probe gate before Load, native initialization during definition discovery, or general
machine-diagnostics facility is added.

At this checkpoint, the **concrete Qwen/Supra resolver policies and composition remained unwired**.
The shared resolver seam was tracked here deliberately; it was not reported as an engine
compatibility implementation. Whisper/STT and final cross-row acceptance also remain outstanding.
No UI files, family/dependency versions or package archives are changed by this unit.

Verification: Tensor passes **192 tests** and Connect passes **291 tests**, in both Debug and Release,
with zero build warnings/errors. Tensor/Connect formatting and transitive vulnerability audits pass.
The new tests cover exact bounded ranges, ignored ranges without body reads, pinned source identity,
protected redirects, variant listing, manifest identity/provenance/snapshots, explicit registration,
save-without-install, unresolved/cancelled candidate rejection, stale choice results, gated failures,
backend invalidation, CPU checks and controlled CUDA unavailable/unknown states. These are deterministic
boundary tests; no live GPU or native model allocation is claimed. No dependency restore was necessary
because dependency inputs were unchanged. The independently staged UI/rules edits and consumer-gap
document changes remain outside this commit; there are no remaining untracked files.

### Unit 3d — concrete Qwen/Supra metadata resolution and settings selection (2026-09-22)

Implementation phase: the engine resolvers now use the existing format readers, not duplicate
checkpoint parsers. TensorSharp's `darkspyre` commit `9b248715` exposes bounded metadata inspection
for GGUF, safetensors and restricted PyTorch ZIP checkpoints. The caller retains its seekable stream;
inspection does not read tensor payloads, create temporary files or initialize native models.
The approved local-only dependency closure is rebuilt as `2.8.6.4-local.gguf.9b248715`; no release
version or Inference archive is created. The fork's 36 focused metadata/persistence/parser tests
pass in Debug/Release, with zero build warnings/errors and changed-file formatting verified.

`TensorMetadataReader` adapts these synchronous readers to cached 64 KiB metadata ranges on a
worker thread, preserving cancellation and typed source failures. Format inspection is limited to
16 MiB of parser reads; one resolution shares a 64 MiB transfer budget. Unknown sizes, inconsistent
ranges and exhausted budgets cannot produce a supported installable pack.

`QwenModelResolver` validates complete single/split GGUF headers with the same Qwen3 policy as Load,
including tensor storage bounds, tokenizer/chat metadata and shard identity. Every required shard
must be present exactly once. Selecting another shard yields the same canonical manifest/key.
`SupraModelResolver` inspects the DiT's actual restricted checkpoint metadata and resolves all three
pinned companions from their respective immutable repositories. The loader and resolver share one
DiT/encoder/VAE schema; tokenizer compatibility uses the known supported vocabulary's pinned hash
and size, with bytes verified at installation. Same-schema DiT fine-tunes receive distinct complete
pack identities. Missing companions/integrity/access stays Unverified; incompatible schemas are
Unsupported. Repository tags and names are never the compatibility decision.

The Qwen/Supra setup factories compose these resolvers through their optional `source` argument.
The generic example accepts the same host-owned source. Tests exercise real setup-to-resolution-to-
Save for both engines, without registration, installation or loading. The separate existing explicit
Install path remains the only settings operation that registers and downloads a resolved pack.
The backend availability observations completed in unit 3c are retained unchanged.

Offline Supra fixtures contain factual metadata extracted from the already-installed published
checkpoint, not weights or executable code. Synthetic sparse archives exercise the real readers.
Optional live bounded-metadata checks also resolved both built-in public Hugging Face packs as
Supported, retaining their original keys: Qwen reported 398 tensors and a 40,960-token context;
Supra returned all four artifact roles. These checks did not install/load models or download full
weights and do not claim native inference or memory-allocation success.

Phase exit: this Qwen/Supra unit is complete. Tensor passes **221 tests** and Connect **291 tests**
in Debug and Release, with zero build warnings/errors. Both focused-solution formatting checks and
transitive dependency vulnerability audits pass. The TensorSharp dependency update was restored
with process-scoped private-feed credentials; subsequent checks used the restored assets. Existing
UI/rules changes and the consumer-gap document remain outside this commit; no untracked files remain.
No UI work, Inference package creation, family version change or publication occurred. Whisper/STT
and the final capability-table acceptance pass remain required; implementation continues for those.

### Unit 4 — resident Whisper STT, model metadata and generic settings (2026-09-22)

Implementation phase: `.Tensor.STT` owns the typed PCM/request/result/window contracts and
`.Tensor.STT.Whisper` implements them using public Whisper.net/Whisper.net.Runtime 1.9.1 and
NAudio.Core 2.2.1. Dependencies are centrally pinned and declared by the Tensor package project;
third-party DLLs are not added as embedded package contents. STT assemblies and XML documentation
are included in that project's existing product layout. There is no new product NuGet or host runtime
inside the library. The non-packable loopback bridge is separate source in the Tensor solution.

Load obtains a verified pack lease, validates the standard Whisper F32/F16 GGML header, and eagerly
constructs/validates one factory. Requests normalize finite complete-frame PCM (8–192 kHz, 1–8
channels) to mono 16 kHz, use one processor, then await its disposal before releasing model use.
The factory remains resident. Empty clips return an explicit empty result. Options use language
codes or null for automatic detection; English-only and turbo models reject translation requests.
Batch/window results carry actual selected-pack provenance and window-relative segment times.

`TensorModelLifetime` remains the request/teardown authority. Window enumeration holds its lease
until completion/disposal; the host's input enumeration must honor cancellation. Unload and Stop
protect normal work or request urgent cancellation, then await safe native completion. Native
load is also awaited on cancellation before cleanup. Stop is terminal and Start remains a no-op.
Whisper's adapter retains a FileStream or private bounded-copy projection until factory disposal;
failed/cancelled loads release the source, temporary file and pack lease. No independent durable
cache, implicit download, process supervisor, capture, VAD or partial-token stream is introduced.

`TensorSpeechModels.Whisper` contains all twelve required variants, using actual publisher lengths
and SHA-256 values at immutable `ggerganov/whisper.cpp` revision
`5359861c739e955e79d9a303bcbc70fb988958b1`. CPU is the only advertised runtime; Auto maps to CPU.
`WhisperSetup` uses the shared Tensor session and typed host commit. `WhisperModelResolver` combines
the initial catalog's known weight identities with bounded GGML header inspection. Unknown remote
content remains Unverified rather than trusting a name/tag. Custom host packs may use the declared
standard loading schema; modified architectures/quantized variants are not claimed supported.
The explicit backend action invokes the native runtime; listing/composition never initializes it.
The generic example offers Whisper without provider-specific renderer logic. Request language and
translation defaults remain a host policy over the typed request, not a new generic settings bag.

Source evidence: the [1.9.1 factory](https://github.com/sandrohanea/whisper.net/blob/1.9.1/Whisper.net/WhisperFactory.cs)
and [processor](https://github.com/sandrohanea/whisper.net/blob/1.9.1/Whisper.net/WhisperProcessor.cs)
confirm eager loading, per-request processing and asynchronous disposal; the
[Whisper model documentation](https://github.com/openai/whisper#available-models-and-languages)
identifies turbo's translation limitation. Optional live metadata-only resolution verified all twelve
published definitions as Supported and preserved every built-in key. No full weights or inference
were needed for that check.

Phase exit: the Whisper implementation unit is complete. Tensor passes **257 tests** (36 new
Whisper cases) and Connect **291 tests**, in Debug and Release, with zero build warnings/errors.
Both focused solutions pass formatting verification and transitive dependency vulnerability audits.
The new tests exercise actual PCM resampling, pack persistence/projection, metadata resolution and
settings commit, with a controlled native boundary for repeated resident requests, model switching,
language/translation rejection, failed/cancelled loads, serialized execution, empty results, identified
window streams and graceful/urgent Unload and Stop. They do not claim full-weight/native transcription
accuracy. The bridge and generic host composition compile in their solutions.

No UI source, shared version or released packages changed; no Inference archives or full model weights
were downloaded/created. All new source/test/example files are tracked. Other-worker UI/rules and
consumer-gap-document edits remain outside this commit. Implementation remains active for the final
capability-table acceptance pass and completion report; this checkpoint does not mark the goal done.

### Unit 5 — capability-table acceptance and completion (2026-09-22)

Implementation phase exit: all eight runtime rows in the consumer's primary capability table are
complete in source. The table now links the actual implementation acceptance suites and explicitly
separates current support from the historical published 11.2.0 gaps. The final pass reviewed shared
contract/host boundaries, all eight Connect Text providers, ComfyUI, PocketTTS, TangoFlux, Qwen,
Supra, Whisper and Laya, including discovery, create/edit/commit, explicit operations, compatibility
and truthful observations. No provider-specific UI behavior was added.

The final conformance additions execute one identical create/edit/save loop across all eight real
LLM registrations and a complete PocketTTS create/edit round trip without network calls. Existing
Qwen, Supra and Whisper setup tests now also reopen saved configurations, edit typed model/backend
choices, preserve the expected host version and report unload requirements without installation or
native activation. Existing ComfyUI, TangoFlux and Laya tests already cover create and edit flows.
The shared example remains a compiled semantic field/action renderer with typed host composition;
it is not a claim that the separate Blazor application integration has been completed.

Final gates: zero-warning/error Debug and Release builds and deterministic tests pass for both
focused solutions. **Connect: 293 tests** (22 Common, 136 LLM, 48 Bedrock, 18 Image, 18 TTS, 51 Audio).
**Tensor: 257 tests** (22 Common, 62 Tensor.Common, 35 Qwen, 32 Supra, 70 Laya, 36 Whisper).
Common's 22 tests occur in each solution total, not two different suites. Both full-solution
formatting checks and transitive dependency vulnerability audits pass. The source-only bridge and
generic composition examples compile with these builds. No new full-weight/native inference run is
claimed; optional live checks in units 3d/4 inspected bounded metadata only.

There are no outstanding library/settings-data implementation items in the approved table.
Compatibility remains deliberately bounded: Qwen3 GGUF, the current Supra2 pipeline, host-supplied
Laya-family packs and the initial twelve published Whisper content identities for remote resolution.
Unknown compatibility/runtime information stays unknown, not fabricated readiness. UI, host
application integration, additional model architectures and general diagnostics remain outside scope.

The source still references the locally verified TensorSharp dependency closure
`2.8.6.4-local.gguf.9b248715`. A distributable dependency release and coordinated Inference release
require their own authorization; published 11.2.0 does not contain these changes. The family version
is unchanged, and no Inference package was created or published. This whole-file documentation unit
preserves the other worker's corrected `Inference/UI/` workspace path in the gap document; their
staged UI source, UI design and governing-rules changes remain outside the commit.
