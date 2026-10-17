# Darkspyre.Inference

This repository builds the `Darkspyre.Inference.*` library family: four NuGet packages released together on the private DarkspyreStudios feed. The Darkspyre Harness rules, including the current restrictions, are in agent-ds `AGENTS.md`. This file holds only what is specific to Inference.

| Item | Value |
| --- | --- |
| Target framework | `net11.0` |
| Assembly and namespace prefix | `Darkspyre.Inference` |
| Version | One version for all four packages, declared in `/Directory.Build.props` |
| Branches | `master` is primary; finished work merges into `dev` first |

## Design record (L8)

These pages describe how the code works now, and a change to current behaviour updates the page that owns it:

| Area | Page |
| --- | --- |
| Connect product family and the LLM integration contract | `Connect/README.md` |
| Tensor package boundary, persistence and modalities | `Tensor/README.md` |
| Settings editor | `UI/README.md` |
| Release scripts and gates | `eng/README.md` |

The `*.tech-design.md` files in `docs/` and `Connect/docs/` are design history. When one conflicts with a README above, the README is current.

## Public contracts (L1)

None. Every consumer is a Darkspyre repository. A breaking change fixes its consumers in the same unit and advances the release component of the version.

## Project laws

### L14 — Untrusted input is classified and handled once at the boundary

Every untrusted value receives one declared boundary policy. Display text uses the owned sanitizer at its entry boundary. Paths, URLs, files, imports and structured values use their authoritative parsers and validators there. Downstream handlers do not repeat ad hoc stripping.

Opaque narrative text, source material, provider payloads and credentials may skip text sanitization only through an annotated policy. The policy says why mutation would corrupt the value and how it is kept from becoming executable markup, a path, a command or a log secret.

Never log or commit credentials, OAuth tokens, secret headers, or raw provider responses that contain secrets.

### P1 — Product-family boundaries are explicit

`Connect` owns connections to out-of-process providers, through their native protocol or a wrapper around an official CLI. The provider may be remote or local; ComfyUI is a connector. `Tensor` loads and runs the engine inside the consuming process. `UI` holds the host-placed settings editor. `Console` is a separate product surface.

A capability belongs to exactly one product family. Sharing the repository does not let one family absorb another's runtime, persistence, user interface or deployment concerns. Families integrate through explicit package contracts, never through shared source, linked files or an umbrella assembly.

### M-b — Request path and dependency direction

The provider path is one-way:

`Host -> provider-neutral capability -> selected adapter -> external provider`

Hosts supply connection information, protected credentials and policy through capability contracts. Adapters own provider protocols. A capability assembly does not reach into a host's persistence, filesystem, media library, authorization system or user interface. The public API stays provider-neutral; provider wire formats and credentials stay inside their adapters.

### M-c — Concrete implementations stay inside their owning adapters

Implementation-specific APIs are legal only inside the adapter that owns the implementation and the host composition root that selects it. Provider SDK usage stays in provider-specific assemblies, and the owning product package declares those SDK dependencies. Vault APIs stay in host tools or credential adapters. Persistence APIs stay out of shipping capability libraries unless the capability contract owns persistence.

Consumers depend on semantic contracts. They never receive provider-native payloads, concrete SDK clients, secret-store handles or persistence handles as a way around them. Do not create universal wrappers only to hide implementations; a contract is named for the capability its consumer needs.

## Operating-system boundaries

All OS-specific code in every Darkspyre project must live behind an OS-agnostic capability contract.

Portable consumers call that contract. Platform implementations own OS detection, native bindings and ABI layouts, native-library names and loading, system commands, platform paths, filesystem semantics, and platform-specific process and UI behaviour. Only platform composition selects the implementation. SDK contracts, plugins, extensions, core modules, and portable host code must not require consumers to know the operating system or call a platform implementation directly.

An interface that exposes OS-specific commands, types, paths, or layouts does not satisfy this rule. Reuse the existing capability seam. A platform without an implementation reports the capability unavailable through the same contract.

This boundary lets another operating system supply an implementation without changing portable consumers.

