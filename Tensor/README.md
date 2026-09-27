# Darkspyre.Inference.Tensor

Version **11.10.5-rc.1** adopts the shared typed reasoning-level contract; Qwen supports `None` and `High`.
It includes Laya's CPU/GPU-only Core ML update and retains the ONNX and user-selected
model-location/import contracts introduced in 11.4.0-rc.1.

In-process, first-class .NET inference built on TensorSharp, ONNX Runtime and Whisper.net and distributed as the
`Darkspyre.Inference.Tensor` NuGet. The current package targets .NET 11 and references the synchronized
`Darkspyre.Inference.Common` NuGet rather than embedding Common. Model weights are installed
separately through Tensor's model-pack manager into a pack directory on the local file system;
constructing a runtime or running inference never downloads weights. The pack directory defaults to
`LocalApplicationData/Darkspyre Studios/Inference/Tensor`, and the host can supply another
directory, such as its app folder. Install and Import write every artifact into the pack directory's
own `.tensor-staging` folder, check its length and SHA-256, and rename it into place, so a download
never passes through the system temporary folder or another volume, and never needs twice its size.
An install or import removes staging files untouched for more than an hour, which a stopped process
leaves behind; a newer file may belong to another process's download. Each engine receives the
installed files as plain paths: Qwen and
Supra pass them to TensorSharp, Laya creates its ONNX Runtime session from the model path, and
Whisper loads through `WhisperFactory.FromPath`.

Assemblies included by Tensor **11.10.5-rc.1**:

- `Darkspyre.Inference.Tensor.Common` — the host-controlled model lifecycle, model packs,
  verification, the local pack directory, and execution coordination.
- `Darkspyre.Inference.Tensor.LLM`, `.Image`, and `.Decision` — interchangeable capability
  interfaces using the typed requests and results from `Darkspyre.Inference.Common`.
- `Darkspyre.Inference.Tensor.LLM.Qwen` — Qwen-compatible GGUF text inference.
- `Darkspyre.Inference.Tensor.Image.Supra` — Supra2-IMG text-to-image inference. It runs the pinned
  Supra DiT, Flan-T5-Base encoder, and Stable Diffusion VAE decoder directly through TensorSharp on
  CPU or CUDA. It returns PNG bytes through the shared image result. The current model supports
  256x256 output, deterministic seeds, Euler step counts, and classifier-free guidance; it does not
  support negative prompts.
- `Darkspyre.Inference.Tensor.Decision.Laya` — Laya-family typed decision inference over a
  host-registered checkpoint exported to ONNX. The ModernBERT encoder and typed decision heads run
  through ONNX Runtime: the Core ML execution provider on macOS, the CPU provider elsewhere. The BPE
  tokenizer, sequence framing and calibration run in C#. Neither PyTorch nor a Python runtime is
  used.
- `Darkspyre.Inference.Tensor.STT` — interchangeable typed PCM transcription and identified-window
  streaming contracts, separate from capture, VAD and playback.
- `Darkspyre.Inference.Tensor.STT.Whisper` — resident CPU Whisper inference through the public
  Whisper.net runtime. Whisper.net, Whisper.net.Runtime and NAudio.Core remain declared NuGet
  dependencies, not third-party DLLs embedded by the Darkspyre package.

Implementation names identify the model family they support, not a particular checkpoint or training
run. Laya is not a general-purpose ModernBERT engine: a custom checkpoint must retain the Laya
architecture, typed heads and tokenizer conventions, and its ONNX export must match the graph
contract in `docs/laya-onnx-runtime.tech-design.md`. Its sequence
limits and fitted calibration come from the selected pack's agent configuration. Selecting a
fine-tuned checkpoint does not independently establish calibration accuracy for a new distribution.

Supra's publisher supplies the DiT as a PyTorch ZIP state dictionary rather than safetensors. The
Darkspyre TensorSharp fork reads that file as data through a whitelist-only checkpoint reader. It
accepts only the tensor rebuild, storage, and container forms required by the pinned checkpoint and
rejects arbitrary pickle globals and reducers. Flan-T5 and the VAE use their pinned safetensors
artifacts. No Python or PyTorch component is loaded or executed.

