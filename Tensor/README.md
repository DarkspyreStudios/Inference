# Darkspyre.Inference.Tensor

In-process, first-class .NET inference built on TensorSharp and distributed as the
`Darkspyre.Inference.Tensor` NuGet. The current package targets .NET 11 and references the synchronized
`Darkspyre.Inference.Common` NuGet rather than embedding Common. Model weights are installed
separately through Tensor's
model-pack manager into the `IPersistenceStore` supplied by the application; constructing a runtime
or running inference never downloads weights. The optional `TensorDirectFilePersistStore` is the
filesystem default and still exposes only `IPersistenceStore`. TensorSharp uses a returned
`FileStream` in place and temporarily projects non-file store streams when a native loader needs a
path.

Assemblies included in the Tensor NuGet:

- `Darkspyre.Inference.Tensor.Common` — the host-controlled model lifecycle, model packs,
  verification, direct-file persistence, and execution coordination.
- `Darkspyre.Inference.Tensor.LLM`, `.Image`, and `.Decision` — interchangeable capability
  interfaces using the typed requests and results from `Darkspyre.Inference.Common`.
- `Darkspyre.Inference.Tensor.LLM.Qwen` — Qwen-compatible GGUF text inference.
- `Darkspyre.Inference.Tensor.Image.Supra` — Supra2-IMG text-to-image inference. It runs the pinned
  Supra DiT, Flan-T5-Base encoder, and Stable Diffusion VAE decoder directly through TensorSharp on
  CPU or CUDA. It returns PNG bytes through the shared image result. The current model supports
  256x256 output, deterministic seeds, Euler step counts, and classifier-free guidance; it does not
  support negative prompts.
- `Darkspyre.Inference.Tensor.Decision.Laya` — Laya-family typed decision inference, including the
  built-in `convaiinnovations/laya-typed-decisions` safetensors checkpoint. Its BPE tokenizer, ModernBERT
  encoder, typed decision heads, and calibration execute directly through TensorSharp on CPU or
  CUDA; neither PyTorch nor a Python runtime is used.

Implementation names identify the model family they support, not a particular checkpoint or training
run. Laya is not a general-purpose ModernBERT engine: a custom checkpoint must retain the supported
Laya tensor schema, encoder dimensions/layers, typed heads and tokenizer conventions. Its existing
input formatting and confidence calibration remain unchanged. Selecting a fine-tuned checkpoint
does not independently establish that calibration is accurate for the new training distribution.

Supra's publisher supplies the DiT as a PyTorch ZIP state dictionary rather than safetensors. The
Darkspyre TensorSharp fork reads that file as data through a whitelist-only checkpoint reader. It
accepts only the tensor rebuild, storage, and container forms required by the pinned checkpoint and
rejects arbitrary pickle globals and reducers. Flan-T5 and the VAE use their pinned safetensors
artifacts. No Python or PyTorch component is loaded or executed.

Tensor uses `Darkspyre.ML.Tokenizers` for the focused SentencePiece correction needed by Flan-T5
models whose BOS token is intentionally disabled. TensorSharp 2.8.6.3 supplies the direct runtime,
including rank-four channels-last image group normalization. These fork packages retain the upstream
assemblies and namespaces; consuming code remains first-class C# and does not load Python or PyTorch.

Each capability assembly publishes its pack definition separately from installation. A desktop
application composes the packs it supports into a `TensorModelCatalog`, constructs one
`TensorModelPackManager` with its own `IPersistenceStore`, and explicitly calls `InstallAsync` when
the user chooses to download a model. Inference only acquires verified packs and never initiates a
download.

## Host-controlled lifecycle

The application starts and shuts down independently of this library. For a selected Tensor engine,
the host calls `StartAsync`, `LoadAsync`, the capability's typed inference method as many times as
needed, `UnloadAsync`, and `StopAsync`. `StartAsync` can be a no-op when an engine has no resources
independent of its loaded model. `LoadAsync` accepts a registered `TensorModelKey` and attempts to
load its installed pack. Running a request never loads a model implicitly. One engine instance holds
one model at a time; after Unload, the host may load another model supported by that engine or use
another instance. Separate instances can hold models concurrently, subject to available resources.
The host serializes lifecycle calls (`LoadAsync`, `UnloadAsync`, and `StopAsync`) on a single instance;
requests can run concurrently with a host-initiated unload or stop.

`StopAsync` unloads any remaining model and releases the engine's other resources. Stop is terminal
for that instance: create a new instance to run again. This is a host usage contract, not an extra
state check added solely to reject a second Start call. `DisposeAsync` performs normal Stop cleanup.

The following example assumes the user has already installed the Supra model pack through the
explicit `TensorModelPackManager.InstallAsync` action:

