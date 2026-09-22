# Design: Darkspyre.Inference.Connect 10.0

**Status:** The Inference repository structure, LLM assembly-family rename, provider-neutral Image
assembly, and shared typed Common inference lane are implemented. Capability-scoped 10.0 packages
are historical. The approved 10.1 release surface is exactly Common, Connect, and Tensor, published
in tandem from the one shared version. **Date:** 2026-09-22.
**Baseline:** LlmConnect 3.0.0.

This record preserves the agreed destination while prerequisite housekeeping refactors are made. It
defines product and package naming, ownership boundaries, dependency rules, the first 10.0 scope, and
the order of migration. It does not authorize placeholder modality projects or expand a housekeeping
change into the 10.0 migration.

## 1. Product model

`Darkspyre.Inference` is the product family. `Connect` means a connection to an external inference
provider. The capability follows `Connect` in assembly and namespace names:

```text
Darkspyre.Inference.Connect.LLM
Darkspyre.Inference.Connect.Image
Darkspyre.Inference.Connect.Audio
Darkspyre.Inference.Connect.STT
Darkspyre.Inference.Connect.TTS
```

`LLM` does not name the product and `Connect` does not name a model type. Together,
`Darkspyre.Inference.Connect.LLM` means the reusable external-provider connection capability for
large language models.

Self-hosted inference is a separate product line rather than another Connect provider. Its namespace
is parallel to Connect. For example, self-hosted image generation belongs under:

```text
Darkspyre.Inference.Tensor.Image
```

`Tensor` is the canonical spelling. Tensor is included here only to make the product boundary
explicit; no Tensor project is part of this work.

`Darkspyre.Inference` is the canonical repository. `Common`, `Connect`, `Console`, and `Tensor` are
sibling product workspaces inside it. Connect is distributed as the
`Darkspyre.Inference.Connect` NuGet containing capability-focused assemblies; it is not one
monolithic assembly. Common defines only semantic contracts implemented by both Connect and Tensor;
it is a separate package dependency and is never embedded into either product package.

## 2. Housekeeping prerequisite: shared storage packages

Darkspyre's storage implementations are independent products and are not owned or copied by the
Connect family. Before the 10.0 namespace and assembly refactor, remove any embedded SecureStore or
local persistence-store projects and source from this repository and replace any consumers with the
canonical NuGet packages:

```text
Darkspyre.SecureStore
Darkspyre.Persistence
```

Package versions are centrally pinned. A project that directly consumes a store references its
package; capability projects do not acquire store dependencies merely because a host may use them.
There are no wrapper copies, linked source files, or compatibility projects left in the solution.

The LLM core's `ILlmConnectionSource` remains the provider-neutral host seam. It is not a storage
implementation and must not be replaced by a direct SecureStore dependency. A host or tool may adapt
`Darkspyre.SecureStore` to that seam while preserving its existing `ServiceName`, credential names,
and stored values. Secrets and OAuth credentials are never stored through
`Darkspyre.Persistence`.

`Darkspyre.Persistence` is used only where a Connect-owned tool needs durable, non-secret local
state. The shipping capability libraries remain storage-agnostic; application conversation state,
jobs, provider configuration, and media persistence remain host-owned as defined in section 6.

At the 3.0 baseline, this repository has no tracked local persistence implementation to delete. Its
console tester is the only SecureStore consumer. Therefore this housekeeping unit consists of
finishing the SecureStore package-ID migration and ensuring that later source movement does not copy
either storage implementation into Connect. If a local persistence implementation appears on the
implementation branch, it is deleted only after all of its consumers have moved to
`Darkspyre.Persistence` and compatibility has been verified.

## 3. Version 10.0 scope

The first 10.0 Connect implementation contains two capabilities plus their cross-family contract:

1. Refactor the existing LlmConnect implementation into `Darkspyre.Inference.Connect.LLM`.
2. Extract image-generation contracts and implementations from ChatEngine into
   `Darkspyre.Inference.Connect.Image`, using ChatEngine's current image transport as the basis.
3. Publish `Darkspyre.Inference.Common` typed LLM and image inference contracts, with explicit Bind
   adapters from the richer Connect services and matching implementations in Tensor.

