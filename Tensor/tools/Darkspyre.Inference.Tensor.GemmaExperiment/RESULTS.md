# Mac text execution results

The [latest shared-fix and resident-row qualification](#shared-fixes-and-resident-row-qualification-2026-10-13)
records the corrected path and fresh WebGPU comparison.

Measurement dates: 2026-10-12–13. Machine: Apple M4 Pro, 48 GiB unified memory,
macOS 26.7.1 (25G241), arm64. The executable uses .NET 11 and a Release build.

The initial table uses Inference `3ef82f2a3bce462463d48044535dc51c04b7ac8c`.
Later sections name the diagnostic revisions used for their measurements.
Initial TensorSharp source is `0f6757935c5ed7c8a06c0387575c31804c7b9940`.
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
The numerical comparison above covers each process's final vector. The all-call
A/B check below exposes intermittent failures that this final-vector comparison misses.

## Cumulative Mac A/B comparison

The cumulative variants retain the pinned FP32 weights, activation precision,
token IDs, pooling and 2–512-token scope. Each process executes six calls. The
comparison script checks every vector, including the first call, against ONNX CPU.
It also compares each optimization with the preceding variant's final vector.
The criteria are maximum component error at most 1e-4, cosine similarity at least
0.999999 and unit-norm error at most 1e-6. The component and cosine limits admit
the original final-vector rounding difference. They are execution checks, not
retrieval-quality qualification. Failing cases remain failures; no warmup vector
is discarded from the parity check.

Source commits are `17c7fa75` for resident weights, `6a533663` for Gemma fusions,
and `806f0673` for grouped attention. TensorSharp and the prebuilt driver retain
the baseline revisions above. The variants make these cumulative changes:

1. `metal-resident` packs each linear matrix once into contiguous `[output, input]`
   FP32 storage during loading and uses the existing `AddmmQuant` F32 path.
2. `metal-fused` adds `FusedRmsNormMatMulQuant`, `FusedFFNActProjectQuant` with
   GELU, `RmsNormResidualAdd` and `FusedPleBlockQuant`. It retains generic attention.
3. `metal-grouped` adds the exact-extent FP32 grouped attention operation. It uses
   an all-visible mask for these bounded inputs and replaces eight K/V head copies
   per layer with three head-first layout conversions.

Observed warm medians in the complete comparison round (`ab-repeat`):

| Variant | 12 tokens | 128 tokens | 512 tokens | Peak RSS at 512 tokens | All-call parity |
| --- | ---: | ---: | ---: | ---: | --- |
| ONNX CPU | 21.7 ms | 61.1 ms | 168.8 ms | 862 MiB | Pass at all three lengths |
| ONNX WebGPU | 31.4 ms | 31.1 ms | 86.0 ms | 1,534 MiB | Pass at all three lengths |
| Original Metal | 933.5 ms | 1,005.0 ms | 1,276.6 ms | 3,946 MiB | Fail at all three lengths |
| Resident FP32 | 472.3 ms | 539.7 ms | 806.0 ms | 4,455 MiB | Fail at all three lengths |
| Resident + Gemma fusions | 249.0 ms | 303.3 ms | 491.5 ms | 4,417 MiB | Fail at all three lengths |
| Resident + fusions + grouped attention | 296.9 ms | 350.2 ms | 545.0 ms | 4,413 MiB | Pass at 128; fail at 12 and 512 |

These timings include calls that fail output parity. They do not qualify a correct
implementation's performance. Resident weights remove a substantial observed cost;
the Gemma fusions remove another substantial cost. Combined, their observed warm
medians are about 2.6–3.7 times faster than the original prototype in this round.
They remain slower than ONNX WebGPU. Resident packing increases loading time from
about 330–450 ms to about 850–940 ms in this round and increases peak process memory.
RSS includes transient loading arrays, pooled buffers and runtime caches; it does
not establish steady resident model memory.

A confirmation round reverses the fused/grouped ordering. Its warm medians are:

| Variant | 12 tokens | 128 tokens | 512 tokens |
| --- | ---: | ---: | ---: |
| Gemma fusions | 257.4 ms | 312.6 ms | 600.6 ms |
| Gemma fusions + grouped attention | 412.5 ms | 306.4 ms | 478.3 ms |

Grouped attention has no consistent additional latency gain across the two rounds.
It is faster at 512 tokens in the confirmation round, while the first round shows
the opposite result. The workstation is shared, individual calls vary and some
within-process timings rise. A precise throughput ranking between the two variants
is not established. The earlier CPU round also measures 12.8 / 37.4 / 127.8 ms,
which shows the variability in these short process runs.

### Parity failure exposed by checking every call

All 34 measured processes across the staged, complete, confirmation and diagnostic
rounds exit 0 and return finite normalized vectors. Every Metal process reports
successful native shutdown. The comparison script nevertheless rejects 18 cases
because at least one vector exceeds the parity limits. Unit length and successful
shutdown do not detect this failure.

Failures occur in both the first call and later calls. Examples in the complete round:

- Original Metal at 128 tokens: the first call agrees within 5.182e-5, but call 2
  differs by 0.012058795. Subsequent calls return to the smaller difference.
- Fused Metal at 128 tokens: the largest component difference is 0.02738989, with
  cosine 0.97877521. Several later calls agree within 4.756e-5.
- Grouped Metal at 512 tokens: calls 1–4 agree within 6.571e-5, but the final call
  differs by 0.019058593. Discarding only the first call cannot hide this example.

Call numbers above are zero-based, matching the executable output. The confirmation
round also produces failures in both fused and grouped variants. The operations'
algebraic match and several agreeing calls do not establish reliable execution.
The failure exists in the original unfused path as well as the new variants.
Its cause is not isolated to a particular operation, lifetime or native mechanism.

The existing `GGML_METAL_CONCURRENCY_DISABLE=1` switch is exercised only in three
diagnostic grouped-attention processes. Their logs confirm concurrency is disabled.
The 12- and 512-token cases pass, but a warm 128-token call differs by 0.004182157
and fails. This switch is not a sufficient remedy. No production setting changes.

ONNX CPU repeats exactly in the measured runs. WebGPU also repeats exactly within
each tested process and differs from CPU by at most 2.7e-7 across the three lengths.
This evidence covers the synthetic text inputs only.

### Reproduction and qualification boundary

`compare.py` and the probe README contain the commands and parity limits.
`artifacts/gemma2/ab-summary.json` consolidates every measured case, every call's
CPU comparison, native shutdown outcome and the diagnostic environment. Raw results
and logs remain in `ab`, `ab-repeat`, `ab-confirm` and `ab-no-concurrency` below
`artifacts/gemma2/`. The initial failing measurements remain available.

Every cumulative variant builds in Release with zero warnings and errors. Formatting
verification passes. The runs establish the latency reductions above and expose a
correctness failure; they do not establish a qualified TensorSharp embedding engine.
Host, Search, TensorSharp source and the shipped engine registry have no changes.
No native compilation or production engine selection occurs. The intermittent
TensorSharp output failure and within-process timing drift remain unresolved.

## Correctness and optimized-path profiling, 2026-10-13

This investigation uses the same pinned FP32 graph, token sequences, TensorSharp
commit and verified prebuilt driver. Inference diagnostic commits are `d1bbaff8`,
`1ca01b62`, `6c19e007`, `ee711ce9` and `f859d133`. Only the isolated executable and
its documentation change. Host, TensorSharp source and native drivers are unchanged.
The local `2.8.6.9-osx-arm64-metal-96f321c263a76984` driver supports Metal and CPU;
its catalog and binary SHA-256 match the probe copy. No download or native build runs.

### Numerical result: host copies require synchronization

The unchanged execution-parity criteria apply to every final embedding. Controls
retain FP32 weights, activations and the same computations.

| Case | Processes | Calls passing / checked | Largest component error |
| --- | ---: | ---: | ---: |
| Untraced original/fused Metal, two rounds at 12/128/512 tokens | 12 | 54 / 72 | 0.022114 |
| Barrier before probe host copies, fused/grouped at 12/128/512 tokens | 6 | 72 / 72 | 0.00006571 |
| Existing synchronous-compute setting, fused/grouped at 12/128/512 tokens | 6 | 72 / 72 | 0.00006571 |

The controls execute twelve calls per process, including the first call. Neither
control requires new native code. The original/fused controls execute six calls
per process. The barrier and synchronous controls also meet cosine >= 0.999999
and norm error <= 1e-6. Their vectors repeat exactly within each process.

The same TensorSharp forward on GGML CPU repeats exactly across six traced
12-token calls. Its largest difference from ONNX CPU is 2.796e-5. The fused GGML
CPU path repeats exactly across six 128-token calls, with largest difference
3.003e-5. This supports the model mapping for those inputs; it does not qualify
other lengths, preprocessing or media behavior.

Capturing every intermediate tensor masks the intermittent failure in the measured
Metal trace because host readback drains pending work. Single-checkpoint traces
retain failures. At the first layer's attention checkpoint, one 128-token call
has maximum output difference 11.9793 from an agreeing call in the same process.
Capturing its Q/K/V inputs *after* completion shows identical query and value
inputs but key differences up to 1.38160. The corresponding final embedding differs
from ONNX CPU by 0.015981. This places that failure before attention consumes its
key input. A later capture also records the original key, but no first-layer
failure occurs in that particular run; it does not prove the exact first write.

The relevant shared implementation is `TensorSharp.Backends.GGML/GgmlBasicOps.cs`:
`Copy` at line 4304 uses CPU pointer copies, while `GetBufferStart` at line 5591
returns `PtrAtElement` without `EnsureHostReadable`. The native bridge can enqueue
an asynchronous output download (`TensorSharp.GGML.Native/ggml_ops_internal.h:537`).
`GgmlStorage.CopyToStorage` and `TensorComputePrimitives.GetFloatPointer` already
call `EnsureHostReadable`, but the managed `Ops.Copy` path does not. The probe
uses that path to materialize slices, repeat K/V heads and create head-first layouts.

`GEMMA_COPY_BARRIER=1` invokes the existing `HostReadBarrier` before those host
copies. Its passing controls, the captured key corruption and the missing wait in
source identify a host-copy visibility defect. The specific GPU write that races
with the captured bad copy is not separately traced. These controls do not qualify
every GGML caller, workload or platform, and no production fix is applied here.

### Repeated-call latency: retained ownership registrations

In the fused 128-token profile, the process ownership registry contains 662 entries
after the first request and 7,141 after request twenty. Every request adds exactly
341 entries. Only 3,236 entries still have live weak targets at the last observation;
collected targets do not remove their registration cells. A second run reaches
8,505 entries after twenty-four requests, with 1,817 live targets.

At this TensorSharp baseline, `NativeQuarantineAuthority.ValidateRegistration`
(`TensorSharp.Core/NativeQuarantineAuthority.cs:200`) calls `ValidateCells`, which
scans every owner (`:147`). Tensor access invokes that path. The managed stack
capture during request eleven lands in `ValidateCells` beneath `Tensor.View`.
`GgmlNativeLoader.OwnedResourceLease.CompleteRelease`
(`TensorSharp.Backends.GGML/GgmlCudaDependency.cs:214`) completes the quarantine
registration only when `CudaAttached` is true. Metal and CPU disposal remove the
runtime lease but leave the quarantine registration. This is metadata accumulation;
it does not establish a retained GPU-allocation leak.

The first profile measures request three at 346.9 ms and request twenty at 733.3 ms.
The second measures request two at 269.5 ms and request twenty-four at 715.9 ms.
Phase timing grows in attention preparation, QKV work and other tensor-heavy code.
Managed allocation is approximately 4.3 MiB per request after the initial calls.
The observations and stack/source evidence identify increasing registry-validation
work. They do not assign an exact fraction of total runtime to it. Profilers and
other workstation workloads also affect elapsed time. No lifecycle checks are
removed or bypassed by the experiment.

### Remaining fixed costs

The first optimized native sample contains 2,253 main-thread samples. Metal
`waitUntilCompleted` appears in 633 (28.1%), backend graph-buffer allocation in
218 (9.7%), and `TSGgml_IndexSelectF32` in 388 (17.2%). These are inclusive,
overlapping stack counts, not exclusive time shares or GPU utilization. Weight
packing does not appear in this sample. The retained FP32 linear path avoids the
original repeated matrix packing.

Phase timing records the embedding lookup near 90 ms in this 128-token profile.
The generic index-select graph constructs `ggml_cont` over the complete
262,144-by-512 FP32 embedding table before gathering rows
(`TensorSharp.GGML.Native/ggml_ops_elementwise.cpp:1440`). That table occupies
512 MiB. The existing `GgmlBasicOps.GetRowsQuant` path (`GgmlBasicOps.cs:1371`)
accepts an explicit GGML type; its native constructor accepts F32 and binds cached
source weights directly before row selection (`ggml_ops_matmul.cpp:538`). Reuse
of that operation is a smaller performance candidate than a whole-model graph.
Its embedding parity and speed are not measured by this investigation.

### Evidence and limits

`artifacts/gemma2/correctness/correctness-profile-summary.json` consolidates 35
processes and 360 calls, including failed controls and diagnostic traces. All
processes report successful shutdown. Raw vectors, checkpoint arrays, phase
measurements, native samples and managed stacks reside beside it. The diagnostic
README describes each control. Trace/profile measurements are labeled separately.
A first managed-stack attachment fails because the default diagnostic socket path
is too long. A short isolated scratch directory permits the subsequent four
captures; no software installation or machine setting changes.

The later control timings vary substantially on this shared workstation and retain
the growing-registry cost. They do not establish a performance ranking between
barriers and synchronous execution, nor a new comparison with WebGPU. The existing
WebGPU comparison remains the measured baseline. Whole-graph implementation,
production lifecycle/copy fixes, native compilation and engine selection remain
outside this investigation. The next smaller candidates are shared host-copy
synchronization, correct ownership-registration release, and resident embedding
row lookup. Images, tokenizer parity, retrieval quality and other platforms remain
unqualified.

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

## TensorSharp path investigation

The source references below use TensorSharp `0f675793` and this experiment's
`MetalText.cs` at Inference `af1dfe4c`. The experiment executes the generic tensor
path; it does not execute TensorSharp's existing resident-weight model path.

macOS `sample` observes two separate 512-token processes at a 1 ms sampling
interval. The first capture lasts four seconds and starts about one second after
launch. The second capture lasts three seconds and starts after `Run 0` completes.
The second capture contains 2,369 main-thread samples:

| Function in the sampled stack | Samples | Share of main-thread samples |
| --- | ---: | ---: |
| `TSGgml_AddmmF32` | 1,086 | 45.8% |
| `create_packed_m2_binding` | 665 | 28.1% |
| Metal `waitUntilCompleted` | 513 | 21.7% |
| `ggml_backend_alloc_ctx_tensors` | 354 | 14.9% |

These are inclusive stack counts. Categories overlap, and they do not sum to a
latency breakdown or measure GPU utilization. Managed frames lack symbols in this
capture. The first capture also contains substantial weight packing (30.7%) and
Metal waits (21.6%). Both processes exit 0 and return the same vector as the earlier
512-token Metal run: maximum component difference from ONNX CPU is 7.31154e-5,
with cosine similarity 0.9999998257584505. Their five-call warm medians are
1,270.0 ms and 1,373.6 ms. Profiling perturbs execution; these timings establish
that the sampled runs reproduce the slow path, not a new ranking of engines.

### Weight layout and repeated copies

`MetalText.cs:183` sends the exported contiguous `[input, output]` matrix to
`Ops.Addmm` on every linear operation. GGML's direct matrix binding requires
`stride0 == 1` (`TensorSharp.GGML.Native/ggml_ops_core.cpp:1038`). The export's
row-major layout fails that condition. The generic operation transposes the matrix
into a new CPU vector, allocates backend tensors, uploads inputs and weights, then
copies the result back (`ggml_ops_matmul.cpp:120`, `:184`, `:252`, `:259`;
`ggml_ops_core.cpp:2098`). These copies remain copies on unified memory.

The pinned initializer metadata contains 146 linear matrices totaling 546,832,384
bytes (521.5 MiB), excluding the embedding table. Each matrix is used once per
forward pass. The prototype therefore repacks and uploads that entire matrix set
per request, independent of token count. This provides a concrete source of its
large latency even at 12 tokens.

TensorSharp already has a resident FP32 path:
`TensorSharp.Models/ModelBase.cs:1554`, `TryGgmlF32LinearResident`. It accepts
contiguous `[output, input]` weights and calls `GgmlBasicOps.AddmmQuant` with
`GGML_TYPE_F32` (0). The native operation caches weight bindings and reuses its
compute buffer (`ggml_ops_matmul.cpp:401`, `:481`). Its `Quant` name does not require
quantizing the weights. A one-time layout conversion and stable weight ownership
fit this existing path. A transpose view of the original ONNX storage alone does
not provide the required contiguous weight storage.

### Existing fused operations

These operations exist in `TensorSharp.Backends.GGML/GgmlBasicOps.cs`. Native
symbol inspection confirms that the installed matching driver exports all of them.
Their use with this model is source-verified but not execution-qualified.

| Existing operation | Matching portion of this experiment | Reuse limits |
| --- | --- | --- |
| `FusedRmsNormMatMulQuant`, line 700 | Input norm plus QKV projection; final norm plus embedding projection | The native descriptor accepts F32 weights in GGML layout. The export's epsilon adjustments remain part of the computation. |
| `FusedFFNActProjectQuant`, line 851, `actType: 1` | Pre-FFN norm, packed gate/up projection, GELU multiplication and down projection | Produces a separate projection for Gemma's post-FFN norm. The native graph uses a persistent liveness allocator. The model helper currently selects only `_quantWeights`; it does not automatically select this path for the probe's FP32 tensors. |
| `RmsNormResidualAdd`, line 756 | Post-attention, post-FFN and post-PLE normalization plus residual addition | Residual and input are distinct FP32 tensors. The PLE fusion below already includes its norm and add. |
| `FusedPleBlockQuant`, line 780 | PLE gate projection, GELU multiplication, projection, post-norm and residual addition | Native descriptors accept F32; the existing model-level helper selects only `_quantWeights`. The native graph still allocates an intermediate buffer per call. |

The FP32 acceptance follows the supplied `ggml_type` in the native constructors
(`ggml_ops_fused.cpp:108`, `:1071`, `:2885`), without a quantized-type-only check.
Resident FP32 linear use also exists in the model code. Full-model numerical parity
for the fused combinations remains unmeasured. Each fused operation submits a
GGML graph; this does not imply one GPU kernel.

`GELUMul` already executes in the prototype. SwiGLU has a different activation,
and plain matrix-plus-residual fusion omits Gemma's intervening post-norm. Those
operations do not replace the corresponding Gemma blocks. The full generation
decode blocks also carry KV-cache and causal-attention behavior that this embedding
pass does not use.

### Attention and synchronization

The generic SDPA operation already groups its attention arithmetic in one native
graph (`ggml_ops_norm_attn.cpp:1264`). Its managed contract requires equal Q/K/V
head counts (`GgmlBasicOps.cs:5992`). The prototype consequently performs eight
K/V head-copy calls per layer, or 192 per request (`MetalText.cs:198`).

`FusedPrefillAttention` already accepts unequal head counts. Its precision-preserving
overload uses exact sequence extents and avoids the F16 flash path
(`GgmlBasicOps.cs:2820`, `GgmlNative.cs:5802`, `ggml_ops_norm_attn.cpp:2931`). It
requires contiguous head-first inputs. For this unpadded, at-most-512-token probe,
`maskStartPos = kvLen - 1`, `slidingWindow = 0` and `scale = 1` yield an all-zero
mask: every valid key index is at or below every query threshold
(`ggml_ops_norm_attn.cpp:2791`). This makes grouped attention a reuse candidate
without physically repeating K/V heads, but introduces input-layout conversion.
Its net cost and output parity are unmeasured. These mask parameters do not implement
long-context bidirectional sliding attention. The ordinary cached causal path also
pads K/V extents; it is not an interchangeable choice for this mask construction.

Metal async mode is already enabled by default (`GgmlContext.cs:223`). However,
the operations above call `tsg::compute_graph`, which calls the synchronous
`ggml_backend_graph_compute` (`ggml_ops_internal.h:476`). The profiles confirm
waits beneath that call. Skipping the later `finalize_compute` synchronization does
not remove a wait that has already occurred. Turning on the existing async flag
alone therefore does not make this execution path asynchronous. Fewer graph calls
avoid some waits without changing the native synchronization contract.

The probe also reads 23 invariant layer scalars through a host-read barrier per
request and recreates position tensors for Q and K in every layer. It copies all
token projections to managed memory for pooling (1.5 MiB at 512 tokens). These
costs are visible in the source but have no separate timing attribution. The cause
of repeated-call timing drift remains unresolved.

Raw profiles and vectors reside in the ignored
`artifacts/gemma2/performance-investigation/` directory. The investigation changes
no executable code or native driver. It identifies reusable mechanisms and measured
overhead; it establishes no optimized TensorSharp speedup or engine selection.

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


## Shared fixes and resident-row qualification, 2026-10-13

The shared host-copy and ownership fixes pass the measured native and model checks.
Resident FP32 row lookup returns exactly the fused control's embeddings. ONNX WebGPU
remains faster on this Mac. Host and whole-graph code remain untouched.

**Source and driver.** Inference `61da38bb` supplies the executable. TensorSharp
`8eb71550` adds the source/destination host-copy barriers and unscoped owner completion;
`a4f00448` refreshes the generated export inventory; `f1f4d0f1` completes storage
registration when context lease acquisition fails before memory allocation. That last
regression first fails with owner count changing from zero to one, then passes with
zero remaining registrations. Unscoped completion requires the exact owner, empty
scopes and zero active effects. CUDA/MLX retain their existing gated release paths.

The approved identity-only relink validates all 59 unchanged native object/archive
inputs and recompiles the identity object. The 296 required exports, native source
tree and pinned GGML revision remain unchanged. The final driver identifies source
`f1f4d0f16b351cf2e53fa3e10f03cbe361df3519`, fingerprint
`6dd126abcccee668a0b51406a4df3ec3d5fef638b3ab9e44eebbfe1c486431a5`, and binary SHA-256
`be0af1000e34872f66b110cbfe8fec8e246035596ec7a056bfdaefb11a8f36a4`.
The original shared driver retains its original bytes. A local test package supplies
the checked catalog and runtime files to the probe output. Nothing is published.
CMake configuration reports the existing optional OpenMP absence; the reused backend
objects do not change. Managed builds have zero warnings/errors.

**Focused native checks.** Both actual CPU and Metal selections pass the existing
runtime fixture. It checks contiguous/strided copy results with deferred writes on
both source and destination, performs a real native addition, and verifies that
context/storage, worker, refused-construction and native-allocation cleanup return
ownership registrations to the original count. Both terminal shutdowns succeed.
The copy regression uses real GGML pool storage and a deterministic pending-write
stand-in at the storage barrier. The model runs below exercise default Metal async
execution with the shared fix and no probe-only copy barrier.

Eight managed authority cases pass: unattached-release, healthy, promotion,
lease-negatives, mismatched-lease, race, constructor and lock-order. The existing
wildcard fixture fails because its independent MLX owner calls release without the
required destructive reservation. That unchanged path is not a passing qualification.

**Numerical checks.** Two complete rounds measure six calls per provider at 12, 128
and 512 tokens. The second reverses the candidate order. Two 24-call profiles at 128
tokens exercise the fused and resident-row variants. All 192 outputs pass, including
all first calls and all 120 TensorSharp calls. The largest component difference from
ONNX CPU is 6.570906e-5, minimum cosine is 0.9999998994, and largest norm error is
3.55e-7. Bounds remain 1e-4, 0.999999 and 1e-6 respectively. Every process repeats its
vector exactly; every resident-row vector exactly matches the fused control for
that input. All 14 TensorSharp processes report successful native shutdown.

**Warm latency.** Values are medians of five calls after the first call, in milliseconds.
Each provider/input combination uses its own process. ONNX profiling remains enabled;
TensorSharp's timing rounds have no intermediate trace or phase profiling enabled.
Every WebGPU profile identifies 1032 distinct node events on WebGPU and 37 on CPU;
GPU selection does not imply that every node runs on the GPU.

| Runtime | 12 tokens | 128 tokens | 512 tokens |
| --- | ---: | ---: | ---: |
| ONNX CPU | 12.75 | 29.99 | 124.51 |
| TensorSharp fused, generic row lookup | 176.42 | 220.18 | 422.68 |
| TensorSharp fused, resident FP32 rows | 102.37 | 135.92 | 301.27 |
| ONNX WebGPU | 30.77 | 36.50 | 82.95 |

| Reverse-order confirmation | 12 tokens | 128 tokens | 512 tokens |
| --- | ---: | ---: | ---: |
| ONNX CPU | 12.87 | 34.84 | 135.01 |
| TensorSharp fused, generic row lookup | 171.29 | 219.20 | 405.57 |
| TensorSharp fused, resident FP32 rows | 96.77 | 144.99 | 315.09 |
| ONNX WebGPU | 30.63 | 30.31 | 87.71 |

Resident rows reduce warm latency by 22–44% across these input lengths and rounds. WebGPU is about 3.2–4.8 times faster than the resident-row path.
The shared workstation produces outliers and changing load times; these are measured
ranges, not a throughput guarantee or a finished engine benchmark.

At 512 tokens, main-round first inference is 545.25 ms for fused, 363.68 ms for rows,
112.17 ms for WebGPU and 143.59 ms for CPU. Their respective load times are
886.21, 865.58, 643.51 and 182.41 ms. Runtime initialization is outside the load timer.
Peak RSS is 4416.50, 3950.36, 1201.28 and 862.25 MiB respectively. Resident rows lower
that measured peak by 466 MiB. RSS includes loading/transient allocations and pools;
it is not steady model memory or dedicated GPU memory. Reverse-round WebGPU peak
RSS is 1548.94 MiB, illustrating process/run variation.

**Ownership and phase profile.** Both 24-call profiles retain exactly 321 registrations
and 321 live targets after every call. The old growth of 341 registrations per request
is absent. First-five versus last-five warm medians are 219.67 to 210.48 ms for fused,
and 135.10 to 129.76 ms for resident rows. No accumulating latency trend appears in
these measured runs. The host-observed embedding phase falls from a warm median of
82.08 ms to 0.282 ms. Phase boundaries add no intermediate readback, so that number
is not an independently synchronized GPU kernel duration.

**Evidence and limits.** The ignored `artifacts/gemma2/shared-fixes` directory contains
all raw vectors, logs, provider profiles and `shared-fixes-summary.json`. The summary
contains every call's parity measurements, both timing rounds and the ownership
profiles. TensorSharp's `artifacts/dsa-993-shared-fixes` holds the native checks,
constructor regression, relink proof and local test package. The shared source,
export inventory and reports are committed; generated artifacts are not. Scoped
format checks pass for the experiment, runtime fixture, storage and ownership files.
A broader check reports existing whitespace findings in unchanged portions of
`GgmlBasicOps.cs`; those unrelated lines remain unchanged.

The result qualifies these synthetic, unpadded FP32 text sequences and focused
lifetime checks on macOS arm64. Tokenization, retrieval quality, multimodal inputs,
long contexts, cancellation, Linux/Windows execution and the unchanged failing MLX
fixture remain outside this qualification. No shipping engine is selected or registered.
## ONNX native platform and image qualification, 2026-10-13

The FP32 text and reference-prepared image paths pass on macOS arm64, Linux x64 and
Windows x64 with ORT 1.30.0 and the external WebGPU provider 0.4.0. Explicit
CPU/default input memory fixes the measured plugin binding failure. All final CPU
and plugin processes exit successfully. The shared image-preprocessing candidate
fails resized-image parity, so the complete qualification is not a pass.
No shipping engine is registered. Host has no changes.

**Runtime and inputs.** Inference `9b6fff59` supplies the final explicit-memory
executable. `e507e3cc` supplies the initial packaged provider experiment and
`2998e7d6` supplies the image probe. The pinned model revision remains
`daa72c51243991dfcaf9f9137d2c573d8f7790c0`. `prepare.py --vision` verifies the FP32
vision graph and its 671,026,176-byte external weights; the text weights contain
1,084,170,240 bytes. No model conversion or native compilation is involved.

| Platform | Hardware and runtime |
| --- | --- |
| macOS 26.7.1 arm64 | Apple M4 Pro; .NET SDK 11.0.100-rc.1.26425.128 |
| Ubuntu 24.04 x64 | Two Tesla P40 GPUs; NVIDIA 580.178.04; same .NET SDK |
| Windows 11 Enterprise 10.0.26200 x64 | RTX 3090 Ti; NVIDIA 32.0.15.9597; same .NET SDK |

The Linux core NuGet runtime exposes CPU only. The Windows core exposes Azure and
CPU providers. The Mac core also includes WebGPU. The external package supplies
the measured GPU path on all three platforms. Mac device enumeration contains both
built-in 1.30.0 and external 0.4.0 providers under the same name. The probe selects
external devices by their `library_path` metadata. Linux and Windows runs select
reported NVIDIA vendor 4318; logs retain device and library metadata. The Windows
entry names the RTX 3090 Ti. The Windows Intel UHD 770 is not qualified.

**Input memory and cleanup.** The ordinary C# named-value input API creates CPU
memory with `OrtMemType.Cpu`, value -1. The external provider's tensor adapter
rejects that value as a device memory type. Mac and Linux controls abort before
inference completes. The Linux control reproduces on the NVIDIA selection too.
Explicit `OrtMemoryInfo("Cpu", DeviceAllocator, 0, Default)` with the `OrtValue`
input API avoids the failure without changing tensor values. The qualified plugin
runs set `GEMMA_DEFAULT_HOST_MEMORY=1`. The probe owns and disposes the pinned inputs,
run options, outputs and sessions. It does not patch native code.

Source evidence: [C# memory defaults](https://github.com/microsoft/onnxruntime/blob/v1.30.0/csharp/src/Microsoft.ML.OnnxRuntime/OrtAllocator.shared.cs)
and [external-provider tensor conversion](https://github.com/microsoft/onnxruntime/blob/f2c39fe/include/onnxruntime/ep/adapter/tensor_helper.h).

The built-in Mac image control computes all 18 vectors, then crashes during session
teardown in `BufferManager::Release`, below GPU tensor and session destruction.
Its vectors pass parity; its process does not pass. A separate-context control fails
because nonzero context IDs require an explicit native WebGPU instance/device.
The external provider with explicit default host memory completes both sessions and
teardown on all three platforms. No custom native-context implementation is added.

**Numerical coverage.** The final matrix contains 198 embeddings: 108 external
WebGPU outputs and 90 CPU outputs. It covers six calls per case, including the first,
text lengths 12/128/512 and three image fixtures. Mac CPU text comes from the existing
FP32 reference round; this matrix adds Linux/Windows CPU text and CPU images on all
three machines. All 198 embeddings pass the existing bounds: maximum component
error 1e-4, cosine at least 0.999999 and unit-norm error at most 1e-6.

The largest measured component error is 4.5e-7, minimum cosine is
0.9999999999975431 and largest norm error is 8.9946e-7. Every case repeats its
embedding exactly within its process. Raw, unnormalized vision features have maximum
component difference 0.000243187, relative L2 error at most 1.02301e-5 and minimum
per-token cosine 0.999999997987366. These feature measurements remain separate from
the bounds for normalized 768-component embeddings.

The summary retains 222 captured vectors including 18 from the failed built-in Mac
cleanup control and six from an initial Mac run that selects the built-in provider.
Those 24 vectors do not count toward the final external-provider qualification.
Aborted controls and the rejected separate-context run remain explicit failures in
its execution list. The summary verifies the expected final case list, six calls
per case and successful process exits. It reports `runtimePassed: true` and
`preprocessingPassed: false`, and returns exit code 1 for the remaining failure.

**Image contract.** The deterministic square, landscape and portrait fixtures use
RGB gradients and sharp checker boundaries. The pinned
[PIL reference recipe](https://github.com/huggingface/transformers/blob/e880ddc57baa4e642c357e7c0a8e77b8d7f1b3f0/src/transformers/models/gemma4/image_processing_pil_gemma4.py)
produces 16x16 RGB patches, float32 values in [0,1], zero padding to 2,520 patches,
and int64 [column,row] positions padded with -1. Vision emits [tokens,512]. The
main graph receives BOS, BOI, one image placeholder per vision token, EOI and EOS,
plus empty [0,512] audio/video features. The square emits 256 tokens; the two resized
cases emit 253. The reference files carry SHA-256 hashes checked by the executable.

The existing `Gemma4ImageProcessor` with reference sizing and [0,1] normalization
matches target dimensions, patch order, padding and positions. The unresized square
matches all pixel values exactly. Resized inputs fail:

| Fixture | Original → target size | Maximum pixel error | Candidate pixel range |
| --- | --- | ---: | --- |
| Square | 768x768 → 768x768 | 0 | 0 to 1 |
| Landscape | 1200x600 → 1104x528 | 0.141823649 | -0.141824 to 1.141824 |
| Portrait | 201x401 → 528x1104 | 0.159111992 | -0.159112 to 1.159079 |

The shared float bicubic path retains overshoot and floating-point intermediates.
PIL resizes RGB byte images with clipping and quantization. Clamping only the final
float output does not reproduce the intermediate rounding. These tests establish
a preprocessing reuse gap; they do not establish the retrieval impact. Encoder
execution uses the independently prepared reference inputs. No shared resizer change
or second production preprocessing system is part of this experiment.

**Warm timings.** Values are medians of five calls after the first, in milliseconds.
ORT profiling remains enabled. Machines are shared and CPU/GPU runs occur at different
times; these values describe the measured processes, not an isolated hardware ranking.

| Platform/provider | Text 12 | Text 128 | Text 512 |
| --- | ---: | ---: | ---: |
| Mac external WebGPU | 17.11 | 25.63 | 76.08 |
| Linux CPU | 72.63 | 161.41 | 785.77 |
| Linux external WebGPU | 42.20 | 36.54 | 66.23 |
| Windows CPU | 35.75 | 78.36 | 259.10 |
| Windows external WebGPU | 25.58 | 25.86 | 26.84 |

Each image cell shows vision encoding / main embedding, in milliseconds. Feature
readback is included; file serialization and general image preprocessing are outside
these timers. They are not end-to-end indexing latencies.

| Platform/provider | Square | Landscape | Portrait |
| --- | ---: | ---: | ---: |
| Mac CPU | 1518.00 / 93.00 | 1503.26 / 75.43 | 1455.51 / 67.69 |
| Mac external WebGPU | 508.38 / 40.69 | 510.11 / 43.34 | 520.28 / 44.73 |
| Linux CPU | 2980.16 / 273.18 | 2900.90 / 268.59 | 2881.26 / 280.96 |
| Linux external WebGPU | 490.58 / 87.86 | 488.32 / 88.30 | 488.95 / 88.62 |
| Windows CPU | 10577.40 / 3035.87 | 10683.31 / 3022.62 | 10618.26 / 3096.50 |
| Windows external WebGPU | 119.93 / 30.55 | 135.99 / 100.40 | 137.96 / 103.61 |

Every final plugin text profile identifies 1,032 distinct GPU node events and 37 CPU
events. Vision profiles identify 677 GPU events and 40 CPU events. CPU shape/index
partitions remain; selecting WebGPU does not move every node to the GPU.

**Environment and coexistence.** Windows has an isolated .NET 11 SDK at
`C:\Users\Public\Darkspyre\agent\dotnet`. The SDK archive matches Microsoft's
published SHA-512; no PATH or machine-wide installation change is involved. The SSH
account is WSL `agent`, while native Windows execution reports `azuread\brandon`.
Linux uses its existing SDK. Both GPU arbiters remain running with unchanged settings.
Linux has about 24 GB free per GPU and requires no model unload. The Windows model
unload/load API provides the test window: reported GPU usage falls from about
22.7 GB to 886 MiB, then returns to 22,696 MiB with the same model loaded after tests.
The restore runs while the arbiter is idle with no waiters. ComfyUI reports unreachable;
no ComfyUI action occurs. These manual checks do not implement arbiter integration
or qualify simultaneous production workload scheduling.

**Evidence and limits.** `artifacts/gemma2/qualification/` retains reference inputs,
every output, process exit records, native failure summaries, provider profiles,
hardware metadata, arbiter operation results and `qualification-summary.json`.
The executable builds with zero warnings/errors; scoped C# formatting and Python
syntax checks pass. No full suite runs. General tokenization, retrieval quality,
quantization, video decoding, long contexts, cancellation and concurrent production
loads remain unqualified. Shared preprocessing parity is the observed remaining
failure. The source and results stay in the isolated probe.
## Shared RGB resizer and image parity, 2026-10-13

The shared resizer matches all reference preprocessing fixtures exactly on Mac,
Linux and Windows. The complete shared-preprocessing image matrix passes all
108 embeddings against the frozen Mac CPU reference. All six final CPU/WebGPU
processes exit successfully; all three preprocessing processes also exit zero.

TensorSharp `64532dc6` changes the existing bicubic helper to use 22-bit fixed-point
coefficients, source-window rounding and byte clipping after each separable pass.
Normalization follows the vertical pass. The existing white compositing helper
supplies RGB bytes. The Gemma4 reference-sizing caller uses this shared correction;
the letterboxed bilinear path retains its existing behavior. No second production
preprocessor is introduced. The behavior follows
[Pillow 12.1.1 RGB resizing](https://github.com/python-pillow/Pillow/blob/12.1.1/src/libImaging/Resample.c).

The frozen Pillow regression first fails on the original upsample and downsample
paths. All three regression cases pass with the correction, including unchanged
size, and both existing Gemma4 image tests pass: five focused cases, no skips.
Managed builds have zero warnings/errors. Scoped C# formatting, Python syntax and
diff checks pass. No native compilation occurs.

Inference `88cad369` adds `images-shared` to the existing isolated experiment.
It feeds the existing Gemma4 processor and shared patch packing into both ONNX
graphs. The image summary requires this mode, six outputs per case, complete
CPU/WebGPU coverage on all three platforms and successful process cleanup.
Frozen-input controls cannot substitute for the shared path.

| Check | Result |
| --- | --- |
| Square, landscape and portrait preprocessing on all three platforms | Zero pixel error; shapes, positions and padding match |
| CPU and external WebGPU image embeddings | 108/108 pass |
| Largest normalized embedding component error | 4.5e-7 |
| Minimum embedding cosine | 0.9999999999975431 |
| Largest unit-norm error | 8.9946e-7 |
| Within-process repeat error | Zero |
| Final preprocessing and inference process exits | All zero |

Mac CPU features and embeddings exactly match the frozen reference. Across all
platforms, raw vision-feature maximum error is 0.000243187, relative L2 error is
at most 1.02301e-5 and minimum token cosine is 0.999999997987366. GPU profiles
retain 677 GPU/40 CPU vision events and 1032 GPU/37 CPU main-graph events.
The reference model revision, FP32 precision, ORT 1.30.0 and external WebGPU 0.4.0
remain the same as the preceding qualification.

The first Windows GPU test copy omits packaged `dxcompiler.dll` and `dxil.dll` and
fails during provider creation, before inference. Its failure evidence remains
under `windows/controls/missing-dxc`. Including those existing package dependencies
resolves startup. Both Windows GPU windows use gpu-arbiter's unload/load API; Qwen
is restored successfully after each. The final state reports the same idle model
loaded and 22,676 MiB used. Arbiter settings and ComfyUI have no changes.

Evidence resides under ignored `artifacts/gemma2/resizer-fix/`: all patches,
vectors, features, profiles, process exits, arbiter states and
`qualification-summary.json`. The summary reports `runtimePassed: true`,
`preprocessingPassed: true`, 108 calls and no missing or failed matrix cases;
the comparison exits zero. The earlier failed qualification remains separate.
Host and the production Inference.Tensor engine have no changes. These results
cover the three RGB fixtures and bounded model execution, not general tokenizer,
retrieval quality, video decoding, long-context or concurrency qualification.


## Production Gemma library and Connect path

The production-library fixture uses the pinned FP32 graphs and tokenizer with the shared TensorSharp
preprocessing. The Mac CPU and WebGPU runs match all 19 Hugging Face tokenizer cases, seven text
inputs and three image inputs. Direct and Connect vectors match exactly on each backend. The largest
CPU/WebGPU component difference is 4.21e-7; every cosine exceeds 0.99999999999. Three retrieval
queries rank their expected document first. Oversized-batch rejection, cancellation followed by reuse,
unload and pack removal pass. Results are in `artifacts/gemma2/engine/mac-{cpu,webgpu}/result.json`.

On this M4 Pro run the subsequent Connect calls take 8–18 ms for short text on CPU and 17–25 ms on
WebGPU. Images take 1,422–1,482 ms on CPU and 556–579 ms on WebGPU. These are fixture timings,
not a general model throughput claim. Linux and Windows production-library qualification is not
established by these Mac results.

## Production ONNX engine and Connect route

The independent Gemma library passes CPU and external WebGPU qualification on Mac arm64, Linux x64 and Windows x64. The same managed tester binary runs on all three. Each run checks 19 reference token sequences, seven text inputs and three image inputs. Every input runs directly and through SelfHost Connect. All 120 measured calls agree within the existing execution bounds; direct/Connect vectors match exactly.

The maximum component difference from Mac CPU is `4.425e-7`. Image outputs also agree with the prior frozen CPU reference. All runs pass three retrieval rankings, whole-batch over-limit rejection, active cancellation, reuse, unload and model-pack removal. The fixed Christmas recall case scores about 0.704 for the related document and 0.507–0.572 for the three unrelated documents at the agreed 0.60 threshold. This fixture does not establish general corpus relevance.

| Platform/backend | Load ms | Median subsequent text call ms | Median subsequent image call ms |
|---|---:|---:|---:|
| mac-cpu | 4135 | 10.1 | 1442.7 |
| mac-webgpu | 4961 | 19.0 | 562.7 |
| linux-cpu | 8023 | 45.9 | 3337.7 |
| linux-webgpu | 11593 | 44.7 | 631.2 |
| windows-cpu | 5124 | 341.6 | 13555.7 |
| windows-webgpu | 5515 | 25.6 | 241.4 |

These timings measure one direct call followed by one Connect call per fixture, including preparation. They are bounded qualification observations, not sustained-throughput benchmarks. Hardware is Apple M4 Pro, Linux Tesla P40 and Windows RTX 3090 Ti. The Windows GPU run temporarily unloads the idle qwen3.8-27b model through gpu-arbiter and restores it afterward. The arbiter returns to the same loaded/idle state.

The four existing console/serialization checks pass with JSON reflection defaults disabled. Fourteen SelfHost/embedding checks, forty managed-conversation checks and nine embedding/lifetime checks pass. Package-input checks pass without creating archives; the package verifier builds with zero warnings. No package is published. The checks use no Harness Host process or source changes.

The captured vectors and machine-readable comparison are under `artifacts/gemma2/engine/`. `summarize_engine.py` requires all six platform/backend results and fails on a missing run.
