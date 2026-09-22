# Darkspyre.Inference repository and package design rules

These rules define the repository structure and package boundaries. Capability behavior belongs in
the applicable technical design rather than in this file.

## Product roots

The repository root is `Darkspyre.Inference`. `Connect/`, `Console/`, and `Tensor/` are sibling
product workspaces with separate responsibilities. A product workspace owns its solution, build
configuration, package versions, documentation, tests, examples, and tools.

`Connect` is the only active workspace in the 10.0 refactor. Empty product roots do not justify
placeholder projects or packages.

## Connect capability layout

Connect groups shipping projects and tests by capability:

```text
Connect/
  src/<Capability>/<PackageId>/
  tests/<Capability>/<TestProject>/
  examples/<ExampleProject>/
  tools/<ToolProject>/
```

Examples may be capability-specific without another directory level. A tool that intentionally
exercises several Connect capabilities remains at the product-level `tools/` directory.

The solution's logical folders mirror the physical capability grouping. Every relative project and
content reference resolves from the project that declares it; moving a project includes updating and
verifying every reference in the same unit.

## Package identity

For every shipping library, package ID, assembly name, project directory, project file, and root
namespace match. Capability package names use `Darkspyre.Inference.Connect.<Capability>`. A
provider-specific package appends the provider name, for example
`Darkspyre.Inference.Connect.LLM.Bedrock`.

There is no empty `Darkspyre.Inference` or `Darkspyre.Inference.Connect` umbrella package.

## Provider dependencies

A capability package contains its provider-neutral contracts and implementations that need no
provider-specific third-party SDK. An implementation that requires such an SDK lives in a separate
provider package referencing the capability package. Consumers that do not select that provider do
not acquire its SDK dependencies.

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
confirm that content references still resolve after directory changes.

Package creation is a publish-stage action. During refactoring, do not run `dotnet pack` or create
`.nupkg` artifacts unless the user explicitly says to publish. Ordinary verification stops at
restore, zero-warning build, tests, format verification, and the dependency vulnerability audit.