Audio generation, speech-to-text, and text-to-speech are part of the intended Connect family but are
not part of the first implementation unit. Their current GMTools implementations inform the boundary;
they are migrated only after a separate design pass confirms each public contract. Empty packages,
unwired forward projects, and duplicate seams are not created in advance.

The rename and contract movement were intentionally breaking. The historical capability packages
used version `10.0.0`. The first coordinated three-product release uses `10.1.0`. There are no
`Mathis.LlmConnect` compatibility packages, namespace forwarders, deprecated duplicate contracts, or
other transition shims. Every in-scope consumer is updated in the same migration unit in accordance
with L1 and L9.

## 4. Package and project layout

The 10.0 names are:

| Current | Version 10.0 |
| --- | --- |
| `Mathis.LlmConnect` | `Darkspyre.Inference.Connect.LLM` |
| `Mathis.LlmConnect.Bedrock` | `Darkspyre.Inference.Connect.LLM.Bedrock` |
| New capability | `Darkspyre.Inference.Connect.Image` |
| `Mathis.LlmConnect.slnx` | `Connect/Darkspyre.Inference.Connect.slnx` |

These remain assembly and namespace names in 10.1. They are no longer independently published
package IDs. The intended repository layout is:

```text
Inference/
  Common/
    src/Darkspyre.Inference.Common/
    tests/Darkspyre.Inference.Common.Tests/
  Connect/
    Darkspyre.Inference.Connect.slnx
    src/
      Darkspyre.Inference.Connect/
      LLM/
        Darkspyre.Inference.Connect.LLM/
        Darkspyre.Inference.Connect.LLM.Bedrock/
      Image/
        Darkspyre.Inference.Connect.Image/
    tests/
      LLM/
        Darkspyre.Inference.Connect.LLM.Tests/
        Darkspyre.Inference.Connect.LLM.Bedrock.Tests/
      Image/
        Darkspyre.Inference.Connect.Image.Tests/
    tools/
      Darkspyre.Inference.Connect.ConsoleTester/
    examples/
      Darkspyre.Inference.Connect.LLM.Examples/
  Console/
  Tensor/
```

Source and test projects are grouped physically and logically by capability. The test split enforces
dependency isolation: core LLM tests do not acquire AWS SDK dependencies, and image tests do not
acquire LLM or Bedrock dependencies. Non-shipping tools and examples may reference more than one
assembly when their purpose requires it.

The 10.1 publish surface is `Darkspyre.Inference.Common`, `Darkspyre.Inference.Connect`, and
`Darkspyre.Inference.Tensor`. Connect includes its LLM, Bedrock, and Image assemblies and declares a
NuGet dependency on Common at the synchronized version; it does not include the Common assembly.

Existing LLM-specific public vocabulary remains LLM-specific inside the new namespace. Types such as
`ILlmClient`, `LlmConnection`, `LlmProviderService`, and the `AddLlmConnect` registration method do
not need generic replacements merely because the product family is broader. Their 10.0 namespace is
`Darkspyre.Inference.Connect.LLM`. Image, STT, TTS, and audio contracts use their own capability
vocabularies; no universal `IProvider`, `ProviderRequest`, or least-common-denominator result is
introduced.

LLM messages, tools, typed request/result objects, stream events, image generation request/results,
provenance, and portable failures have the same semantics across Connect and Tensor and therefore live
in `Darkspyre.Inference.Common`. Rich connection management, authentication, provider diagnostics,
workflow preparation, and durable provider job state remain in their Connect capability assembly.
`Darkspyre.Inference.Connect.Common` is not created because no connector-only contract is shared by
two implemented Connect capabilities.

The portable adapters are obtained explicitly from `LlmProviderService.Bind(connectionId)` and
`ImageProviderService.Bind(connection, workflow)`. They never auto-select a backend and never fall
back between Connect and Tensor. Existing richer Connect APIs remain first-class for applications
that need provider-specific management or session behavior.

The repository identity is `DarkspyreStudios/Inference`. Changing a developer's checkout directory is
operational housekeeping, but package metadata and release links must target the Inference repository
before 10.0 is packed.

## 5. Provider implementation dependency rule

A capability assembly contains its provider-neutral contracts and implementations that use the .NET
base libraries, shared framework facilities, or the capability assembly's ordinary foundational
dependencies. HTTP, WebSocket, and process-based adapters do not need separate packages solely because
they speak to a named provider.

