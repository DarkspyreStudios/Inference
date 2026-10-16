# Darkspyre.Inference.Tensor

Source package version **11.15.1-rc.1** uses the shared typed reasoning-level contract; Qwen supports `None` and `High`.
It includes Laya's CPU/GPU-only Core ML update and retains the ONNX and user-selected
model-location/import contracts introduced in 11.4.0-rc.1.

In-process .NET inference uses TensorSharp, ONNX Runtime and Whisper.net. Each Tensor library is
independently consumable. Libraries target .NET 11 and reference their synchronized Common and
Tensor dependencies. Each package owns one assembly and its XML documentation. Model weights are installed
separately through Tensor's model-pack manager into a pack directory on the local file system;
constructing a runtime or running inference never downloads weights. The pack directory defaults to
`LocalApplicationData/Darkspyre Studios/Inference/Tensor`, and the host can supply another
directory, such as its app folder. Install and Import write every artifact into the pack directory's
own `.tensor-staging` folder, check its length and SHA-256, and rename it into place, so a download
never passes through the system temporary folder or another volume, and never needs twice its size.
An install or import removes staging files untouched for more than an hour, which a stopped process
leaves behind; a newer file may belong to another process's download. Each engine receives the
installed files as plain paths: Qwen and
Supra pass them to TensorSharp, Laya creates its ONNX Runtime session from the model path,
Whisper loads through `WhisperFactory.FromPath`, and the embedding engine loads through
TensorSharp's `EmbeddingModelFactory.Load`. Gemma creates its ONNX text and vision sessions from verified pack paths.

Library boundaries:

- `Darkspyre.Inference.Tensor.Common` — the host-controlled model lifecycle, model packs,
  verification, the local pack directory, execution coordination and all Tensor capability interfaces.
  The capability namespaces remain `.Tensor.LLM`, `.Image`, `.Decision`, `.STT` and `.Embedding`;
  they resolve from the Common assembly without loading a model implementation.
- `Darkspyre.Inference.Tensor.Ggml` — `GgmlRuntimeOwner`, the one owner of the GGML native runtime
  that every engine running on GGML borrows.
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
- `Darkspyre.Inference.Tensor.STT.Whisper` — resident CPU/CUDA Whisper inference through the public
  Whisper.net runtime. Whisper.net, Whisper.net.Runtime, Whisper.net.Runtime.Cuda12.Linux and NAudio.Core remain declared NuGet
  dependencies, not third-party DLLs embedded by the Darkspyre package.
- `Darkspyre.Inference.Tensor.Embedding` — resident text embeddings through TensorSharp's GGUF
  embedding models. Models arrive by import; the resolver accepts a GGUF whose header is a Qwen3
  embedding model (last-token pooling; managed CPU, GGML CPU and Metal) or a BERT embedding model
  (mean, CLS or last-token pooling; managed CPU and GGML CPU, because TensorSharp's Metal encoder
  cannot run it), with 1,024 dimensions and at least an 8,192-token context.
  On Auto and the GGML backends the engine borrows the shared `GgmlRuntimeOwner`, so it runs on the
  backend the process's other GGML engines use; a pack that does not support that backend fails to
  load. The runtime formats queries itself: callers state whether inputs are queries or documents,
  using Common's `TextEmbeddingInput`. Capabilities declare text support; a batch containing
  `MediaEmbeddingInput` fails with `UnsupportedFeature` before the model runs.
  Documents are embedded as given. Qwen3 queries carry an instruction, by default the codebase
  retrieval task; BERT queries take the `query: ` prefix. The runtime counts every input before
  embedding any; an input over the limit fails the request with `EmbeddingInputTooLongException`,
  which names each over-limit input, unless the request permits truncation, in which case the
  result lists the inputs that were cut. A smaller requested size is taken from the vector's leading
  values and scaled back to unit length (Qwen3: 32 to 1,024; BERT: 256 and 1,024). Each result
  carries the model fingerprint, a SHA-256 over the weights, architecture and query format; vectors
  with different fingerprints are not comparable.
  A failed model release retains the model and its artifact/runtime leases. An explicit unload or
  stop can retry release. Concurrent and repeated disposal calls share the original cleanup outcome.
  The owning Embedding tests use verified local packs and a stand-in model loader; they cover load
  cancellation, metadata failure, retained cleanup failure and teardown drain without native execution.
- `Darkspyre.Inference.Tensor.Embedding.Gemma` — FP32 EmbeddingGemma 2 text/image embeddings through
  ONNX Runtime 1.30.0 and its external WebGPU 0.4.0 provider. Auto selects WebGPU; CPU is explicit.
  The pinned pack includes text/vision graphs, their external weights and tokenizer.json. The adapter
  uses TensorSharp's BPE tokenizer and shared Gemma image preprocessing. Queries use
  `task: search result | query: {text}`; documents use `title: {title-or-none} | text: {text}`.
  PNG/JPEG inputs carry caller-owned bytes. The output is 768-dimensional and unit length. The
  fingerprint includes every artifact hash and the formatting/preprocessing/runtime recipe.
  The full batch is admitted before any graph runs. The 8,192-token limit includes formatting and
  BOS/EOS. Explicit truncation retains EOS and reports affected input indices. The graph supplies
  pooling and projection. A cancelled ONNX call is joined before tensors, sessions or pack leases
  are released. Tensor's existing lifecycle serializes calls and drains before unloading.
  OrtEnv owns process-wide WebGPU registration; each engine owns two sessions. Explicit CPU input
  memory uses the Default memory type required by the external WebGPU provider. GPU absence is
  reported as BackendUnavailable. No implicit CPU fallback occurs.