**Check.** Audit source and package configuration for OS branches, native imports, platform paths and commands, filesystem assumptions, and platform-only dependencies. Trace each hit through its callers and registration. Every OS-specific operation must remain inside a platform implementation selected through an OS-agnostic contract. A filename, namespace, annotation, or `IsMacOS()` guard alone does not establish that boundary.

### Exception: Connect's in-library OS switching

Connect keeps its platform handling for macOS, Linux and Windows inside its own libraries, and picks the right OS commands at runtime. This includes the CLI process launch and termination (`Connect/src/LLM/Darkspyre.Inference.Connect.LLM/Providers/CliProcessLauncher.cs`, `CliProcessTermination.cs`), `AppleProcessRunner.cs`, the CLI clients and `LlmAgentRuntimeProfile.cs`. It is not split behind a separate OS capability contract. The reason: this code is battle-tested and works on all three platforms today. Brandon approved this exception on 2026-10-12.

## Packages

| Package | Holds |
| --- | --- |
| `Darkspyre.Inference.Common` | Contracts and models that Connect and Tensor genuinely share |
| `Darkspyre.Inference.Connect` | Every out-of-process connector, as capability assemblies such as `.Connect.LLM`, `.Connect.LLM.Bedrock` and `.Connect.Image` |
| `Darkspyre.Inference.Tensor` | Every in-process engine: `.Tensor.Common`, the `.Tensor.LLM`, `.Tensor.Image`, `.Tensor.STT` and `.Tensor.Decision` contracts, their `.Qwen`, `.Supra`, `.Whisper` and `.Laya` implementations, and `.Tensor.Registry` |
| `Darkspyre.Inference.UI.Settings` | Host-placed Avalonia editor for provider settings and their managers |

- Capability names are assembly and namespace boundaries inside their product package. They are never packed or versioned on their own. There is no root `Darkspyre.Inference` package or facade assembly.
- Connect, Tensor and UI.Settings reference the Common package at the exact release version and never embed the Common assembly. No project overrides the version from `/Directory.Build.props`.
- Put a contract in Common only when Connect and Tensor share its semantics. Similar property names are not a shared contract. Connector-only contracts stay in their Connect capability assembly.
- `Tensor.Common` holds only behaviour two Tensor capabilities share. `Tensor.Registry` owns `TensorEngineRegistry` and `AddTensorEngines`; `Tensor.Common` and the implementations never reference it.
- UI.Settings references Common and Avalonia, never Connect, Tensor or an application assembly. Common, Connect and Tensor stay UI-framework-neutral. A host owns its connection list, editor placement, persistence, protected credentials, consent and manager composition. Field edits stay in the settings object; only Save reaches the host.
- An implementation that needs a provider SDK lives in its own provider assembly.
- Shipping Connect libraries stay independent of application persistence and credential storage. Host tools use `Darkspyre.SecureStore` for secrets and `Darkspyre.Persistence` for durable non-secret state, and never copy either into this repository.
- Provider protocol versions are independent of the package version. The direct Codex adapter uses one explicit Codex compatibility version for its `User-Agent`, `version` header and model-catalog `client_version`.
- Tokenizers come from the `Darkspyre.ML.Tokenizers` fork. Connect keeps Microsoft's `Microsoft.ML.Tokenizers.Data.O200kBase` data package. Do not fork an adjacent Microsoft package unless Darkspyre changes its implementation.

## Repository layout

`Connect/`, `Console/`, `Tensor/` and `UI/` are sibling product roots. Each owns its solution, build configuration, documentation, tests, examples and tools. An empty product root does not justify a placeholder project or package.

```text
<Product>/
  src/<Capability>/<AssemblyName>/
  src/<ProductPackageId>/                 # Connect and Tensor packaging projects only
  tests/<Capability>/<TestProject>/
  examples/<ExampleProject>/
  tools/<ToolProject>/
```

Solution folders mirror the capability grouping. Moving a project updates and verifies every relative reference in the same change.

Package versions live only in `/Directory.Packages.props` (Central Package Management). Projects omit versions from `PackageReference`. Build and package metadata live in `/Directory.Build.props`.

