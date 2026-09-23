# Laya decision engine on ONNX Runtime: technical design

Date: 2026-09-23

Status: **Implementation authorized on 2026-09-23.** Decisions are recorded in section 10.

## 1. Scope

`Darkspyre.Inference.Tensor.Decision.Laya` executes the Laya model through ONNX Runtime. On macOS the
Core ML execution provider runs the model on the GPU and Neural Engine. The CPU execution provider
runs it everywhere else. Every public contract keeps its current shape, except the one enum value
added in section 4.

Checkpoint producers export the ONNX artifact described in section 6. The RightSize repository
owns the only current producer; section 8 states what any producer must do.

Out of scope: Qwen and Supra, which remain on TensorSharp; Windows CUDA execution (section 7); UI;
version change; package creation or publication.

## 2. Evidence

The feasibility check ran on an M4 Pro against the pinned `laya-typed-decisions` checkpoint
(`f9ab0b22`), with laya 0.3.5, transformers 5.17.0, torch 2.14.0 and ONNX Runtime 1.30.0. It
compared three questions (choice with four options, score with five levels, noul) at 160, 176 and
1024 tokens.

| Configuration | Core ML coverage | Latency at 1024 padded tokens | Decisions vs PyTorch |
|---|---|---|---|
| TensorSharp direct runtime (current) | none; C# CPU only | not measured | reference |
| ONNX, dynamic shapes | 168 of 1,703 nodes | not usable | identical on ORT CPU |
| ONNX, static shapes, SDPA attention | 1,362 of 1,455 nodes, 60 partitions | ~620 ms | identical |
| ONNX, static shapes, eager attention, `Neg` rewritten | 1,335 of 1,343 nodes, 4 partitions | ~200 ms | identical, logits within 1e-5 |
| PyTorch MPS, SDPA attention | n/a | ~180 ms | reference |

TensorSharp's direct runtime has two paths: CUDA kernels and managed CPU loops. It has no Metal or
MLX path, so the current engine runs Laya on the CPU on macOS.

Two export details decide Core ML coverage:

- The ONNX exporter lowers `scaled_dot_product_attention` with a boolean mask to `-inf` plus an
  `IsNaN` guard. Core ML does not support `IsNaN`, and the guard splits every encoder layer. Eager
  attention uses a finite additive mask and produces no guard.
- Core ML does not support `Neg`, which RoPE's rotate-half emits in every layer. `Mul` by `-1` is
  exact and supported.

The remaining CPU nodes (`TopK`, `GatherElements`, `Log`, `Clip`, `Expand`) sit in the decision tail
after the encoder and add one partition boundary.

## 3. Unchanged surface

- `ITensorDecisionInference`, and the Common decision requests and results.
- `LayaDecisionInference`'s public constructor, and the Start, Load, request, Unload and Stop lifecycle.
- `TensorModelPackManager`, host-registered packs, and the setup session. `LayaSetup.CreateSession`
  keeps its shape except that `offeredModels` becomes required (section 10).
- `LayaTokenizer` and its 24 golden rows, `LayaSequenceBuilder` and its framing, and
  `LayaAgentConfiguration`.
- Temperature calibration, softmax and answer construction in `LayaDecisionInference`.

## 4. Contract addition

`TensorBackendKind` in `Darkspyre.Inference.Tensor.Common` gains `CoreMl`. Settings, pack
definitions and provenance name the provider that actually runs. `GgmlMetal` and `Mlx` keep their
current meanings.

Laya's `ResolveBackend` maps `Auto` to `CoreMl` on macOS and to `Cpu` elsewhere.
`LayaModelCompatibility` accepts packs that declare `CoreMl`, `Cpu`, or both.

## 5. Engine changes

The engine reaches the model only through `ILayaModelLoader` and
`ILayaDecisionModel.Forward(LayaSequence) → LayaModelOutput`. The change stays behind that seam.

- `LayaOnnxModel : ILayaDecisionModel` owns one `InferenceSession`. `Forward` pads the sequence to
  the model's fixed shape: `input_ids` with the pad token, `attention_mask`, `marker_pos`,
  `marker_mask` and `qtype`. It returns the first *k* entries of `logits`, where *k* is the real
  option count, and `softmax(act_logits)[0]` as `ActionProbability`. The current C# computes the same
  two values.
