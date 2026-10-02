# gpu-arbiter-tensor

This Windows NVIDIA GPU host consumes `Darkspyre.Inference.Common` and
`Darkspyre.Inference.Tensor` 11.14.0-rc.1. It shares admission and ComfyUI controls with
`GpuArbiter` through `GpuArbiter.Core`. Model execution runs in process through the public
Tensor interfaces. The application targets .NET 11.

The default inference port is 8082. The ComfyUI proxy port is 8190. ComfyUI remains a
separately launched application on loopback 8188. Only one arbiter can own the GPU at a time.
Stop or idle and unload the other arbiter before admitting Tensor requests. Separate ports
permit startup checks but do not provide shared ownership between two processes.

## HTTP operations

| Route | Behavior |
| --- | --- |
| `GET /v1/models`, `GET /v1/models/{alias}` | Configured installed models; listing never loads weights |
| `POST /v1/chat/completions` | Messages, tools and tool results, JSON output, reasoning, multiple choices, JSON or SSE |
| `POST /v1/completions` | Text prompts and prompt arrays mapped to user messages, JSON or SSE |
| `POST /v1/responses` | Stateless text/message/function input, instructions, structured output and semantic SSE |
| `POST /v1/images/generations` | Engine-supported dimensions, counts and generation controls; base64 bytes |
| `POST /v1/audio/transcriptions`, `POST /v1/audio/translations` | PCM or IEEE float WAV uploads; JSON, text, verbose JSON, SRT or VTT |
| `GET /gpu` | Ownership, handoff, queue and NVIDIA VRAM observation |
| `POST /models/load`, `POST /models/unload` | Explicit lifecycle completion; body `{"model":"alias"}` |
| `GET /health/live` | Host liveness |
| Comfy proxy `/prompt` and websocket routes | Shared admission gate and direct-job reconciliation |

The selected engine must advertise each requested feature. Qwen accepts reasoning `none`
and `high`. Unsupported sampling fields, tool-choice controls, modalities and operations
return explicit errors. Images use `b64_json`; hosted URLs and image edits have no storage
contract. Audio accepts complete interleaved PCM frames, with model limits applied after WAV
decoding. Compressed audio formats and word timestamps are unsupported. Translation requires
an engine that advertises English translation.

Responses require the client to supply complete history. `store: true`, continuation by
`previous_response_id`, hosted tools and background execution are unsupported. Clients execute
function calls and submit their results. The host reports actual token usage or null. Chat SSE
ends with `[DONE]`; Responses SSE ends with `response.completed` or `response.incomplete`.
Queued streams emit wait comments after two seconds. Client cancellation disposes engine
execution before releasing GPU admission. Lifecycle acceptance does not grant admission;
the host waits for the engine's Loaded or Unloaded observation.

The request limit defaults to 25 MiB. Multipart uploads stay in bounded memory. The maximum
prompt/choice combination count defaults to eight. Prompt content is opaque text and never
becomes a command, path, markup or log entry. HTTP and engine error boundaries omit raw
provider payloads. The application has no API key. Restrict network access with the host firewall.

## Prepare a local Qwen model

Preparation is an explicit administration command. It imports through `QwenModelResolver`
and `TensorModelPackManager`, checks compatibility and artifact identity, and writes an immutable
manifest. Inference requests do not download weights. Import retains the original file and
copies the checkpoint into the pack store, so provide disk space for the copy.

From a Windows publish directory:

```powershell
.\gpu-arbiter-tensor.exe prepare-qwen `
  --file G:\AI\models\qwen3.8-27b\Qwen3.8-27B-Q4_K_M.gguf `
  --store G:\AI\Tensor `
  --manifest G:\AI\gpu-arbiter-tensor\qwen3.8-27b.json `
  --license Apache-2.0 --backend Cuda
```