| Solution | Scope |
| --- | --- |
| `Darkspyre.Inference.slnx` | Every library in the family |
| `Connect/Darkspyre.Inference.Connect.slnx` | Common plus the Connect libraries and tests |
| `Tensor/Darkspyre.Inference.Tensor.slnx` | The Tensor libraries and tests |
| `UI/Darkspyre.Inference.UI.slnx` | Common, UI.Settings and its tests |

## Commands

Run Connect commands from `Connect/` so its `global.json` selects the SDK.

| Gate | Command |
| --- | --- |
| Restore | `dotnet restore Darkspyre.Inference.Connect.slnx` |
| Build | `dotnet build Darkspyre.Inference.Connect.slnx --no-restore` |
| Focused test | `dotnet test <owning-test-project.csproj> --no-restore --filter <changed-behaviour>` |
| Format | `dotnet format Darkspyre.Inference.Connect.slnx --verify-no-changes --no-restore` |
| Dependency audit | `dotnet list Darkspyre.Inference.Connect.slnx package --vulnerable --include-transitive` |
| Pack | `dotnet pack Darkspyre.Inference.Connect.slnx --no-build --no-restore`, only when the user says to publish |

## Testing

Testing rules for this repository:

- Paid or metered model calls are never development or commit gates.
- A conditional live experiment reports that it was skipped when its service or credential is absent. It never returns silently and appears to pass.
- A build, format check or package-input audit proves only its own property. It does not establish provider, native-runtime or ownership behaviour. Record an unexecuted qualification as unverified.

## Releases

The version is `<net>.<release>.<minor>`: the .NET generation, a breaking-contract release, and a compatible fix. All four packages release together. A published version is never overwritten, and each release gets an immutable Git tag.

Before a release, each active solution is at zero warnings and passes restore, build, tests, format verification, pack and the dependency audit. `deploy-major.sh` and `deploy-minor.sh` are the release entry points; `eng/README.md` documents their gates. Run the offline orchestration tests when changing the release scripts, and never test a script change by publishing a dummy version.

Every package carries a readable README and the repository license. `eng/VerifyPackages` and `eng/VerifyConsumer` check package boundaries, the exact Common dependency and the release source; `eng/README.md` says how to run them.

## Law exceptions

### L2 — Code kept unbuilt or unused during phase 1 of the conversation contract

Phase 1 of the conversation contract builds only the native Codex client and the Claude CLI. These stay in the repository on purpose and are not dead code under L2:

- **Excluded provider files.** The LLM provider files excluded with `Compile Remove` in `Connect/src/LLM/Darkspyre.Inference.Connect.LLM/Darkspyre.Inference.Connect.LLM.csproj`, and the xAI transport files excluded in the Image and TTS projects. Their source is unchanged.
- **Projects out of the build.** The Bedrock, Vertex and Azure projects, and the STT, Realtime, Video and Resources projects. They are out of the solution, the package and the source references, and their source is unchanged.
- **Tensor projects that do not compile.** Any Tensor project that does not compile against the phase-1 library is out of this phase's build.
- **Facades with no built implementer.** The tokenizer facade (`TokenizeAsync`, `ILlmTextTokenizer`, `LlmTokenization`) and the context-cache facade stay compiled. Each refuses with its documented message until a provider that implements it returns.
- **Background-work settings.** `LlmAgentHostRuntimePolicy.AllowRuntimeBackgroundWork` and `ResourceOutputRetentionBytes` stay public. The Host shows and stores them, and no phase-1 runtime reads them.

When a provider returns, its files rejoin the build and the matching line here is deleted.

## Design rule exceptions

### D6 — Per-model context lengths are repeatable rows

An OpenAI API connection's `Context lengths` setting in the provider editor uses the existing repeatable group (`SetupRepeatableGroupField`, rendered by `SetupFieldInput.RenderGroup` in `UI/src/Settings/Darkspyre.Inference.UI.Settings/SetupFieldInput.cs`). Each row holds a model and its context length, edited inline, with `Add row` and `Remove row`. D6 asks for a list with an **Add** button that opens a layered editor.

The reason: the repeatable group is the provider editor's one collection control (D3), and it shows no row until `Add row` is pressed, so no empty entry form appears. A layered row editor inside the provider editor's sheet would be a second collection control for the same job. Brandon approved this on 2026-10-11.
