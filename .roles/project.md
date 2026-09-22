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
| Purpose | Consolidate Darkspyre Studios' inference-related implementations into one cohesive family of libraries and NuGet packages |
| Repository | `/Volumes/Data/Development/DarkspyreStudios/Inference` |
| Target framework | `net10.0` |
| Distribution | Private DarkspyreStudios NuGet feed |
| Version line | `10.x.x`; the package major version follows the supported .NET major version |
| Release model | All `Darkspyre.Inference.*` packages share one version; the initial 10.0 rollout may publish completed package IDs in stages before the immutable family tag |
| Primary branch | `master` |
| Lifecycle stage | Breaking consolidation and refactor toward the first unified `10.x.x` release baseline |
| Jira project and parent | TBD |
| Jira subject label | TBD |

The technical assembly and namespace prefix is `Darkspyre.Inference`.

## Package families

`Connect` and `Tensor` are namespace and package-family headings. There is no
`Darkspyre.Inference.Connect` or `Darkspyre.Inference.Tensor` package.

| Package or family | Responsibility |
|---|---|
| `Darkspyre.Inference.Common` | Interfaces and models genuinely shared by connector-based and in-process tensor inference |
| `Darkspyre.Inference.Connect.*` | Family of connectors to out-of-process inference providers |
| `Darkspyre.Inference.Connect.Common` | Interfaces and models shared by connector packages, but not by tensor packages |
| `Darkspyre.Inference.Connect.LLM` | Connectors for hosted LLM providers, including Codex, Claude, the Antigravity (`AGV`) CLI, Bedrock, and the OpenAI API |
| `Darkspyre.Inference.Connect.Image` | Connectors for out-of-process image providers, initially including ComfyUI |
| `Darkspyre.Inference.Connect.STT` | Connectors for hosted speech-to-text providers |
| `Darkspyre.Inference.Connect.TTS` | Connectors for hosted text-to-speech providers |
| `Darkspyre.Inference.Tensor.*` | Family of inference engines loaded and executed inside the consuming process |
| `Darkspyre.Inference.Tensor.LLM` | In-process text inference |
| `Darkspyre.Inference.Tensor.Image` | In-process image inference |
| `Darkspyre.Inference.Tensor.Decision` | In-process decision inference using Laya, an open-source BERT-based decision model |

`Darkspyre.Inference.Connect.LLM` and `Darkspyre.Inference.Connect.Image` are the implemented Connect
capabilities in the first unified release. Image initially contains the HTTP/WebSocket ComfyUI
transport. The implementation state of later capabilities is not defined by this document.

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
├── Darkspyre.Inference.Connect.LLM
├── Darkspyre.Inference.Connect.Image
├── Darkspyre.Inference.Tensor.LLM
└── Darkspyre.Inference.Tensor.Image

Darkspyre.Inference.Tensor.Common
├── Darkspyre.Inference.Tensor.LLM
├── Darkspyre.Inference.Tensor.Image
└── Darkspyre.Inference.Tensor.Decision
```

Place a contract or model in `Darkspyre.Inference.Common` only when connector and tensor packages
truly share it. Connector-only contracts and models belong in `Darkspyre.Inference.Connect.Common`.
Do not create a Common package until at least two implemented capabilities share a contract with the
same semantics. Similar property names, such as endpoint or display-name fields, are not by themselves
a shared contract. LLM messages, tools, typed requests/results, streaming events, and image-generation
requests/results now qualify because both Connect and Tensor implement those semantics.
`Darkspyre.Inference.Tensor.Common` is limited to behavior actually shared by two Tensor capabilities,
such as model packs, persistence-backed weights, and execution coordination. Laya-specific decision
contracts remain in `Tensor.Decision` unless a second family implements the same decision semantics.
There is still no `Connect.Common`: no implemented connector-only contract is shared broadly enough.

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
a package or assembly.

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

For the first 10.0 baseline only, the user explicitly authorized publishing the completed Common and
Connect package IDs while Tensor remains under development. Those package versions are immutable;
Tensor packages join the same 10.0.0 version as distinct package IDs after their own gates pass. The
family `v10.0.0` tag is created only when the intended baseline is complete.

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
