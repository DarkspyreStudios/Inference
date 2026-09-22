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
| Target framework | `net10.0` |
| Distribution | Private DarkspyreStudios NuGet feed |
| Version line | `<net>.<release>.<minor>`: .NET generation, breaking-contract release, compatible tweak/fix |
| Release model | `Common`, `Connect`, and `Tensor` always publish together at the one version declared in `/Directory.Build.props` |
| Primary branch | `master` |
| Lifecycle stage | Breaking consolidation and refactor toward the first unified `10.x.x` release baseline |
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
| `Darkspyre.Inference.Tensor` | All implemented in-process engines; contains `.Tensor.Common`, `.Tensor.LLM`, `.Tensor.Image`, and `.Tensor.Decision` assemblies |

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
    ├── Darkspyre.Inference.Tensor.LLM assembly
    ├── Darkspyre.Inference.Tensor.Image assembly
    └── Darkspyre.Inference.Tensor.Decision assembly
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
capabilities, such as model packs, persistence-backed weights, and execution coordination. Laya
architecture, tokenization, calibration, and model-pack details remain in the Tensor Decision
assembly. There is still no `Connect.Common` assembly: no implemented connector-only contract is
shared broadly enough.

The repository uses .NET Central Package Management (CPM). Package versions are declared centrally,
and any implementation package may reference the centrally managed version of a NuGet dependency
when its design requires it. Supporting DarkspyreStudios packages available through the private feed
include `Darkspyre.SecureStore` and `Darkspyre.Persistence`.

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
| Detailed implementation work | Jira tickets; project and parent TBD |

If this document conflicts with an approved design bible or Jira technical specification, stop and
resolve the conflict rather than inferring which shape is current.

## Compatibility posture

The unified `10.x.x` line is a deliberately breaking baseline. Source and binary compatibility with
previously published inference packages, including the `Mathis.LlmConnect` 3.0 predecessor, is not
required. Do not add compatibility packages, type forwarders, duplicate namespaces, parallel legacy
APIs, or migration paths unless an approved design bible or Jira ticket explicitly requires one.

Previously published packages remain historical releases on the private feed; they are not the
design authority for the unified package family. Internal `10.x.x` work remains disposable until the
first unified release. Provider behavior and stored credential identities are preserved unless an
approved design explicitly changes them.
