# EmbeddingGemma 2 runtime experiment

This standalone executable compares the same FP32 text weights through TensorSharp Metal,
ONNX CPU, ONNX Core ML and ONNX WebGPU. It runs outside Host and registers no shipping engine.
The existing Tensor ConsoleTester supplies the standalone ownership pattern. RightSize's
export validation supplies the numerical comparison pattern.

[Measured results](RESULTS.md) record Mac, Linux and Windows execution, numerical agreement,
provider placement and timing limits.

The pinned export is
[`onnx-community/embeddinggemma-2-ONNX`, daa72c51](https://huggingface.co/onnx-community/embeddinggemma-2-ONNX/tree/daa72c51243991dfcaf9f9137d2c573d8f7790c0).
`prepare.py` verifies artifact lengths and SHA-256 hashes. Its `--vision` option also
fetches the pinned FP32 vision graph and external weights. It reads the graph's initializer
metadata without executing external model code. Both runtimes consume those exact weights.
TensorSharp uses their exported layout, including the folded embedding scale and normalization
epsilon adjustments. This probe does not load arbitrary architectures or export new models.

Run from the Inference worktree in the normal sibling source workspace. Restore uses the
workstation's private NuGet credential procedure. No native build is part of these commands.

```bash
bash ../agent-ds/scripts/workspace-env.sh dotnet restore Tensor/tools/Darkspyre.Inference.Tensor.GemmaExperiment/Darkspyre.Inference.Tensor.GemmaExperiment.csproj
```

```bash
bash ../agent-ds/scripts/workspace-env.sh dotnet build Tensor/tools/Darkspyre.Inference.Tensor.GemmaExperiment/Darkspyre.Inference.Tensor.GemmaExperiment.csproj -c Release --no-restore
```

```bash
python3 Tensor/tools/Darkspyre.Inference.Tensor.GemmaExperiment/prepare.py artifacts/gemma2/model
```

Metal requires the matching prebuilt native driver in the executable output, with its
`ggml/baseline.artifact.json` and `runtimes/<rid>/native` files. The existing GGML loader verifies
that catalog, hashes and ABI before selecting Metal. A missing driver fails; the probe does not
download or compile one. The driver fingerprint covers managed GGML source too;
managed backend changes require a correspondingly identified driver.

```bash
bash ../agent-ds/scripts/workspace-env.sh dotnet Tensor/tools/Darkspyre.Inference.Tensor.GemmaExperiment/bin/Release/net11.0/Darkspyre.Inference.Tensor.GemmaExperiment.dll cpu artifacts/gemma2/model artifacts/gemma2/cpu-128 128
```

Replace `cpu` with `metal`, `metal-resident`, `metal-fused`, `metal-grouped`, `metal-rows`, `coreml`, `coreml-static` or `webgpu`, and use a separate output directory for each
run. `webgpu-plugin` loads Microsoft.ML.OnnxRuntime.EP.WebGpu 0.4.0 with the same
ORT 1.30.0 core. This packaged provider supplies native Linux and Windows GPU
execution; the core package alone does not include WebGPU on every platform.
Windows staging includes the package's `dxcompiler.dll` and `dxil.dll` dependencies
alongside its ONNX libraries. Omitting them fails WebGPU device creation.
It fails if the plugin finds no device. `providers` lists the ONNX providers available in the installed runtime. The Metal command
does not initialize ONNX Runtime. Run one process at a time to avoid GPU contention.

`metal` retains the original generic matrix path for A/B measurement. `metal-resident`
packs the exported matrices into contiguous `[output, input]` FP32 storage during load
and calls the existing resident `AddmmQuant` operation with GGML type F32. Its other
operations match `metal`. Neither variant quantizes weights. Each result records all
six output vectors as well as the final vector.

`metal-fused` adds the existing GGML norm/projection, GELU feed-forward,
post-norm/residual and PLE fusions to resident weights. It retains the generic
attention path and its repeated K/V heads. All weights and activations remain FP32.

`metal-grouped` adds the existing grouped prefill attention operation with contiguous
head-first FP32 inputs, exact sequence extents and an all-visible mask for this
unpadded 2–512-token experiment. It replaces eight K/V head copies per layer with
three head-first layout conversions. It retains the Gemma block fusions.

`metal-rows` uses the fused path with the existing resident `GetRowsQuant` operation
and GGML type F32. The same contiguous FP32 embedding table supplies the selected rows;
no quantization or whole-model graph is involved. Its A/B control is `metal-fused`.

The Mac comparison script runs one process at a time for each provider at 12, 128
and 512 tokens, records `/usr/bin/time -l` peak RSS, and compares all six vectors
with the CPU reference in the same output root. Run `cpu` before the other providers.
It also compares each optimization with its preceding variant when that result exists.
The execution-parity bounds are maximum component error 1e-4, cosine at least
0.999999 and norm error at most 1e-6. The bounds admit the measured original Metal
baseline; they do not qualify retrieval quality.
Every requested case runs even when another case fails numerical parity; the script
then exits with failure and names every failing case. First-call vectors participate
in parity checks even though warm latency excludes the first call.

```bash
python3 Tensor/tools/Darkspyre.Inference.Tensor.GemmaExperiment/compare.py artifacts/gemma2/model artifacts/gemma2/ab cpu metal-fused metal-rows webgpu
```

The optional length defaults to 12. Each input consists of BOS, repeated frozen ordinary token
IDs and EOS. All tokens participate in pooling. The 512-token maximum keeps the full and
sliding masks equivalent. These synthetic sequences test execution; they are not a retrieval
corpus. Each process records load time, one first call and five subsequent calls, a 768-value
vector, and process peak working set when the runtime supplies it (otherwise null). ONNX records its provider profile. TensorSharp reports
actual Metal selection and releases model tensors before terminating the native runtime.

Compare every vector against ONNX CPU for the same token count. Unit length alone does not
establish agreement. Profile node placement separately from timings: selecting a GPU provider
does not prove that all nodes execute there. Core ML uses `CPUAndGPU` and `MLProgram` with
dynamic shapes admitted; the original graph can fail at compilation or inference.
`coreml-static` binds the batch, token and absent-modality dimensions for that one session
and admits only Core ML partitions with static input shapes. This measures whether shape
specialization helps the same graph; it does not export another model.

This is a loading and numerical feasibility experiment. TensorSharp's baseline uses
individual tensor operations; the cumulative variants measure existing native fusions.
Its timings do not measure a finished Gemma engine. The FP32 ONNX text and reference-prepared
image runs pass on Mac arm64, Linux x64 and Windows x64. Shared image resizing uses
Pillow RGB rounding and clipping in both passes. Video decoding, tokenizer parity, realistic retrieval, quantization,
long contexts and cancellation remain unqualified. Google warns against FP16
activations for this model; all probe activations and weights use FP32.

## Correctness and optimized-path diagnostics

`ggml-cpu` and `ggml-cpu-fused` execute the same TensorSharp forward and FP32 weights
through the CPU backend in the matching prebuilt driver. They do not initialize ONNX.
`GEMMA_RUNS` changes the number of measured calls; its default is six.
`GEMMA_DIAGNOSTIC=trace` writes contiguous intermediate FP32 arrays as `.f32` files
and their names/shapes in `diagnostic-NN.json`. These host reads synchronize execution.
`GEMMA_TRACE_POINT` restricts readback to one named checkpoint, such as
`layer-11-output`, to localize a failure without synchronizing earlier checkpoints. An attention checkpoint also captures its Q/K/V inputs after
reading the completed result, so those reads do not precede that invocation.
Trace-mode timings do not measure the undisturbed inference path.
`GEMMA_DIAGNOSTIC=profile` records elapsed phases and managed allocation/GC counts
without reading intermediate tensor data. It also reads the pinned runtime ownership
registry count under its metadata lock, without mutating it. `GEMMA_SYNCHRONOUS=1`
disables the existing GGML async-compute setting as a diagnostic control. Its default
retains the backend setting. CPU copies use the shared `GgmlBasicOps.Copy` storage
barrier. Every mode retains all final vectors.

```bash
GEMMA_DIAGNOSTIC=trace bash ../agent-ds/scripts/workspace-env.sh dotnet Tensor/tools/Darkspyre.Inference.Tensor.GemmaExperiment/bin/Release/net11.0/Darkspyre.Inference.Tensor.GemmaExperiment.dll ggml-cpu artifacts/gemma2/model artifacts/gemma2/correctness/cpu-trace-12 12
```

The synchronous control uses the existing comparison runner and a separate output root:

```bash
GEMMA_SYNCHRONOUS=1 GEMMA_RUNS=12 python3 Tensor/tools/Darkspyre.Inference.Tensor.GemmaExperiment/compare.py artifacts/gemma2/model artifacts/gemma2/sync-control cpu metal-fused metal-rows
```

The ONNX reference retains six calls. Default comparisons leave `GEMMA_SYNCHRONOUS`
unset. Measured diagnostic conclusions and limitations appear in RESULTS.md.

## Image and native platform qualification

`prepare_images.py <directory>` creates square, landscape and portrait RGB fixtures.
It uses installed Pillow and NumPy and follows the pinned Hugging Face Gemma4 PIL
recipe named in its manifest: bicubic resize, [0,1] scaling, 16x16 RGB patches,
zero padding to 2,520 patches and [column,row] positions padded with -1.
The square is already at the target size. The other cases exercise downsampling
and upsampling. Generated inputs carry SHA-256 hashes; inference checks the hashes.

`preprocess <fixtures> <output>` compares the existing TensorSharp Gemma4 processor
and the probe's ONNX patch packing with the reference. It checks dimensions,
positions and pixel error at 1e-6 and returns failure on any mismatch.

`images <cpu|webgpu|webgpu-plugin> <model> <fixtures> <output>` runs six vision
and full image-embedding calls per fixture. It writes each vision output, every
embedding, separate inference timings and both provider profiles. Inputs are the
reference patches, independent of the preprocessing candidate. Image placeholders
use the pinned model's BOS/BOI/image/EOI/EOS IDs. General tokenizer and retrieval
quality are outside this execution check.

`images-shared` accepts the same arguments as `images` and feeds the existing Gemma4
processor's pixels into both graphs. The preprocessing check and this mode share patch
packing. Each result identifies its preprocessing path, so a frozen-input control cannot
stand in for the shared path's end-to-end check.

The plugin mode selects only devices from the external provider library, excluding
a built-in WebGPU provider with the same name. `GEMMA_GPU_VENDOR` optionally selects
a reported numeric hardware vendor for qualification on a multi-vendor machine.
Device metadata appears in the run log. Provider assignment and hardware inventory
must accompany GPU timing claims. These controls belong to the isolated probe.

`GEMMA_DEFAULT_HOST_MEMORY=1` uses explicit CPU/default `OrtMemoryInfo` and the
`OrtValue` API for inputs. The control retains C#'s ordinary named-value API.
This tests the external provider's rejection of the default C# CPU-output memory
tag (-1); it changes no tensor values and applies to both text and vision inputs.

`GEMMA_SEPARATE_WEBGPU_CONTEXTS=1` assigns the built-in WebGPU text session
to context 1 and retains vision on context 0. It tests teardown with independent
Dawn contexts after the shared-context image run crashes during buffer release.
The control uses context 0 for both sessions. Both paths dispose their sessions.
The separate-context control fails because a nonzero context ID requires an explicit
native WebGPU instance/device. The external provider with default host memory completes
the measured two-session image runs and cleanup on all three platforms.

`summarize_qualification.py <qualification> <text-reference>` checks every captured vector
and records raw vision-feature error separately. Its explicit run matrix requires six calls
and successful process exit for each CPU/plugin case. Diagnostic failures stay in the report.
The summary records runtime execution and preprocessing as separate outcomes; the command
returns failure if either fails or any expected case is missing. The Mac CPU text reference
comes from the existing FP32 comparison directory. Raw inputs, outputs, profiles and the
summary reside under the ignored `artifacts/gemma2/qualification/` directory.

`summarize_qualification.py images <shared-run-root> <frozen-qualification-root>`
compares the shared path against the existing CPU image reference. It requires six
outputs per fixture from CPU and external WebGPU on Mac, Linux and Windows, successful
process exits, and preprocessing parity on all three. A result from `images` cannot
pass this matrix; every result must identify `sharedPreprocessing: true`.


## Production-library fixture

`prepare_engine.py <model-directory> <image-fixtures> <output-directory>` uses the pinned tokenizer
through Python's `tokenizers` reference. It writes multilingual, code and whitespace token cases,
text/image inputs and retrieval expectations. The existing Tensor ConsoleTester command
`gemma <cpu|webgpu> <model-directory> <fixture-directory> <output-directory>` runs these through the
production Gemma engine and the SelfHost embedding connection. Model artifacts are installed and
verified through the real pack manager. The tester checks cancellation, reuse, unload and removal.
The experiment's shared image path calls the production patch packer.

`summarize_engine.py <run-root> <frozen-image-reference>` compares all ten embeddings from
`mac-cpu`, `mac-webgpu`, `linux-cpu`, `linux-webgpu`, `windows-cpu` and `windows-webgpu`.
Each directory contains the tester's `result.json`. The frozen image reference contains
the prior CPU results for square, portrait and landscape. Missing runs fail the summary.
The report checks the existing parity bounds, identical fingerprints and direct/Connect
agreement. The Christmas recall fixture requires its related document to reach the agreed
0.60 cosine threshold and the three unrelated documents to remain below it. This bounded
fixture does not establish relevance accuracy for an arbitrary corpus.
