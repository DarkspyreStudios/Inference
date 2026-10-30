# Coordinated NuGet deployment

A release has two parts. A root script prepares, verifies and tags it; the **Publish Darkspyre.Inference**
workflow (`.github/workflows/publish.yml`) publishes the tag to nuget.org. Run either root script from any
directory. Both invoke the same Bash implementation in `eng/deploy.sh`; the .NET generation is never changed by
these commands.

| Command | Example version change |
|---|---|
| `./deploy-major.sh` | `11.5.1-rc.1` → `11.6.0-rc.1` |
| `./deploy-minor.sh` | `11.5.1-rc.1` → `11.5.2-rc.1` |

“Major” here is this repository's breaking-contract **release** component, not the .NET component.
The prerelease suffix is preserved. It must name the .NET prerelease the family builds on (AGENTS.md, "Releases"); when
.NET moves to a new prerelease, change the suffix by hand in `Directory.Build.props` and the README files before
running a script. `--dry-run` prints the next version without requiring credentials,
changing files, compiling or committing. `--help` lists usage.

## Prerequisites and side effects

- A clean `master` checkout (including no untracked files), with all intended implementation work
  already committed. The scripts never stage application changes or stash them.
- A Darkspyre.Common checkout beside it, at `../Darkspyre.Common`. HostTools and Antigravity carry its
  LocalServices libraries.
- `origin` must be `DarkspyreStudios/Inference`. Local master must contain the fetched remote master. An existing
  target tag, or the target version of any library already on nuget.org, aborts.
- Bash, Git, GitHub CLI, jq, curl, the pinned .NET SDK, and standard shell utilities must be installed.
- The selected GitHub identity needs repository push permission. The default is `GH_TOKEN` if supplied, otherwise
  the active `gh` login. To select another already-authenticated account without switching the active login:

  ```bash
  INFERENCE_GITHUB_USER=brandonm4 ./deploy-minor.sh
  ```

Credentials are process-scoped and are never written into NuGet.Config or Git. Native runtime
requirements for the existing consumer smoke test still apply to the deployment machine.

`VerifyConsumer` restores the declared Inference library references and checks their release
contracts. It loads no native GGML driver and no model weights, and it does not qualify inference.
TensorSharp's packages ship no GGML native library: the application supplies verified drivers.

## Release sequence

1. Check tooling, checkout, credentials, remote, the tag and every target library version on nuget.org.
2. Update the shared `Directory.Build.props` version and matching references in the five product/root
   README files. Commit only those files. Historical design/release versions remain unchanged.
3. Restore all three solutions; build and test Debug and Release; explicitly reject vulnerability
   findings from the transitive JSON audits.
4. `eng/pack.sh` packs Common, the media Connect package, every Connect provider/Common and Tensor library, and the
   UI.Settings Core, Avalonia and Blazor libraries. `eng/VerifyPackages` checks owned assembly boundaries, exact
   family dependencies and release-source identity.
5. Restore/run `eng/VerifyConsumer` against the local archives. Its build rejects distinct package
   assets targeting the same copy-output path. Reject concurrent source changes.
6. Tag the release-source commit and push master plus that one release tag to origin. The annotated
   `v<version>` tag is the release record. No other tag or branch is pushed.
7. Run the Publish workflow on the tag, from the repository's Actions page. It checks out the tag and
   Darkspyre.Common's `main` branch side by side, builds the three solutions in Release in Packages mode, runs
   `eng/pack.sh` against the tag's commit, signs in to nuget.org through Trusted Publishing (`NuGet/login`, user
   `darkspyre`) and pushes every library package without overwrite or skip-duplicate switches. It refuses a ref
   that is not a `v<version>` tag matching the version in `Directory.Build.props`.

No live provider calls, model downloads or consumer-application migrations run. Audit reports and
packages are ignored outputs under `artifacts/`.

## Package contents