- `Darkspyre.Inference.Tensor.Registry` — `TensorEngineRegistry` and the `AddTensorEngines`
  optional composition of the shipped engines. A caller can reference and construct a single
  engine library without referencing Registry or loading another family. It references the engine assemblies; neither
  `.Tensor.Common` nor an engine assembly references it.

Implementation names identify the model family they support, not a particular checkpoint or training
run. Laya is not a general-purpose ModernBERT engine: a custom checkpoint must retain the Laya
architecture, typed heads and tokenizer conventions, and its ONNX export must match the graph
contract in `docs/laya-onnx-runtime.tech-design.md`. Its sequence
limits and fitted calibration come from the selected pack's agent configuration. Selecting a
fine-tuned checkpoint does not independently establish calibration accuracy for a new distribution.

Source composition callers reference `Darkspyre.Inference.Tensor.Ggml` directly
when a constructor exposes `GgmlRuntimeOwner`, including optional parameters.
Setup Examples and Tensor ConsoleTester declare that reference alongside their
engine and registry references. A transitive implementation reference does not
supply the constructor's compile-time type in this source build.

The existing Tensor ConsoleTester runs `tensor embedding --store <model-store> --backend GgmlMetal`
against the first model the Embedding family reports in that model store. It downloads nothing. The command checks ordered
document/query vectors at 512 dimensions, unit length, matching-document ranking, actual fingerprint
and provenance, and a result round-trip through `InferenceJson.EmbeddingResult`. A loaded GGML model
must report Busy on runtime shutdown. The command disposes the model before terminal runtime cleanup.
This qualification does not exercise over-limit inputs, truncation, native failures or other models.

The console tester command `tensor gemma <cpu|webgpu> <model-directory> <fixture-directory> <output-directory>`
installs the pinned local artifacts, checks reference token IDs, runs text/image inputs both directly and
through Connect, checks retrieval fixtures, rejects an oversized batch, cancels an active request, checks
reuse and unloads. Fixture preparation uses `prepare_engine.py` in the existing Gemma experiment.
Only the tester owns the engine lifecycle. Connect borrows it through `ILlmSelfHost`.

`Tensor/tools/Darkspyre.Inference.Tensor.GemmaExperiment` is an isolated, non-packaged
EmbeddingGemma 2 runtime probe. It references the native suppliers directly and runs outside
Host. Its pinned FP32 ONNX graph consumes frozen token IDs, which separates execution
comparison from tokenizer and retrieval qualification. It records session load time, the first
inference and five subsequent calls, vectors, process peak working set and an ONNX provider
profile. Core ML uses CPU and GPU compute units; WebGPU is a separate ONNX option.
The `webgpu-plugin` option loads Microsoft's packaged WebGPU provider for native cross-platform qualification.
The image mode executes the pinned vision graph and then the main embedding graph using
verified PIL-reference patches and positions. A separate preprocessing check compares the
existing Gemma4 processor with those reference inputs, including patch padding and position order.
The `images-shared` mode feeds the shared processor's pixels and the same patch packing
directly into both ONNX graphs. It retains the frozen-input mode as an execution control.
A separate-context control tests WebGPU cleanup with independent vision/text contexts.
An explicit host-memory diagnostic compares ONNX input binding APIs without changing tensor values.
The qualification summary checks the complete CPU/plugin run matrix, process cleanup and every
embedding, and reports image preprocessing separately. External WebGPU 0.4.0 with explicit
CPU/default input memory passes the measured text/image runs on Mac, Linux and Windows.
The shared RGB bicubic resizer clips and rounds both passes before normalization to match
the PIL reference. The qualification summary keeps preprocessing and encoder agreement separate.
The profile identifies CPU partitions. The TensorSharp option loads the same export's FP32
weights and runs its text forward pass through existing GGML tensor operations on Metal.
Its resident-weight variant packs FP32 matrices once during loading and uses the existing
GGML resident matrix operation. The Mac comparison script records every call's vector
and checks execution parity against ONNX CPU and the preceding measured variant.
The resident-row variant combines the fused path with `GetRowsQuant` using FP32
weights. It avoids materializing the full embedding table before row selection.
The fused variant also uses the existing Gemma norm/projection, feed-forward,
post-norm/residual and PLE graphs while retaining the generic attention path.
The grouped variant adds the existing FP32 grouped attention graph with an all-visible
mask for the probe's bounded inputs and replaces repeated K/V copies with layout conversions.
The `ggml-cpu` and `ggml-cpu-fused` options run the same TensorSharp forward on CPU.
Optional diagnostic modes capture intermediate FP32 tensors or phase timings and managed
allocation counts and inspect runtime ownership growth. An explicit diagnostic control
disables GGML async compute. Host copies use the shared storage barrier. Trace reads synchronize execution and are excluded from performance claims.
Each option runs in its own process. The probe accepts 2–512 tokens, where the full and
sliding attention masks coincide, and does not register an engine in the shipped Tensor catalog.
The [probe instructions](tools/Darkspyre.Inference.Tensor.GemmaExperiment/README.md)
describe reproducible runs and the remaining qualification limits.

The production-library tester covers tokenizer parity, direct/Connect text and image agreement,
retrieval, cancellation, reuse and pack release. Its summary compares CPU and WebGPU outputs
across Mac, Linux and Windows with the frozen CPU image reference. The SelfHost tool fixture
checks the supplied time tool alongside the universal conversation path's built-in tools.

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