An implementation that requires an additional provider-specific third-party SDK is placed in its own
assembly. The Connect product package includes all implemented provider assemblies and therefore
declares the union of their dependencies. Assembly names append the provider to the full capability
name:

```text
Darkspyre.Inference.Connect.LLM.Bedrock
Darkspyre.Inference.Connect.Image.<Provider>
Darkspyre.Inference.Connect.STT.<Provider>
Darkspyre.Inference.Connect.TTS.<Provider>
```

Bedrock remains the concrete precedent: AWS SDK usage stays in the
`Darkspyre.Inference.Connect.LLM.Bedrock` assembly. The initial ComfyUI image implementation uses
HTTP and WebSockets and therefore remains in `Darkspyre.Inference.Connect.Image`.

## 6. Ownership boundary

Connect owns reusable provider-connection behavior. Applications own product policy and durable
application state.

| Connect package owns | Host application owns |
| --- | --- |
| Provider-neutral capability contracts | Persistent provider selection and settings |
| Connection and request validation | Credential storage and secret protection |
| Provider descriptors and capability declarations | Authorization and tenant/user policy |
| Protocol request construction and response parsing | Durable job scheduling, retry, and recovery |
| Provider health/test probes | Media asset storage and application metadata |
| Provider-specific endpoint behavior | Caches, UI, and user-facing orchestration |
| Deterministic protocol tests | Domain workflows and business rules |

Credentials and provider wire models remain adapter details. A host supplies connection information
through neutral contracts and stores secrets through its chosen protected mechanism. Connect never
owns a consumer application's database, authorization rules, media library, or UI.

Each capability has exactly one seam. Multiple providers implement that seam; an application does not
create a second local interface for the same capability. Runtime provider selection may use the
capability's provider service where it is reusable, while persistence and configuration writing stay
with the host.

## 7. Image capability

ChatEngine's current image-generation transport is the behavioral starting point. The reusable image
package owns neutral equivalents of these concepts:

- transport capabilities and a self-describing transport descriptor;
- generation preparation input and prepared request;
- submitted job identity, progress, output, cancellation, and health operations;
- image workflow metadata with opaque provider configuration;
- provider-neutral image references and connection information;
- the `IImageTransport` seam and the ComfyUI implementation.

The extraction removes ChatEngine domain dependencies. Public Connect contracts do not accept
ChatEngine entities such as `ImageProviderConnection`, `MediaAsset`, or application workflow records.
The host maps its entities to neutral connection, workflow, and reference values at its boundary.
Opaque workflow configuration remains data (for example, `JsonElement`) and is interpreted only by
the responsible adapter.

ChatEngine retains image-provider persistence, settings authorization, application-level provider
management, generation-job durability, media storage, caching, UI, and orchestration. It consumes the
new package and deletes the duplicate local transport contracts and ComfyUI implementation in the
same migration. The package does not reach back into ChatEngine.

The ComfyUI adapter preserves the established behavior that is provider-connection work: workflow
rendering, health probing, reference upload, request submission, WebSocket progress, polling, output
download, and cancellation. Deterministic tests pin request construction, parsing, progress ordering,
error behavior, cancellation, and empty/edge paths without requiring a live ComfyUI server.

The reusable API consists of `ImageConnection`, `ImageWorkflow`, `ImageReference`, the transport
request/progress/job/output contracts, `IImageTransport`, and `ImageProviderService`. The built-in
ComfyUI implementation is registered through `AddImageConnect`. Its prepared request and completed
output state are opaque round-trip values; hosts may persist them for durable job recovery but do not
interpret provider wire data. Host persistence, retry timing, and asset storage remain outside the
package.

## 8. Future capability boundaries

Later capability assemblies follow the same ownership and dependency rules and join the Connect
product package when implemented:

- `Darkspyre.Inference.Connect.Audio` covers generated audio such as sound effects, not speech
  transcription or speech synthesis.
- `Darkspyre.Inference.Connect.STT` covers batch and streaming speech-to-text.
- `Darkspyre.Inference.Connect.TTS` covers text-to-speech synthesis.

GMTools' current STT, TTS, and generated-audio implementations are source material, not contracts that
must be copied unchanged. Application-specific path services, file providers, planned cue models,
memory caches, persistence, and orchestration remain in GMTools. Provider prompts, secret-bearing
headers, and raw responses are never logged by the extracted implementations.