Every package includes its own nuget.org page as `README.md` and the repository license. The page's sources are
`PACKAGE.md` beside the package's project, which starts with `# <PackageId>` and holds only that package's consumer
documentation, and `eng/PackageFamily.md`, the family section written once. The `ComposePackageReadme` target in
`Directory.Build.targets` runs before `_GetPackageFiles` and writes `obj/<configuration>/<tfm>/package/README.md`: the
page and the family section, each with trailing white space removed, one blank line between them and one final line
break, in UTF-8 without a byte order mark. It fails the pack when `PACKAGE.md` is missing, when its first line is not
`# <PackageId>`, or when the page or the family section holds a work note: a ticket key (`DSA-<n>`), "phase 1" or a
person's name the check lists. The repository READMEs are the design record and are not packed. Package pages carry no
version number, so a release changes no page. Pack verification
confirms that the Common assembly appears only in the Common package, that internal library dependencies use the exact synchronized version, and that each Tensor library
contains only its own assembly plus XML documentation. Connect.Common and each provider own one assembly.
The media Connect package keeps Image, TTS and Audio together and depends on Connect.Common.
HostTools owns its own assembly, HostTools.Contracts and Darkspyre.Common's five LocalServices assemblies, which have
no package of their own, and depends on no other Inference package. Only `lib/` DLLs count as a package's assemblies: HostTools
and Antigravity also carry the host-tools service and MCP program for each runtime under `hosttools/<rid>/`, which a
consumer copies beside its output and never references (`Connect/README.md`).

Source builds use project references. `Directory.Build.targets` pins internal library dependencies to `[$(Version)]` when generating each library's NuGet manifest. `eng/VerifyPackages`
rejects foreign DLLs, native binaries and shader sources, local-only dependencies, missing owned
assemblies or documentation, and mismatched Common versions. It also recomputes each archive's expected `README.md`
from the package's `PACKAGE.md` and `eng/PackageFamily.md` in its source context with the same composition rule, and
rejects an archive whose `README.md` differs from it in any byte, starts with a byte order mark or is not valid UTF-8.
It finds the page beside the one `<PackageId>.csproj` under the repository root, skipping `bin`, `obj`, `artifacts`
and `tmp` folders.

Whisper runtime build assets flow through its public NuGet dependency (`PrivateAssets="none"`).
The shared build target marks assets imported from `Whisper.net.Runtime`, `Whisper.net.Runtime.Metal` and
`Whisper.net.Runtime.Cuda12.Linux` non-packable for Whisper and its consumers without disabling consumer output copying.
`ggml-metal.metal` must come only from the public runtime dependency, never from Tensor's `content` or `contentFiles`.

`eng/VerifyConsumer` is a non-packable release smoke test with explicit library PackageReferences and no source
project references. It checks loaded assembly source identities, the Settings contracts and Whisper's
public native runtime. It installs and connects to the host-tools service from the payload the packages copied beside
its output, and checks that the MCP program is there. It restores against `eng/VerifyConsumer/NuGet.Preflight.Config`
(local Inference archives plus nuget.org). It does not load weights or
call providers. `eng/package-assets.tests.sh` verifies pack inputs and collision checks without
creating archives.

## Failure behavior

The first failing step stops deployment. Version commits and generated archives remain in place; the script
never resets source. Publication across the library NuGets is not atomic. If the workflow fails after some
packages are pushed, fix the cause and publish the remaining packages of the same tag; nuget.org refuses the ones
already there, and a package can only be unlisted, never deleted.
**Do not blindly rerun a bump script:** another invocation chooses the next version. Do not overwrite
an existing package. A failed preflight leaves version files unchanged; a later failure can leave a
local version commit that has not been pushed.

## Offline verification

```bash
bash -n deploy-major.sh deploy-minor.sh eng/deploy.sh eng/pack.sh
bash eng/package-assets.tests.sh # Requires restored Tensor dependency assets; no archives created.
```

The package-asset checks evaluate real Tensor pack inputs and synthetic copy-path fixtures.
They verify that Whisper's shader remains a copy-to-output asset but is excluded from Tensor's own
archive inputs. Duplicate None/None and None/Content destinations fail; unique paths and noncopied
files pass. For each of the 22 projects `eng/pack.sh` lists, they check that `_GetPackageFiles` packs exactly one
`README.md`, from `obj/.../package/README.md`, and that the file equals the composition of the project's `PACKAGE.md`
and `eng/PackageFamily.md`. `eng/VerifyConsumer.Tests/PackagePageFixture.proj` composes a page holding a ticket key,
and the checks expect the work-note error. The archive verifier also rejects versioned ELF libraries, `.metal` shader sources,
compiled shaders and GPU code alongside native binaries.

