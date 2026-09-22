# Darkspyre.Inference.Tensor

In-process, first-class .NET inference built on TensorSharp and distributed as the
`Darkspyre.Inference.Tensor` NuGet. It references the synchronized
`Darkspyre.Inference.Common` NuGet rather than embedding Common. Model weights are installed
separately through Tensor's
model-pack manager into the `IPersistenceStore` supplied by the application; constructing a runtime
or running inference never downloads weights. The optional `TensorDirectFilePersistStore` is the
filesystem default and still exposes only `IPersistenceStore`. TensorSharp uses a returned
`FileStream` in place and temporarily projects non-file store streams when a native loader needs a
path.

Assemblies included in the Tensor package:

- `Darkspyre.Inference.Tensor.Common` — model packs, verification, direct-file persistence, and
  execution coordination.
- `Darkspyre.Inference.Tensor.LLM` — Qwen-compatible GGUF text inference.
- `Darkspyre.Inference.Tensor.Image` — Supra2-IMG text-to-image inference. The assembly runs the
  pinned Supra DiT, Flan-T5-Base encoder, and Stable Diffusion VAE decoder directly through
  TensorSharp on CPU or CUDA. It accepts the shared typed image request and returns PNG bytes in the
  shared typed result. The current model supports 256x256 output, deterministic seeds, Euler step
  counts, and classifier-free guidance; it does not support negative prompts.
- `Darkspyre.Inference.Tensor.Decision` — Laya typed decision inference using the pinned
  `convaiinnovations/laya-typed-decisions` safetensors checkpoint. Its BPE tokenizer, ModernBERT
  encoder, typed decision heads, and calibration execute directly through TensorSharp on CPU or
CUDA; neither PyTorch nor a Python runtime is used.

Supra's publisher supplies the DiT as a PyTorch ZIP state dictionary rather than safetensors. The
Darkspyre TensorSharp fork reads that file as data through a whitelist-only checkpoint reader. It
accepts only the tensor rebuild, storage, and container forms required by the pinned checkpoint and
rejects arbitrary pickle globals and reducers. Flan-T5 and the VAE use their pinned safetensors
artifacts. No Python or PyTorch component is loaded or executed.

Each capability assembly publishes its pack definition separately from installation. A desktop
application composes the packs it supports into a `TensorModelCatalog`, constructs one
`TensorModelPackManager` with its own `IPersistenceStore`, and explicitly calls `InstallAsync` when
the user chooses to download a model. Inference only acquires verified packs and never initiates a
download.
