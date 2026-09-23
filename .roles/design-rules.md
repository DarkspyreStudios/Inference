# Darkspyre.Inference repository and package design rules

These rules define the repository structure and package boundaries. Capability behavior belongs in
the applicable technical design rather than in this file.

## Product roots

The repository root is `Darkspyre.Inference`. `Connect/`, `Console/`, `Tensor/`, and `UI/` are sibling
product workspaces with separate responsibilities. A product workspace owns its solution, build
configuration, package versions, documentation, tests, examples, and tools.

`Connect`, `Tensor`, and `UI` are active workspaces in the unified 11.x family. Empty product roots do not
justify placeholder projects or packages.

## Capability layout

Connect, Tensor, and UI group source assemblies and tests by capability:

```text
<Product>/
  src/<Capability>/<AssemblyName>/
  src/<ProductPackageId>/                 # Connect and Tensor packaging projects only
  tests/<Capability>/<TestProject>/
  examples/<ExampleProject>/
  tools/<ToolProject>/
```

Examples may be capability-specific without another directory level. A tool that intentionally
exercises several capabilities remains at the product-level `tools/` directory.

The solution's logical folders mirror the physical capability grouping. Every relative project and
content reference resolves from the project that declares it; moving a project includes updating and
verifying every reference in the same unit.

## Package identity

The repository publishes exactly four packages: `Darkspyre.Inference.Common`,
`Darkspyre.Inference.Connect`, `Darkspyre.Inference.Tensor`, and
`Darkspyre.Inference.UI.Settings`. Common and UI.Settings each have one matching assembly.
Connect and Tensor are product packages containing their capability-specific assemblies; their
package projects do not create empty facade assemblies. Capability project, assembly, folder, and
root-namespace names match, but those projects are not independently packable.

The Connect, Tensor, and UI.Settings packages reference the `Darkspyre.Inference.Common` NuGet at the synchronized
release version. They do not embed the Common assembly. All four package versions come from the
single repository `/Directory.Build.props`; a project must not override that version.

UI.Settings is a host-placed Blazor editor for provider setup sessions. It references Common and
Lumeo, not Connect, Tensor, or an application assembly. A host owns its connection list, editor
placement, persistence, protected credentials, consent, and provider-specific session composition.
Common, Connect, and Tensor remain UI-framework-neutral.

## Provider dependencies

A capability assembly contains its provider-neutral contracts and implementations. An implementation
that requires a provider-specific SDK remains in a separate provider assembly so its dependency and
ownership boundary are visible in source. The Connect product NuGet includes all implemented
Connect assemblies and declares the union of their runtime package dependencies.

## Storage dependencies

Shipping Connect capability libraries remain independent of application persistence and credential
storage. Host tools use `Darkspyre.SecureStore` for secrets and `Darkspyre.Persistence` for non-secret
durable local state only when they have a real storage requirement. Do not copy either implementation
into this repository.

Repository-wide dependency versions live only in `/Directory.Packages.props`. Projects reference only
the packages they consume and omit versions from `PackageReference`; Central Package Management
selects the shared version. Repository-wide build and package metadata, including the unified package
version, live in `/Directory.Build.props`.

Provider protocol compatibility versions are independent of the NuGet package version. In
particular, the direct Codex adapter uses one explicit Codex compatibility version for its
`User-Agent`, `version` header, and model-catalog `client_version` query; it must not derive those
values from the `Darkspyre.Inference` release version.

## Documentation and packages

`Connect/README.md` is the Connect product-family entry point and current integration contract.
Capability-specific detail may live beside its examples or in `Connect/docs/`, but current guidance
must not describe one capability as the whole Connect family once another capability ships.

Every package includes a readable package README and the repository license. Pack verification must
confirm that the Common assembly appears only in the Common package, Connect, Tensor, and UI.Settings
declare an exact synchronized dependency on Common, and each product package contains every owned
assembly plus its XML documentation. UI.Settings also includes its static web assets and
`THIRD-PARTY-NOTICES.md` for its owned numeric control's Lumeo-derived layout/accessibility. Lumeo is
an unmodified package dependency, not a fork; exact typed parsing and input validation belong to
UI.Settings.

Source builds use project references. `Directory.Build.targets` pins the package dependency on
Common to `[$(Version)]` when generating each consumer product's NuGet manifest. This does not
embed Common or introduce dependencies between Connect, Tensor and UI.Settings. Run the compiled
`eng/VerifyPackages` tool against all four release archives; it rejects foreign DLLs/native binaries,
local-only dependencies, missing owned assemblies/documentation and mismatched Common versions.
Whisper runtime build assets flow through its public NuGet dependency (`PrivateAssets="none"`);
Tensor marks the imported native files non-packable without disabling consumer output copying.

Package creation is a publish-stage action. During refactoring, do not run `dotnet pack` or create
`.nupkg` artifacts unless the user explicitly says to publish. Ordinary verification stops at
restore, zero-warning build, tests, format verification, and the dependency vulnerability audit.