Whether a named future implementation lives in the capability assembly or a provider child assembly
is decided by the third-party SDK rule in section 5, not by provider popularity or by a desire to
create a symmetrical project tree.

## 9. Sequencing

Work proceeds in discrete, reviewable units:

1. **Repository-structure housekeeping.** Establish the Inference repository baseline, correct the
   copied governance documents, use the canonical `Tensor` spelling, and repair all project, content,
   solution, package, and documentation paths after capability grouping. The complete Connect
   solution must build before capability work continues.
2. **Storage housekeeping.** Complete the shared-package migration in section 2 without changing the
   public LLM behavior or the stored credential identities.
3. **Other housekeeping.** Complete separately agreed refactors against the 3.0 baseline. Each
   refactor keeps behavior stable, updates its tests and documentation, and is committed independently.
   This design does not pre-approve any other housekeeping change.
4. **LLM 10.0 rename.** Rename solution, projects, packages, assemblies, namespaces, tests, tools,
   examples, documentation, and all in-scope callers in one coordinated breaking change. Bump the
   package family to `10.0.0` and remove the old names rather than retaining shims.
5. **Image extraction.** Add `Darkspyre.Inference.Connect.Image`, adapt the donor contracts to
   provider-neutral values, and move and test the ComfyUI transport. Application adoption and removal
   of displaced application code occur in each consumer's own migration unit.
6. **Shared inference lane.** Add `Darkspyre.Inference.Common` only after both Connect and Tensor have
   approved implementations, then expose explicit Connect Bind adapters and verify their typed maps.
7. **Release verification.** Restore, build, test, verify formatting, pack all three product packages, audit
   dependencies for vulnerabilities, inspect package contents and dependency isolation, and perform
   any agreed live smoke tests before creating the immutable `10.0.0` tag.
8. **Later modalities.** Design and migrate Audio, STT, and TTS one capability at a time. Each
   migration updates all of its consumers and removes the previous duplicate implementation.

Housekeeping must not partially apply the 10.0 naming or introduce parallel seams. Conversely, the
10.0 migration must not absorb unrelated cleanup. If a prerequisite reveals that the agreed boundary
cannot be preserved, implementation stops and the design is revisited.

## 10. Completion criteria for 10.0

The initial 10.0 work is complete only when:

- shipping package composition and assembly, project, and namespace names match section 4;
- the repository and physical capability layout match section 4, with every solution, project,
  package-content, documentation, and tooling path resolving from its declaring file;
- current repository instructions and build commands describe Inference rather than donor projects;
- no embedded SecureStore or local persistence-store implementation remains, and any direct store
  consumer uses the package named in section 2;
- Common, Connect, and Tensor report the one version selected by `/Directory.Build.props`;
- the LLM and image APIs are provider-neutral and provider wire types remain internal;
- AWS SDK usage exists only in the Bedrock assembly and its tests/consumers;
- Common and Connect packages contain only their owned contracts and implementations; consumer
  migrations remove duplicate application seams in their own completed units;
- source, tests, examples, and current integration documentation contain no accidental references to
  the removed package or namespace names;
- restore, zero-warning build, tests, format verification, package creation, and vulnerability audit
  all pass; and
- package contents and dependency graphs have been inspected before an immutable release tag is
  created.

Historical release records and archived package artifacts may retain their original names when they
are clearly historical rather than current integration guidance.

## 11. Initial Common and Connect publication

The user authorized publishing the completed Common and Connect packages while the new Tensor package
IDs remain under development. On 2026-09-22, commit `bf9e6d5` passed zero-warning Debug and Release
builds, 187 tests (Common 1, Image 14, LLM 127, Bedrock 45), formatting verification, and a transitive
dependency vulnerability audit with no reported vulnerabilities. Package contents and dependency
graphs were inspected before these immutable private packages were published and read back through the
GitHub Packages API:

- `Darkspyre.Inference.Common` 10.0.0
- `Darkspyre.Inference.Connect.Image` 10.0.0
- `Darkspyre.Inference.Connect.LLM` 10.0.0
- `Darkspyre.Inference.Connect.LLM.Bedrock` 10.0.0

No family Git tag was created because the intended Tensor baseline was still in development. These
10.0 capability packages remain historical and are not the current three-package release surface.