`VerifyPackages` reads expected dependency versions from `Directory.Packages.props` in its own
source context, found from the verifier assembly's directory. It rejects missing, duplicate,
conditional, imported or computed central version inputs. MSBuild element and attribute names use
ordinal case-insensitive comparisons. Differently cased duplicate attributes are ambiguous and
rejected. NuGet's version parser validates Tensor's
required dependency declarations against these inputs. TensorSharp declarations use one synchronized
version. ONNX, both Whisper packages and NAudio remain required. Whisper runtime includes all build
assets. A matching minimum declaration is not an exact restore pin: restored versions are checked
separately by the inventory's managed-GGML gate. Exact Common declarations remain mandatory.

Every archive rejects GGML native package dependencies even without embedded native files. Packaged
MSBuild XML rejects known CLI acquisition targets/items, download tasks, remote HTTP imports, and
acquisition commands in execution/inline-task/property inputs. These checks preserve local managed
imports, static-asset manifests and normal copy targets. They are a known acquisition-pattern gate,
not a sandbox for arbitrary MSBuild code.

The published `11.5.2-rc.1` Tensor archive (and earlier affected archives) contains the duplicate
shader and intentionally fails the strengthened checks. Corrected releases must leave the shader
owned by the Whisper runtime dependency; do not overwrite old versions. Fresh local/feed consumer
checks verify this boundary on each deployment after the corrected archive is available.

## Runtime dependency inventory

`VerifyRuntimeInventory` reads the restored Tensor `project.assets.json`, checks each installed
package manifest's identity/version and hashes the actual native/GPU asset files. It records exact
package versions, restore dependency edges, managed assembly paths, declared RIDs, asset sizes and
SHA-256 values. Whisper's build-target assets remain visible even though they do not use NuGet
`runtimeTargets`. The report covers all restored native assets and checks primary vendor-library
presence for `osx-arm64`, `linux-x64`, `linux-arm64`, `win-x64` and `win-arm64`.

```bash
dotnet run --project eng/VerifyRuntimeInventory -- \
  eng/VerifyConsumer/obj/project.assets.json \
  tmp/tensor-runtime-inventory.json "$(git rev-parse HEAD)"
```

The report has schema version 1. Windows DLL architecture comes from the actual PE header, not the
directory name. Other binary architectures remain `NotInspected` in this tool. `AssetsPresent`
records package-file presence, not initialization, dynamic dependency closure, accelerator support
or engine execution. Every execution qualification remains `NotRun`. The command never loads
native libraries, acquires a CLI or builds/publishes a package. Missing declared files and conflicting
installed manifests fail verification. Missing RID payloads and header/RID mismatches remain explicit
report entries rather than disappearing from the inventory.

Optional GGML developer-package files under `ggml/<variant>/` are inventoried alongside baseline
`runtimes/<rid>/native/` files. Their RID comes from the matching native package identity, not a guess
from the filename. Unbound GGML variant files remain visible with an unknown RID. License files are
not native payloads. Five-RID fixture coverage records file hashes and Windows PE facts without
executing those files; every runtime qualification remains `NotRun`.

`--require-managed-ggml` rejects GGML native package identities even without payloads, legacy GGML
native payloads, missing required managed dependencies and restored versions that differ from the
central policy. It writes the inventory before reporting a closure failure. This gate distinguishes
Tensor's managed-only GGML dependency requirement from standalone consumer native selection. The
source graph uses synchronized TensorSharp `2.8.6.9` packages and requires an explicit managed GGML
dependency. The genuine managed package graph passes this gate without GGML native package edges or
payloads. CUDA retains committed PTX dependency assets; Whisper, ONNX and image runtime native assets
remain separate inventory entries, not a claim that the entire Tensor graph is native-free.
The OpenCV Windows ARM64 runtime is absent. The OpenCV Windows x64 runtime includes an x86
`opencv_videoio_ffmpeg.dll`; the inventory reports the mismatch. Asset inventory does not qualify
execution on any of the five RIDs.

## Connect provider discovery assets

Each provider package carries a buildTransitive target that roots its assembly during trimming.
Source-mode consumers use `ConnectDiscovery.targets`, imported by the source map and repository
build, to root the included provider outputs after reference resolution. This keeps automatic
registration reachable when an application uses only the Common facade. The package-input audit
checks the roots and the separate library boundaries without creating archives.
