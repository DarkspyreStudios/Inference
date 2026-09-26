# Darkspyre.Inference project configuration

This document gives agents a high-level map of the intended `Darkspyre.Inference.*` library family.
It describes the target structure, not merely the repository's current implementation state. Do not
infer requirements from existing code, previously published packages, or donor repositories. The
design bibles and Jira tickets remain authoritative for detailed designs and technical
specifications.

## Identity

| Item | Value |
|---|---|
| Product family | `Darkspyre.Inference.*` |
| Purpose | Consolidate Darkspyre Studios' inference-related implementations into one cohesive family of four synchronized NuGet packages |
| Repository | `/Volumes/Data/Development/DarkspyreStudios/Inference` |
| Target framework | `net11.0` |
| Distribution | Private DarkspyreStudios NuGet feed |
| Version line | `<net>.<release>.<minor>`: .NET generation, breaking-contract release, compatible tweak/fix |
| Release model | `Common`, `Connect`, `Tensor`, and `UI.Settings` publish together at the one version declared in `/Directory.Build.props` |
| Primary branch | `master` |
| Lifecycle stage | Four-package `11.10.3-rc.1` published privately |
| Jira project and parent | TBD |
| Jira subject label | TBD |

The technical assembly and namespace prefix is `Darkspyre.Inference`.

## Package families

The planned release surface contains exactly four NuGet packages. Capability names remain assembly and
namespace boundaries inside their owning product package; they are not independently versioned or
published NuGets.

| Package or family | Responsibility |
|---|---|
| `Darkspyre.Inference.Common` | Interfaces and models genuinely shared by connector-based and in-process tensor inference |
| `Darkspyre.Inference.Connect` | All implemented out-of-process connectors; contains capability-specific assemblies such as `.Connect.LLM`, `.Connect.LLM.Bedrock`, and `.Connect.Image` |
| `Darkspyre.Inference.Tensor` | All implemented in-process engines; contains `.Tensor.Common`, the `.Tensor.LLM`, `.Tensor.Image`, `.Tensor.STT`, and `.Tensor.Decision` capability contracts, and the `.Tensor.LLM.Qwen`, `.Tensor.Image.Supra`, `.Tensor.STT.Whisper`, and `.Tensor.Decision.Laya` implementations |
| `Darkspyre.Inference.UI.Settings` | Host-placed Blazor/Lumeo editor for provider settings and their managers; references Common but not Connect or Tensor |

Connect implements LLM, ComfyUI Image, PocketTTS speech synthesis and TangoFlux audio generation.
Tensor implements Qwen LLM, Supra Image, Laya Decision and Whisper speech transcription. Audio tasks
retain their own typed contracts. UI.Settings renders the shared provider-owned setup contract.

## Execution boundary

The distinction between the package families is the process boundary, not where a provider is
physically hosted:

- `Connect.*` communicates with an out-of-process provider through its native protocol or a wrapper
  around an official CLI. The provider may be remote or locally hosted; ComfyUI remains a connector.
- `Tensor.*` loads and executes the inference engine directly inside the consuming process.

## Dependency direction

The intended shared dependency flow is:

```text
Darkspyre.Inference.Common
├── Darkspyre.Inference.Connect
├── Darkspyre.Inference.Tensor
└── Darkspyre.Inference.UI.Settings

Darkspyre.Inference.Tensor package
└── Darkspyre.Inference.Tensor.Common assembly
    ├── Darkspyre.Inference.Tensor.LLM assembly → .Tensor.LLM.Qwen implementation
    ├── Darkspyre.Inference.Tensor.Image assembly → .Tensor.Image.Supra implementation
    ├── Darkspyre.Inference.Tensor.STT assembly → .Tensor.STT.Whisper implementation
    └── Darkspyre.Inference.Tensor.Decision assembly → .Tensor.Decision.Laya implementation
```

Place a contract or model in `Darkspyre.Inference.Common` only when the Connect and Tensor products
truly share it. Connector-only contracts and models belong in their Connect capability assembly.
Do not create a Common package until at least two implemented capabilities share a contract with the
same semantics. Similar property names, such as endpoint or display-name fields, are not by themselves
a shared contract. LLM messages, tools, typed requests/results, streaming events, and image-generation
requests/results now qualify because both Connect and Tensor implement those semantics. Typed
decision requests and results also live in Common: the host-facing product and the in-process Laya
implementation share the same semantic contract even though the first Connect release does not yet
ship a decision provider.
`Darkspyre.Inference.Tensor.Common` is an internal product assembly, included in the
`Darkspyre.Inference.Tensor` NuGet, and is limited to behavior actually shared by two Tensor
capabilities, such as the host-controlled model lifecycle, model packs in a local pack directory,
and execution coordination. The capability assemblies define interchangeable interfaces; engine
implementations live in separate assemblies within the same NuGet. Laya architecture, tokenization,
calibration, and its model-pack definition remain in the Tensor Decision Laya assembly. There is
still no `Connect.Common` assembly: no implemented connector-only contract is shared broadly enough.

The `11.2.0` Tensor release has the explicit `StartAsync`/`LoadAsync`/request/`UnloadAsync`/`StopAsync`
contract described in `Tensor/README.md`. The host controls when to load an installed pack and when
to end the resident model's lifetime; no request implicitly loads or downloads weights. Stop is
terminal for an instance. An uncancelled teardown waits for active requests; cancellation requests
their prompt termination but must still wait for native execution to stop using the model. This is
a breaking Tensor contract and assembly-layout change from the published `11.1.0` baseline; the
coordinated family version advances to `11.2.0`. The host, Bridge, API transport, and Desktop
application migrations remain outside this package release.

