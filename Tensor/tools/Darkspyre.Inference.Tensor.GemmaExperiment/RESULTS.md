# Mac text execution results

Measurement date: 2026-10-12. Machine: Apple M4 Pro, 48 GiB unified memory,
macOS 26.7.1 (25G241), arm64. The executable uses .NET 11 and a Release build.

The executable source is Inference `3ef82f2a3bce462463d48044535dc51c04b7ac8c`.
TensorSharp source is `0f6757935c5ed7c8a06c0387575c31804c7b9940`.
The prebuilt Metal driver has native ABI
`96f321c263a76984f6386b7cf7894789c4fc0de23c4ec8e5319c7f30e2f3504b`.
ONNX Runtime is 1.30.0. The graph and weights are the FP32 text artifacts at
[`daa72c51243991dfcaf9f9137d2c573d8f7790c0`](https://huggingface.co/onnx-community/embeddinggemma-2-ONNX/tree/daa72c51243991dfcaf9f9137d2c573d8f7790c0).

Each cell below is the median of five calls after one initial call. Each backend and
token count uses a fresh process. The input is one unpadded synthetic sequence of
frozen token IDs. ONNX profiling is enabled. These are feasibility measurements,
not a production throughput benchmark. Filesystem and driver caches are not cleared.

| Runtime | 12 tokens | 128 tokens | 512 tokens | Peak RSS at 512 tokens |
| --- | ---: | ---: | ---: | ---: |
| ONNX CPU | 13.1 ms | 34.2 ms | 126.3 ms | 861 MiB |
| ONNX WebGPU | 30.9 ms | 31.0 ms | 87.2 ms | 1,216 MiB |
| ONNX Core ML, fixed dimensions | 19.7 ms | 60.3 ms | 209.4 ms | 2,612 MiB |
| TensorSharp Metal, unfused text pass | 864.5 ms | 991.4 ms | 1,310.5 ms | 3,947 MiB |

Peak RSS comes from macOS `/usr/bin/time -l` and includes loading, execution and
temporary allocations. It is not steady model memory or dedicated GPU memory.
The .NET peak-working-set property returns zero on this machine; the probe represents
that unavailable counter as null.

The 512-token session/weight load times are 208 ms for CPU, 998 ms for WebGPU,
8,383 ms for Core ML and 332 ms for TensorSharp. This timer excludes runtime
initialization. The corresponding first inference times are 156, 119, 456 and
1,579 ms. The first call is separate from the five-call medians.

All twelve final processes exit 0. Every output contains 768 finite values with
unit length. Numerical differences against ONNX CPU over the three input lengths:

| Runtime | Largest component difference | Lowest cosine similarity |
| --- | ---: | ---: |
| ONNX WebGPU | 2.7e-7 | 0.999999999998 |
| ONNX Core ML, fixed dimensions | 2.33e-7 | 0.999999999998 |
| TensorSharp Metal | 7.32e-5 | 0.999999825758 |

These comparisons establish agreement for the tested synthetic sequences. They do
not establish parity with Google's preprocessing or retrieval quality.

## Provider placement and limitations

ONNX WebGPU executes the matrix multiplications, normalization, rotary embeddings,
attention, activation and pooling operations. Its profile records 6,192 WebGPU
events and 222 CPU events over six calls. CPU work consists of shape, indexing and
input preparation operations. Provider event counts are not GPU utilization or time
percentages. This experiment does not capture device-level GPU counters.

The unmodified graph fails with Core ML when dynamic dimensions are admitted.
The errors include unbounded intermediate dimensions. Binding the batch and sequence
dimensions and setting absent-modality dimensions to zero allows execution with
`RequireStaticInputShapes=1`. Core ML accepts 822 of 1,034 nodes in 125 partitions.
Normalization and rotary operations remain on CPU. `CPUAndGPU` permits both devices;
the ONNX profile identifies Core ML partition execution, not Core ML's internal
device choice. The probe does not claim that every Core ML partition executes on GPU.

TensorSharp reports the actual Metal backend on Apple M4 Pro, releases every model
tensor and reports successful native shutdown. Its implementation dispatches existing
tensor operations individually. The five warm 512-token calls range from 1,178 to
1,640 ms and rise across the run. This drift and dispatch overhead remain unqualified.
The figures do not predict an optimized, fused Gemma implementation.

The runtimes use separate processes. A diagnostic that initializes ONNX Runtime
before running Metal produces a native process-exit failure in an earlier run. The
isolated Metal runs pass. The underlying mixed-runtime shutdown cause is unverified.

## Harness fit

Inference's existing Tensor ConsoleTester has the useful embedding and lifetime
patterns, but its current build fails in `SelfHostSuite.cs` because Connect excludes
`AddLlmConnectSelfHost`. The experiment has its own small executable in `Tensor/tools`
and directly references the existing runtime libraries. The RightSize console and
export script supply local-checkpoint and numerical-comparison patterns; RightSize
is otherwise specific to Laya decisions. Neither existing app receives a refactor fix.

The isolated project builds without warnings in Debug and Release. Host, Search,
the shipping Tensor registry and production engine contracts have no changes.
No native compilation occurs. No production engine is selected.

Images and video frames, tokenizer parity, real retrieval, quantization, inputs beyond
512 tokens, cancellation, Linux and Windows are unqualified. The present evidence
establishes a working Mac text experiment and makes ONNX WebGPU a viable GPU candidate.
