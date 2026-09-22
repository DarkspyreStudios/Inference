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
| Purpose | Consolidate Darkspyre Studios' inference-related implementations into one cohesive family of libraries distributed as three NuGet packages |
| Repository | `/Volumes/Data/Development/DarkspyreStudios/Inference` |
| Target framework | `net11.0` |
| Distribution | Private DarkspyreStudios NuGet feed |
| Version line | `<net>.<release>.<minor>`: .NET generation, breaking-contract release, compatible tweak/fix |
| Release model | `Common`, `Connect`, and `Tensor` always publish together at the one version declared in `/Directory.Build.props` |
| Primary branch | `master` |
| Lifecycle stage | Unified `11.2.0` release published; compatible maintenance remains on `11.2.x` |
| Jira project and parent | TBD |
| Jira subject label | TBD |

The technical assembly and namespace prefix is `Darkspyre.Inference`.

## Package families

The release surface contains exactly three NuGet packages. Capability names remain assembly and
namespace boundaries inside their owning product package; they are not independently versioned or
published NuGets.

| Package or family | Responsibility |
|---|---|
| `Darkspyre.Inference.Common` | Interfaces and models genuinely shared by connector-based and in-process tensor inference |
| `Darkspyre.Inference.Connect` | All implemented out-of-process connectors; contains capability-specific assemblies such as `.Connect.LLM`, `.Connect.LLM.Bedrock`, and `.Connect.Image` |
| `Darkspyre.Inference.Tensor` | All implemented in-process engines; contains `.Tensor.Common`, the `.Tensor.LLM`, `.Tensor.Image`, and `.Tensor.Decision` capability contracts, and the `.Tensor.LLM.Qwen`, `.Tensor.Image.Supra`, and `.Tensor.Decision.Laya` implementations |

LLM and Image are the implemented Connect capabilities in the first unified release. Image initially
contains the HTTP/WebSocket ComfyUI transport. LLM, Image, and Decision are the implemented Tensor
capabilities. The implementation state of later capabilities is not defined by this document.

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
└── Darkspyre.Inference.Tensor

Darkspyre.Inference.Tensor package
└── Darkspyre.Inference.Tensor.Common assembly
    ├── Darkspyre.Inference.Tensor.LLM assembly → .Tensor.LLM.Qwen implementation
    ├── Darkspyre.Inference.Tensor.Image assembly → .Tensor.Image.Supra implementation
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
capabilities, such as the host-controlled model lifecycle, model packs, persistence-backed weights,
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

Additional subset solutions are not yet defined by this document.

There is no root `Darkspyre.Inference` package. The root solution is an aggregate build surface, not
a package or assembly. The product packaging projects are
`Connect/src/Darkspyre.Inference.Connect` and `Tensor/src/Darkspyre.Inference.Tensor`.

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

## Current published 11.2.0 baseline

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
| Managed-agent verification | `Connect/docs/managed-agent-verification.md` |
| Tensor package boundary, persistence, and implemented modalities | `Tensor/README.md` |
| Shared CE / Agent-DS Settings UI requirements and provider contract gaps (not an approved API design) | `docs/provider-settings-contract-support-and-gaps.md` |
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
