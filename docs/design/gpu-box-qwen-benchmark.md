# Qwen3.8-27B on the RTX 3090 Ti

The 2026-10-02 comparison runs both engines on Windows `gpu-box`, using the
RTX 3090 Ti (24 GB, compute capability 8.6) and NVIDIA driver 595.97.
TensorSharp's `GgmlCuda` rerun measures 28.77 tokens/sec, 6.87 times its direct
`Cuda` result. The saved llama.cpp result is 41.10 tokens/sec. `GgmlCuda` has
29.98 percent lower throughput than that llama.cpp baseline. The rerun changes
the Tensor backend and native bridge; it does not rerun llama.cpp.

## Matched input and measurement

Both paths use `Qwen3.8-27B-Q4_K_M.gguf`, 18,973,870,528 bytes. Independent hashes
of llama's model file and Tensor's installed pack artifact both equal
`c600de0300ae8a0eb3a6c0b8b5561b8b96f16bd2c863c2a66c42de29d391a747`.

The prompt is:

> Write a detailed explanation of how a hash table works, including collisions, resizing, and time complexity. Use concrete examples.

Each path uses a 16,384-token context, FP16 KV cache, temperature zero, disabled
reasoning and a 256-token output limit. A separate short array-definition request
warms each engine before three measured requests. Every measured request reports
36 prompt tokens, 256 completion tokens and the length finish reason. No streamed
reasoning text appears. Model loading and warm-up are excluded from response timing.

The client runs on the Mac through SSH loopback forwarding. Model execution occurs
on the GPU box. First-content and response times include the client connection.
Decode speed is `(completion tokens - 1) / (last content time - first content time)`.
The comparison uses the same client and OpenAI streaming endpoint protocol.

llama.cpp disables prompt caching for each measured request and reports zero cached
tokens. Tensor creates a new `ChatSession` for every request; its cached-token and
reasoning-token usage fields are null. Those null fields are not measured zeroes.

## Results

| Path | Median decode tokens/sec | Observed decode range | Median first content | Median response |
| --- | ---: | ---: | ---: | ---: |
| llama.cpp b11238, CUDA | 41.10 | 41.09–41.11 | 0.259 sec | 6.462 sec |
| Tensor arbiter, TensorSharp direct CUDA | 4.19 | 4.18–4.19 | 6.672 sec | 67.719 sec |
| Tensor arbiter, TensorSharp GGML CUDA | 28.77 | 28.75–33.69 | 0.900 sec | 9.724 sec |

The temporary llama server uses one sequence slot, all GPU layers and flash attention.
Its observed VRAM use is 19,763 MiB. It listens on loopback 8083; the normal llama
arbiter is unloaded during the experiment. The Tensor arbiter listens on 8082 and
uses an isolated copy of the existing application with the rebuilt CUDA DLL and
both `sm_86` PTX modules from TensorSharp commit
`c7fd85b5609a92c7c56dbce116914fc97d757fa3`.
Its package/model baseline is TensorSharp 2.8.6.7. This is a local library replacement,
not a new published package or a production application deployment.

Tensor's explicit load completes in 30.25 seconds, including arbiter handoff.
It reports 22,178 MB of resident quantized weights across 450 tensors and reaches
24,269 MiB total GPU memory. During generation its GPU utilization reaches 100%.
The process loads the rebuilt CUDA DLL and CUDA 12 cuBLAS libraries. Its console
has no unavailable-PTX, CUDA-error or slow CPU-fallback warning.

Tensor reports that direct CUDA has no fused whole-model prefill or decode graph
and uses per-operation loops. That diagnostic identifies a difference in execution
paths; this benchmark does not isolate each source of the speed difference.

## GGML CUDA rerun

The Tensor arbiter uses the same existing managed assemblies and model pack.
Its isolated configuration selects `GgmlCuda`. The bridge is the Release
`GgmlOps.dll` built from TensorSharp source
`c7fd85b5609a92c7c56dbce116914fc97d757fa3` and unchanged upstream GGML source
`456172ec733a135778adcd32d00e576a58232e45`, using CUDA 12.8 and MSVC 14.44 for
`sm_86`. Its SHA-256 is
`7b4d982af1f0d8b01d98230461acdfc3a549b7736721870e94d0c8864ecbb058`.
This is the same GGML backend family as the Mac's `GgmlMetal` comparison.

