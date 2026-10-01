# Provider settings contracts: completion and baseline gaps

Date: 2026-09-23

Consumers: DarkspyreCE Desktop and Agent-DS, through the shared `Darkspyre.Inference.UI.Settings` library

Current publication: synchronized Common / Connect / Tensor / UI.Settings NuGet **11.5.1-rc.1**,
private DarkspyreStudios feed, release source `1d24f8f`. This release adds Basic source credentials
and retains the 11.5.0-rc.1 replacement of the LLM reasoning
boolean/string contracts with typed levels and per-level setup overrides; see
[the reasoning design](llm-reasoning-level.tech-design.md). Historical baseline: three-package **11.2.0**,
release source `83c1ac3`.

Status: **All eight approved runtime/library rows are implemented and published in 11.4.0-rc.1.
These changes are not in the historical 11.2.0 packages.** The approved authority and implementation record are in
[the provider setup and audio technical design](provider-setup-and-audio-technical-design.md).
UI surfaces and consumer integration belong to their separate work; this report does not certify them.

The initial row coverage shipped in 11.3.1-rc.1. The current table also includes Laya ONNX and
model-location extensions, now published together in 11.4.0-rc.1.

Settings corrections are included in **11.3.1-rc.1**, not in the previous 11.3.0-rc.1 archives:
provider-owned dirty state and uncertain-commit state, snapshot-owned authentication interactions,
attempt-scoped one-way callback input, explicit choice capabilities and decimal step metadata.
UI.Settings consumes these with a local-input Save gate, latest-request choice loading, compact
bounded selects and an owned exact numeric control. All Connect/Tensor setup implementations are
updated together; application integration remains separate. All four packages were published and
verified with an empty-cache feed-only consumer. See
[the UI correction record](settings.ui.tech-design.md#12-post-release-settings-corrections-2026-09-23).

## Shared UI workspace and library

The configuration editor belongs to the Blazor library
**`Darkspyre.Inference.UI.Settings`**, built with **Lumeo**, in this repository's UI product workspace:

`/Volumes/Data/Development/DarkspyreStudios/Inference/UI/`

The workspace name deliberately covers more than Settings so other genuinely shared inference UI
libraries can be added later. Settings is the first library; no additional libraries are presently
specified. It is not a feature folder inside CE, Agent-DS, Connect, or Tensor.

The shared library consumes the **published 11.4.0-rc.1 metadata and operation contracts
of the Connect and Tensor objects**. The current table below reports that source implementation;
the historical 11.2.0 inventory after it records why those contracts were needed. Do not substitute
host-specific forms or a second local metadata model for the provider-owned contracts.

`Darkspyre.Inference.UI.Settings` provides one reusable, host-placed editor surface for a provider
setup session. It renders the provider's fields, steps, validation, status, actions, and progress.
Provider definitions drive its content and behavior. It does not own connection lists, cards, Add
entry points, dialogs, drawers, routes, or the editor's placement. Lumeo supplies the controls;
presentation remains separate from provider protocol, compatibility and lifecycle implementations.

DarkspyreCE and Agent-DS each compose the shared components and layer their own themes on them.
The library must support inheritance/extension as well as theme customization without duplicating
provider configuration logic. The exact public extension points and theme contract belong in the
technical design. CE's World Atlas palette and application shell are not baked into the library.
Each host retains local persistence, protected credential storage, user consent and application
operation ownership through the appropriate seams.

The new UI library does not introduce a Blazor or Lumeo dependency into Common, Connect or Tensor.
It is the fourth synchronized Inference package and references Common only among Inference
products. Its packaging, dependency versions and release policy belong to
[its approved design](settings.ui.tech-design.md), not this runtime acceptance pass.

## Required consumer experience

DarkspyreCE needs Settings grouped by **Text, Image, Audio**, each supporting both **Connect**
(out-of-process) and **Tensor** (in-process) implementations. Each host decides how to list existing
connections, start Add/Edit, and place the shared editor. The shared library does not impose a
collection layout or display container. Its own CSS supplies layout only; the host supplies colors
and theme through Lumeo.

The panels and setup wizards must be driven by **contract properties and callable methods**.
The provider supplies its configuration semantics, field/section/step descriptions, available
choices, validation, actions, and status. The shared UI library supplies reusable Lumeo controls,
editor layout and accessible interaction. Each consuming application supplies its theme, local
persistence, protected credential storage and explicit user authorization.

The UI may map semantic field kinds to shared controls. It must not switch on provider names or
transport IDs to decide which fields to invent, which API to call, what a successful setup means,
or whether a model is compatible. Reflection over a configuration object is not sufficient: the
contract must describe purposeful grouping and progressive disclosure, not a flat property editor.
If a required behavior is missing, update the owning provider library rather than adding a
consumer-specific or UI-library substitute.

## Summary by capability and implementation

This table describes the current 11.4.0-rc.1 release, including Laya ONNX and model-location/import
extensions. “Complete” is bounded by the
implemented family policies and qualifications below; host application integration is separate.

| Area | Current library/settings data | Acceptance evidence |
|---|---|---|
| Common contracts | Complete: task grouping; typed fields, sections/steps, explicit choice capabilities, dependent choices, validation, current actions/interactions, draft dirty state, protected secret edits, versioned host commits and distinct status observations | [Shared contract tests](../Common/tests/Darkspyre.Inference.Common.Tests/InferenceSettingsTests.cs) and [provider-independent example renderer](../Common/examples/Darkspyre.Inference.Setup.Examples/SetupEditorExample.cs) |
| Connect Text | Complete: all eight existing providers have real create/edit setup, field/choice metadata, auth/prerequisite checks and explicit tests; AWS bindings remain in Bedrock | [All-eight create/edit conformance](../Connect/tests/LLM/Darkspyre.Inference.Connect.LLM.Bedrock.Tests/BedrockRegistrationTests.cs), [setup/auth tests](../Connect/tests/LLM/Darkspyre.Inference.Connect.LLM.Tests/LlmSettingsTests.cs) and existing provider protocol suites |
| Connect Image / ComfyUI | Complete: endpoint, timeout, protected headers, host workflow choices, validation, editing and separate health/generation actions; granular durable-job transport preserved | [Setup tests](../Connect/tests/Image/Darkspyre.Inference.Connect.Image.Tests/ImageSetupTests.cs) and [real transport protocol tests](../Connect/tests/Image/Darkspyre.Inference.Connect.Image.Tests/ComfyUiImageTransportTests.cs) |
| Tensor Text / Qwen | Complete: setup/editing, repository or local file/folder/URL selection, bounded Qwen 3 and Qwen 3.5 family GGUF inspection, complete single/split-pack resolution, explicit Import/Install/lifecycle and backend/readiness observations | [File/folder/URL and shard tests](../Tensor/tests/LLM/Darkspyre.Inference.Tensor.LLM.Qwen.Tests/QwenLocationTests.cs), [resolver/setup tests](../Tensor/tests/LLM/Darkspyre.Inference.Tensor.LLM.Qwen.Tests/QwenModelResolverTests.cs), [selected-pack/edit tests](../Tensor/tests/LLM/Darkspyre.Inference.Tensor.LLM.Qwen.Tests/QwenPackSelectionTests.cs) |
| Tensor Image / Supra | Complete: source-independent location/companion metadata and same-schema DiT resolution with all four roles; pinned companion content remains mandatory, Hugging Face origin does not; explicit Import/Install/lifecycle | [Complete-pipeline and independent-URL tests](../Tensor/tests/Image/Darkspyre.Inference.Tensor.Image.Supra.Tests/SupraModelResolverTests.cs) and [selected-pack/edit tests](../Tensor/tests/Image/Darkspyre.Inference.Tensor.Image.Supra.Tests/SupraPackSelectionTests.cs) |
| Connect Audio | Complete: external PocketTTS speech synthesis and TangoFlux audio generation, typed contracts/capabilities, real protocol adapters, discovery where supported, setup/defaults/editing and explicit tests | [PocketTTS protocol and settings tests](../Connect/tests/TTS/Darkspyre.Inference.Connect.TTS.Tests/PocketTtsTests.cs), [TangoFlux settings tests](../Connect/tests/Audio/Darkspyre.Inference.Connect.Audio.Tests/AudioSetupTests.cs) and [audio protocol suite](../Connect/tests/Audio/Darkspyre.Inference.Connect.Audio.Tests/) |
| Tensor Audio | Complete: resident Whisper STT, batch/identified-window transcription, twelve supported content identities, local/file/URL/repository metadata and explicit Import; setup/editing and CPU-runtime checks | [Model/settings/independent-source tests](../Tensor/tests/STT/Darkspyre.Inference.Tensor.STT.Whisper.Tests/WhisperMetadataTests.cs), [lifecycle tests](../Tensor/tests/STT/Darkspyre.Inference.Tensor.STT.Whisper.Tests/WhisperLifecycleTests.cs) and [PCM/storage tests](../Tensor/tests/STT/Darkspyre.Inference.Tensor.STT.Whisper.Tests/WhisperPcmAndStorageTests.cs) |
| Tensor Decision / Laya | Complete: ONNX/Core ML/CPU implementation, local/file/URL/repository metadata, exactly three required artifacts, bounded metadata-only inspection and explicit Import; shared setup/lifecycle and selected-pack calibration/tokenizer settings | [Three-artifact import/inspection tests](../Tensor/tests/Decision/Darkspyre.Inference.Tensor.Decision.Laya.Tests/LayaModelResolverTests.cs), [pack-selection/settings tests](../Tensor/tests/Decision/Darkspyre.Inference.Tensor.Decision.Laya.Tests/LayaPackSelectionTests.cs) and [serving-parity/golden-token tests](../Tensor/tests/Decision/Darkspyre.Inference.Tensor.Decision.Laya.Tests/LayaServingParityTests.cs) |

### Cross-row acceptance and limits

The shared contract tests cover secret Keep/Replace/Clear, cancelled edits, stale async completions,
busy mutation/commit, version conflicts, uncertain commits and observer failures. Typed host commit
targets remain the only persistence authority. Connect tests exercise actual HTTP/CLI/provider
adapters at controlled boundaries; saving does not authenticate, connect or infer. All eight LLM
definitions pass the same create/edit/commit test. ComfyUI, PocketTTS, TangoFlux and each Tensor
implementation also exercise create and existing-configuration edit flows.

[Tensor source tests](../Tensor/tests/Common/Darkspyre.Inference.Tensor.Common.Tests/TensorSourceTests.cs),
[resolution tests](../Tensor/tests/Common/Darkspyre.Inference.Tensor.Common.Tests/TensorResolutionTests.cs),
[remote settings tests](../Tensor/tests/Common/Darkspyre.Inference.Tensor.Common.Tests/TensorRemoteSetupTests.cs)
and [manager tests](../Tensor/tests/Common/Darkspyre.Inference.Tensor.Common.Tests/TensorModelPackManagerTests.cs)
cover bounded reads, private/gated/denied access, origin-scoped secrets through redirects, immutable
identity, stale choices, explicit Install, progress, corrupt/partial installs and active leases.
Opening/saving Settings does not register, download or load a remote selection. The typed commit
carries its complete definition; explicit Install registers it through the existing manager.

[Location/import tests](../Tensor/tests/Common/Darkspyre.Inference.Tensor.Common.Tests/TensorLocationImportTests.cs)
cover explicit unknown-hash acquisition through the host store, missing license, ambiguity, mismatch,
cancellation, range rejection and settings commit without a live engine. The source extension is
specified in [the location/import design](tensor-model-locations.tech-design.md), published in 11.4.0-rc.1.

- Qwen supports the Qwen 3 and Qwen 3.5 family GGUF schemas, not every GGUF architecture. Supra supports its
  current Supra2/Flan-T5/VAE pipeline and matching DiT variants, not arbitrary diffusion families.
- Whisper remote compatibility initially recognizes the twelve published weight identities plus
  actual GGML header evidence. Unknown remote content remains Unverified. Only the CPU runtime
  is supplied; English-only and turbo translation restrictions are explicit.
- Supported schema, available backend, installed/verified pack and loaded/ready instance are distinct.
  Missing evidence stays unknown; a backend probe does not guarantee enough memory for a load.
- Installation does not claim resume support. No automatic retry of potentially billed generation,
  model load, authentication, microphone capture, VAD, worker supervision or UI-owned engine lifetime
  was added. Normal teardown protects requests; urgent teardown still waits for safe native completion.
- Deterministic tests do not certify full-weight transcription quality or GPU/native performance.
  Optional bounded live metadata checks resolved Qwen, Supra and all twelve Whisper definitions
  without loading models or downloading full weights. The prior full-weight Supra run remains
  historical evidence, not a new experiment in this acceptance pass.
- The original source acceptance used the local TensorSharp validation pin. The later 11.3.0-rc.1
  release uses the privately published TensorSharp 2.8.6.4 package closure, as recorded in
  [the release record](../.roles/project.md#published-1130-rc1-2026-09-23).
- UI rendering/accessibility/themes and CE/Agent-DS integration are outside this runtime report.

Final verification results are recorded in
[implementation unit 5](provider-setup-and-audio-technical-design.md#unit-5--capability-table-acceptance-and-completion-2026-09-22).

## Historical 11.2.0 baseline and original requirements

The numbered sections below preserve the release-source gap assessment at `83c1ac3`.
Their “Current support” and “Missing” labels refer to **that published baseline**, not today's
completed source. The summary table above and approved design are the current implementation record.

## 1. Shared discovery, configuration and wizard contracts

### Current support

`InferenceImplementationDescriptor` identifies an implementation and whether it belongs to Connect
or Tensor. Portable inference interfaces expose their own typed capabilities. Connect Text has a
separate metadata catalog; Connect Image discovers registered transports; Tensor accepts a host-
composed model catalog. These are useful existing contracts, but not one complete configuration
surface covering the requested groups.

### Missing

- Discoverable provider definitions with stable identity, display metadata, capability type, family,
  supported setup/edit operations, and availability on the current machine. Discovery must not
  require opening a connection, loading weights, or downloading a model.
- Purposeful sections and ordered wizard steps, including advanced sections, semantic input kinds,
  defaults, constraints, required/secret/read-only state, and conditional visibility or enablement.
- Static and asynchronous choices with stable values, labels, paging/search where needed, dependency
  refresh behavior, and an explicit declaration of whether manual entry is supported.
- Callable validation returning field- and step-associated failures, warnings and actionable
  messages. Human-readable completion instructions alone cannot drive Next, Save or Finish.
- Provider-owned action availability and execution: begin/continue/cancel setup, authenticate,
  discover/select models, test/check readiness, and applicable installation/lifecycle actions.
  Actions need typed results, progress, cancellation, retryability and completion state. Names here
  describe required semantics, not approved method signatures.
- A usable existing-configuration edit flow: safe readback, secret-present indicators without secret
  disclosure, validation of changes, and identification of changes requiring reauthentication,
  retesting, model unload or other explicit operations.
- A consistent status projection for cards and dialogs. Configured, authenticated, installed,
  verified, loaded and ready are distinct states; unknown or stale health is not success.
- Declared side effects: a check must say when it may send a paid inference request, open a browser,
  interact with an external CLI, download weights or load native resources. Saving or browsing
  Settings must not silently perform those actions.

Share only semantics genuinely common to Connect and Tensor in `Darkspyre.Inference.Common`.
Authentication/protocol details remain in Connect; model-pack/runtime details remain in Tensor.
This request does not authorize a universal wrapper, an untyped settings bag, or new facade packages.

## 2. Connect Text contracts and individual providers

### Current support

`LlmProviderDefinition` supplies ID, name, description, transport, default endpoint/model,
guided-setup visibility, ordering and setup steps. `LlmSetupStep` supplies instructions, an action
enum, inputs, platform-specific guidance, completion guidance and troubleshooting. `LlmSetupInput`
currently supplies only ID, display name, help text, required and secret flags.

`LlmProviderService` already provides authentication, credential-setting, sign-in, model listing,
capability and inference operations. `ILlmConnectionSource` is the existing host-owned credential
seam: credential replacement compares an opaque version atomically and must not upsert. Keep it;
the gaps below are not a request to move the application's database or vault into Connect.
`LlmConnectionAccess` owns runtime OAuth refresh for chat and embeddings. Its connection-bound
`LlmConnectionAuthorization` supplies Common's neutral authorization header without exposing storage.

The xAI connection contract supplies `XaiResponses`, typed region/tier options, fixed endpoints,
and OAuth-only draft setup and sign-in routing. The internal authenticator implements the device
protocol, refresh and `/v1/me`; the HTTP classifier does not yet recognize subscription-specific
403 responses. `AddLlmConnect` does not register a Grok provider or authenticator.
The shared Responses pipeline handles terminal-authoritative output and function completion,
account-bound opaque replay, context-exhaustion results and encrypted-context diagnostic redaction.
The internal `XaiResponsesLlmClient` supplies OAuth-only chat requests with typed endpoint/tier checks,
catalog-driven profiles and numeric schema preflight. Cross-family catalog ownership, multi-agent
setup controls and the complete provider schema subset remain unwired or incomplete.
[Connect's setup contract](../Connect/README.md#provider-setup-and-editing) describes this unregistered
boundary; it is not an additional completed or live-qualified provider in the table below.

| Provider | Current definition/behavior | Needed in the configuration contract |
|---|---|---|
| OpenAI API key / Responses | API-key setup, endpoint default, model and test steps | Typed key/endpoint inputs, validation and callable setup state; explicit test side effects |
| OpenAI-compatible Chat | Endpoint default, optional-key metadata, model/test steps; `IsGuidedSetupOption = false` | Contract-driven advanced-provider availability, endpoint and authentication rules, discovery/manual-entry behavior without a CE exception |
| Anthropic API key / Messages | API-key definition, endpoint default, model/test steps | Same complete field/action/editing semantics, retaining its own credential policy |
| ChatGPT subscription / direct Codex | Device authorization metadata and existing sign-in methods | Generic authorization interaction/result/progress/expiry/cancel semantics and safe credential completion through the versioned host seam |
| Claude subscription / official CLI | CLI verification step, external-login guidance, model/test steps | Callable prerequisite/auth checks and typed executable configuration; distinguish CLI-owned authentication from optional model-catalog OAuth |
| Gemini subscription / Antigravity CLI | Executable input, platform instructions, CLI verification, model/test steps | Typed executable input, availability and verification results; no host scraping or copying CLI tokens |
| AWS Bedrock Converse | AWS-profile setup and region-aware model/test guidance | Declarative profile/region choices and validation tied to existing AWS operations; no host hardcoded AWS wizard |
| AWS AgentCore Harness | Profile, region, Harness ARN inputs, model override and session-based test guidance | Typed ARN/profile/region configuration, action binding, capability-driven model selection and test outcome |

### Missing across these providers

The existing action enum describes what a host should do, but is not a complete generic action
execution contract. Several execution methods are provider-specific. CE must not reconstruct the
dispatch rules from provider IDs, input IDs, help text or endpoint strings. Extend the existing
metadata and expose provider-owned setup operations so one reusable renderer can support all of
these definitions, including editing after first-time setup. Preserve native versus host-owned
authentication distinctions and the current credential concurrency guarantees.

Sources: [definitions and setup inputs](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmProviderDefinition.cs),
[catalog](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmProviderCatalog.cs),
[built-in definitions](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/Providers/ProviderDefinitions.cs),
[Bedrock definitions](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Bedrock/BedrockProviderDefinitions.cs),
[provider service](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/LlmProviderService.cs),
[credential seam](../Connect/src/LLM/Darkspyre.Inference.Connect.LLM/ILlmConnectionSource.cs).

## 3. Connect Image / ComfyUI

### Current support

`ImageProviderService` lists registered transport descriptors and resolves the implementation.
`ImageConnection` carries endpoint, per-request timeout and optional headers. `ImageWorkflow`
supplies workflow metadata plus opaque transport configuration. `IImageTransport` exposes health
testing, preparation, reference upload, submission, status, WebSocket progress, download and
cancellation. The host retains workflow storage, durable jobs, media, cache and retry policy.

### Missing

The descriptor does not describe editable connection fields, validation, grouped setup steps,
authentication/header configuration, workflow choices or setup action state. Provide those through
the library contract. Where choices belong to the host, such as its saved workflows, declare the
required host capability rather than embedding CE persistence or filesystem access in Connect.
The existing execution contract and opaque provider state must remain usable by CE's durable jobs;
a generic wizard must not require replacing them with an all-in-one generation call.

Sources: [transport descriptor](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageTransport.cs),
[connection](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageConnection.cs),
[workflow](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageWorkflow.cs),
[transport operations](../Connect/src/Image/Darkspyre.Inference.Connect.Image/ImageTransportContracts.cs).

## 4. Tensor configuration, discovery and model installation

### Current support

`TensorModelPackDefinition` records a model/revision key, capability, runtime architecture, license,
project URI, supported backends and artifacts. Each artifact has a role, relative path, expected
length, SHA-256 and source URIs. `TensorModelCatalog` validates host-supplied definitions; it does
not search a hosting service.

`ITensorModelPackManager` already provides definition lookup, status, installation with progress and
cancellation, verification, lease acquisition and removal. `TensorModelInstallProgress` includes
stage, artifact role and transferred/total bytes. Installation checks pinned artifact lengths and
hashes. Public Hugging Face resolve URLs already appear in the built-in model definitions.
**Do not replace this downloader with a CE implementation.**

`ITensorModelInstance` supplies explicit Start, Load, Unload and Stop operations. Requests never
implicitly download or load weights. Stop is terminal for an instance, and cancellation cannot free
native resources while active work still uses them. Preserve these lifecycle rules.

### Missing: model-host discovery and compatibility

1. Provider-owned, cancellable search/browse of supported hosts such as Hugging Face, including
   paged results, variants and revisions. The host service adapter handles its protocol; CE does
   not scrape pages or construct download URLs.
2. Engine-owned compatibility evaluation. Check actual architecture, format, quantization where
   applicable, required companion artifacts, tokenizer/configuration, backend and platform
   requirements. Distinguish supported, unsupported and not yet verified; return reasons. Names,
   extensions and model-host tags alone are not proof of compatibility.
3. Resolution of a selected candidate into a complete, immutable, installable pack: pinned revision,
   all artifact roles, source identities, integrity metadata, sizes and license information.
   Unknown size or unverifiable integrity must be explicit, not invented. Model repository content
   is untrusted input and must not become executable setup instructions.
4. Protected access for private/gated model hosts, access-denied and license/gating state, and
   explicit user action when required. Do not leak a host token across redirects/source hosts or
   persist it in a model URL or ordinary configuration.
5. Selection support in the actual engines. Current Qwen and Supra implementations select their
   built-in pinned definitions; allowing an arbitrary catalog entry in a picker does not make it
   loadable. The engine must validate and accept each model/variant advertised as compatible.
6. Provider-described configuration choices and current-machine readiness: supported versus
   available backends, relevant memory/disk/runtime requirements, model selection and advanced
   options. An enum of backend names is not a successful hardware-availability check.

### Missing: generic setup integration over the existing manager

- Describe the model picker and explicit Download/Install action as wizard properties and methods.
  A selection or Save alone must not start a potentially large download.
- Expose total download/disk requirements where known, artifact-level and overall progress,
  cancellation, verification results, failed/cancelled state and actionable retry behavior.
  Report whether resume is supported; do not claim resumability from the presence of cancellation.
- Keep discovery, installation, verification and loading visibly distinct. An installed model is
  not necessarily loadable on this machine; a configured provider is not necessarily ready.
- Define duplicate-install, active-lease, removal, unload and shutdown behavior without weakening
  the existing model manager and lifetime rules. Operation status must remain observable after a
  settings dialog closes; UI component lifetime must not accidentally own the operation.

| Tensor implementation | Existing model/runtime boundary | Additional provider-specific requirement |
|---|---|---|
| Qwen Text | `QwenLlmInference`, `TensorLlmModels.Qwen3FourBillionQ4Km`, typed LLM interface and explicit lifecycle | Enumerate genuinely supported GGUF candidates/variants; validate their architecture and backend compatibility; load the selected supported pack rather than always selecting the built-in one |
| Supra Image | `SupraImageInference`, `TensorImageModels.Supra2Image`, typed image interface; pack includes Supra, text encoder/tokenizer and VAE artifacts | Resolve and validate the entire compatible artifact set. Surface current output and prompt limitations through capabilities; do not offer arbitrary image-model repositories as interchangeable |
| Laya Decision | Typed decision interface and its own pinned definition | Keep correct capability identity in shared discovery. CE's requested three settings groups do not authorize adding a Decision section |
| Audio | No engine or model-pack capability contract found | Define actual audio tasks, engines, supported artifacts and compatibility rules before an audio model picker can be functional |

Sources: [catalog and pack definitions](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModels.cs),
[pack manager](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/TensorModelPackManager.cs),
[model lifecycle](../Tensor/src/Common/Darkspyre.Inference.Tensor.Common/ITensorModelInstance.cs),
[Qwen engine](../Tensor/src/LLM/Darkspyre.Inference.Tensor.LLM.Qwen/QwenLlmInference.cs),
[Qwen pack](../Tensor/src/LLM/Darkspyre.Inference.Tensor.LLM.Qwen/TensorLlmModels.cs),
[Supra engine](../Tensor/src/Image/Darkspyre.Inference.Tensor.Image.Supra/SupraImageInference.cs),
[Supra pack](../Tensor/src/Image/Darkspyre.Inference.Tensor.Image.Supra/TensorImageModels.cs),
[Tensor integration guide](../Tensor/README.md).

## 5. Audio contract gap

The inspected 11.2.0 package assemblies, public documentation and source provide LLM/Text, Image
and Decision capabilities, not a standalone Audio lane. LLM image/document attachments or incidental
mentions of audio do not constitute an audio provider configuration or execution contract.

To satisfy the requested Connect and Tensor Audio section, define the intended tasks and their
typed input/output, capability, discovery, configuration, setup, testing, progress/cancellation and
failure contracts. Implement the selected Connect adapters and Tensor engines. Tensor audio must
participate in the same compatible-model discovery and verified installation flow. This document
does not select vendors, engine architectures, voice catalogs or supported audio formats by guess.

## Ownership and acceptance gates

Common, Connect and Tensor own provider semantics, provider/model-host protocols, compatibility
decisions, setup metadata and operations, and the existing Tensor model manager/lifecycle.
`Darkspyre.Inference.UI.Settings` owns the shared Lumeo/Blazor presentation foundation. CE and
Agent-DS own their themes, application composition, local connection/configuration persistence,
protected secret storage through supported seams, user consent and application operation ownership.
The runtime libraries remain UI-framework-neutral. No host-specific database model or copied
provider source belongs in the shared UI library.

Before delivering the shared settings surface to CE and Agent-DS, the library work should demonstrate:

1. A single reusable editor can configure and edit each supported Text/Image/Audio Connect/Tensor
   provider session without provider-name/transport-specific UI branches. Each host owns listing,
   Add/Edit entry points and placement.
2. Typed descriptors and methods cover dependent fields, dynamic choices, validation, authentication,
   tests and asynchronous progress. Existing configuration and secrets survive cancelled edits and
   stale concurrent completions cannot overwrite newer credentials/configuration.
3. Tensor discovery returns only candidates with explicit compatibility outcomes and can resolve a
   supported selection into a verified installable pack that its chosen engine can actually load.
4. Installation is explicit, cancellable and observable; corrupted, inaccessible, incompatible or
   partially downloaded artifacts never appear ready. No automatic download/load happens merely
   from opening Settings, constructing an engine or making an inference request.
5. Audio is backed by real implementations and contracts. Missing capabilities produce a library
   blocker, not invented UI support or an undocumented reduction of the requirement.
6. Deterministic tests use controlled provider/model-host responses and small fixtures. Paid calls,
   full model downloads and native inference are not mandatory development gates.
7. CE and Agent-DS consume the same Settings editor with distinct themes and supported extensions.
   Neither host duplicates provider forms or wizard behavior. The host controls any surrounding
   dialog, page or inline layout; the editor remains accessible and responsive in each placement.

The runtime contract design was subsequently approved and implemented; the current summary above
and canonical technical design supersede this baseline's missing-capability assessment. Acceptance
gates 1–6 have source-level runtime evidence; gate 7 and visual integration remain the separate UI
worker's scope. Consumers can integrate against the source contracts now; consuming released NuGets
requires a separately authorized coordinated release. This completion report does not authorize
publication, full model downloads, UI changes or changes to a user's configured providers.