Tensor uses `Darkspyre.ML.Tokenizers` for the focused SentencePiece correction needed by Flan-T5
models whose BOS token is intentionally disabled. This release uses TensorSharp 2.8.6.4;
this development checkout uses the local-only GGUF fix described below. Both include rank-four
channels-last image group normalization. These fork packages retain the upstream
assemblies and namespaces; consuming code remains first-class C# and does not load Python or PyTorch.

Each capability assembly publishes its pack definition separately from installation. A desktop
application composes the packs it supports into a `TensorModelCatalog`, constructs one
`TensorModelPackManager` over its pack directory, and explicitly calls `InstallAsync` when
the user chooses to download a model. Inference only acquires verified packs and never initiates a
download.

## Host-controlled lifecycle

The application starts and shuts down independently of this library. For a selected Tensor engine,
the host calls `StartAsync`, `LoadAsync`, the capability's typed inference method as many times as
needed, `UnloadAsync`, and `StopAsync`. Running a request never loads a model implicitly. One engine
instance holds one model at a time; after Unload, the host may load another model supported by that
engine or use another instance. Separate instances can hold models concurrently, subject to available
resources. Requests can run concurrently with a host-initiated unload or stop.

`StartAsync`, `LoadAsync`, `UnloadAsync` and `StopAsync` validate their input and return once the
engine accepts the request. A model that is not registered, installed or supported fails on the
call. The engine runs accepted work in order on a background thread and reports each transition
through `EngineStatusChanged`: Starting, Started, Loading, Loaded, Unloading, Unloaded, Stopping,
Stopped, or Failed with its error. The engine calls each listener and moves on; a listener with async
work posts it and returns. `GetObservationAsync` returns the resident model, its backend and the
latest state.

The token passed to `UnloadAsync` or `StopAsync` stays live until the teardown finishes. From the
moment a teardown is accepted, new requests are refused. An uncancelled teardown waits for active
requests; cancelling the token, at the call or later, cancels them. Native work still finishes
before model memory is released, and the engine reports Unloaded or Stopped only after that.

`StopAsync` unloads any remaining model and releases the engine's other resources. Stop is terminal
for that instance: create a new instance to run again. A second Stop and a Start after Stop are
no-ops. `DisposeAsync` performs Stop and completes after native work has released model memory.

The following example assumes the user has already installed the Supra model pack through the
explicit `TensorModelPackManager.InstallAsync` action:

```csharp
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Image;
using Darkspyre.Inference.Tensor.Image.Supra;

// packDirectory is the host's pack directory (or omit it to use the per-user default).
TensorModelPackDefinition supra = TensorImageModels.Supra2Image;
using var packs = new TensorModelPackManager(new TensorModelCatalog([supra]), packDirectory);
await using ITensorImageInference image = new SupraImageInference(packs);

var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
image.EngineStatusChanged += (_, status) =>
{
    if (status.State == TensorEngineState.Loaded) { loaded.TrySetResult(); }
    else if (status.State == TensorEngineState.Failed) { loaded.TrySetException(status.Error!); }
};

await image.StartAsync();
try
{
    await image.LoadAsync(supra.Key); // Returns once the engine accepts the load.
    await loaded.Task;               // Loaded, or Failed with its error, arrives through the event.
    try
    {
        ImageGenerationResult result = await image.GenerateAsync(new ImageGenerationRequest
        {
            ModelId = supra.Key.ModelId,
            Prompt = "a small glowing jellyfish"
        });
        // The host consumes the typed result and may make more requests against this loaded model.
    }
    finally
    {
        await image.UnloadAsync();
    }
}
finally
{
    await image.StopAsync();
}
```

With an uncancelled token, `UnloadAsync` and `StopAsync` wait for active requests to finish before
releasing the model. Cancellation escalates teardown: the engine cancels active requests, whose
results may be lost, then waits until native work stops using the model before freeing it. Passing
an already-cancelled token requests that urgent path immediately; cancelling a token while waiting
escalates at that point. In-process native work may not observe cancellation immediately, so the
host must not expect a fixed shutdown deadline. A host that abandons an LLM stream must dispose its
enumerator so model teardown can complete. Cancellation does not authorize releasing memory still
in use by native execution.