The repository uses .NET Central Package Management (CPM). Package versions are declared centrally,
and any implementation package may reference the centrally managed version of a NuGet dependency
when its design requires it. Supporting DarkspyreStudios packages available through the private feed
include `Darkspyre.SecureStore` and `Darkspyre.Persistence`.

The tokenizer runtime assembly comes from the focused `Darkspyre.ML.Tokenizers` fork. Connect keeps
Microsoft's public `Microsoft.ML.Tokenizers.Data.O200kBase` package rather than forking unchanged
encoding data; its direct fork dependency is the nearer runtime asset than the data package's
transitive public tokenizer dependency. Tensor uses the same fork for SentencePiece and BPE. Do not
fork adjacent Microsoft packages unless Darkspyre actually needs to change their implementation.

## Solutions

| Solution | Intended scope |
|---|---|
| `Darkspyre.Inference.slnx` | Complete solution containing every library in the package family |
| `Darkspyre.Inference.Connect.slnx` | Focused subset containing Common plus the `Connect.*` libraries and their tests |
| `Tensor/Darkspyre.Inference.Tensor.slnx` | Focused subset containing the Tensor libraries and their tests |
| `UI/Darkspyre.Inference.UI.slnx` | Focused subset containing Common, the UI.Settings editor library, and its tests |

Additional subset solutions are not yet defined by this document.

There is no root `Darkspyre.Inference` package. The root solution is an aggregate build surface, not
a package or assembly. The product packaging projects are
`Connect/src/Darkspyre.Inference.Connect` and `Tensor/src/Darkspyre.Inference.Tensor`.
`UI/src/Settings/Darkspyre.Inference.UI.Settings` is its own Razor Class Library package.

## Required commands

The complete unified-solution commands are TBD. Do not substitute commands from a donor project or
infer them from the repository's current state.

### Current Connect subset

Run these commands from `Connect/` so its `global.json` selects the SDK. The repository-root
`NuGet.Config`, `Directory.Build.props`, and `Directory.Packages.props` remain authoritative for every
project discovered by the subset solution.

| Gate | Command |
|---|---|
| Restore | `dotnet restore Darkspyre.Inference.Connect.slnx` |
| Build | `dotnet build Darkspyre.Inference.Connect.slnx --no-restore` |
| Test | `dotnet test Darkspyre.Inference.Connect.slnx --no-build --no-restore` |
| Format | `dotnet format Darkspyre.Inference.Connect.slnx --verify-no-changes --no-restore` |
| Pack | `dotnet pack Darkspyre.Inference.Connect.slnx --no-build --no-restore` |
| Dependency audit | `dotnet list Darkspyre.Inference.Connect.slnx package --vulnerable --include-transitive` |

The Pack command is reserved for the explicitly authorized publish phase. Do not run it during the
ongoing refactor or create package artifacts merely as a routine verification step.

Several capability-scoped packages were initially published at `10.0.0` and remain immutable
historical artifacts. The first release of the three-package product surface uses `10.1.0`; those
three packages are `Darkspyre.Inference.Common`, `Darkspyre.Inference.Connect`, and
`Darkspyre.Inference.Tensor`. All read the same version from `/Directory.Build.props` and are always
released in tandem. Compatible tweaks and bug fixes advance the minor component (`10.1.1`,
`10.1.2`, and so on). A breaking contract advances the release component (`10.2.0`). Moving the
build to .NET 11 advances the first component and resets the later components for the selected
release (`11.<release>.<minor>`). Immutable releases are never overwritten. The family tag is
created only after all three packages have passed their gates.

## Published 11.5.1-rc.1 source authentication update (2026-09-23)

The user authorized the coordinated `11.5.1-rc.1` release, including the staged Basic source
authentication implementation and its tests/documentation from another worker. Both Bearer and
Basic credentials retain the existing exact-origin request boundary and cross-origin redirect
protection. All in-repository callers migrate to the credential factories. The source-credential
amendment in `docs/tensor-model-locations.tech-design.md` records the release scope.

All four packages were published privately from `1d24f8f244355f4d43a4d39d7d50aea7b29d5409`.
Authenticated restores, zero-warning Debug/Release builds, formatting checks and transitive audits
pass. Tests pass in both configurations: Connect 327, Tensor 298 and UI 49 (24 Common tests occur
in each subset). Archive inspection confirms exact `[11.5.1-rc.1]` Common dependencies and no
embedded sibling or third-party/native binaries. Local-archive and empty-cache feed-only consumer
checks pass, including Whisper native initialization. Downloaded archives are byte-identical to
the verified local files; GitHub readback confirms all four versions and private visibility.
Release source, publication record and tag `v11.5.1-rc.1` are pushed to origin. No model download
or paid provider call was used as a release gate.

## Published 11.5.0-rc.1 reasoning contract release (2026-09-23)

The shared version advances to `11.5.0-rc.1` for the provider-neutral reasoning-level contract
replacement. Common, Connect, Tensor and UI.Settings remain synchronized. This source-only unit
includes the complete reasoning implementation and documentation prepared by another worker,
together with the version update. The subsequent user-authorized publication includes all four
packages from release source `500a572f6de98a994d1a4cbc5eae041577f04290`.
Source is hosted privately at `https://github.com/DarkspyreStudios/Inference` on `origin/master`.

Authenticated restores, zero-warning Debug/Release builds and tests pass: Connect 327, Tensor 292
and UI 49 per configuration (24 Common tests are included in each). Formatting checks and all
transitive vulnerability audits pass. Release binaries were rebuilt/retested at the source commit.
Archive checks verify owned assemblies only, exact `[11.5.0-rc.1]` Common dependencies, required UI
assets and no embedded sibling or third-party/native binaries. The package-only consumer passes
against local archives and again with an empty package cache and no HTTP cache against normal
feeds, including Whisper native runtime initialization. All four feed archives are byte-identical
to the verified local archives; GitHub readback confirms version and private visibility.
The release source is tagged `v11.5.0-rc.1`; source, publication record and tag are pushed to origin.
No paid provider calls, model downloads or consuming-application migrations were performed.

