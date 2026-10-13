# EmbeddingGemma 2 runtime experiment

This standalone executable compares the same FP32 text weights through TensorSharp Metal,
ONNX CPU, ONNX Core ML and ONNX WebGPU. It runs outside Host and registers no shipping engine.
The existing Tensor ConsoleTester supplies the standalone ownership pattern. RightSize's
export validation supplies the numerical comparison pattern.

The pinned export is
[`onnx-community/embeddinggemma-2-ONNX`, daa72c51](https://huggingface.co/onnx-community/embeddinggemma-2-ONNX/tree/daa72c51243991dfcaf9f9137d2c573d8f7790c0).
`prepare.py` verifies artifact lengths and SHA-256 hashes. It reads the graph's initializer
metadata without executing external model code. Both runtimes consume those exact weights.
TensorSharp uses their exported layout, including the folded embedding scale and normalization
epsilon adjustments. This probe does not load arbitrary architectures or export new models.

Run from the Inference worktree in the normal sibling source workspace. Restore uses the
workstation's private NuGet credential procedure. No native build is part of these commands.

```bash
bash ../agent-ds/scripts/workspace-env.sh dotnet restore Tensor/tools/Darkspyre.Inference.Tensor.GemmaExperiment/Darkspyre.Inference.Tensor.GemmaExperiment.csproj
```

```bash
bash ../agent-ds/scripts/workspace-env.sh dotnet build Tensor/tools/Darkspyre.Inference.Tensor.GemmaExperiment/Darkspyre.Inference.Tensor.GemmaExperiment.csproj --no-restore -maxcpucount:1
```

```bash
python3 Tensor/tools/Darkspyre.Inference.Tensor.GemmaExperiment/prepare.py artifacts/gemma2/model
```

Metal requires the matching prebuilt native driver in the executable output, with its
`ggml/baseline.artifact.json` and `runtimes/<rid>/native` files. The existing GGML loader verifies
that catalog, hashes and ABI before selecting Metal. A missing driver fails; the probe does not
download or compile one.

```bash
bash ../agent-ds/scripts/workspace-env.sh dotnet Tensor/tools/Darkspyre.Inference.Tensor.GemmaExperiment/bin/Debug/net11.0/Darkspyre.Inference.Tensor.GemmaExperiment.dll cpu artifacts/gemma2/model artifacts/gemma2/cpu-128 128
```

Replace `cpu` with `metal`, `coreml` or `webgpu`, and use a separate output directory for each
run. `providers` lists the ONNX providers available in the installed runtime. The Metal command
does not initialize ONNX Runtime. Run one process at a time to avoid GPU contention.

The optional length defaults to 12. Each input consists of BOS, repeated frozen ordinary token
IDs and EOS. All tokens participate in pooling. The 512-token maximum keeps the full and
sliding masks equivalent. These synthetic sequences test execution; they are not a retrieval
corpus. Each process records load time, one first call and five subsequent calls, a 768-value
vector, and process peak working set. ONNX records its provider profile. TensorSharp reports
actual Metal selection and releases model tensors before terminating the native runtime.

Compare every vector against ONNX CPU for the same token count. Unit length alone does not
establish agreement. Profile node placement separately from timings: selecting a GPU provider
does not prove that all nodes execute there. Core ML uses `CPUAndGPU` and `MLProgram` with
dynamic shapes admitted; the original graph can fail at compilation or inference.

This is a loading and numerical feasibility experiment. TensorSharp's forward pass uses
individual tensor operations without model-level fusion. Its timings do not measure a finished
Gemma engine. Images, video frames, tokenizer parity, realistic retrieval, quantization, long
contexts, cancellation, Linux and Windows remain unqualified. Google warns against FP16
activations for this model; all probe activations and weights use FP32.