## Provider setup

`QwenSetup.Definition` and `SupraSetup.Definition` describe providers without constructing an engine,
opening a store or loading native libraries. Their `CreateSettingsManager` methods bind a typed host
`ISetupCommitTarget<TensorSetupChanges>` and the existing pack manager to a `TensorSettingsManager`.
Its `OpenAsync(identity, existing)` returns the `TensorSettings` object the common setup editor renders.
For Qwen and Supra, the default setup list contains the engine's built-in pack, and hosts may supply
`offeredModels` with their own registered compatible packs. Laya has no built-in pack, so its
`offeredModels` is required. Qwen and Supra validate the pack's capability, family,
roles and declared backends before offering it. All four factories bind their model-family resolver
for local files/folders and independent URLs. Supply an `ITensorModelSource` through the optional
`source` argument to additionally enable repository browsing. The optional `reader` argument accepts
host-configured artifact access, for example `new TensorArtifactReader(source, httpClient, access)`.

Selection/Save does not install or load. Install, verify, load, unload and stop are distinct callable
actions with declared side effects. Load, Unload and Stop return once the engine accepts the request;
the outcome arrives through `EngineStatusChanged`. The supplied instance is optional and remains
host-owned. `OpenAsync` reads its state through `GetObservationAsync`. The manager implements
`IInferenceSettingsStatusSource`: while a listener is attached to `StatusChanged`, it relays each
`EngineStatusChanged` transition, and `RefreshStatusAsync` reads the new observation into the settings.
Without an instance, Loaded/Ready remain unknown. A backend name is a supported selection, not proof
that the backend is available. The explicit **Check backend prerequisites** action records available,
unavailable or unknown runtime/device prerequisites without installing or loading a model. Qwen uses
the loader's same Auto policy (GGML Metal on macOS, GGML CUDA elsewhere); Supra Auto can fall back
to managed CPU. Laya Auto selects Core ML on macOS and CPU elsewhere. No check starts a backend: GGML
checks `GgmlBasicOps.CanInitializeBackend`, MLX checks that its native library is present, direct
CUDA checks its device/driver, and Core ML checks that ONNX Runtime offers its Core ML execution
provider. Every check runs on a background thread. These observations do not guarantee memory allocation or successful model
loading. Edits clear the observation, and opening or browsing settings never runs a probe.
Loading requires an instance configured for the selected backend;
the editor does not silently replace instances or interrupt application work.

`TensorSetupChanges.RequiresUnload` tells the host when a saved model/backend edit changes the
previous configuration. `Definition` carries the immutable selected pack manifest; persist it with
the configuration and reconstruct registrations when composing the manager after restart. Saving
does not perform that unload. Stop remains terminal, and an
already-cancelled teardown action token reaches the engine as urgency rather than skipping cleanup.
See the [shared editor example](../Common/examples/Darkspyre.Inference.Setup.Examples/README.md).
These setup contracts are included in the synchronized 11.10.5-rc.1 package family, including
provider-owned dirty state and explicit choice capabilities for the shared Settings editor.

Qwen and Supra now load the definition selected by `LoadAsync(key)`, rather than requiring the
built-in key. Register complete, immutable definitions in the same manager used by the engine.
Neither Save nor a prompt installs/loads weights. Requests, execution coordination, observations
and provenance use the resident pack's actual key. The host can Unload and then Load another
compatible pack on the same instance; Stop remains terminal.