Each implementation library publishes its pack definition separately from installation. A desktop
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
The shared lifecycle idle task preserves the latest accepted work's failure after publishing
Failed. Disposal observes that actual cleanup failure; repeated waits do not turn it into success.

Qwen owns stream production on a background task. Its reader receives at most 32 queued events,
matching Connect's managed stream delivery bound. Accepted unload or stop retires delivery and
drains actual generation and enumeration cleanup independently of a paused reader. Graceful
teardown does not cancel generation; urgency cancellation still uses the model lifetime's token.
Reader abandonment cancels its own generation and joins cancellation callbacks before disposing
the runtime enumerator. Cancellation observations do not duplicate the producer's actual failure.
Failed enumeration cleanup fences further inference and retains the refused iterator and model/pack
ownership. Teardown reports the original operation and each independent cleanup error once.
Repeated disposal preserves that outcome without replaying failed stream cleanup.

Direct MLX Qwen models and default probes borrow the same runtime owner's admission fence.
Concurrent models keep separate leases. Unload and Stop release the instance's lease and leave
the shared worker reusable. Explicit runtime shutdown refuses live model/probe/execution leases.
After drain, terminal cleanup clears checked MLX caches and retires the worker through
`MlxBackend.Shutdown`. The owner's Plan and Current remain GGML-specific; they do not describe
an MLX native ABI. Common and Connect contracts stay unchanged.

Laya owns an acquired pack lease and each returned model before checking load cancellation.
Failed load cleanup preserves the load error and the cleanup error. The engine retains resources
whose release fails and refuses replacement loads until explicit Unload releases them. The pack
remains protected from removal until its dependent model has released successfully.

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

## Engine registry

Each engine assembly publishes an `ITensorEngineProvider` (`Darkspyre.Inference.Tensor.Common`):
`QwenEngineProvider`, `SupraEngineProvider`, `LayaEngineProvider`, `WhisperEngineProvider` and
`EmbeddingEngineProvider`.
A provider exposes four operations:

- `Definition` is a `TensorEngineDefinition`. Its `Setup` is the engine's existing setup definition
  (`QwenSetup.Definition` and so on), the only source of implementation ID, display name, tasks and
  prerequisites. Its `ModelStoreKey` names the engine's pack namespace: `Qwen`, `Supra`, `Laya`,
  `Whisper` or `Embedding`. Reading a definition constructs no engine, loads no native library, opens no store,
  reads no credential and makes no network call.
- `BuiltInModels` lists the engine's built-in packs. Whisper has its twelve published variants, Supra
  and EmbeddingGemma have one each, and Qwen, Embedding and Laya have none: their models arrive by
  import. Reading it touches no file.
- `GetInstalledModelsAsync(IPersist modelStore)` returns the models the engine can load from its
  `ModelStoreKey` store in the model store, through `TensorInstalledPacks.ReportAsync`. A pack is
  reported when its `install.json` names artifacts that are all present at their recorded length, and
  the family recognises it: a built-in whose key and artifact hashes match `install.json`, or a pack
  whose `model.json` matches `install.json`, passes the engine's compatibility validator and, where the
  family has a resolver, resolves as Supported from its installed files. The report hashes nothing and
  loads no native code; the lease before a load hashes every artifact. Built-ins come first, then the
  others by key.
- `CreateInstance(packs, backend)` constructs the engine with its own execution coordinator. The
  returned `ITensorModelInstance` also implements the engine's capability interface:
  `ITensorLlmInference`, `ITensorImageInference`, `ITensorDecisionInference` or
  `ITensorSpeechToTextInference`. Creation does not Start, Load or download anything. The engines'
  public constructors remain available for compositions that supply their own coordinator.
- `CreateSettingsManager(target, packs, models, instance, source, reader, backendProbe)` delegates to the
  engine's `*Setup.CreateSettingsManager`. `models` is the host's live `ITensorModelList` of this family's
  models in the model store; the editor follows it. The instance is optional. A supplied instance must
  implement the engine's capability interface and report the provider's implementation ID;
  otherwise the call throws `ArgumentException`. The final optional `ITensorBackendProbe` replaces
  the local probe. The setup factories accept the same optional parameter. The manager borrows the
  probe and invokes it only through the explicit backend-check action. Opening, editing and browsing
  settings never invoke it. An injected probe's failure or cancellation never falls back to a local
  native check. Passing null retains the engine's lazy local probe.

`TensorEngineRegistry` (`Darkspyre.Inference.Tensor.Registry`) takes every registered provider and
reads each definition once. It rejects a missing provider or definition, two providers with the
same implementation ID, and two store keys that are equal ignoring case. `Definitions` is ordered
by `DisplayOrder` and then by ordinal implementation ID. `GetDefinitions(task)` filters that list
by declared task. `TryGetProvider(id)` resolves the exact, case-sensitive implementation ID and
returns false for an unregistered one. The registry holds no pack manager, store, credential or
live instance.

`services.AddTensorEngines()` registers the six shipped providers and the registry as singletons.
A repeated call adds nothing. A host that calls it receives an engine added in a later package
release without its own engine list. A provider registered separately joins the same registry. A
provider whose implementation ID or store key collides with another registration fails when the
registry is resolved; the last registration never wins silently. The registry has no assembly
scanning and loads no plugins.

`TensorEngineDefinition` accepts a store key of one to 64 ASCII letters, digits, `-`, `_` and `.`
that starts with a letter or digit, does not end with `.`, and is not a Windows device name. It
rejects a descriptor whose kind is not Tensor. The host joins a store key beneath its own storage
root and constructs one pack manager per engine kind:

