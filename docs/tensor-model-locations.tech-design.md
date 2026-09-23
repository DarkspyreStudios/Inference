# Tensor model locations and explicit import

Date: 2026-09-23

Status: implementation complete, verified and published in the private 11.4.0-rc.1 release
together with the independently completed Laya ONNX change. Publication evidence is recorded in
`.roles/project.md`.

## Ownership and contracts

`TensorModelLocation` in Tensor.Common has four typed variants: `LocalFile`, `LocalFolder`,
`RemoteFile`, and `RepositoryArtifact`. `TensorModelSelection` contains the location, backend,
optional declared license/expected primary length/SHA-256, and typed companion locations. A
repository location contains an adapter ID and its repository/revision/artifact selection. Local
and URL candidates have no fabricated repository or revision. Their final key uses the primary
artifact name and canonical manifest fingerprint, never an absolute path as the model ID.

`ITensorArtifactReader` owns bounded metadata access. `ITensorModelSource` adds optional repository
browsing and candidate lookup. `HuggingFaceModelSource` remains a real repository adapter; it is
not the authority for general model locations. `TensorArtifactReader` handles local files and
direct HTTP ranges, retaining the existing origin-scoped HTTP credential/redirect boundary.

`TensorModelResolver` is the shared acquisition coordinator. Each family supplies its artifact
requirements and `ResolveCandidateAsync` policy. `DiscoverAsync` resolves exactly the required
inputs; `ResolveAsync` applies the family policy to bounded metadata. `EvaluateAsync` evaluates an
already assembled candidate. Complete resolution returns the existing immutable pack definition.
Repository listing is not a second installed-model catalog.

All four setup factories always bind their family resolver; a repository adapter is optional.
Their optional artifact reader lets the host configure protected URL inspection. The host supplies
the corresponding access callback/client to its pack manager for actual acquisition. Neither reader
nor setup factory owns the host's supplied clients or repository adapter.

## Discovery and compatibility

Local paths must be absolute. A folder scan covers its top-level files only, at most 256 files;
it does not recurse or follow selected symbolic links. A unique family-format match may supply the
primary filename. Ambiguity requires an explicit primary selection. A single-file selection does
not discover adjacent files. A URL does not imply directory enumeration. Direct URLs must be
durable HTTPS (or numeric loopback HTTP) locations without query strings, fragments or user-info;
protected access uses the host callback, not embedded/transient URL credentials.

Family requirements:

- Qwen: supported Qwen3 GGUF schema and all numbered shards. A folder/repository can list them;
  independent files/URLs can be supplied as explicit numbered companion inputs. They pass the
  same complete-set and header policy as repository candidates. Other GGUF architectures are not
  newly supported.
- Supra: primary DiT plus text encoder, tokenizer and VAE. Pinned companion sources remain visible
  defaults. Local mirrors/URL overrides are accepted only when their sizes/hashes match the
  pipeline's supported companions; the actual encoder/VAE headers are still inspected. Hugging
  Face origin is no longer a compatibility condition.
- Whisper: retain the twelve supported published content identities and actual GGML header checks.
  Moving a file to another host/path does not change this policy; unknown content remains Unverified.
- Laya: exactly `model` (embedded-weight ONNX), `tokenizer`, and `agent-config`. The bounded ONNX
  metadata scanner skips tensor payloads and checks the fixed input shapes and Laya metadata against
  parsed serving configuration, without constructing `InferenceSession`. Tokenizer parsing is shared
  with the runtime. The existing Laya validator and loader are unchanged. Native graph validation
  still happens at Load; metadata compatibility does not guarantee allocation or execution.