Qwen targets `TensorLlmModels.QwenRuntimeArchitecture`: GGUF architectures `qwen3`, and the Qwen 3.5
family `qwen35`, `qwen35moe` and `qwen3next`, which mixes full-attention layers with GatedDeltaNet
recurrent layers. Other TensorSharp architectures, including `qwen4exp`, are not loaded. Its loader
checks the installed GGUF architecture/version, tokenizer and chat metadata, dimensions, storage
encoding and complete shard declarations before native allocation. Qwen 3 checks every layer's
tensor shapes; for the Qwen 3.5 family it checks the recurrent-state keys and layer layout, and
TensorSharp's own load validates each layer. Listed context/output limits remain unknown until validated loaded-runtime
metadata is available; the built-in 4B limits are not copied onto other models. A split GGUF pack
installs every declared shard beside the primary file under its numbered name, and Qwen passes
TensorSharp the primary path; TensorSharp opens the sibling shards from the same directory. Shard
count/names/numbering and duplicate tensors are checked before native allocation.

**Capability addition (11.10.5-rc.1):** Qwen's Common `LlmCapabilities.OutputLimitEnforcement` is
`EngineEnforced`, describing its existing `MaxOutputTokens` runtime limit. This is separate from
nullable discovered model limits and does not add a Tensor accounting/compaction service.

Qwen follows the chat protocol TensorSharp registers for the loaded architecture. That protocol's
output parser separates the answer, the thinking and the tool calls, which stream as
`LlmTextDeltaEvent`, `LlmReasoningDeltaEvent` and the tool-call started, arguments and completed
events. `SupportsToolUse` is true: request tools pass to TensorSharp, and history messages carry
their tool calls and text tool results. `ToolChoice` and `AllowParallelToolCalls` fail as unsupported
features, and a binary tool-result part fails as well. `ReasoningLevels` is `None` (thinking off) and
`High` (thinking on). With structured output and thinking on, the schema's grammar starts after the
thinking block. Image input is off.

A loaded Qwen model reports its usable context as `LlmModelInfo.MaxContextTokens`. Its
`MaxOutputTokens` is null: Qwen declares no separate output limit, and a reply can use whatever context
is left. TensorSharp resolves the declared context from the GGUF metadata and grows the
attention cache on demand. After the weights are resident, Qwen measures the memory left (the GC's
available memory, minus the working set, minus the larger of 512 MiB and one sixteenth) and divides
it by the cache bytes one token adds: keys and values for each full-attention layer at two bytes per
element. The usable context is the smaller of the declared context and that quotient. It is a
snapshot; memory taken later does not lower it. A request whose estimated prompt, rendered with the
protocol's template and counted with the model's tokenizer, plus `MaxOutputTokens` exceeds it fails
with `CapacityExhausted` before the engine runs.

Supra uses `TensorImageModels.SupraRuntimeArchitecture` and requires all four roles: `supra-model`,
`text-model`, `text-tokenizer`, `vae-model`. Its resolver and loader share the required DiT, text
encoder and VAE tensor schema, checked before native allocation. The restricted checkpoint reader
and native component constructors still validate actual data. A pack label is not proof of compatibility.
The 256x256 output, prompt/sampler constraints and generation algorithm are unchanged.

## Model source metadata and registration

### User-selected locations

The same settings expose registered packs, local file/folder paths, direct artifact URLs and
optional repository selection. Location fields are inputs to Inspect and Import; Save needs only the
selected pack and backend. Required companion locations and split-GGUF shard inputs come from
the family resolver. A folder scan is top-level only (at most 256 files); ambiguous primary models
require a filename. Direct file selections do not scan adjacent files. Direct URLs use durable HTTPS
or numeric loopback HTTP, without query strings, fragments or user-info; access credentials remain
host-owned. No web directory crawling or native model activation occurs during inspection.

Inspect reads bounded metadata. When complete lengths/hashes are unavailable, **Import model
artifacts** is an explicit full-copy/download action: it calculates integrity, checks the selected
family against staged bytes, then uses the existing pack manager to install the complete pack into
its pack directory. Missing licenses are requested, never fabricated. A calculated hash
identifies the imported content, not its publisher. Expected hashes must match. Save still only
commits a complete compatible definition; neither Save nor Import loads the model. Closing an
unsaved editor does not remove an imported pack.