```csharp
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Registry;
using Microsoft.Extensions.DependencyInjection;

TensorEngineRegistry registry = serviceProvider.GetRequiredService<TensorEngineRegistry>();
foreach (TensorEngineDefinition definition in registry.Definitions)
{
    // Render definition.Setup in the host's provider list.
}

if (registry.TryGetProvider(savedConfiguration.ImplementationId, out ITensorEngineProvider? provider))
{
    var modelStore = new OsUserPersist(hostStorageRoot);
    IReadOnlyList<TensorModelPackDefinition> installed = await provider.GetInstalledModelsAsync(modelStore);
    TensorModelListing listing = await TensorInstalledPacks.ListAsync(modelStore, hostStorageRoot,
        provider.Definition.ModelStoreKey, provider.BuiltInModels, installed, savedDefinition);
    var packs = new TensorModelPackManager(new TensorModelCatalog(listing.Models.Select(model => model.Definition)),
        Path.Combine(hostStorageRoot, provider.Definition.ModelStoreKey));
    ITensorModelInstance instance = provider.CreateInstance(packs, savedConfiguration.Backend);
    // A host with a live model store implements ITensorModelList and raises Changed after each new report.
    TensorSettingsManager manager = provider.CreateSettingsManager(commitTarget, packs, TensorModelList.Fixed(listing), instance);
}
// Otherwise the saved entry's engine is not registered: keep the record and show it unavailable.
```

`tensor registry --store <dir>` in the Tensor ConsoleTester lists every registered engine, its built-in
count, each offered model with its install state in that model store (`installed`, `not-downloaded` or
`files-missing (<file>)`), and opens its settings through the same provider calls. It installs, loads and saves nothing.

The Qwen console runner retains the shared runtime owner that its engine borrows. It disposes the
engine before retiring the runtime. Operation, engine cleanup and runtime cleanup failures remain
independent causes. Runtime disposal preserves the supplier's original failure. A failed retirement
is not retried to obtain a status. After successful retirement, the runner prints the owned JSON
shutdown observation from `ShutdownAsync`; an already retired owner reports `AlreadyStopped`.
An unsafe or busy observation and any cleanup failure produce a nonzero process exit. Successful
generation alone does not establish runtime retirement.

The console's direct Qwen and Connect SelfHost tool suites share one parsed JSON schema for
`get_time`. Schema construction does not use reflection serialization. SelfHost borrows the loaded
engine through the managed agent lane. Its schema setup works with JSON reflection defaults disabled;
the supplier's prompt serializers have separate runtime requirements.
`--trace-exceptions` prints first-line exception diagnostics and invalid-operation stacks.
It exposes the throw site of errors a managed turn reports through its safe failure boundary.

## Provider setup

`QwenSetup.Definition` and `SupraSetup.Definition` describe providers without constructing an engine,
opening a store or loading native libraries. Their `CreateSettingsManager` methods bind a typed host
`ISetupCommitTarget<TensorSetupChanges>` and the existing pack manager to a `TensorSettingsManager`.
Its `OpenAsync(identity, existing)` returns the `TensorSettings` object the common setup editor renders.
Every factory takes a required `ITensorModelList`, the host's live list of the family's models in the
model store. `TensorInstalledPacks.ListAsync` builds its `TensorModelListing`: the family's built-ins in
their declared order, then every other model the family reports, each with a `SetupInstallState`.
A reported model is Installed. Any other model is FilesMissing when its `install.json` names an artifact
that is absent or not at its recorded length, and NotDownloaded otherwise. `TensorModelListing.Saved`
carries the edited engine's own saved model when the family does not report it; it is never offered as a
choice and shows only in the status card. The listing holds only built-ins and models that passed the
family's own check in the report, so the factories validate nothing further. `TensorModelList.Fixed`
serves hosts with no live model store, such as the examples and the console. All four factories bind their model-family resolver
for local files/folders and independent URLs. Supply an `ITensorModelSource` through the optional
`source` argument to additionally enable repository browsing. The optional `reader` argument accepts
host-configured artifact access, for example `new TensorArtifactReader(source, httpClient, access)`.

The model choice offers each listed model with a `SetupChoiceInstall` chip: whether it is installed and
its download size. The choice shows its value only while the selected model is Installed
(`SetupChoiceField.ShowValue`). Under it, a `SetupInstallStatusField` ("Install state") carries the
selected model's `SetupInstallStatus` card. An Installed model offers **Delete download**, enabled only
while the instance holds no model; otherwise its reason reads "Unload the model first." A NotDownloaded
model offers **Download (<size>)**. A FilesMissing model names the missing file and offers **Download
again** and **Delete download**. Download and Download again run the `download` action: register,
install, verify and refresh; a FilesMissing pack is downloaded again in full. Delete download runs the
`remove` action: it registers the definition, removes the pack's files and refreshes. The pack's
contents, files and provenance fold away under "Model definition (what the pack contains, not whether
it is downloaded)", with sizes in readable units (`SetupByteSize.Format`). The fields that bring in a
model from a file, folder, URL or repository, with their companions, inspection results and extra
shards, share one advanced section, "Use a model from somewhere else". The backend field is labelled
"Runs on" and names each backend: Automatic, CPU, Apple GPU, NVIDIA GPU, Vulkan GPU, Apple MLX, Core ML
or WebGPU. The `verify` action reads **Check files** and `load` reads **Load now**. Qwen and
EmbeddingGemma carry their own logo in `InferenceSetupDefinition.Icon`, Whisper the shared OpenAI mark;
all three list in the "Run on this computer" group. Every `SetupStatus` observation of Tensor settings
is not applicable; the card shows install state.

