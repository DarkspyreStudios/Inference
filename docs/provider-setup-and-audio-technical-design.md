# Provider setup and audio: technical design

Date: 2026-09-22

Phase: **Implementation — technical design approved by the user on 2026-09-22.**

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
| `Connect/.../LLM/` and `.LLM.Bedrock` | LLM setup sessions, typed LLM edits, authentication and provider-specific protocol bindings |
| `Connect/.../Image/` | Image setup sessions, typed image edits, host workflow-choice seam |
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
existing catalog/service exposes its definitions and creates its setup sessions. A host composes
these definitions at its composition root; there is no global service locator or inference wrapper.
Listing definitions does not resolve secrets, start CLIs, probe devices, connect, or load weights.
Actual availability is an observation obtained by an explicit check, not a declaration inferred
from a registered implementation or backend enum.

### Typed fields and drafts

Each provider owns a typed draft. A setup session exposes a safe, read-only projection containing:

- Ordered `SetupStep` and `SetupSection` records, including advanced sections and help text.
- `SetupField` variants for text, URI, executable reference, integer, decimal, Boolean, choice,
  secret, and repeatable groups (needed for headers). Each variant exposes its corresponding typed
  edit method; there is no `object`, arbitrary JSON or property dictionary as the editing API.
- Stable field IDs, labels, defaults, constraints, required/read-only flags and evaluated
  visible/enabled state. Providers compute dependent state; the renderer does not evaluate scripts
  or derive behavior from labels, IDs or provider names.
- Secret fields expose only `HasValue` and explicit Keep/Replace/Clear operations. They never return
  the current secret. Replace accepts the typed secret input without retaining it in the safe view.
- Choice fields expose `GetChoicesAsync(SetupChoiceQuery, CancellationToken)`. Queries contain
  search text, an opaque continuation token, bounded page size and expected draft revision. Results
  contain stable value/label pairs, continuation and the revision they describe. Manual entry is
  explicitly enabled or disabled by the field, never guessed by the host.

The renderer branches on these shared field types only. The provider binds each edit method to its
typed draft property. A field edit advances the session revision and invalidates dependent choices,
validation and readiness evidence. Async completions for an older revision cannot update the draft.
Choice selection is revalidated by the provider; a host-supplied choice ID is not trusted evidence.

### Session and action signatures

The shared callable surface is:

```csharp
public interface IInferenceSetupSession : IAsyncDisposable
{
    SetupSnapshot Snapshot { get; }
    Task<SetupValidation> ValidateAsync(CancellationToken cancellationToken = default);
    Task<SetupCommitResult> CommitAsync(CancellationToken cancellationToken = default);
}

public interface ISetupAction
{
    SetupActionDescriptor Descriptor { get; }
    Task<SetupActionResult> ExecuteAsync(
        IProgress<SetupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface ISetupCommitTarget<TChanges>
{
    Task<SetupCommitResult> CommitAsync(
        TChanges changes,
        CancellationToken cancellationToken = default);
}
```

`SetupSnapshot` owns the current revision, steps/fields, available bound actions, validation and
status observations. Fields/actions are bound to their originating session and recheck availability
and revision when invoked. They are not string commands for the host to dispatch.

Actions declare their semantic purpose and side effects: network access, potential billing,
browser interaction, CLI execution, download, local verification, model load/unload or terminal stop.
Execution returns Completed, NeedsInteraction, Failed or Cancelled, with structured issues and
retryability. `SetupValidation` associates errors/warnings with fields or steps; warnings do not
silently become errors. Busy, stale revision and unsupported action are explicit outcomes.

Interactions are typed variants: browser/device authorization with URI/code/expiry, callback input,
or external prerequisite instructions followed by a bound recheck action. Continuation and cancel
actions remain provider-owned. Authentication protocol fields such as OAuth verifier/state remain
inside the provider, not a host-interpreted payload. The host opens a browser or presents instructions
only after its own consent checks. A provider cannot execute shell text from remote metadata.

### Commit, cancellation and concurrency

At composition, the host creates a typed session with a capability-specific commit target. The
generic renderer calls `CommitAsync`; the provider passes a typed change set to that target:
`LlmSetupChanges`, `ImageSetupChanges`, `TtsSetupChanges`, `AudioSetupChanges`, or
`TensorSetupChanges`. Each contains create/edit intent, configuration ID, expected opaque version,
the complete typed proposed configuration, and that family's typed secret changes where applicable.
Common defines no universal configuration or secret-storage format.