```csharp
var resolver = new QwenModelResolver();
var selection = new TensorModelSelection(
    new TensorModelLocation.LocalFolder(modelDirectory, "model.gguf"),
    License: actualModelLicense);
TensorModelImportResult imported = await packs.ImportAsync(
    new TensorModelImportRequest(selection, resolver), cancellationToken: cancellationToken);
// Check imported.Resolution.Evaluation and Definition. The host separately saves configuration
// and decides when to call its engine's LoadAsync with the installed definition's key.
```

Local/URL artifact provenance has no fictitious repository/revision. Pack identity is deterministic
over the complete manifest; local paths are not model IDs. Supra's companions remain constrained to
the supported content, but their origin may be a local mirror or an independent URL. Whisper retains
its twelve known content identities. Laya requires its three ONNX/tokenizer/agent-config artifacts
and inspects metadata without creating an inference session. See the
[canonical location/import design](../docs/tensor-model-locations.tech-design.md).

### Repository adapters and compatibility

`HuggingFaceModelSource` implements `ITensorModelSource` for paged repository search, revision
listing and candidate metadata. Candidate lookup pins the returned immutable commit and retains
per-artifact provenance, license, nullable length and nullable SHA-256. A Git blob ID is never used
as a file hash. These calls read bounded JSON metadata only: they neither install/load weights nor
execute repository code. A candidate is not evidence that an engine supports it; use the engine's
resolver to obtain compatibility evidence and a complete installable manifest.

`ListVariantsAsync` lists repository artifact metadata without opening weight bodies.
`ReadMetadataAsync` is explicit bounded inspection (at most 16 MiB per header range, or 4 MiB for an
explicitly requested complete small metadata file). It validates the pinned source identity, range,
total length and response body bounds. A weight server that ignores Range is Unverified: no fallback
whole-weight read occurs. Protected redirect rules also apply to these reads.

The shared `TensorSettingsManager` accepts an engine-owned `ITensorModelResolver`. A supplied repository
adapter adds network-declared repository/revision/variant choices alongside the other locations and
the separate **Inspect model location** action. Only a Supported result with a validated complete
definition becomes selectable/saveable. Source/backend changes clear the resolution; a choice query
never selects a model. Browsing, resolving and Save do not register, install or load.
Install registers the resolved immutable definition in the supplied manager before downloading and
verifying it. Unregistered resolved selections report not installed rather than querying an unknown
manager key. `QwenSetup.CreateSettingsManager` and `SupraSetup.CreateSettingsManager` compose their respective
`QwenModelResolver` and `SupraModelResolver` regardless of whether a repository adapter is supplied.
Construction performs no I/O.

`QwenModelResolver` inspects actual GGUF headers, including every numbered shard, through the same
architecture/tokenizer/tensor/storage policy used by Load. Selecting any shard resolves the complete
set under one canonical identity. `SupraModelResolver` inspects the restricted PyTorch ZIP metadata
of the selected DiT and resolves the pinned Flan-T5 text encoder, SentencePiece tokenizer and VAE
from visible pinned defaults or explicit compatible locations. The encoder/VAE headers must satisfy the shared pipeline
schema. The tokenizer must match the known supported vocabulary's pinned length and SHA-256;
installation verifies its bytes. Same-schema DiT fine-tunes resolve under their own keys; arbitrary
encoder/tokenizer/VAE substitutions are not advertised as supported.

`TensorMetadataReader` bridges the existing format parsers to cached 64 KiB source ranges on a
worker thread. Each format inspection has a 16 MiB parser-read bound and a complete resolution has
a 64 MiB transfer budget. It creates no temporary files, installs no weights and allocates no native
model. Missing metadata, range support, integrity or companions stays Unverified; incompatible
inspected schemas are Unsupported. Supported is not a claim that memory allocation will succeed.

`TensorResolvedPack` owns deterministic manifest identity, including variant, capability, backend
set, integrity and per-artifact source/license provenance. Artifact/source ordering does not change
identity. Exact built-in matches retain their established keys and provenance; different companion
or revision manifests do not reuse those keys. Resolved manifests snapshot their input collections.