Selection/Save does not install or load. Download, remove, verify, load, unload and stop are distinct callable
actions with declared side effects. Load, Unload and Stop return once the engine accepts the request;
the outcome arrives through `EngineStatusChanged`. The supplied instance is optional and remains
host-owned. `OpenAsync` reads the model list and the instance's state through `GetObservationAsync`.
The manager implements `IInferenceSettingsStatusSource`: while a listener is attached to `StatusChanged`,
it relays each `ITensorModelList.Changed` and `EngineStatusChanged` event, and `RefreshStatusAsync`
reads the new listing and observation into the settings. A backend name is a supported selection, not proof
that the backend is available. The explicit **Check backend prerequisites** action records available,
unavailable or unknown runtime/device prerequisites without installing or loading a model. Qwen uses
the shared runtime owner's Auto policy: Metal then GGML CPU on macOS; CUDA, Vulkan, then GGML CPU
on supported non-macOS targets, excluding CUDA on Windows ARM64. Supra Auto can fall back
to managed CPU. Laya Auto selects Core ML on macOS and CPU elsewhere. Qwen's GGML check resolves
the bound candidate plan and initializes its one backend; it never loads model weights. MLX checks that its native library is present, direct
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
These setup contracts use the synchronized source package version 11.15.1-rc.1, including
provider-owned dirty state and explicit choice capabilities for the shared Settings editor.

`TensorJson` exports assembly-owned `JsonTypeInfo<T>` for `SetupConfiguration`, `SetupChanges`,
`ModelPack`, `PackStatus`, `ModelObservation`, `BackendProbeRequest` and `BackendAvailability`. Consumers pass these typed metadata properties to
`JsonSerializer` when reading or writing Tensor-owned data. The metadata belongs to the Tensor
assembly generation; consumers release their references when that generation retires.
Each getter returns metadata with its own options and type cache, released with the caller's metadata.
The pack manager uses the same source-generated context for installed manifests.
Each install writes the pack's definition as `model.json` beside `install.json` with `ModelPack` metadata; a removal deletes it. Manifest fingerprinting
also uses generated metadata and retains the established canonical bytes. These formats retain
their property names, numeric enum values and null/empty distinctions. Unknown properties are
ignored; model validation remains separate from JSON parsing. Serializer tests load the real Tensor
assembly into a collectible context and verify collection after each owned family retires.
Controlled caller tests also run canonical fingerprinting and the real pack manager in a foreign
generation. Tiny local fixtures exercise installation, asynchronous manifest writes and reads,
verification, artifact lease release, removal and disposal. Exact manifest bytes retain artifact
order and hashes. Malformed JSON, null manifests, missing/null artifact collections, null artifact
entries and mismatched identities report Invalid; unknown manifest properties remain ignored.
The settled caller graph retires after owned metadata and
operation locals are released. These tests execute no native runtime and download no models.

`ITensorBackendProbe.CheckAsync` takes a `TensorBackendProbeRequest`. The request carries an operation
ID, implementation ID, requested backend, target RID, optional exact TensorSharp build identity and
an ordered native candidate list. `TensorSharpBuildIdentity` keeps the package version, native ABI
hash and pinned GGML commit separate. Each candidate carries its own ID, absolute bridge path,
variant, build and RID. A bound probe supplies its trusted runtime plan. Settings never supply
executable paths; the manager sends its implementation/backend identity and a fresh operation ID.
Null candidates select ordinary package probing; an empty candidate list never falls through to
ambient native discovery. The local defaults return an explicit Unknown observation for a nonempty
native plan they cannot execute, instead of treating it as permission to probe an ambient runtime.

`TensorBackendAvailability.State` is Unknown, Unsupported, Unavailable, Available or
RequiresProcessRestart. `IsAvailable` derives from State. Detected device capabilities, candidate
presence and successful initialization are separate observations. `ActualBackend` identifies a
successfully initialized runtime, not a suggested selection. Prerequisite checks leave actual
backend, initialization and loaded-candidate identity unknown. Diagnostics preserve candidate
order and report phase, safe code/message and possible surviving native state.
Qwen's direct-native probe reports Unknown when a runtime query fails. A TensorSharp-recorded
unsafe cleanup failure reports RequiresProcessRestart with a possible-surviving-state diagnostic.
Neither result becomes missing-driver evidence or triggers another backend attempt. The settings manager
rejects a result for another operation, implementation, requested backend or target RID. RID matching
is exact and ordinal. Each settings draft reserves its own backend checks. Only the latest check
can publish evidence, and every accepted draft edit invalidates outstanding checks, including an
edit back to the original value. A superseded valid result returns `Unavailable` without replacing
current evidence. Cancellation and source errors retain the manager's existing result semantics;
they do not allow an older check to publish. Separate drafts do not fence each other. Restart-required and
unsupported results are not retryable. No observation guarantees future model allocation.

The controlled collectible settings test loads the real registry, Qwen provider, Tensor settings
and Common identities in one foreign generation. It settles injected checks and a settings save,
round-trips saved changes through owned metadata, then releases the graph and verifies collection.
This test does not initialize a native runtime or qualify an active model/session generation.