Commit validates the captured revision, freezes mutation while committing, and makes one host call.
The host atomically compares the configuration version and publishes configuration plus protected
secret changes, or returns Conflict/Failed without exposing a partial update. When vault and settings
storage cannot share a transaction, the host stages a new secret and atomically switches the saved
reference; its implementation owns cleanup. No distributed transaction is promised by the library.
Creates require absence; edits require the same existing version. Neither silently upserts.

LLM interactive setup uses a draft-local `ILlmConnectionSource`, so authentication completion updates
the draft rather than saved credentials. Runtime refresh continues using the existing host
`ILlmConnectionSource.SaveCredentialAsync` compare-and-replace seam unchanged. The final setup commit
compares the original saved version, including credentials; a concurrent refresh can therefore cause
a real conflict. Do not solve conflicts by overwriting newer credentials or replaying authorization.

Cancelled/disposed edits discard draft changes and do not save or clear existing credentials.
Already-performed external effects (authorization, download or paid test) are not rolled back.
Commit cancellation before invocation prevents the write; once invoked, the host must report the
actual outcome. An uncertain outcome requires readback, not automatic retry or a false Cancelled.

One mutating action runs per session. Conflicting edits/commit return Busy. Read-only choice queries
may overlap, guarded by revision. The host retains the session, task, progress sink and cancellation
source for any action that should survive a dialog closing. Closing a view is not cancellation.
Disposal explicitly cancels and awaits session actions before releasing their resources; it does not
stop an application-owned inference instance. There is no library background hosted service or
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
| Direct Codex | Endpoint, device authorization, model | Start/poll/cancel device auth with expiry; draft-local credential completion |
| Claude CLI | Executable, model; separate optional catalog authorization | Verify CLI/auth without copying CLI tokens; manual model entry without catalog OAuth |
| Antigravity CLI | Executable, model | Verify installed/authenticated CLI; discover via current adapter; no token scraping |
| Bedrock Converse | AWS profile and region choices, model | SDK-owned choices/checks in Bedrock assembly; standard credential chain remains owner |
| AgentCore Harness | Profile, region, Harness ARN, optional model override | Validate ARN/region; default/manual override semantics; explicit session-based test |

ComfyUI setup edits endpoint, timeout, protected headers and workflow selection. An
`IImageWorkflowChoiceSource` supplied by the host provides paged/searchable workflow identities and
resolves the selected identity to the existing `ImageWorkflow`. Missing host workflow support is an
explicit unavailable field/action, not a fake empty successful choice list. Workflow persistence,
import UI and durable jobs stay host-owned. Header names/values are validated; existing secret values
are masked. HTTP status/protocol health and a potentially costly generation test are separate actions.
Preparation/upload/submission/status/progress/download/cancel APIs are not replaced or hidden.

## 5. Tensor discovery, compatible selection and installation

### Source discovery versus engine compatibility

`ITensorModelSource` belongs in Tensor.Common. Its cancellable `SearchAsync`, `ListRevisionsAsync`
and `GetCandidateAsync` methods use typed queries and paged results. The initial implementation is
Hugging Face; candidates carry source/repository/revision/variant identities, artifact metadata,
access/license state and nullable size/integrity values. Search is not an installable catalog.

Each engine owns an `ITensorModelResolver` with `EvaluateAsync(candidate, backend, token)` and
`ResolveAsync(selection, token)`. Compatibility is Supported, Unsupported or Unverified with reasons
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

Installation still uses `ITensorModelPackManager` and the application-supplied `IPersistenceStore`
(including the direct-file default). Add a source-access seam to the existing manager for protected
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

Qwen initially targets the current Qwen3 GGUF architecture, not every TensorSharp architecture.
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
Cover conditional/dependent fields, paging, secret masking/Keep/Replace/Clear, cancelled drafts,
concurrent credential changes, stale completions, commit conflicts/uncertainty, side-effect declarations,
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
  metadata, Decision capability, the Laya family, required model/tokenizer roles and CPU/CUDA declarations
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
  and architecture support do not change.

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