The host may supply `TensorSourceAccess` to the source and pack manager. On the unpublished 11.10
branch its return type is `Task<TensorSourceCredential?>`: credential resolution is awaited, not a
notification. The host may return a completed Task for a credential already in memory. It returns a short-lived
credential for the exact requested origin: `TensorSourceCredential.Bearer(token)` or
`TensorSourceCredential.Basic(username, password)`. The credential exposes no secret through a public
member and is attached per request. No credential is stored in a pack definition.
Authentication required, gated access, denied access, not found (possibly private), transient failure
and unverified metadata are distinct source results. Redirects are bounded and authorization is
never forwarded to another origin or reattached after leaving the original origin. Caller-supplied
HTTP clients must disable automatic redirects, cookies/default credentials and default headers;
the default clients already do so. Never persist transient signed download URLs.

`TensorModelCatalog.Register` and the manager's `RegisterAsync` return an immutable snapshot.
Identical registrations are idempotent; a different definition at the same key fails. The catalog is
the one owner read by `TensorModelPackManager.GetDefinitionsAsync` and `GetRequiredDefinitionAsync`,
not a separate discovery cache.
The host persists chosen definitions and reconstructs registrations after restart. Registration,
installation and configuration Save remain separate operations.

Install progress includes per-artifact and overall byte counts, Completed/Failed/Cancelled terminal
states, and `SupportsResume = false`. Duplicate installs serialize and verify existing artifacts.
Active leases prevent removal or repair; interrupted repair invalidates its old install manifest.
Progress observers cannot change the installation outcome. An installed manifest is not a substitute
for content verification; use `VerifyAsync` before treating weights as verified.

## Host-selected Laya packs

Laya follows the same lifecycle: construct the engine with the pack manager, choose the model in
`LoadAsync(key)`, run typed requests, and `UnloadAsync` before loading another compatible pack on
the **same instance**. Stop is still terminal. The constructor does not pin a checkpoint, and neither
construction nor a request downloads or loads one implicitly. Tensor ships no built-in Laya pack;
the host registers the packs it offers. No source copy or additional engine implementation is
required for a compatible fine-tune.

The host constructs `TensorModelPackDefinition` values using
`TensorDecisionModels.LayaRuntimeArchitecture`, capability `Decision`, `CoreMl` and/or `Cpu` backend
declarations, and the correct per-artifact lengths/hashes. A pack that declares `Cuda` fails
compatibility: ONNX CUDA execution is not yet available. The runtime requires `model` (the ONNX
graph), `tokenizer`, and `agent-config` artifact roles. `agent-config` identifies the selected
checkpoint's `rl_agent_config.json`, read before native allocation. Missing configuration fails
compatibility; malformed configuration fails Load without retaining model resources. Load then
checks the graph's `laya.*` metadata and fixed input shapes against the configuration's `max_len`
and fails with `ModelCorrupt` on a mismatch. A request with more options than the graph's option
slots fails with `InvalidRequest`. The architecture label alone is not proof that arbitrary weights
will work. `TensorModelDefinitionValidator.Validate`
is the common metadata validator, also used by the catalog and Laya's family policy.

On Core ML, Laya compiles the graph on every `LoadAsync`; Tensor keeps no compiled-model cache.
For the 1.7 GB fp32 RightSize checkpoint on an M4 Pro, Load takes about 31 s and each question
about 83 ms. A host that needs decisions shortly after start begins `LoadAsync` in the background
and keeps the instance loaded. The measurements and the reasons Tensor keeps no compiled-model cache
are in `docs/laya-onnx-runtime.tech-design.md`.

`max_len` and `head_max_len` control sequence truncation and the head/option budget.
`temperature_by_options` supplies the supported option-count buckets, falling back to that pack's
`temperature[questionType]` when a bucket is absent. Temperatures must be positive and finite;
they are not silently clamped or replaced with another checkpoint's values. Capabilities report
the loaded `max_len`, or null before Load/after Unload. Tokenization explicitly requires ByteLevel
(`add_prefix_space:false`, `trim_offsets:true`, `use_regex:true`), uses the GPT-2 splitting pattern,
and recognizes every added token, including non-special whitespace/placeholder tokens.
The 24 supplied HF golden cases run offline against a pinned tokenizer fixture. Full RightSize
checkpoint probability parity remains an external acceptance check, not a claim made by these tests.

