# Darkspyre.Inference project configuration

## Identity

| Item | Value |
|---|---|
| Product name | Darkspyre.Inference |
| Repository | `/Volumes/Data/Development/DarkspyreStudios/Inference` |
| Primary branch | `master` |
| Lifecycle stage | First approved vertical slice in implementation; no production release or compatibility baseline |
| Jira parent | DCE-90 |
| Jira subject label | `project-darkspyre-inference` |

The technical assembly and namespace prefix is `Darkspyre.Inference`.

## Required commands

| Gate | Command |
|---|---|
| Restore | `dotnet restore DarkspyreCE.sln` |
| Build | `dotnet build DarkspyreCE.sln --no-restore` |
| Test | `dotnet test DarkspyreCE.sln --no-build --no-restore` |
| Format | `dotnet format DarkspyreCE.sln --verify-no-changes --no-restore` |
| Dependency audit | `dotnet list DarkspyreCE.sln package --vulnerable --include-transitive` |

## Canonical documents

| Area | Authority |
|---|---|
| Engineering constraints | `.roles/laws.md` |
| Interface and repository conventions | `.roles/design-rules.md` |
| Current architecture and design gates | `docs/architecture.md` |
| Donor-code classification | `docs/source-extraction-manifest.md` |
| Local runtime foundation and first vertical slice | `docs/local-runtime-foundation-technical-design.md` |
| Interface visual language and shared primitives | `docs/ui-design-language.md` |

## Compatibility posture

There are no public compatibility contracts or deployed data. Until an initial release baseline is
declared, local databases, packages, and internal interfaces are disposable development shapes. Do
not introduce migration shims, dual readers, deprecated parallel APIs, or importers solely to
preserve unreleased scaffold state.