The ONNX scanner follows the [upstream protobuf definition](https://github.com/onnx/onnx/blob/main/onnx/onnx.proto).
It bounds fields and strings in addition to the shared reader's 64 MiB transfer budget. Small agent
configuration/tokenizer reads have explicit size bounds. Missing metadata, integrity, permissions,
range support or companions cannot produce a saveable pack. No repository code or pickle globals
are executed. Inspection does not silently read full weight bodies when Range is ignored.

## Import and persistence transaction

Final artifact definitions still require actual lengths and SHA-256 hashes. `InstallAsync` remains
the explicit path for already-complete definitions. `ITensorModelPackManager.ImportAsync` takes a
typed `TensorModelImportRequest` containing the selection and selected family resolver:

1. Discover the exact artifact set; require actual supplied/source licenses.
2. Stage complete artifacts, computing their lengths and hashes. Supplied expectations must match.
3. Run family validation through a reader restricted to those staged bytes.
4. Register the resulting immutable definition and use the existing installation path to write
   artifacts through the host's `IPersistenceStore`, publishing the installed manifest last.

Install and Import share copying/hashing, per-key installation serialization, active-lease checks
and manifest publication. Import cannot fetch additional un-staged sources after validation. Staging
files are cleaned on success, rejection, failure or cancellation; incomplete persistence writes do
not advertise an installed pack. Retry uses the established installation/repair rules. Cancellation
does not promise rollback after a completed external write. An observed hash identifies copied bytes,
not publisher authenticity. No license, digest, size or compatibility evidence is invented.

Copying/importing is not loading. Runtime artifact access still goes through installed pack leases
and the application-supplied persistence store. No new durable model cache, hosted process, inference
transport or engine-lifetime mechanism is introduced.

## Setup and host UI

`TensorSetupSession` remains the single draft owner. It exposes registered packs, location kind,
path/URL or repository inputs, explicit companion overrides, required license and optional primary
integrity fields, and inspection evidence. Split-file families expose a repeatable artifact group.
Edits invalidate the resolved model and observations. Mutating actions remain serialized by the
existing session; stale bound edits/results cannot select an obsolete pack.

Inspect, Import, Install and Save are distinct operations. Only Supported, complete definitions are
saveable. Save returns `TensorSetupChanges` with the selected configuration and definition, without
installation or lifecycle calls. A successful Import installs but does not commit settings. Closing
an unsaved editor does not silently remove an installed pack. Explicit Load/Unload/Stop actions remain
available only under the existing host-owned instance contract.

Common's optional `SetupTextField.PathSelection` hint describes file/folder selection without a UI
dependency. UI.Settings uses an optional `IInferenceSettingsPathPicker` capability on its host. The
same picker supports Connect executable fields while preserving command-name entry. Without a picker,
manual entry works. The host owns native dialogs and access grants. Cancelled, stale or superseded
picker results do not edit the provider. The existing invalid-input Save gate remains authoritative.

## Verification and release boundary

Deterministic tests cover local files/folders/URLs, complete split GGUF, alternate-origin Supra and
Whisper metadata, three-artifact Laya import, missing companions, ambiguous folders, schema failures,
expected-hash mismatch, range rejection, cancellation, custom persistence and metadata-driven commit.
Picker tests cover file/folder selection, executable names, cancellation and stale completions.
No new full-weight model download, paid inference or consumer application migration is a gate.

Public resolver/selection/provenance contracts changed; callers are migrated in this unit, without
parallel legacy APIs. The authorized coordinated release is 11.4.0-rc.1. The original implementation
unit did not bump a version, pack, publish or change dependencies; publication is a separate unit.

### Verification record

Debug and Release builds completed with zero warnings/errors for Connect, Tensor and UI.Settings.
Each configuration passes Connect 301, Tensor 291 and UI 49 tests; the 24 Common tests appear in each
subset total. All three formatting verifications and transitive vulnerability audits pass. Dependency
inputs were unchanged, so existing restored assets were reused with `--no-restore`. No package
archives were created. Laya's validator and loader are unchanged from the approved ONNX unit.
Native picker rendering has component-test coverage, not consuming Desktop application certification.
