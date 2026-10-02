# Qwen3.8-27B on the RTX 3090 Ti

The 2026-10-02 comparison runs both engines on Windows `gpu-box`, using the
RTX 3090 Ti (24 GB, compute capability 8.6) and NVIDIA driver 595.97.
llama.cpp is 9.82 times faster than TensorSharp's measured direct `Cuda` path.
This result does not qualify TensorSharp's separate `GgmlCuda` backend.

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