- `LayaModelLoader` opens the `model` artifact as an ONNX file. It builds the session options from
  the resolved backend:
  - `CoreMl`: Core ML execution provider with `ModelFormat=MLProgram`, `MLComputeUnits=ALL` and
    `RequireStaticInputShapes=1`, with the CPU provider behind it.
  - `Cpu`: CPU execution provider.
- The loader reads the embedded model metadata (section 6) and compares `laya.max_len` with the
  agent configuration's `max_len`. A mismatch, a missing key, or input dimensions that differ from
  the metadata fail the load as `ModelCorrupt`. A request with more options than `laya.max_markers`
  fails as `InvalidRequest` before execution.
- A cancelled request sets `RunOptions.Terminate`. Teardown still waits for `Run` to return, as the
  current lifetime contract requires.
- Provenance reports `Runtime = "OnnxRuntime"`, the ONNX Runtime assembly version, and the resolved
  backend.
- `LayaDecisionModel`'s TensorSharp forward pass is deleted, with the Laya project's
  `Darkspyre.TensorSharp.*` references and PTX content items and the tests that exercise them. The
  `Softmax` helper moves to its remaining caller. `Darkspyre.ML.Tokenizers` stays.
- `TensorDecisionModels.LayaTypedDecisions` is deleted (section 10), with its revision constant and
  artifact helper.
- `Microsoft.ML.OnnxRuntime` is added through Central Package Management. The Tensor packaging
  project declares it with `PrivateAssets="none"`, as it declares `Whisper.net.Runtime`, so the native
  runtime flows to consumers through the public package. ONNX Runtime's imported native `None` items
  carry no `Pack` metadata, so, unlike Whisper's, they need no exclusion target to stay out of the
  Tensor archive.

## 6. Model artifact

- The graph is the full `DecisionModel`: encoder, question-type embedding, two head layers, scorer
  and act head. It is exported with eager attention and fixed shapes `[1, max_len]` for tokens and
  `[1, 32]` for option slots, then `Neg` is rewritten to `Mul(-1)`.
- Weights are stored as fp16 with fp32 inputs and outputs, which keeps the file near the
  checkpoint's 843 MB. The export gate (section 8) decides this: if fp16 fails the gate, the export
  stores fp32 (about 1.7 GB).
- The file embeds its weights, and both sizes stay below protobuf's 2 GB limit. The loader reads the
  `model` artifact through the host's `IPersistenceStore` and passes the bytes to ONNX Runtime. The
  host decides where and how model files are stored, so the engine never assumes a file path. The
  bytes are released once the session exists, so the load briefly holds one extra copy of the model.
- Metadata properties: `laya.max_len`, `laya.max_markers`, `laya.export_version`, and
  `laya.source_sha256` (the SHA-256 of the source `model.safetensors`).
- Pack artifact roles: `model` (`laya.onnx`), `tokenizer` and `agent-config`.
  `tokenizer-config` and `encoder-config` are no longer Laya artifacts, because nothing reads them.

## 7. Windows CUDA seam

Windows CUDA is a later phase. This unit keeps the slot for it and wires nothing:

- `TensorBackendKind.Cuda` remains in the enum. `LayaModelCompatibility` rejects it for Laya packs
  in this phase, with a message stating that ONNX CUDA execution is not yet available.
- Execution-provider selection is one switch in `LayaModelLoader`. The CUDA phase adds a case there
  and a dependency on `Microsoft.ML.OnnxRuntime.Gpu`, which replaces the standard package on Windows
  and needs CUDA 12 and cuDNN 9 runtimes. That phase decides whether the host or the Tensor package
  owns the GPU package.
- The artifact needs no change for CUDA. The same graph runs on the CUDA execution provider.

Per L2, this seam is documented here as not yet wired. It moves to the tracker when the board exists.

## 8. Producing the artifact

A producer exports from the trained PyTorch checkpoint, on the host that trained it:

- The export uses eager attention, the fixed shapes, the `Neg` rewrite, the precision and the
  metadata in section 6.
- The export writes `export-manifest.json` beside `laya.onnx`. It records the artifact's SHA-256 and
  length, its shapes and precision, and the torch, transformers, onnx and ONNX Runtime versions.
- An export gate runs the ONNX model on the ONNX Runtime CPU provider against PyTorch over a fixed
  set of held-out items. Any changed decision, or any logit outside tolerance, fails the export, and
  no `laya.onnx` is written.