`ITensorRuntimeOwner` declares generation runtime control alongside `ITensorBackendProbe`. It exposes
the immutable `TensorRuntimePlan`, the runtime state, the nullable actual backend identity and a
structured shutdown observation. `GgmlRuntimeOwner.GetOrCreate(plan)`, in
`Darkspyre.Inference.Tensor.Ggml`, binds one assembly-generation owner of the GGML native runtime.
Equal plans join it; conflicting plans refuse, including after terminal shutdown. Every engine that runs
on GGML borrows that same owner: Qwen engines, model resolvers and settings, and the embedding engine.
A loaded model holds one lease and enters the owner for each call. Each engine's probe checks its own
implementation before it asks the owner about a backend. Constructing or serializing a plan never resolves a
package catalog, inspects files, initializes a backend or establishes availability.

`AddTensorEngines(services, ggmlRuntimePlan)` carries the optional immutable logical plan into its
lazy GGML owner factory. A factory-created owner is container-owned; an explicitly registered owner
instance remains borrowed. Model instances release their own leases and never shut down the shared
runtime. Stop/dispose instances before terminal runtime shutdown.
Dispose a service provider that resolves these registrations asynchronously with `await using` or
`DisposeAsync`. Its factory-created GGML runtime owner has an asynchronous terminal lifetime.

Qwen consumes the TensorSharp 2.8.6.9 managed loader APIs through ordinary PackageReferences.
GGML is a direct Qwen and Ggml library dependency. Source restore and compilation do not substitute
supplier project references. The actual private-generation test composes Qwen with the package's
Chat/Models/GGML/Core/Runtime assemblies, checks an explicit empty plan, and retires owned metadata
and zero-handle runtime state. It does not initialize a backend, resolve a catalog or load a model.

Null candidates resolve the supplier's finite deployed package catalog once at the first admitted
GGML check/load. Empty candidates admit nothing and never resolve or load ambient libraries. Auto
considers compatible concrete backend groups in priority order. It stops fallback after any possibly
retained native state. Actual backend, variant, build, ABI and native source commit come from successful
supplier initialization, not the requested backend or current managed source checkout.
Initialization exceptions remain private to the owner. Model admission/load failures retain the
original exception as an inner cause; public probe reasons and diagnostics remain sanitized.
Managed owner tests verify that later restart-required refusals retain the same initialization
cause without serializing it into the probe observation.
An initialization `OutOfMemoryException` escapes unchanged. The owner retains that same cause
for later restart-required admission and execution refusals without retrying initialization.

Initialization, queued model loading, resident models and execution retain owner leases until their
actual work/cleanup settles. Ordinary shutdown with active leases reports Busy without teardown.
Disposal synchronously fences new work and reports Busy/failure visibly. Existing instances retain
release-only cleanup access; explicit terminal shutdown can finish after they drain. Concurrent
shutdown/disposal calls share cleanup and failure. A cancelled shutdown wait does not cancel cleanup
or reopen admission. Supplier refusals without structured safe-Busy proof report FailedRequiresProcessExit;
no diagnostic text is parsed to invent counts or recoverability.
Managed owner tests hold supplier cleanup pending to verify cancelled-wait isolation and shared
completion. They verify explicit safe-Busy retry through the injected supplier without native execution.
Supplier shutdown exceptions remain private to the owner. Disposal preserves the actual exception
as its inner cause, and repeated disposal shares that failure without repeating terminal cleanup.
Structured shutdown observations retain their safe diagnostics and do not serialize that exception.
Cleanup allocation failures use that same terminal outcome. They do not skip the other admitted
terminal action or erase an earlier failure. Disposal preserves each actual cause without replay.
An initialized supplier that remains Ready after its guard refuses teardown reports Busy with zero
owner leases and possible external native resources. Its control is retained for explicit retry after
those resources drain; the admission fence stays closed. Unknown or poisoned refusals remain terminal.

If acquisition cancellation is followed by model-lease cleanup failure, the runtime owner retains
that exact cleanup handle and both failures remain visible. It does not count failed cleanup as
release or invoke native shutdown over it. Explicit shutdown may retry only that held release;
failure reports FailedRequiresProcessExit with the outstanding ownership, while successful release
permits guarded terminal shutdown. Disposal preserves a failed held release as its inner cause;
independent held-release failures remain together in an aggregate. The structured shutdown result
does not serialize those exceptions. Each private shutdown completion binds its result to its cause,
so a later explicit release retry does not replace the cause awaited by disposal.
An allocation failure from a held lease's release retains the lease and reports the same failed
shutdown observation. It does not enter supplier teardown while the lease remains outstanding.
Drained terminal owners drop supplier/catalog task references
without disposing borrowed collaborators.

A failed model load/unload that cannot complete cleanup keeps its pack/runtime ownership. New loading
or execution refuses until release-only cleanup succeeds, rather than replacing those held leases.
Qwen stops new operational admissions synchronously and shares the pending cleanup attempt. Stop still
returns admission and publishes Stopped/Failed through the existing lifecycle; a completed failed Stop
can be explicitly retried for release only. Dispose waits for actual cleanup and preserves its shared
failure for repeated callers, even if a later explicit Stop successfully releases the resources.

The embedding engine receives a loaded model before checking cancellation or pack metadata. Failed
model release retains that model and its dependent runtime/artifact leases. After model release,
runtime and artifact lease cleanup proceeds independently and preserves each failure. Failed cleanup
refuses another load or execution. Explicit Stop retries only release work; Dispose shares the actual
terminal cleanup outcome, including failure. A pack lease invokes its release callback once and shares
that outcome with later disposal calls; a failed callback does not become a successful no-op.