`DecideAsync` truncates a state that is longer than a question's room: the sequence builder keeps
the state tokens that fit before the final `[SEP]` and raises nothing. A caller detects truncation by
measuring first. `LayaDecisionInference` implements `IDecisionInputMeasurement`: `MeasureAsync`
returns the tokens the state encodes to and, per question ID, the most state tokens that question's
sequence keeps. It uses the same tokenizer and framing as `DecideAsync`, runs no forward pass and
acquires no backend, and fails as `DecideAsync` does when no pack is loaded or the model ID differs.
`LayaInputMeter.LoadAsync` builds the same measurement from a pack's `tokenizer` and `agent-config`
artifacts alone, without the `model` artifact. A question truncates the state exactly when
`StateTokens` exceeds its budget.

```csharp
// customPack describes the host's compatible trained checkpoint and its actual artifact sources.
var catalog = new TensorModelCatalog([customPack]);
using var packs = new TensorModelPackManager(catalog, applicationStore, hostHttpClient);
await packs.InstallAsync(customPack.Key, cancellationToken: cancellationToken); // Explicit download.

await using var laya = new LayaDecisionInference(packs); // Auto: Core ML on macOS, CPU elsewhere.
await laya.StartAsync(cancellationToken);
await laya.LoadAsync(customPack.Key, cancellationToken);
DecisionInferenceResult result = await laya.DecideAsync(
    request with { ModelId = customPack.Key.ModelId }, cancellationToken);
await laya.UnloadAsync(); // A later Load may choose a different registered, installed Laya pack.
```

The manager must use a catalog containing the selected definition. An unknown key fails with
`ModelUnavailable`; wrong family/capability or malformed pack metadata fails before native work.
Missing/corrupt weights do not produce a resident model. The request's model ID is checked against
the resident pack while holding its execution lease. Observation, execution coordination and result
provenance use that pack's actual key. Unloaded capabilities describe the family;
they do not assert that a named pack is registered, installed or verified.

`LayaSetup.Definition` advertises the Decision task without activating the engine.
`LayaSetup.CreateSettingsManager(target, packs, offeredModels, instance, source, reader)` feeds the host's
compatible definitions to the existing shared editor. `offeredModels` is required; an empty list
offers no pre-registered selections; local/URL resolution and optional repository browsing remain
available. Validate/register the same definitions in the manager's
catalog. The editor saves the selected key/backend in `TensorSetupConfiguration`, not raw weight
bytes or a provider-specific UI payload. Selection and Save never unload an active model. The usual
explicit install/verify/load/unload/stop actions apply; changing the saved selection reports
`RequiresUnload`, and closing the editor does not stop the host's engine. See the compile-checked
`LayaModelSelectionExample` in the shared examples.

## Speech transcription / Whisper

`WhisperSpeechToTextInference` implements `ITensorSpeechToTextInference`. Create its pack manager
with `TensorSpeechModels.Whisper` (or supported host definitions), explicitly install the chosen
pack, then Start / Load / Transcribe / Unload / Stop. The model factory is created during Load and
stays resident across prompts; per-request processors are disposed before the request completes.
One instance serializes processing, including a whole window-stream enumeration. Start is a no-op;
no host process, microphone service or model-per-request cache is created.

`SpeechAudio` is interleaved float PCM, with a declared sample rate and channel count. Finite samples,
complete frames, 8–192 kHz and 1–8 channels are accepted and normalized to mono 16 kHz using NAudio's
managed WDL resampler. Inputs are not mutated. Empty clips produce empty results, not fabricated text.
`SpeechToTextOptions` carries an optional language code and TranslateToEnglish; null means automatic
detection for multilingual packs or English for English-only packs. Invalid languages and translation
on English-only/turbo packs are rejected before processing. Capabilities distinguish known per-pack
support from unknown custom-pack metadata; the loaded header supplies actual vocabulary capabilities.