```csharp
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Image;
using Darkspyre.Inference.Tensor.Image.Supra;

// applicationStore is the host's IPersistenceStore (or omit it to use the file-store default).
TensorModelPackDefinition supra = TensorImageModels.Supra2Image;
using var packs = new TensorModelPackManager(new TensorModelCatalog([supra]), applicationStore);
await using ITensorImageInference image = new SupraImageInference(packs);

await image.StartAsync();
try
{
    await image.LoadAsync(supra.Key);
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

## Provider setup (unreleased development)

`QwenSetup.Definition` and `SupraSetup.Definition` describe providers without constructing an engine,
opening a store or loading native libraries. Their `CreateSession` methods bind a typed host
`ISetupCommitTarget<TensorSetupChanges>` and the existing pack manager to the common setup editor.
The current setup list contains the built-in supported pack for each engine; remote model discovery
and compatible selection beyond those definitions remain pending in the approved implementation.

Selection/Save does not install or load. Install, verify, load, unload and stop are distinct callable
actions with declared side effects. The supplied instance is optional and remains host-owned. Its
`Observation` is a read-only view published by `TensorModelLifetime`, not a parallel setup cache.
Without an instance, Loaded/Ready remain unknown. A backend name is a supported selection, not proof
that the backend is available. Loading requires an instance configured for the selected backend;
the editor does not silently replace instances or interrupt application work.

`TensorSetupChanges.RequiresUnload` tells the host when a saved model/backend edit changes the
previous configuration. Saving does not perform that unload. Stop remains terminal, and an
already-cancelled teardown action token reaches the engine as urgency rather than skipping cleanup.
See the [shared editor example](../Common/examples/Darkspyre.Inference.Setup.Examples/README.md).
These source changes have not been published and do not change the development version.

## Model source metadata and registration (unreleased development)

`HuggingFaceModelSource` implements `ITensorModelSource` for paged repository search, revision
listing and candidate metadata. Candidate lookup pins the returned immutable commit and retains
per-artifact provenance, license, nullable length and nullable SHA-256. A Git blob ID is never used
as a file hash. These calls read bounded JSON metadata only: they neither install/load weights nor
execute repository code. A candidate is not evidence that an engine supports it; engine-specific
resolution and remote setup selection are still pending in the approved implementation.

The host may supply `TensorSourceAccess` to the source and pack manager. It returns a short-lived
bearer credential for the exact requested origin. No credential is stored in a pack definition.
Authentication required, gated access, denied access, not found (possibly private), transient failure
and unverified metadata are distinct source results. Redirects are bounded and authorization is
never forwarded to another origin or reattached after leaving the original origin. Caller-supplied
HTTP clients must disable automatic redirects, cookies/default credentials and default headers;
the default clients already do so. Never persist transient signed download URLs.

`TensorModelCatalog.Register` and the manager's forwarding `Register` return an immutable snapshot.
Identical registrations are idempotent; a different definition at the same key fails. The catalog is
the one owner observed by `TensorModelPackManager.Definitions`, not a separate discovery cache.
The host persists chosen definitions and reconstructs registrations after restart. Registration,
installation and configuration Save remain separate operations.

Install progress includes per-artifact and overall byte counts, Completed/Failed/Cancelled terminal
states, and `SupportsResume = false`. Duplicate installs serialize and verify existing artifacts.
Active leases prevent removal or repair; interrupted repair invalidates its old install manifest.
Progress observers cannot change the installation outcome. An installed manifest is not a substitute
for content verification; use `VerifyAsync` before treating weights as verified.

## Host-selected Laya packs (unreleased development)

Laya follows the same lifecycle: construct the engine with the pack manager, choose the model in
`LoadAsync(key)`, run typed requests, and `UnloadAsync` before loading another compatible pack on
the **same instance**. Stop is still terminal. The constructor does not pin a checkpoint, and neither
construction nor a request downloads or loads one implicitly. The built-in pack remains available;
no source copy or additional engine implementation is required for a compatible fine-tune.

The host constructs `TensorModelPackDefinition` values using
`TensorDecisionModels.LayaRuntimeArchitecture`, capability `Decision`, CPU/CUDA backend declarations,
and the correct per-artifact lengths/hashes. The runtime requires `model` and `tokenizer` artifact
roles. It continues to validate actual tensors/tokenizer data during native loading; the architecture
label alone is not proof that arbitrary weights will work. `TensorModelDefinitionValidator.Validate`
is the common metadata validator, also used by the catalog and Laya's family policy.

```csharp
// customPack describes the host's compatible trained checkpoint and its actual artifact sources.
var catalog = new TensorModelCatalog([TensorDecisionModels.LayaTypedDecisions, customPack]);
using var packs = new TensorModelPackManager(catalog, applicationStore, hostHttpClient);
await packs.InstallAsync(customPack.Key, cancellationToken: cancellationToken); // Explicit download.

await using var laya = new LayaDecisionInference(packs, TensorBackendKind.Cpu);
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
provenance use that pack's actual key, not the built-in key. Unloaded capabilities describe the family;
they do not assert that a named pack is registered, installed or verified.

`LayaSetup.Definition` advertises the Decision task without activating the engine.
`LayaSetup.CreateSession(identity, target, packs, offeredModels, existing, instance)` feeds the host's
compatible definitions to the existing shared editor. Omit `offeredModels` to offer the built-in
pack; pass an empty list to offer none. Validate/register the same definitions in the manager's
catalog. The editor saves the selected key/backend in `TensorSetupConfiguration`, not raw weight
bytes or a provider-specific UI payload. Selection and Save never unload an active model. The usual
explicit install/verify/load/unload/stop actions apply; changing the saved selection reports
`RequiresUnload`, and session disposal does not stop the host's engine. See the compile-checked
`LayaModelSelectionExample` in the shared examples.
