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
8. Record publication in `.roles/project.md`, commit that record, tag the release-source commit,
   and push master plus that one release tag to origin. No other tag or branch is pushed.

No live provider calls, model downloads or consumer-application migrations run. Audit reports and
packages are ignored outputs under `artifacts/`; the fresh verification cache is retained in the
system temporary directory and its exact path is printed on success.

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
bash -n deploy-major.sh deploy-minor.sh eng/deploy.sh eng/deploy.tests.sh
bash eng/deploy.tests.sh
bash eng/package-assets.tests.sh # Requires restored Tensor dependency assets; no archives created.
```

Tests exercise stable/prerelease version arithmetic, both bump modes, dry-run, dirty checkouts,
existing versions, build failure, vulnerability failure and the successful four-package flow.
Integration cases use disposable local Git repositories plus fake `gh` and `dotnet` commands; they
do not contact GitHub, publish a package or modify this repository. Fixtures are retained under the
printed temporary path for inspection. They test orchestration, not native runtime availability.

The separate package-asset checks evaluate real Tensor pack inputs and synthetic copy-path fixtures.
They verify that Whisper's shader remains a copy-to-output asset but is excluded from Tensor's own
archive inputs. Duplicate None/None and None/Content destinations fail; unique paths and noncopied
files pass. The archive verifier also rejects `.metal` shader sources alongside native binaries.

The published `11.5.2-rc.1` Tensor archive (and earlier affected archives) contains the duplicate
shader and intentionally fails the strengthened checks. Corrected releases must leave the shader
owned by the Whisper runtime dependency; do not overwrite old versions. Fresh local/feed consumer
checks verify this boundary on each deployment after the corrected archive is available.
