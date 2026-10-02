# GPU arbiter with Splash

This C# application runs the installed Splash engine on Apple Silicon macOS.
It shares the App coordinator and ComfyUI controls with the llama.cpp and Tensor arbiters.
Only one arbiter may own admission on this machine at a time.

The default model ID is `qwen3.8-27b-splash`.
Its installed pack is `/Volumes/Data/Models/Qwen3.8-27B-Splash`.
The runtime paths select the installed Homebrew Splash 1.1.0 package.
The host never downloads weights and does not modify the installed runtime.
Startup validates the pack manifest and artifact lengths.

## Run

From `App/`:

```bash
dotnet restore src/gpu-arbiter-splash
dotnet build src/gpu-arbiter-splash -c Release --no-restore
dotnet run --project src/gpu-arbiter-splash -c Release --no-build
```

The inference and control listener is `http://127.0.0.1:8084`.
The ComfyUI proxy is `http://127.0.0.1:8191`.
The private Splash server uses loopback port 8085.
ComfyUI remains independently operated on port 8188.
The app starts no model until a load or inference request arrives.

```bash
curl http://127.0.0.1:8084/v1/models
curl http://127.0.0.1:8084/gpu
curl http://127.0.0.1:8084/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"model":"qwen3.8-27b-splash","messages":[{"role":"user","content":"Reply only with OK."}],"max_tokens":16,"temperature":0}'
curl http://127.0.0.1:8084/models/unload \
  -H 'Content-Type: application/json' -d '{"model":"qwen3.8-27b-splash"}'
```

`appsettings.json` contains paths, ports, model catalog, and limits.
Runtime paths must match the installed package version after a Homebrew upgrade.
The scratch directory must be an absolute, writable task-owned directory.
ASP.NET configuration environment variables can override settings, such as `Splash__ScratchDir`.
The default context is 16,384 tokens with int8 KV and reasoning `none`.
Requests can select native supported reasoning levels.
The default disk cache is disabled.
No automatic startup service is installed.

## API

The host forwards native request and response bytes for these operations:

| Operations | Behavior |
| --- | --- |
| Chat Completions and Responses | Streaming, tools, structured output, and native supported multimodal input |
| Anthropic Messages and count_tokens | Native protocol, including Messages streaming |
| tokenize, apply-template | Native tokenizer/template operations under model admission |
| judgments, systemone | Native non-streaming scoring operations |
| Responses GET and DELETE | Read or delete within the current resident instance |

Every inference POST requires a configured string `model` ID.
Provider fields remain opaque. Splash validates their meaning.
`GET /v1/models` and `GET /v1/models/{id}` describe the configured installed catalog without loading it.
Model discovery reports configured context and does not invent modality capabilities.
Unsupported operations, including embeddings, legacy completions, audio, and image generation, return an explicit error.
Non-streaming native routes reject `stream: true`.

Stored Responses and `previous_response_id` continuation belong to one live Splash server instance.
Unload, model switch, crash, and shutdown invalidate its stored IDs.
Response reads and deletes never load a model and hold a resident lease during access.
The host adds no durable response store or background execution service.

The host exposes `GET /gpu`, `POST /models/load`, and `POST /models/unload`.
An explicit unload waits for active work, then confirms server and native-engine process-group termination.
Queued streams receive SSE wait comments after two seconds.
Disconnect cleanup retains the model lease until fresh native status confirms completion.
Unconfirmed cancellation stops the owned process group and invalidates its other active leases.

Submit ComfyUI prompts through port 8191.
Other routes and websockets pass through the ComfyUI proxy.
Exceptional direct submissions on 8188 retain shared coordinator reconciliation.
ComfyUI must drain and acknowledge cache release before Splash loads.

## Memory and process ownership

`/gpu` reports ownership, queued requests, ComfyUI state, and safe Splash allocation/health observations.
`vramUsedMiB` is null on Mac because the host has no machine-wide Metal allocation probe.
The release basis is `owned-process-termination`.
Splash memory values describe its own allocations, not total machine GPU usage.
Unavailable and stale native status remain explicit.

The Mac adapter creates a dedicated POSIX process group for Python and the native engine.
Readiness must identify the owned Python PID and a healthy native Metal engine.
Unload sends SIGTERM, then SIGKILL after the configured timeout, and confirms group disappearance.
An unexpected exit cancels active forwards; later requests can load a new instance after cleanup.
The host never replays a failed request.

Operational logs contain lifecycle facts and exit codes.
Child stdout and stderr are drained without retention.
Prompts, responses, credentials, and arbitrary native diagnostics are not logged.
The public listeners default to loopback and have no host API-key authentication.

The [technical design](../../../docs/design/gpu-arbiter-splash-tech.md) defines lifecycle and forwarding ownership.