## Published 11.4.1-rc.1 Tensor update (2026-09-23)

The user authorized a synchronized compatible-tweak release of all four packages. It includes
`5ba23f9`: Laya Core ML uses `MLComputeUnits=CPUAndGPU`, excluding Neural Engine preparation.
The recorded acceptance experiment and no-cache decision remain in the Laya technical design.
No public contract or dependency version changes. All four packages were published to the private
DarkspyreStudios feed from `eee7c30176600b980585c20d172fcbbb7b112d9b`.

Authenticated restores, zero-warning Debug/Release builds and tests pass: Connect 301, Tensor 291
and UI 49 per configuration (24 shared Common tests appear in each total). All formatting checks
and transitive vulnerability audits pass. Archive verification confirms owned assemblies only,
exact `[11.4.1-rc.1]` Common dependencies and no embedded sibling or third-party/native binaries.
The package-only consumer passes against local archives and again against normal feeds with an
empty package cache and no HTTP cache, including Whisper native runtime initialization. All four
downloaded archives are byte-identical to the verified local files; GitHub readback confirms the
version and private visibility. The full-weight acceptance experiment was not rerun for publication.

Local immutable tag `v11.4.1-rc.1` points to the release source. No Git remote is configured, so no
source/tag push occurred. Existing unrelated untracked root README and GitHub files were excluded.

## Published 11.4.0-rc.1 contract release (2026-09-23)

The user authorized all four packages at `11.4.0-rc.1`. This advances the breaking-contract
release component for the typed model-location/resolver/import contracts and includes the completed
Laya ONNX/Core ML migration. Common remains an exact synchronized NuGet dependency rather than an
embedded DLL. The release also includes the optional host-owned file/folder picker capability.
All four packages were published to the private DarkspyreStudios GitHub Packages feed from source
`4b6657649d307b7a8764ec894f92870ac30bdabc`. Authenticated restores, zero-warning Debug/Release
builds and tests pass: Connect 301, Tensor 291 and UI 49 per configuration (24 Common tests are
included in each subset). Formatting verification and transitive vulnerability audits pass.

`eng/VerifyPackages` passes for all four archives: only owned assemblies/XML, exact
`[11.4.0-rc.1]` Common dependencies, required UI assets and no embedded sibling, third-party or
native binaries. The package-only consumer passes against local archives and then normal feeds
with a new empty package cache and no HTTP cache. It loads the release source identity and
initializes Whisper's public native runtime. All four downloaded archives are byte-identical to
the verified local archives. GitHub readback confirms all four versions and private visibility.

Immutable local tag `v11.4.0-rc.1` identifies the release source. No Git remote is configured,
so source and tag were not pushed. No model downloads, paid calls or consumer migrations were run.

## Published 11.3.1-rc.1 correction release (2026-09-23)

The user approved the Settings edits and requested a minor bump and private publication of all four
packages. `/Directory.Build.props` advances to `11.3.1-rc.1`, following the user-defined
`<net>.<release>.<minor>` scheme and retaining the .NET 11 RC prerequisite suffix. No third-party
dependency version changes. Connect, Tensor and UI.Settings reference Common at exactly
`[11.3.1-rc.1]`; archive inspection confirms no embedded Common, sibling, third-party or native DLLs.
UI.Settings contains its Lumeo attribution and static assets. The following implementation record
documents the approved fixes.

All four packages were published to the private DarkspyreStudios GitHub Packages feed from source
`e2b4daedd8a96e60c986e7ea51a653de7cb8d546`. Authenticated restores, zero-warning Debug/Release
builds and all tests pass: Connect 301, Tensor 260 and UI 45 per configuration (shared Common tests
are included in each total). Formatting checks and transitive vulnerability audits pass. The final
Release binaries were rebuilt and retested after the release-source commit, then all four archives
passed `eng/VerifyPackages`, including exact dependencies and source identity.

`eng/VerifyConsumer` passes against local release archives and again with a new empty package cache,
no HTTP cache and only normal public/private NuGet sources. It loads the new Settings contracts and
provider surfaces at the release source identity, and initializes Whisper's public native runtime
without a direct Whisper package reference. All four feed-downloaded archives are byte-identical to
the verified local files. GitHub API readback confirms each version and private visibility. No model
weights, paid calls or consumer-application migrations are part of the release.

Immutable local tag `v11.3.1-rc.1` points to the release source. This repository still has no configured
Git remote, so no source or tag was pushed. Earlier published versions remain unchanged.

## Settings corrections implementation (2026-09-23)

The approved five-defect Settings unit adds a local-input Save/Next gate, authoritative snapshot
authentication interactions, provider-owned draft dirty state, latest-request-wins choice loading,
explicit choice capabilities and exact typed numeric controls. Common, all Connect/Tensor setup
implementations and UI.Settings are updated together. Hosts receive `SetupEditorState` for dirty
close, Save availability and uncertain-commit readback; they still own placement and operation lifetime.

The numeric component is owned by UI.Settings and adapts Lumeo's MIT-licensed layout/accessibility,
not its double-based parsing. Lumeo remains an unmodified dependency. Its attribution is included
in source and required by the future archive-verification gate. No consumer application is changed.

Debug/Release builds have zero warnings/errors. In each configuration, Connect passes 301 tests,
Tensor 260 and UI 45 (24 Common plus 21 component tests); Common is included in all three totals.
Formatting verification and transitive vulnerability audits pass for all three solutions. Browser
layout review uses rendered component fixtures under light inline and dark framed Lumeo themes at
1440×1000 and 390×844, including errors, labels and keyboard focus. This is not CE/Agent-DS live
application certification. Verification requires no live provider calls or model weights.

