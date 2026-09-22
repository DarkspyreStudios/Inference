# Darkspyre.Inference.Tensor

In-process, first-class .NET inference packages built on TensorSharp. Applications select a
capability package explicitly. Model weights are installed separately through Tensor's
model-pack manager into the `IPersistenceStore` supplied by the application; constructing a runtime
or running inference never downloads weights. The optional `TensorDirectFilePersistStore` is the
filesystem default and still exposes only `IPersistenceStore`. TensorSharp uses a returned
`FileStream` in place and temporarily projects non-file store streams when a native loader needs a
path.

Packages:

- `Darkspyre.Inference.Tensor.Common` — model packs, verification, direct-file persistence, and
  execution coordination.
- `Darkspyre.Inference.Tensor.LLM` — Qwen-compatible GGUF text inference.
- `Darkspyre.Inference.Tensor.Image` — Supra text-to-image inference.
- `Darkspyre.Inference.Tensor.Decision` — Laya typed decision inference using the pinned
  `convaiinnovations/laya-typed-decisions` safetensors checkpoint. Its BPE tokenizer, ModernBERT
  encoder, typed decision heads, and calibration execute directly through TensorSharp on CPU or
  CUDA; neither PyTorch nor a Python runtime is used.

Each capability package publishes its pack definition separately from installation. A desktop
application composes the packs it supports into a `TensorModelCatalog`, constructs one
`TensorModelPackManager` with its own `IPersistenceStore`, and explicitly calls `InstallAsync` when
the user chooses to download a model. Inference only acquires verified packs and never initiates a
download.