Batch results contain text, timestamped segments, nullable detected language and selected-pack
provenance. `TranscribeStreamAsync` accepts caller-identified `SpeechAudioWindow` values and returns
`IAsyncEnumerable<SpeechWindowTranscription>` directly, as Qwen's `StreamAsync` returns its event
sequence directly. Setup and processing remain lazy during enumeration; no extra Task acquisition
or implicit model load is introduced. Enumeration yields
one completed result per window, including empty results. Timestamps stay relative to that window.
There is no overlapping-window deduplication, speaker diarization or continuous partial recognition.
Hosts must dispose abandoned enumerators and their input enumerable must honor cancellation.
Graceful teardown waits for the entire stream; urgent teardown requests cancellation but never
releases a factory/storage lease while native processing still uses it.

All twelve initial variants are declared: tiny/tiny.en, base/base.en, small/small.en,
medium/medium.en, large-v1/v2/v3 and large-v3-turbo. Artifact lengths and SHA-256 values come from
the [publisher's immutable revision](https://huggingface.co/ggerganov/whisper.cpp/tree/5359861c739e955e79d9a303bcbc70fb988958b1).
Only CPU is supplied/advertised. Auto selects CPU; other backend values fail explicitly. The setup's
backend check invokes the real native runtime without loading a model. Definition/model listing
does not load native libraries, probe hardware, select a default pack or download anything.

`WhisperSetup.CreateSettingsManager` uses the existing generic Tensor editor, with all twelve definitions,
explicit install/verify/lifecycle actions and typed `TensorSetupChanges`. Its optional source binds
`WhisperModelResolver`: the initial remote policy verifies published weight identities plus actual
GGML architecture/vocabulary headers. Unknown hashes remain Unverified; tags or filenames alone
cannot produce a compatible pack. The native loader rechecks installed headers and validates the
checkpoint. The initial supported loading formats are standard Whisper F32/F16 GGML; arbitrary
quantized variants or modified architecture dimensions are not advertised. No alternate downloader
is introduced. Language/translation are typed request options, not a library-owned application
settings store.

Whisper.net 1.9.1 requires a path or whole-model buffer, not a stream. Its adapter passes the
installed model's path and holds the file open until factory disposal, releasing it on failed or
cancelled loads. Native processing is not crash-isolated and teardown has no guaranteed deadline.

The [source-only bridge](examples/Darkspyre.Inference.Tensor.STT.Bridge/README.md) is a non-packable
ASP.NET example showing a host's explicit startup/load, PCM request mapping and terminal shutdown.
It binds loopback, requires an already-installed model, and is not an authenticated hosted product
or an OpenAI-compatible API. The generic editor composition example also includes Whisper under
the Audio / SpeechTranscription task without provider-name renderer branches.

## Package dependency boundary

Tensor depends on **Darkspyre.Inference.Common [11.10.5-rc.1]** and the **Darkspyre.TensorSharp.*
2.8.6.6** dependency closure through NuGet. None of their DLLs is embedded in Tensor. TensorSharp
2.8.6.6 loads model files from paths and provides bounded metadata inspection through its GGUF,
safetensors and restricted PyTorch readers. `Darkspyre.TensorSharp.Backends.GGML` ships the macOS
arm64 native bridge (`runtimes/osx-arm64/native/libGgmlOps.dylib`), so Qwen and Supra load on the
GGML CPU and Metal backends on Apple silicon without a native build. Other platforms build the bridge
from a TensorSharp checkout with its `TensorSharp.GGML.Native/build-*` script.

Use the root `NuGet.Config` and process-scoped private-feed credentials for normal restores.
Darkspyre dependencies come from the private feed; Microsoft.ML.OnnxRuntime, Whisper.net, its
runtime and NAudio.Core come from public NuGet. Their own packages own their assemblies/native runtime assets. Tensor contains
only its capability assemblies and their XML documentation, README and license.

TensorSharp's managed package build does not embed GGML/MLX native libraries or certify native
device availability. Explicit backend observations retain the documented available/unknown states;
host native-runtime provisioning and full-weight/device validation are not inferred from package
installation. Whisper's advertised CPU runtime and ONNX Runtime's native library, including its
Core ML execution provider, are supplied by their public package dependencies.