The rerun keeps the 16,384-token context, default FP16 KV, temperature zero,
disabled reasoning, separate 32-token array-definition warm-up and 256-token
hash-table requests. It uses the same OpenAI SSE timing calculation and Mac
client through SSH forwarding. Each measured request reports 36 prompt tokens,
256 completion tokens and the length finish reason. No reasoning text appears.
Cached/reasoning usage fields remain null. Each request uses a new ChatSession.

The three decode measurements are 33.69, 28.75 and 28.77 tokens/sec. The spread
is reported without discarding a run. The console confirms `Backend: GgmlCuda`,
21,496 MB of device-resident quantized weights across 449 tensors and the 16k
context limit. It contains no CUDA error or fallback diagnostic. Loading and
warm-up are excluded from timings. The native bridge's separate compatibility
smoke verifies native CUDA backend identity, GPU operations, attention and
16-token Qwen generation before this benchmark.

The original llama arbiter reloads its original 98,304-token context, Q8 KV and
two-sequence preset after the rerun. The copied Tensor server stops and its app
folder is removed. The SSH tunnel closes. No production application or preset
files change. Raw measurements, output, DLL hashes, configuration, console and
restoration evidence remain in this worktree's ignored `tmp/ggml-cuda-benchmark/`
and on GPU box under `G:\AI\benchmarks\qwen-ggml-cuda-20261002`.

## Direct CUDA execution verification

A separate 16-token generation uses the same prompt, model and default FP16
cache. SHA-256 checks confirm that Core, Chat, Models and Runtime assemblies
match the measured arbiter. Both PTX module hashes match the benchmark build.
The diagnostic CUDA DLL adds counters after successful `cuLaunchKernel` calls.
Its source is TensorSharp branch `codex-qwen-cuda-proof`, code commit
`047764a4`. The validation host uses the arbiter's bundled runtime.

The request reports 36 prompt tokens and 16 generated tokens. Its generation
phase records 3,509 successful kernel launch calls and zero counted CPU fallback
operations. The console contains no CUDA error or fallback warning. The calls
include 256 Q4_K projection calls, 49 Q6_K projection calls, 401 RMSNorm calls,
32 FP16 decode attention calls and 96 packed GatedDeltaNet calls.

The counts include CUDA graph capture calls and exclude subsequent graph
replays. They do not count every device execution. CPU fallback counters cover
`CudaCpuFallback.Invoke` operations, not all possible managed model paths or
CPU tokenization and sampling. The counters and generated output directly
confirm GPU model computation. This instrumented short run does not replace
the throughput measurements above.

The untouched `G:\AI\gpu-arbiter-tensor\smoke` folder still contains older PTX
modules. An isolated probe using those modules reports CUDA error 218 and CPU
fallbacks. Both the measured benchmark and the successful verification use the
rebuilt compatible modules. The benchmark result therefore applies to the
isolated updated application, not the untouched installed Tensor application.

Verification JSON, assembly/PTX hashes, console output and restoration evidence
remain in the TensorSharp proof worktree's ignored `tmp/qwen-cuda-proof/` and on
GPU box under `G:\AI\benchmarks\qwen-cuda-proof-20261002`. The copied probe
application is removed. The original llama arbiter holds Qwen and is idle.

## Restoration and evidence

After measurement, Tensor unload completes in 4.02 seconds. The original llama
arbiter reloads Qwen in 11.79 seconds. Its model listing confirms the original
98,304-token context, Q8 KV cache and two-sequence preset. The temporary llama
and Tensor processes stop, and the SSH forwarding connection closes. The production
preset and application files are unchanged.

Raw JSON timings, responses, setup, restoration observations and console logs remain
in the benchmark worktree's ignored `tmp/gpu-box-qwen/`. Copies remain on the GPU
box under `G:\AI\benchmarks\qwen-llama-tensor-20261002`. The copied test application
is removed after measurement. No ComfyUI server is running during this comparison.
