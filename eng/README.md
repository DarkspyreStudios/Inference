# Coordinated private NuGet deployment

Run either root script from any directory. They invoke the same Bash implementation in
`eng/deploy.sh`; the .NET generation is never changed by these commands.

| Command | Example version change |
|---|---|
| `./deploy-major.sh` | `11.5.1-rc.1` → `11.6.0-rc.1` |
| `./deploy-minor.sh` | `11.5.1-rc.1` → `11.5.2-rc.1` |

“Major” here is this repository's breaking-contract **release** component, not the .NET component.
The prerelease suffix is preserved. `--dry-run` prints the next version and destination without
requiring credentials, changing files, compiling, committing or publishing. `--help` lists usage.

## Prerequisites and side effects

- A clean `master` checkout (including no untracked files), with all intended implementation work
  already committed. The scripts never stage application changes or stash them.
- `origin` must be `DarkspyreStudios/Inference`; the repository and four existing packages must be
  private. Local master must contain the fetched remote master. Existing target tags/versions abort.
- Bash, Git, GitHub CLI, jq, the pinned .NET SDK, and standard shell utilities must be installed.
- The selected GitHub identity needs private dependency access, package publication permission,
  and repository push permission. The default is `GH_TOKEN` if supplied, otherwise the active
  `gh` login. To select another already-authenticated account without switching the active login:

  ```bash
  INFERENCE_GITHUB_USER=brandonm4 ./deploy-minor.sh
  ```

Credentials are process-scoped and are never written into NuGet.Config or Git. The destination is
fixed to `https://nuget.pkg.github.com/DarkspyreStudios/index.json`, not nuget.org. Native runtime
requirements for the existing consumer smoke test still apply to the deployment machine.

`VerifyConsumer` accepts an optional third argument, `absent`, to verify that GGML package discovery
and a Qwen runtime check refuse missing native assets without native initialization. On macOS ARM64,
an explicit `-p:VerifyGgmlNativeVersion=<supplier-version>` during restore and build adds the separate
`Darkspyre.TensorSharp.Backends.GGML.Native.osx-arm64` package. The third argument `cpu`, `metal`, or
`auto` then verifies its catalog/file identity, actual Qwen backend initialization and terminal shutdown.
Each mode runs in a separate process. These checks load no model weights and do not qualify inference.
The default two-argument consumer still restores exactly the four Inference package references without
a GGML native package. A native check uses the supplier version required by the selected Tensor package.

## Release sequence

1. Check tooling, checkout, credentials, remote and all four target package versions.
2. Update the shared `Directory.Build.props` version and matching references in the five product/root
   README files. Commit only those files. Historical design/release versions remain unchanged.
3. Restore all three solutions; build and test Debug and Release; verify formatting and explicitly
   reject vulnerability findings from the transitive JSON audits.
4. Pack exactly Common, Connect, Tensor and UI.Settings. `eng/VerifyPackages` checks owned assembly
   boundaries, exact Common dependencies and release-source identity.
5. Restore/run `eng/VerifyConsumer` against the local archives. Its build rejects distinct package
   assets targeting the same copy-output path. Reject concurrent source changes.
6. Push all four packages without overwrite/skip-duplicate switches.
7. Use a new empty package cache and no HTTP cache to restore/run the consumer from normal feeds.
   Compare all four downloaded archives byte-for-byte and check feed versions/private visibility.
8. Tag the release-source commit and push master plus that one release tag to origin. The annotated
   `v<version>` tag is the publication record. No other tag or branch is pushed.

No live provider calls, model downloads or consumer-application migrations run. Audit reports and
packages are ignored outputs under `artifacts/`; the fresh verification cache is retained in the
system temporary directory and its exact path is printed on success.

## Package contents

Every package includes a readable package README and the repository license. Pack verification
confirms that the Common assembly appears only in the Common package, that Connect, Tensor and
UI.Settings declare an exact synchronized dependency on Common, and that each product package
contains every owned assembly plus its XML documentation. UI.Settings also includes its static web
assets and `THIRD-PARTY-NOTICES.md` for its owned numeric control's Lumeo-derived
layout/accessibility. Lumeo is an unmodified package dependency, not a fork.

Source builds use project references. `Directory.Build.targets` pins the package dependency on
Common to `[$(Version)]` when generating each consumer product's NuGet manifest. `eng/VerifyPackages`
rejects foreign DLLs, native binaries and shader sources, local-only dependencies, missing owned
assemblies or documentation, and mismatched Common versions.

Whisper runtime build assets flow through its public NuGet dependency (`PrivateAssets="none"`).
Tensor marks assets imported from both `Whisper.net.Runtime` and `Whisper.net.Runtime.Metal`
non-packable without disabling consumer output copying. `ggml-metal.metal` must come only from the
public runtime dependency, never from Tensor's `content` or `contentFiles`.

`eng/VerifyConsumer` is a non-packable release smoke test with four PackageReferences and no source
project references. It checks loaded assembly source identities, the Settings contracts and Whisper's
public native runtime. Before publication it restores against `eng/VerifyConsumer/NuGet.Preflight.Config`
(local Inference archives plus normal dependency feeds). After publication it restores against the
root `NuGet.Config` with a new empty package cache and `--no-http-cache`. It does not load weights or
call providers. `eng/package-assets.tests.sh` verifies pack inputs and collision checks without
creating archives.

## Failure behavior

The first failing step stops deployment. Version commits, generated archives, and any successfully
published packages remain in place; the script never resets source or deletes remote packages.
Publication across four NuGets is not atomic. If a push, readback or Git push fails, inspect the
reported stage and complete that same release manually using its existing archives/source commit.
**Do not blindly rerun a bump script:** another invocation chooses the next version. Do not overwrite
an existing package. A failed preflight leaves version files unchanged; a later failure can leave a
local version commit that has not been pushed.

## Offline verification

```bash
bash -n deploy-major.sh deploy-minor.sh eng/deploy.sh
bash eng/package-assets.tests.sh # Requires restored Tensor dependency assets; no archives created.
```

The package-asset checks evaluate real Tensor pack inputs and synthetic copy-path fixtures.
They verify that Whisper's shader remains a copy-to-output asset but is excluded from Tensor's own
archive inputs. Duplicate None/None and None/Content destinations fail; unique paths and noncopied
files pass. The archive verifier also rejects versioned ELF libraries, `.metal` shader sources,
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
  Tensor/src/Darkspyre.Inference.Tensor/obj/project.assets.json \
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