- A Laya pack built from the export declares `CoreMl` and `Cpu`, and lists the artifact roles in
  section 6.

The RightSize repository implements this in `training/scripts/export_onnx.py`, called at the end of
training on gpu-box. Its packaging and release flow are in
`RightSize/docs/rightsize-package.tech-design.md`.

## 9. Verification

- The deterministic suite (L7) keeps its controlled `ILayaDecisionModel` doubles for engine,
  lifecycle and pack-selection tests.
- A tracked tiny fixture model with Laya's input and output signature exercises `LayaOnnxModel` on
  the CPU provider: padding, output slicing, metadata and shape validation, option-count rejection,
  cancellation, and resource release after a failed load.
- The full-weight acceptance run is a recorded experiment, not a commit gate. It compares Core ML
  against the PyTorch reference on the published RightSize checkpoint, and records first-load time
  and per-decision latency.
- The same unit updates `Tensor/README.md`, whose Laya paragraph currently describes TensorSharp
  execution on CPU or CUDA, the canonical-document table in `.roles/project.md`, and this document's
  implementation record.

## 10. Decisions

1. **The built-in `TensorDecisionModels.LayaTypedDecisions` pack is deleted.** It points at Hugging
   Face safetensors, which the ONNX engine cannot load, and only this repository's tests reference
   it. Hosts register their own Laya packs, as the RightSize plugin does. `TensorDecisionModels`
   keeps `LayaRuntimeArchitecture`. The tests that used the built-in pack use a test-local pack
   definition, and the pack-selection tests offer only host-registered packs.
2. **This unit adds no Core ML compiled-model cache.** Core ML compiles the model on first load. The
   acceptance run records first-load time. If that time needs a cache, the cache is a separate unit,
   because it needs a host-supplied directory under M-b.
3. **`LayaSetup.CreateSession` requires `offeredModels`.** Without a built-in pack, an absent list has
   no meaning. The host passes its registered packs. An empty list offers no models.

## 11. Implementation record

### Laya ONNX unit (2026-09-23)

- `TensorBackendKind.CoreMl` is added. Laya resolves `Auto` to `CoreMl` on macOS and `Cpu` elsewhere.
  `LayaModelCompatibility` accepts `CoreMl` and `Cpu`, and rejects `Cuda` with the not-yet-available
  message. `LayaBackendProbe` reports `CoreMl` as available when ONNX Runtime lists
  `CoreMLExecutionProvider`, and `LayaSetup` uses it in place of the shared CPU/CUDA probe.
- `LayaOnnxModel` and the rewritten `LayaModelLoader` replace `LayaDecisionModel`. The loader reads
  the `model` artifact's bytes through the lease's store. `ILayaModelLoader.LoadAsync` receives the
  parsed agent configuration, and `ILayaDecisionModel` gains `MaximumOptions` and a cancellation
  token on `Forward`. A run that ONNX Runtime terminates after cancellation surfaces as
  `OperationCanceledException`.
- The Laya project drops its `Darkspyre.TensorSharp.*` references and PTX content. It keeps
  `TensorSharp.Runtime`'s `PersistenceFileReference` through Tensor.Common's pack lease.
- `TensorDecisionModels.LayaTypedDecisions` is deleted. `LayaSetup.CreateSession` requires
  `offeredModels`. Tests use test-local packs that declare `CoreMl` and `Cpu`.
- `LayaOnnxModelTests` runs `LayaOnnxModel` on the CPU provider against three tracked fixture graphs
  from `Fixtures/make_onnx_fixtures.py`: padding and output slicing, metadata and shape rejection,
  option and token limits, cancellation, provider selection, and the probe.
- `Tensor/README.md`, the Laya fixture README, and `.roles/project.md` describe the ONNX engine.

Phase exit: the Tensor solution builds with zero warnings in Debug and Release, and its 267 tests
pass (77 Laya, 8 of them new). The Connect solution, which compiles the shared setup example, builds
with zero warnings and its 301 tests pass. Format verification passes for both solutions, and the
Tensor transitive vulnerability audit reports no vulnerable packages. No package was created or
published, and the shared version is unchanged.

Not yet run: the full-weight acceptance run in section 9, including first-load time. It needs a
checkpoint exported by the RightSize pipeline, which is a later unit.
