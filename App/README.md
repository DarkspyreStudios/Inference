# GPU arbiter

The GPU arbiter shares one Windows x64 NVIDIA GPU between llama.cpp and a user-launched
ComfyUI instance. The app targets .NET 10. Its solution, version and dependencies live in
`App/`. The four Inference NuGet packages have a separate release.

The app listens on HTTP port 8080 for OpenAI-compatible inference and port 8189 for the
ComfyUI proxy. The supervised llama.cpp router listens on loopback port 8081. ComfyUI
listens on port 8188. The app does not start, stop or modify ComfyUI.

## GPU ownership

One coordinator owns admission, queued requests, live request counts and handoffs. Requests
share the current model until another target waits. New requests for that model then queue.
The first queued target receives the next handoff. Its queued requests share that acquisition.
An explicit unload acts as an admission barrier. A disconnected waiter is removed.

A handoff waits for active LLM forwards or the ComfyUI queue and prompt forwards to drain.
The coordinator unloads other router models, frees cached ComfyUI models before LLM
acquisition, and checks VRAM release before loading the requested model. It polls asynchronous
model status after load and unload. Failed loads, load timeouts and release timeouts return 503.
Load errors include the reported router exit code or `unavailable` when no exit code is reported. Idle models
remain loaded until another target requests the GPU.

Submit ComfyUI jobs through port 8189. These requests wait before `/prompt` reaches ComfyUI.
The proxy forwards other routes and websockets through YARP.

The ComfyUI watcher polls `/queue` to reconcile exceptional direct submissions on 8188.
These jobs can allocate VRAM before the LLM unloads. The watcher waits for active LLM
requests to drain and confirms model unload, then records ComfyUI as holder without the
idle VRAM check. ComfyUI's own allocation can exceed that threshold. A blocked LLM waiter
does not prevent this reconciliation; LLM waiters retain their order until ComfyUI drains.
Gated prompts and LLM acquisitions retain the VRAM release check.

## HTTP interface

- `GET /gpu` returns holder, phase, handoff step, LLM request count, ComfyUI queue state, waiters and measured VRAM usage.
- `POST /models/load {"model":"id"}` waits for admission and a loaded model before returning `{"success":true}`.
- `POST /models/unload {"model":"id"}` drains requests and waits for confirmed unload.
- Inference POST routes include chat completions, completions, embeddings, responses, messages,
  infill, tokenize, detokenize and apply-template. Their JSON `model` identifies the target.
- GET routes on 8080 forward to the llama.cpp router.

A streaming request that waits two seconds receives SSE comments describing its wait. The
model output bytes pass through unchanged after admission. An upstream failure after SSE
headers have been sent becomes a JSON error event. Non-streaming requests receive no bytes
before admission. The app has no API key; the host firewall must limit network access.

The Windows supervisor assigns llama-server to a kill-on-close Job Object before admitting
traffic. A router exit cancels active forwards and clears LLM ownership. The router restarts
with exponential backoff from one second to thirty seconds. Daily arbiter and llama-server
logs live in the configured log directory. Request bodies and credentials are not logged.

## Configuration and build

`deploy/appsettings.json` is the configuration source copied into the build output. All
paths are absolute. `deploy/presets.ini` defines model IDs and model-specific context sizes.
The Qwen preset uses a measured 98,304-token shared KV pool with two parallel slots on the
RTX 3090 Ti. One request can use the full pool; concurrent requests share its capacity.
An 88,473-token prompt completes with at least 1,847 MiB free on the configured host.
The 131,072-token profile leaves less than the required 1 GiB margin.
Deployment on a different GPU requires measurement.
`ReleasedMaxUsedMiB` must equal measured idle VRAM plus a margin.

Run from `App/` so `global.json` selects the .NET 10 SDK:

```bash
dotnet restore GpuArbiter.slnx
dotnet build GpuArbiter.slnx --no-restore -c Release
dotnet test GpuArbiter.slnx --no-restore -c Release
dotnet format GpuArbiter.slnx --no-restore --verify-no-changes
dotnet list GpuArbiter.slnx package --vulnerable --include-transitive
dotnet publish src/GpuArbiter -c Release -r win-x64 --self-contained false
```

Tests use fake backends, a controlled clock and an in-memory HTTP server. They make no model
calls. The application is not packable and does not create NuGet archives.

## ConsoleTester

From the repository root, use the Connect ConsoleTester against a running arbiter:

```bash
echo 'Reply only with OK.' | dotnet run --project Connect/tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  prompt --provider openai-compatible-api --endpoint http://gpu-box:8080/v1 --model qwen3.8-27b

echo 'Call darkspyre_inference_connect_echo with text Hello, then reply with its result.' | \
  dotnet run --project Connect/tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  agent --provider openai-compatible-api --endpoint http://gpu-box:8080/v1 --model qwen3.8-27b
```

The endpoint override is local to that process. An unauthenticated endpoint requires no login.
Use the machine's LAN or Tailscale IP if `gpu-box` is only an SSH alias.

## Windows setup and deployment

`deploy/setup-backends.ps1` downloads the pinned b11238 CUDA 13.4 x64 archives and the
Qwen Q4_K_M model. It validates official SHA-256 digests before extraction or use. It copies
the Echidna model into the AI layout. The old model and runtime folders remain available
until end-to-end verification completes. The model presets do not load an image projector.

The service layout is `G:\AI\llama.cpp\b11238`, `G:\AI\models`, and
`G:\AI\gpu-arbiter\app`. Logs remain outside the application directory so deployment
swaps preserve them. The host requires the ASP.NET Core .NET 10 x64 runtime.

Set NVIDIA Control Panel > Manage 3D Settings > Program Settings > CUDA - Sysmem Fallback
Policy to **Prefer No Sysmem Fallback** for `G:\AI\llama.cpp\b11238\llama-server.exe`
and `G:\Image\ComfyUI_windows_portable_2\python_embeded\python.exe`. A runtime upgrade
into another directory requires this setting for its new executable path.

Measure Qwen with q8_0 K/V caches and no projector. Try context sizes 131072, 98304, 65536,
49152 and 32768 in descending order. The selected context must complete a request filling
about 90 percent of its capacity with at least 1 GiB VRAM free. Measure `parallel = 2` with
`kv-unified` separately. With all models unloaded and ComfyUI
idle after `/free`, set `ReleasedMaxUsedMiB` to idle usage plus 1024 MiB.

Copy the Windows publish output into `G:\AI\gpu-arbiter\app` and the measured presets
into `G:\AI\models\presets.ini`. The user runs `deploy/install.ps1` once from an elevated
PowerShell. It prompts for the existing `h3x1e\gpu-inference` account password, grants its
service logon right and filesystem permissions, registers delayed automatic start, grants
service start/stop/query access, and opens ports 8080 and 8189 to LocalSubnet and Tailscale.
The password is never written to a configuration file.

Later updates use `deploy/deploy.sh [ssh-host]`. The script publishes Windows x64, copies
the app into `app-new`, and invokes `deploy.ps1` to stop, swap and restart the service.
The measured machine configuration is preserved. A failed health check restores the previous
application. Service rights granted by the installer permit updates without elevation.
