# Darkspyre.Inference project configuration

## Identity

| Item | Value |
|---|---|
| Product name | Darkspyre.Inference |
| Repository | `/Volumes/Data/Development/DarkspyreStudios/Inference` |
| Primary branch | `master` |
| Lifecycle stage | Connect 4.0 repository restructure; no 4.0 release yet |

The technical assembly and namespace prefix is `Darkspyre.Inference`.

## Product workspaces

| Workspace | Responsibility | Current scope |
|---|---|---|
| `Connect/` | Connections to external inference providers | Active: LLM restructure, then Image |
| `Console/` | Darkspyre Inference console product | Outside the current implementation |
| `Tensor/` | Self-hosted inference products | Outside the current implementation |

There is no root `Darkspyre.Inference` package. Each workspace owns its solutions and build
configuration. Connect groups projects by capability under `src/<Capability>/` and
`tests/<Capability>/`.

## Required Connect commands

Run these commands from `Connect/` so its `global.json`, NuGet configuration, and centrally managed
package versions are authoritative.

| Gate | Command |
|---|---|
| Restore | `dotnet restore Darkspyre.Inference.Connect.slnx` |
| Build | `dotnet build Darkspyre.Inference.Connect.slnx --no-restore` |
| Test | `dotnet test Darkspyre.Inference.Connect.slnx --no-build --no-restore` |
| Format | `dotnet format Darkspyre.Inference.Connect.slnx --verify-no-changes --no-restore` |
| Pack | `dotnet pack Darkspyre.Inference.Connect.slnx --no-build --no-restore` |
| Dependency audit | `dotnet list Darkspyre.Inference.Connect.slnx package --vulnerable --include-transitive` |

## Canonical documents

| Area | Authority |
|---|---|
| Engineering constraints | `.roles/laws.md` |
| Repository and package conventions | `.roles/design-rules.md` |
| Connect 4.0 architecture and sequence | `Connect/docs/darkspyre-inference-connect-4-design.md` |
| Current LLM integration contract | `Connect/README.md` |
| Managed-agent technical design | `Connect/docs/enhancements.md` |
| Managed-agent verification | `Connect/docs/managed-agent-verification.md` |

## Compatibility posture

The `Mathis.LlmConnect` 3.0 packages are the released predecessor contract. Version 4.0 is an
intentional breaking package, assembly, and namespace migration to
`Darkspyre.Inference.Connect.LLM`. Do not add compatibility packages, type forwarders, duplicate
namespaces, or dual APIs. Internal 4.0 work remains disposable until the first 4.0 release, but
provider behavior and stored credential identities are preserved unless the approved design says
otherwise.