The initial implementation kept `11.3.0-rc.1` and created no package artifacts or publications;
those published archives do not contain these fixes. The subsequent publication is recorded above. See
[`docs/settings.ui.tech-design.md`](../docs/settings.ui.tech-design.md#12-post-release-settings-corrections-2026-09-23).

## Published 11.3.0-rc.1 (2026-09-23)

The user authorized the coordinated four-package private-feed release and the prerelease suffix.
The breaking release component changes because the former LLM setup input/action types are replaced
by the shared Common setup contracts. The `rc.1` suffix is required by UI.Settings' .NET 11 RC
ASP.NET component dependency; private feeds do not bypass NuGet's stable/prerelease validation.

All four packages use 11.3.0-rc.1: Common, Connect, Tensor and UI.Settings. Connect, Tensor and
UI.Settings depend on Common at exactly `[11.3.0-rc.1]`; no product embeds another product's DLL.
UI.Settings has no Connect/Tensor dependency. Provider SDKs and public Whisper/NAudio runtimes remain
NuGet dependencies. TensorSharp's nine-package managed closure advances to 2.8.6.4 on its existing
fork, replacing the local-only validation pin.

Debug/Release builds have zero warnings/errors. Tests pass in both configurations: Connect 293,
Tensor 257, UI 25 (22 shared Common plus three editor component tests). All three solution formatting
checks and transitive vulnerability audits pass. `eng/VerifyPackages` verifies the exact manifests,
owned DLL/XML lists, UI static assets and absence of embedded third-party/native binaries. A fresh
four-PackageReference consumer loads the public contracts and initializes the public Whisper native
runtime. No model weights, paid inference or consuming-app migration are part of release verification.
The component suite does not certify the separately planned CE/Agent-DS visual integration matrix.

The initial UI build required removing one redundant event-callback render and updating the test
harness to the pinned bUnit/Lumeo APIs. The first Tensor pack exposed Whisper's imported `Pack=true`
native files; Tensor now excludes those files and lets its public runtime dependency's build assets
reach consumers. No warning was suppressed and no third-party binary was copied into Tensor.

All four versions were published to the private DarkspyreStudios GitHub Packages feed from release
source `1e431c5f56aec954ee13e8f58988d0fc833cfccb`. The required nine TensorSharp packages were published
at 2.8.6.4 from `eb56c0dd354b6307e44d99f607cf240874170748`. GitHub API readback confirms the version
and private visibility of every package, including the newly created UI.Settings package.

A second fresh consumer restored using only the repository's normal public/private NuGet sources,
an empty package cache and no HTTP cache. It loaded all tested Inference surfaces at the release
source identity and initialized Whisper's public native runtime without a direct Whisper reference.
All thirteen downloaded archives are byte-identical to the verified local release archives.
Immutable local tags `v11.3.0-rc.1` (Inference) and `v2.8.6.4` (TensorSharp) identify those source
commits. Inference still has no configured Git remote; no source or tag was pushed by this release.

## Published 11.2.0 baseline

The synchronized three-package release was published to the private DarkspyreStudios GitHub
Packages feed on 2026-09-22 from release-source commit `83c1ac3`:

- `Darkspyre.Inference.Common` 11.2.0
- `Darkspyre.Inference.Connect` 11.2.0
- `Darkspyre.Inference.Tensor` 11.2.0

This breaking-contract release keeps `net11.0` and changes Tensor from concrete capability
assemblies to capability contracts plus separate Qwen, Supra, and Laya implementation assemblies.
It adds explicit host-controlled model load, request, unload, and terminal stop semantics. No host,
Bridge, API transport, or consuming Desktop application is included.

Both focused solutions passed authenticated restore, zero-warning Debug and Release builds,
formatting verification, and transitive dependency vulnerability audits. Connect passed 189 tests
and Tensor passed 32 tests in each configuration; Common's three tests appear in both totals.
Archive inspection confirmed `net11.0` assemblies and XML documentation, exact `11.2.0` Common
dependencies in Connect and Tensor, and no embedded Common assembly in either product package. A
fresh consumer restored and ran first against the local archives and then against the published
feed. All three published versions were read back through the GitHub Packages API. This release did
not rerun the earlier full-weight Supra generation smoke test; it verified package loading and the
automated lifecycle tests. The repository still has no configured Git remote, so any family tag is
local only until a remote is configured.

## Published 11.1.0 baseline

The synchronized three-package baseline was published to the private DarkspyreStudios GitHub
Packages feed on 2026-09-22 from implementation commit `54dbc5a`:

- `Darkspyre.Inference.Common` 11.1.0
- `Darkspyre.Inference.Connect` 11.1.0
- `Darkspyre.Inference.Tensor` 11.1.0

The family targets `net11.0` with SDK `11.0.100-rc.1.26425.128`. The framework-generation change
does not alter the approved inference contracts and does not include upgrading any consuming Desktop
application.

The release uses `Darkspyre.ML.Tokenizers` 2.0.0.1 from its `darkspyre` commit `b1b44954` for the
SentencePiece disabled-special-token correction. Connect continues to use the public
`Microsoft.ML.Tokenizers.Data.O200kBase` 2.0.0 package. Tensor uses the Darkspyre TensorSharp
2.8.6.3 package set from its `darkspyre` commit `28a29882`; that patch preserves the documented
rank-four channels-last result of direct image group normalization.

Both focused solutions pass zero-warning Debug and Release builds. Connect passes 189 tests and
Tensor passes 26 tests in each configuration; Common's three tests appear in both focused totals.
Formatting verification and transitive dependency vulnerability audits pass. The installed
Supra2-IMG model pack also completes a one-step CPU generation with seed 42 and produces a valid
256x256 PNG through both the source projects and a downstream consumer of the final package archives.
Archive inspection confirms the `net11.0` assemblies, the synchronized Common dependency, the
absence of embedded Common assemblies in Connect and Tensor, and the intended tokenizer dependency
graph. All three published versions were read back through the GitHub Packages API. No family tag
was created because this repository has no configured Git remote.

## Published 10.1.0 baseline

The first unified three-package baseline was published to the private DarkspyreStudios GitHub
Packages feed on 2026-09-22 from implementation commit `11b6ec7`:

- `Darkspyre.Inference.Common` 10.1.0
- `Darkspyre.Inference.Connect` 10.1.0
- `Darkspyre.Inference.Tensor` 10.1.0

Connect and Tensor declare `Darkspyre.Inference.Common` 10.1.0 as a NuGet dependency and do not
embed its assembly. Tensor uses the stable Darkspyre TensorSharp fork package set `2.8.6.2`, built
from the fork's `darkspyre` commit `05658d9c`.

Both focused solutions passed zero-warning Debug and Release builds. Connect passed 189 tests and
Tensor passed 26 tests in each configuration; Common's three tests appear in both focused totals.
Formatting verification and transitive dependency vulnerability audits passed. The final archives
were inspected for contents, dependency graphs, synchronized versions, and source commit metadata,
then their published `10.1.0` versions were read back through the GitHub Packages API. No family tag
was created because this repository has no configured Git remote.

## Canonical documents

| Area | Authority |
|---|---|
| High-level project and package map | `.roles/project.md` |
| Engineering constraints | `.roles/laws.md` |
| Repository and package conventions | `.roles/design-rules.md` |
| Connect 10.0 architecture and sequence | `Connect/docs/darkspyre-inference-connect-10-design.md` |
| Current LLM integration contract | `Connect/README.md` |
| Managed-agent technical design | `Connect/docs/enhancements.md` |
| Async service boundaries, local object operations, streams and notifications | `docs/inference-async-tech-design.md` |
| Managed-agent verification | `Connect/docs/managed-agent-verification.md` |
| Tensor package boundary, persistence, and implemented modalities | `Tensor/README.md` |
| Shared CE / Agent-DS Settings UI requirements and provider contract gaps (not an approved API design) | `docs/provider-settings-contract-support-and-gaps.md` |
| Host-placed Settings editor technical design and release validation | `docs/settings.ui.tech-design.md` |
| Completed provider setup and audio technical design | `docs/provider-setup-and-audio-technical-design.md` |
| Laya decision engine on ONNX Runtime: design, artifact contract and implementation record | `docs/laya-onnx-runtime.tech-design.md` |
| Provider-neutral LLM reasoning level: design and implementation record | `docs/llm-reasoning-level.tech-design.md` |
| Tensor user-selected file/folder/URL locations, compatibility and explicit import | `docs/tensor-model-locations.tech-design.md` |
| Detailed implementation work | Jira tickets; project and parent TBD |

If this document conflicts with an approved design bible or Jira technical specification, stop and
resolve the conflict rather than inferring which shape is current.

## Compatibility posture

The unified package surface introduced by `10.1.0` is the compatibility baseline. Source and binary
compatibility with earlier capability packages, including the `Mathis.LlmConnect` 3.0 predecessor,
is not required. Moving to `11.1.0` changes the target framework generation without intentionally
changing those approved inference contracts. Do not add compatibility packages, type forwarders,
duplicate namespaces, parallel legacy APIs, or migration paths unless an approved design bible or
Jira ticket explicitly requires one.

Previously published packages remain historical releases on the private feed; they are not the
design authority for the unified package family. `10.1.0` is the first unified compatibility
baseline. Provider behavior and stored credential identities are preserved unless an approved design
explicitly changes them.

## Automated private release 11.5.2-rc.1

Published all four packages from `ac7130fa726c763e7ce42756abd90fd5eac3dc3c` using `deploy-minor.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.5.2-rc.1`. No model weights or paid inference were used as verification gates.

## Private release 11.5.3-rc.1: Whisper Metal packaging

Published all four packages from `400d18e422852883e5d928e74d5af8a6998d21e2`. Tensor excludes the
shader imported by `Whisper.net.Runtime.Metal` from its own archive, retaining the runtime
dependency's output copying. Archive verification now rejects `.metal`; the package-only consumer
rejects distinct assets targeting the same output path. Regression fixtures verify duplicate,
mixed None/Content, distinct-path and noncopied inputs, and evaluated pack items preserve the
dependency shader while excluding it from Tensor's archive.

`deploy-minor.sh` selected 11.5.3-rc.1 and passed Debug/Release builds, tests, formatting and audits.
An adjacent test project initially made the consumer project selection ambiguous; publication had
not started. The fixture was moved to its own directory and the same version was resumed without
another bump. Final-source Release builds/tests, archives and fresh local/feed consumers pass.
Tests total Connect 329, Tensor 298 and UI 53 per configuration (24 Common tests occur in each).
All four downloaded archives match the local archives byte-for-byte and are private. Fresh consumer
evaluation confirms exactly one `ggml-metal.metal`, owned by `Whisper.net.Runtime.Metal`.
Source, this record and tag `v11.5.3-rc.1` are pushed to origin. No model downloads or paid calls.

## Bounded-compaction accounting and output-cap metadata implementation

Approved source implementation adds `LlmProviderService.MeasureRequestAsync` and
`ILlmAgentSession.MeasureContextAsync`, returning `LlmRequestAccounting`. Standalone accounting uses
the existing adapter projection and read-only catalog discovery, without credential refresh/writes
or inference. Session proposal accounting uses idle/base or paused/frozen run configuration and
queued steering without installing context or consuming a pause. Live status, proposal and standalone
accounting share the existing policy calculation; uncountable/omitted content stays unknown.
Provider-owned native contexts return unsupported. No Agent DS source or compaction ownership changes.

Common's `LlmOutputLimitEnforcement` is mapped through existing LLM capabilities: provider enforcement
for Responses/Anthropic/Bedrock Converse, engine enforcement for Qwen, unsupported for Codex/Claude
CLI/Antigravity, unknown for generic compatible endpoints and AgentCore harnesses. A reserve is never
advertised as a cap. Estimation is not a guarantee of fit; this unit adds no exact-count fallback.

The public session-interface change targets the breaking-contract line `11.6.0-rc.1`.
The initial implementation was held without publication pending possible Codex Native work;
the user subsequently authorized push and publication of this completed unit. No additional browser
restriction changes are included. Guidance is in `Connect/docs/agent-ds-contract-handoff.md` and the canonical accounting
section of `Connect/docs/enhancements.md`; `HostOwnedAgentContext.InspectCompactionAsync` is compiled.

Controlled tests cover projection parity, frozen model/instructions/tools, unchanged history/revision,
queued steering and reusable pause identity, running rejection, cancellation, expired OAuth without
refresh, null limits/counts, encrypted state/attachments, limit precedence, unsupported native routes,
Codex cap rejection before HTTP and portable capability mappings. Zero-warning Debug/Release builds
and tests pass: Connect 340, Tensor 298, UI 53 in each configuration (Common's 24 tests repeat in each).
Formatting verification and all three transitive vulnerability audits pass. No model downloads,
paid inference, package artifacts or consuming-application migrations are required by this unit.

## Automated private release 11.6.0-rc.1

Published all four packages from `1f2ce1aa4feafdc5c301a1f3a2a5014f1afbcdc0` using `deploy-major.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.6.0-rc.1`. No model weights or paid inference were used as verification gates.

## Host-supplied history on a new target implementation

The design is the "Host-supplied history on a new target" section of `Connect/docs/enhancements.md`.
Live runs on 2026-09-24 motivated it: Codex response state failed every switch to another Codex model,
Claude CLI chat and Antigravity CLI chat, and both CLI chat routes rejected tool history.

- `LlmChatRequest.UnboundResponseState` and `LlmAgentSessionOptions.UnboundResponseState` default to
  `Reject`. With `Omit`, `LlmProviderService` projects a copy of the request that leaves out state not
  bound to the target (`ProviderResponseStateCodec.IsBound`), for sending and for `MeasureRequestAsync`,
  and reports `LlmChatResult.OmittedResponseStateCount`. Managed session import follows the same rule
  and warns once on the first turn.
- One `TranscriptText` projection writes history for Claude CLI chat, Antigravity CLI chat and agent
  opening context; tool calls and results become labelled blocks. `LlmProviderCapabilities.ToolHistory`
  is `Text` for both CLI chat routes.
- Claude CLI and AgentCore sessions accept a start `Context` with `Omit` as opening context in the first
  turn, and report `LlmAgentCapabilities.ContextImport = OpeningContext`. Managed Codex reports
  `Structured`.
- Claude CLI chat and Antigravity CLI chat estimate the projected transcript with the shared
  `LocalTokenCount` (`o200k_base`), which the Responses estimator also uses.

The public additions are additive; the default keeps the previous rejection.

Live acceptance on 2026-09-24 ran Agent DS's stored connections against this source. A Direct Codex
`gpt-6-luna` turn made a real tool call with reasoning state; its history, the tool result and a
follow-up then went to each target with `Omit`. Direct Codex `gpt-6-sol`, Claude CLI chat and
Antigravity CLI chat each answered correctly with `OmittedResponseStateCount = 1`, and `Reject` on
`gpt-6-sol` still failed before sending. The same history opened a Claude CLI agent session, which
reported `ContextImport = OpeningContext`, emitted the opening-context warning and answered correctly.
AgentCore was not run, for lack of credentials. The Direct Codex local estimate for the tool-bearing
follow-up was 203 tokens against 119 reported input tokens.

## Automated private release 11.6.1-rc.1

Published all four packages from `7ce5fd84788cc8692fece5215249e4f115e7699a` using `deploy-minor.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.6.1-rc.1`. No model weights or paid inference were used as verification gates.

## Rich tool results implementation

The design is the "Rich tool results" section of `Connect/docs/enhancements.md`. It is a breaking
contract change for `11.7.0-rc.1`.

- `LlmToolOutput` holds ordered text, binary and resource parts, optional structured JSON and
  `IsError`. `LlmToolResult` carries one, and `LlmAgentToolHandler` returns one. Each part serializes
  with a `type` discriminator, so captured contexts round-trip.
- `LlmRichContentSupported` declares each route's carriage per content kind. The records live in
  `RichContentRoutes`. `LlmProviderCapabilities.RichContentSupported` covers the chat lane and
  `LlmAgentCapabilities.RichContentSupported` the agent lane. Capability overrides only lower a carriage.
- `ToolOutputProjector` is the one projection. Responses, Anthropic, Bedrock Converse, the compatible
  chat client, `TranscriptText`, the Claude MCP bridge and AgentCore serialize its `SentToolPart`s,
  and `LlmProviderService.ProjectToolOutputAsync` returns its report.
- Managed Codex, AgentCore and the Claude agent client no longer fail a tool for unsupported content.
- Agent opening context carries tool-output images and documents as first-turn attachments.
- A capture without bytes records tool-output binaries as `LlmOmittedAttachment` with an
  `LlmBinaryKind` and the part index. `LlmToolOutputResource.BodyKind` holds the one media-type
  classification for resource byte bodies.

Verification: Debug build with zero warnings, `dotnet format --verify-no-changes`, and 377 Connect and
Common tests.

Live acceptance on 2026-09-24 ran Agent DS's stored connections against this source. One tool output
carried a text part, a red PNG, a PDF, a plain-text file, a WAV, a resource link and structured JSON,
each with its own verification value. Direct Codex `gpt-6-luna` reported all six text-bearing values;
the projection reported the audio omitted and the request was accepted. An empty output with
`IsError` was also accepted. A Claude CLI agent session reported the text, the colour, the text file
(sent as an embedded text resource), the structured value (sent as the final text part) and the link
name, and reported the PDF as not received; the tool completed successfully. AgentCore and the
Anthropic API were not run, for lack of credentials.

## Automated private release 11.7.0-rc.1

Published all four packages from `7eb7671fa3bff32eec22bd3a0c14fcc88c8c81de` using `deploy-major.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.7.0-rc.1`. No model weights or paid inference were used as verification gates.

## Provider-reported counts for encrypted history

Compaction accounting refused Codex sessions: the Responses estimator returned unknown for any history
carrying encrypted reasoning state, which Codex attaches to most turns. The owner chose to use the
provider's reported input tokens.

- `LlmProviderResponseState` gains `InputTokens` and `OutputTokens`, the provider's reported counts for
  the response that produced the state. `ResponsesLlmClientBase` sets them from the response's usage.
- `ILlmContextEstimator` returns a `ContextEstimate` (tokens, and whether they include a provider
  report). The Responses estimator counts the history up to the last state with both counts as their
  sum, and tokenizes only the input items after it. `ManagedAgentPolicy` labels such an estimate
  `Mixed`.
- Content after that state that the tokenizer cannot count, and opaque state without counts, still
  give unknown.

Verification: Debug build with zero warnings, `dotnet format --verify-no-changes`, and 379 Connect and
Common tests. Live, through Agent DS against this source: three real `gpt-6-luna` turns with encrypted
reasoning were compacted (previously refused), and `gpt-6-sol`, a Claude CLI session and `gpt-6-luna`
again then answered from the compacted history.

## Automated private release 11.7.1-rc.1

Published all four packages from `2be8162c24cfd167be082923a0c486c88fa6614a` using `deploy-minor.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.7.1-rc.1`. No model weights or paid inference were used as verification gates.

## Native background resources implementation

The design is the "Native background resources and provider-started turns" section of
`Connect/docs/enhancements.md`. It is a breaking contract change for `11.8.0-rc.1`.

- `LlmAgentResources.cs` holds the resource contract. `ILlmAgentSession` gains `ObserveAsync`,
  `GetResourcesAsync`, `ReadResourceOutputAsync` and `StopResourceAsync`; the session and resume options
  gain `AllowRuntimeBackgroundWork` and `ResourceOutputRetentionBytes`.
- `ClaudeAgentSession` reads the CLI for the session's lifetime. It routes output by the
  `--replay-user-messages` echo: host turns to their `RunTurnAsync` stream, provider-started turns and
  resource events to the session feed, and subagent messages to the subagent's output.
- `ClaudeBackgroundTasks` maintains resources from the CLI's task messages, tails command output files,
  parses the exit-code trailer, including one appended after the task's end messages, and retains subagent
  output up to the configured limit.
- `NoAgentResources` serves managed Codex and AgentCore, which report none; `AgentResourceOptions`
  rejects background work where observation is not Supported.

Verification: Debug build with zero warnings, `dotnet format --verify-no-changes`, and 392 Connect and
Common tests. Agent DS's live suite drives the real Claude CLI through this source: a background command
listed, read and stopped; a subagent's output and the turn its completion starts; a monitor line's turn; a
host tool call during a provider-started turn with a host turn queued behind it; and interrupting one.

## Automated private release 11.8.0-rc.1

Published all four packages from `7393dbef23052807fd82c59954121f74d9f55d66` using `deploy-major.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.8.0-rc.1`. No model weights or paid inference were used as verification gates.

## Host tool cancellation on the Claude CLI

The design is the "Host tool cancellation on the Claude CLI" section of `Connect/docs/enhancements.md`,
a compatible change for `11.8.1-rc.1`.

- `ClaudeAgentSession` checks each MCP `tools/call` on the reader and runs it through `StartToolCall` on
  its own task, keyed by JSON-RPC ID in `_toolCalls`. `notifications/cancelled`, the CLI's exit (`Fail`)
  and disposal cancel calls in flight; disposal awaits them before releasing the writer.
- A cancelled call is answered with an error tool result. `EmitFor` delivers a call's tool event only to
  the turn that made it, before the answer is written.

Verification: Debug build with zero warnings, `dotnet format --verify-no-changes`, and 396 Connect and
Common tests.


## Automated private release 11.8.1-rc.1

Published all four packages from `df9c7e31aea88cb7739a26ba675b922bc0c7b9e0` using `deploy-minor.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.8.1-rc.1`. No model weights or paid inference were used as verification gates.

## Bedrock Converse strict tools

`BedrockLlmClient` sets `ToolSpecification.Strict` only for a strict tool definition and leaves it unset
otherwise. Amazon Nova rejected every tool request because the SDK wrote `"strict": false`. A compatible
change for `11.8.2-rc.1`. Agent DS confirmed it live: Nova Lite over Converse ran Agent DS's `read` and
`bash` tools on its own agent loop.

Verification: Debug build with zero warnings, `dotnet format --verify-no-changes`, and 396 Connect and
Common tests.

## Automated private release 11.8.2-rc.1

Published all four packages from `b7fa8dff65ac88fc1ba9d119b7ece0c6d030c08b` using `deploy-minor.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.8.2-rc.1`. No model weights or paid inference were used as verification gates.

## Strict tool flags on Anthropic Messages and compatible Chat Completions

`AnthropicMessagesLlmClient` and `OpenAiCompatibleChatLlmClient` write `strict` only for a strict tool, as
Bedrock Converse does. Released with the breaking `11.9.0-rc.1`. Neither route has a configured connection
here, so the change is not live-verified.

Verification: Debug build with zero warnings, `dotnet format --verify-no-changes`, and 396 Connect and
Common tests.

## Model lists kept for the application

The design is the "Model lists kept for the application" section of `Connect/docs/enhancements.md`, a
breaking change for `11.9.0-rc.1` through the new `ILlmInference.RefreshModelsAsync`.

- `LlmModelListCache` holds one list per connection keyed by `LlmConnection.Version`, shares a listing
  among concurrent callers, and keeps no failed listing. `AddLlmConnect` registers it as a singleton.
- `LlmProviderService.ListModelsAsync`, `RefreshModelsAsync` and `MeasureRequestAsync` read through it.
- `ConnectLlmInference` and `QwenLlmInference` implement `RefreshModelsAsync`.

Verification: zero-warning builds and clean formatting for the Connect, Tensor and UI solutions; Connect
403, Tensor 298 and UI 53 tests, Common's 24 included in each.

## One non-strict default for tool definitions

Common's and Connect's `LlmToolDefinition` default to `Strict = false`, matching `LlmAgentToolDefinition`;
structured output keeps `Strict = true`. Breaking, in `11.9.0-rc.1`. The Bedrock and tool-use tests set
`Strict = true` where they test a strict tool.

Verification: zero-warning builds and clean formatting for the Connect, Tensor and UI solutions; Connect
403, Tensor 298 and UI 53 tests.

## Bedrock Converse on the managed agent engine

The design is the "Bedrock Converse on the managed agent engine" section of `Connect/docs/enhancements.md`,
released with `11.9.0-rc.1`.

- `ManagedBedrockAgentClient` opens `ManagedAgentSession` on `AwsBedrockConverse`; `AddLlmConnectBedrock`
  registers it.
- Each Converse reply that reports usage carries counts-only response state
  (`ProviderResponseStateCodec.EncodeCounts`); Converse requests accept only bound state.
- `BedrockLlmClient` estimates requests from the last reported counts plus the tokenized tail.
- The core assembly grants internals to `Darkspyre.Inference.Connect.LLM.Bedrock.Tests` for the estimator
  tests.

No live Bedrock session has run on the managed lane yet.

Verification: zero-warning builds and clean formatting for the Connect, Tensor and UI solutions; Connect
410, Tensor 298 and UI 53 tests.


## Public surface cleanup

The design is the "Public surface cleanup for 11.9.0-rc.1" section of `Connect/docs/enhancements.md`,
released with `11.9.0-rc.1`.

- Removed agent members no runtime implements; `ToolHandler` sits on the session options.
- `ILlmAgentClient` has one start member; resume and fork are `ILlmAgentResumeClient` and
  `ILlmAgentForkClient`.
- `LlmAgentNativeRuntimeOptions` carries native runtime settings; managed sessions reject it.
- Turn status derives from its reason; `IsEstimated` is gone.
- `StructuredOutput` is internal; Bedrock Converse rejects reasoning levels.
- Agent tool descriptions are nullable and the ownership enums are one `LlmAgentOwnership`.
- Connect uses Common's LLM primitives; tool output types and `LlmBinaryKind` live in Common.
- Sign-in drops unused members; the credential setters take `expectedVersion`.

Verification: zero-warning builds and clean formatting for the Connect, Tensor and UI solutions; Connect
415, Tensor 298 and UI 53 tests.

## Automated private release 11.9.0-rc.1

Published all four packages from `f383151293ecb2a1e4178818947fefcbfb2d9432` using `deploy-major.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.9.0-rc.1`. No model weights or paid inference were used as verification gates.

## Bedrock model list and choice values

`BedrockModelResolver.ListAsync` omits a foundation model whose only inference type is `PROVISIONED`.
Bedrock gives such context-window variants the base model's name, so Nova Lite appeared three times in
the setup model list. The setup choice combobox shows a choice's value under its display name when the
two differ, and filters on both. A compatible change for `11.9.1-rc.1`.

Verification: 63 Bedrock and 30 UI.Settings tests, and `dotnet format --verify-no-changes` for the
Connect and UI solutions.

## Automated private release 11.9.1-rc.1

Published all four packages from `8435a743e79c90ffee4fa358aca16a546c55aa5c` using `deploy-minor.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.9.1-rc.1`. No model weights or paid inference were used as verification gates.

## Automated private release 11.9.2-rc.1

Published all four packages from `c7f6e72a6a6686d2d14bfb9b0998deaa4e93ce69` using `deploy-minor.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.9.2-rc.1`. No model weights or paid inference were used as verification gates.

## Automated private release 11.10.0-rc.1

Published all four packages from `898d6ceaf8e4a732bc7cfde38d15aade2a908f67` using `deploy-major.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.10.0-rc.1`. No model weights or paid inference were used as verification gates.

## Automated private release 11.10.1-rc.1

Published all four packages from `ce4bb376fe6e921382b6413d000db4a3f7b00949` using `deploy-minor.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.10.1-rc.1`. No model weights or paid inference were used as verification gates.

## Automated private release 11.10.2-rc.1

Published all four packages from `ce1a096d84e48fdeb241e2dae0b4ea51bd83becd` using `deploy-minor.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.10.2-rc.1`. No model weights or paid inference were used as verification gates.

## Automated private release 11.10.3-rc.1

Published all four packages from `20a54ee4f9cda9e6f7cd576ef71eb7da5216c87f` using `deploy-minor.sh`.
Authenticated restores, Debug/Release builds and tests, formatting, vulnerability audits,
archive boundaries and local/fresh-feed consumer checks passed. Downloaded archives match
the verified local archives; feed readback confirms private visibility. Source is tagged
`v11.10.3-rc.1`. No model weights or paid inference were used as verification gates.