Plans deep-snapshot ordered GGML candidates and their file declarations. Each candidate has a
distinct ID, exact build/RID and concrete GGML backend. Null candidates retain the standard-package
policy; an empty list admits no candidates. File declarations require nonempty portable relative
paths, nonnegative sizes and exact lowercase SHA-256 hashes. Constructor checks are lexical/data
checks only. These checks do not prove actual links, bytes, trusted package provenance or exact
entry-library binding to the complete file closure. A nonempty declaration is not execution evidence.
The four runtime records retain positional construction, named arguments, deconstruction and `with`
copies. Validated init accessors reject invalid copies and snapshot nested builds and diagnostics.
Owned JSON requires every mandatory runtime fact, including zero sizes, observation timestamps,
shutdown counts/state/flags and an explicitly nullable actual candidate ID. Missing facts refuse;
they do not become zero, false, Completed or a default timestamp. Plan candidates remain optional.
The two runtime-state enums retain their numeric wire values. Their owned JSON readers and writers
reject undefined values, strings, nulls and numbers outside the Int32 range.
The finite converters belong to `TensorJson` options, not attributes on public enum declarations.
Caller-owned generated DTO metadata can compose those actual converter instances from the owned
enum metadata options. This composition leaves the established `TensorBackendKind` data policy unchanged.

`TensorRuntimeShutdownResult` snapshots its ordered safe diagnostics and carries the actual
observation time, operation ID, outstanding owner leases and possible surviving native state.
Completed, AlreadyStopped, Busy and FailedRequiresProcessExit remain distinct observations.
The control contract cancels the caller's shutdown wait, not shared terminal cleanup. No native
lease, handle or runtime-owner object has a JSON root. `TensorJson` exports the eight exact
`TensorRuntime*` data/enum getters through its assembly-owned generated metadata. Controlled tests
round-trip all eight through the real collectible Tensor.Common generation and verify collection
after the metadata and operation locals retire; these tests perform no native execution.

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
metadata is available. A split GGUF pack
installs every declared shard beside the primary file under its numbered name, and Qwen passes
TensorSharp the primary path; TensorSharp opens the sibling shards from the same directory. Shard
count/names/numbering and duplicate tensors are checked before native allocation.

**Capability addition (11.14.0-rc.1):** Qwen's Common `LlmCapabilities.OutputLimitEnforcement` is
`EngineEnforced`, describing its existing `MaxOutputTokens` runtime limit. This is separate from
nullable discovered model limits and does not add a Tensor accounting/compaction service.

Qwen follows the chat protocol TensorSharp registers for the loaded architecture. That protocol's
output parser separates the answer, the thinking and the tool calls, which stream as
`LlmTextDeltaEvent`, `LlmReasoningDeltaEvent` and the tool-call started, arguments and completed
events. `SupportsToolUse` is `Supported`: request tools pass to TensorSharp, and history messages carry
their tool calls and text tool results. `ToolChoice` and `AllowParallelToolCalls` fail as unsupported
features, and a binary tool-result part fails as well. `ReasoningLevels` is `None` (thinking off) and
`High` (thinking on). With structured output and thinking on, the schema's grammar starts after the
thinking block. Image input is off.

Qwen's hosted-tool capability set is empty. Supplied `HostedTools` or `MaxHostedTurns` fails as
`UnsupportedFeature` before model/runtime materialization; client functions remain a separate lane.

Qwen uses assembly-owned generated metadata for its tool definitions and argument conversion.
Parsed arguments remain `JsonElement` values or null. The owned metadata also preserves the
supplier's CLR strings, string arrays, numeric/boolean primitives and nested JSON-value
object arrays, lists and dictionaries. Arbitrary CLR model objects are not a tool-argument wire
contract. Tool schemas retain their exact JSON; a missing description becomes an empty string.
The tool-conversion path does not use a default reflection serializer. Its collectible tests cover
Qwen-owned metadata, not the supplier's chat parser or a loaded native runtime generation. The
collectible tool test calls the real Qwen writer with Common tool definitions loaded in the same
foreign generation and checks their assembly provenance and exact serialized schema order.

Qwen rejects referenced prompt files, workspace tool-output resources and non-null request resource
authority before model access or native execution. It does not materialize those inputs and reports
`SupportsFileReferences = InferenceSupport.Unsupported`.

Qwen reports `SupportsStreamingUsage = InferenceSupport.Supported` because streamed completions report final usage.
Qwen requires the runtime's terminal update before publishing a completion. A stream that ends
without that update fails as `ExecutionFailed`; it does not invent successful completion or zero
usage. Cancellation is checked before final parsing and completion publication on every backend,
including managed CPU. A cancelled terminal update does not become a successful result.
Natural `eos`, `stop` and `stop_sequence` stops complete. Output/thinking budgets, repetition
protection and engine abort/cancel stops report `Incomplete` with the actual reason; `max_tokens`
retains the normalized `max_output_tokens` incomplete reason. Runtime `error` stops report `Failed`.
Unrecognized terminal reasons report `Unknown`, not `Completed`.
An unfinished stream attempts request cancellation and runtime-iterator disposal independently.
A failed cancellation callback does not skip iterator disposal. If cleanup also fails after a
stream operation fails, the thrown aggregate retains the original operation and actual cleanup
exceptions. A single cleanup failure is rethrown directly when no operation failed.
Qwen reports no automatic prompt caching, cache-hint acceptance or supported cache retentions in
`LlmCapabilities.PromptCache`. It ignores the advisory `LlmInferenceRequest.PromptCache` hint.
Qwen reports `SupportsExplicitCache=InferenceSupport.Unsupported` and rejects a supplied `ExplicitCache` selection
before model materialization or native entry. Provider-native cache receipts do not transfer
to the local engine.
Qwen rejects non-null `ProviderControls` before model materialization or native entry; it does not
interpret another implementation's provider-specific controls.
Qwen supports top-p, top-k, seed, presence and frequency penalties, and up to 16 non-empty stop
strings. The Common request maps directly to TensorSharp's `SamplingConfig`, including requests
without a structured-output grammar. Top-p, top-k and seed require a positive temperature; null
or zero temperature decodes greedily. Stops and penalties remain effective with greedy decoding.
Numeric controls must be finite and representable by the engine's float fields; penalty values
have no adapter-imposed [-2, 2] limit. Seed does not guarantee identical output across backends.
Qwen reports `SupportsReasoningText = InferenceSupport.Supported` because its registered output parser emits thinking
separately from the answer. Other engines keep the false capability default unless they implement
that output. Running usage remains off for Tensor engines.

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