The manifest destination must not already exist. Set `Tensor:Models` in `appsettings.json`
to its path, public alias, `darkspyre.tensor.qwen` implementation and selected backend.
All runtime paths must be absolute. Other registered engines use their public pack preparation
APIs as demonstrated by the Tensor ConsoleTester; the command above handles Qwen GGUF import.

## Native runtime and build

Run build commands from `App/` to select the .NET 11 SDK in `global.json`:

```bash
dotnet restore GpuArbiter.slnx
dotnet build GpuArbiter.slnx -c Release --no-restore
dotnet test tests/gpu-arbiter-tensor.Tests -c Release --no-build --no-restore
dotnet test tests/GpuArbiter.Tests -c Release --no-build --no-restore
dotnet format GpuArbiter.slnx --no-restore --verify-no-changes --include src/gpu-arbiter-tensor tests/gpu-arbiter-tensor.Tests
dotnet list GpuArbiter.slnx package --vulnerable --include-transitive --no-restore
dotnet publish src/gpu-arbiter-tensor -c Release -r win-x64 --self-contained true -o ../tmp/gpu-arbiter-tensor-win-x64
```

The private feed uses process-scoped `gh` credentials as documented by the workstation rules.
The self-contained publish carries .NET and ASP.NET Core; it does not install a machine runtime.
It is not a NuGet release. The published CUDA dependency owns the PTX files copied into
`cuda_kernels/`. The application contains no copied engine source.

The direct `Cuda` backend requires an NVIDIA driver and cuBLAS runtime DLLs discoverable on
PATH. CUDA Toolkit 12 or 13 provides those libraries. On the reference GPU box the pinned
llama.cpp runtime already provides CUDA 13 cuBLAS; a console launch can expose that directory:

```powershell
$env:PATH = 'G:\AI\llama.cpp\b11238;' + $env:PATH
.\gpu-arbiter-tensor.exe
```

The reference RTX 3090 Ti native smoke is blocked with this dependency pin. Both shipped
CUDA PTX files target `sm_120`; the GPU reports compute capability 8.6. The driver rejects the
main module with CUDA error 218 and TensorSharp reports a slow CPU fallback. Successful
startup and model listing do not prove GPU execution. Do not use this artifact's fallback
for a speed comparison or deploy it for CUDA inference on that GPU.

A compatible dependency needs PTX rebuilt from the matching TensorSharp source with
`CudaArch=compute_86` (or a verified portable target), then published under a new immutable
package version. Windows builds must satisfy the [GPU box compatibility baseline](../../../docs/design/windows-cuda-baseline.md).
The GPU box has no `nvcc` installation. A native build machine requires a CUDA compiler
and supported Visual Studio C++ build tools. Its compiler must produce kernels compatible
with the deployment driver; installing the newest toolkit alone does not establish compatibility.
The app continues to consume the package; it does not rewrite PTX or copy engine source.

The [RTX 3090 Ti Qwen comparison](../../../docs/design/gpu-box-qwen-benchmark.md)
uses local rebuilt dependencies in isolated test applications. Its three-run medians
are 33.71 tokens/sec for `GgmlCuda` with the Mac's GGML 0.25.3 revision,
4.19 for direct CUDA and 41.10 for the saved llama.cpp baseline. The earlier
GGML 0.24.0 run measures 28.77 tokens/sec. The GGML CUDA reruns use separately
built CUDA 12 `GgmlOps.dll` bridges.
These measurements do not change the published dependency pin or deployed application.

`GgmlCuda` is a distinct backend. Its CUDA bridge is not included in the published TensorSharp
2.8.6.7 package. Provision that bridge through the TensorSharp native build documented in
`Tensor/README.md` before selecting it. A managed package restore does not certify native GPU
availability or inference speed.

The shipped configuration is for the reference Windows host. Set its measured idle VRAM
threshold and release/load timeouts before deployment on another GPU. Logs remain outside the
application directory. Windows service integration uses service name `gpu-arbiter-tensor`.
Service registration, account ACLs and firewall changes are separate deployment actions.