Local artifact sources use explicit absolute file URIs across settings and engine-wire serialization.
An installed pack reopens from its managed artifacts without requiring the original source file.
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

The host may supply `TensorSourceAccess` to the source and pack manager. Its return type is
`Task<TensorSourceCredential?>`: credential resolution is awaited, not a
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
states, and `SupportsResume = InferenceSupport.Unsupported`. Duplicate installs serialize and verify existing artifacts.
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
`LayaSetup.CreateSettingsManager(target, packs, models, instance, source, reader)` feeds the host's
model list to the existing shared editor. Laya has no built-in pack, so a list with no reported model
offers no selections; local/URL resolution and optional repository browsing remain available. The editor saves the selected key/backend in `TensorSetupConfiguration`, not raw weight
bytes or a provider-specific UI payload. Selection and Save never unload an active model. The usual
explicit download/verify/load/unload/stop actions apply; changing the saved selection reports
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
The packs advertise CPU and GGML CUDA execution. Auto prefers the published Whisper.net CUDA 12
runtime and permits CPU fallback. Explicit Cpu disables GPU execution. The optional constructor
`gpuDevice` argument selects the zero-based GPU in the process's visible GPU list. The native runtime
is process-wide and remains loaded; a CPU library loaded earlier remains usable by Auto. Settings
probes and model loading share one serialized initialization path. This path sets the runtime order
to CUDA 12 followed by CPU before the first native call. The CUDA 12 Linux package supplies the
published binaries; TensorSharp CUDA payloads do not supply Whisper's native runtime.

Native initialization observations identify the selected CUDA backend or CPU fallback. Loaded
status, model observations and transcription provenance report that observed backend. A callable
runtime does not establish that a selected GPU can allocate a model. The setup's backend check
reports this distinction without loading a model. Auto makes at most one additional CPU allocation
attempt when GPU allocation cannot create a usable factory. A successful CPU factory stays resident
across requests. The adapter does not retry allocation for each transcription request. Definition/model listing
does not load native libraries, probe hardware, select a default pack or download anything.

`WhisperSetup.CreateSettingsManager` uses the existing generic Tensor editor over the host's model list,
which offers all twelve built-in definitions, with explicit download/verify/lifecycle actions and typed `TensorSetupChanges`. Its optional source binds
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

Tensor depends on **Darkspyre.Inference.Common [11.15.1-rc.1]** and the **Darkspyre.TensorSharp.*
2.8.6.9** dependency closure through NuGet. None of their DLLs is embedded in Tensor. TensorSharp
loads model files from paths and provides bounded metadata inspection through its GGUF,
safetensors and restricted PyTorch readers. `Darkspyre.TensorSharp.Backends.GGML` is managed-only.
Its restore graph does not select a GGML native package. Consumers supply compatible native
packages or an explicit runtime candidate plan before an admitted backend check or model load.
An empty candidate plan never falls through to ambient probing. Package presence does not establish
backend availability or successful model allocation.

For the macOS ARM64 engine, explicitly reference
`Darkspyre.TensorSharp.Backends.GGML.Native.osx-arm64` version **2.8.6.9** alongside Tensor.
The native 2.8.6.8 package's catalog does not satisfy this managed wrapper's contract.
The managed Tensor libraries do not select this native dependency. Local archives are not a claim
of private-feed publication, native execution or full-model qualification.

Use the root `NuGet.Config` and process-scoped private-feed credentials for normal restores when the
required package version is available on the feed. A local archive source for verification is an
explicit separate restore configuration, not a permanent shipping-source redirect.
Darkspyre dependencies come from the private feed; Microsoft.ML.OnnxRuntime, Whisper.net, its
runtime and NAudio.Core come from public NuGet. Their own packages own their assemblies/native runtime assets. Tensor contains
only its capability, engine and registry assemblies and their XML documentation, README and license.
The registry's dependency-injection abstractions come from the .NET shared framework.

TensorSharp's managed package build does not embed GGML/MLX native libraries or certify native
device availability. Explicit backend observations retain the documented available/unknown states;
host native-runtime provisioning and full-weight/device validation are not inferred from package
installation. Whisper's CPU/CUDA runtime assets and ONNX Runtime's native library, including its
Core ML execution provider, are supplied by their public package dependencies.
The CUDA dependency retains its committed PTX payload; disabling native builds does not remove that
payload. Tensor's archive boundary excludes those foreign shader files. The strict
`VerifyRuntimeInventory --require-managed-ggml` gate checks actual restored GGML package identities
and payloads while retaining the separate Whisper, ONNX, image-runtime and GPU asset inventory.
