# Splash arbiter technical design

## Composition

`App/src/gpu-arbiter-splash` is an ASP.NET Core C# executable targeting .NET 10.
The App solution uses its existing .NET 11 SDK.
The app references `GpuArbiter.Core` and the existing YARP dependency.
It has no Tensor, TensorSharp, or llama.cpp runtime dependency.
`App/tests/gpu-arbiter-splash.Tests` owns its focused adapter and HTTP tests.

The branch starts from the completed Tensor arbiter branch because that branch owns the shared core.
The functional contract lives in `docs/design/gpu-arbiter-splash.md`.
The App ownership documents must describe both Windows and Mac applications when implementation adds the Mac app.
App remains independent of the Inference package release.

Default listeners are `http://127.0.0.1:8084` for inference and controls,
`http://127.0.0.1:8191` for the ComfyUI proxy, and `http://127.0.0.1:8085` for the private Splash server.
The default ComfyUI upstream remains `http://127.0.0.1:8188`.
Configuration may override the listeners.
The private upstream must remain loopback and its port must be distinct from both public listeners.
Only one arbiter may run against the same GPU and ComfyUI instance.

## Configuration and installed assets

Configuration names absolute paths to the packaged Python executable, server entry point, and native Splash binary.
It includes a catalog of public model IDs and installed pack directories.
Public model IDs use the native required `owner/repo` form.
The initial ID is `incoai/Qwen3.8-27B-Splash`.
Each pack supplies `target`, `draft`, `tokenizer`, and its original manifest.
Startup validates executable paths, catalog uniqueness, manifest format, and required artifacts.
Listing models reads that catalog without starting Splash.
Preparation is external administration; neither startup nor an HTTP request invokes a model downloader.

The reference runtime is Splash 1.1.0 under `/opt/homebrew/Cellar/splash/1.1.0/libexec`.
The reference pack is `/Volumes/Data/Models/Qwen3.8-27B-Splash`.
Paths are configuration, not runtime discovery guesses or embedded assumptions about future Homebrew layouts.
Missing assets produce an actionable startup error.

The server invocation follows the command assembled by Splash's installed launcher:

```text
<python> -u <server.py> <pack>/target <pack>/draft
  --tokenizer <pack>/tokenizer --model <public-model-id>
  --binary <native-splash> --host 127.0.0.1 --port <private-port>
  --max-context <configured-context> --kv-format int8
  --default-reasoning-effort none --no-webui
```

Arguments use structured argument arrays. No argument passes through a shell.
The initial profile uses a 16,384-token context, int8 KV, and reasoning `none`, matching the measured Mac profile.
Explicit request-level reasoning remains Splash's responsibility.
Memory limit and context are configurable native runtime settings.
Disk cache remains disabled unless explicitly configured.
Vision follows the installed pack and native runtime's capability report.
The app sets `PYTHONDONTWRITEBYTECODE=1` and a task-owned scratch directory for the child.
It does not modify Homebrew's installed files.

## Shared state and release confirmation

`GpuCoordinator` remains the sole owner of admission, fairness, live leases, handoffs, and ComfyUI reconciliation.
`SplashModelHost : IModelHost` owns the configured model processes and lifecycle observations.
It does not create another admission queue.

Replace the NVIDIA-only `IVramProbe` seam with `IGpuReleaseGuard`.
Its `ReadAsync` returns a `GpuReleaseObservation` containing `Released`, `Basis`, and nullable `GlobalUsedMiB`.
The coordinator retains its bounded release polling and stores the published observation.
The Windows guard runs the existing NVIDIA probe and evaluates the existing threshold.
The Mac guard checks that every owned Splash process group has terminated.
It reports `GlobalUsedMiB = null` and a process-termination basis.
ComfyUI queue drain and successful `/free` remain coordinator prerequisites before Splash acquisition.
The guard does not duplicate those prerequisites or claim that `/free` measures physical memory release.

Separate the shared release timeout from the NVIDIA threshold configuration.
Update both existing apps, all configuration sources, and affected tests in the same unit.
Delete the old probe contract and configuration shape; no parallel legacy seam remains.
The `/gpu` result retains the existing ownership fields and nullable VRAM usage.
It adds an explicit release basis and observation availability.
The Splash app adds a safe projection of native allocation, memory pressure, and engine health fields.
It excludes raw status payloads and crash traces.
Unavailable or stale native observations are labeled; they are never converted to zero.

## Lifecycle and process containment

The model controller is available when the application starts even though no model process is running.
It marks the coordinator ready after configuration validation.
Normal unload does not mark the controller unavailable or invalidate the coordinator generation.
An unexpected process exit cancels active leases through `ModelHostExited`.
After confirmed cleanup, the controller calls `ModelHostReady` so a later request can load again.
The failed request is not replayed.

Loading launches one owned process group and publishes `loading`.
The Mac process adapter uses `posix_spawn` with `POSIX_SPAWN_SETPGROUP` and group ID zero.
The spawned Python PID becomes the process-group ID; the native child inherits that group.
File actions attach captured stdout and stderr without shell redirection.
The adapter owns file descriptors, child reaping, and process-group signals.
Its native interop is isolated behind a testable process capability.

The controller waits for `/ready` success and a fresh `/status` observation with the expected server PID,
ready state, and healthy Metal engine before publishing `loaded`.
It does not adopt an unrelated server already occupying the port.
A load timeout or failure stops the entire group before publishing failure.
No failed load grants a model lease.

Unloading signals the group with SIGTERM and waits for both the server and native descendants to exit.
The bounded stop timeout escalates to SIGKILL.
The controller must confirm group disappearance before publishing `unloaded`.
Permission errors or a surviving group fail the release operation and block the handoff.
Shutdown rejects admission, cancels active transport, stops the group, and awaits cleanup.
Process termination remains necessary even when native status is unreachable.

Child stdout and stderr are drained without retention.
The host records lifecycle facts directly; arbitrary child lines and exception strings are not copied into logs.
The host logs configured IDs, owned PIDs, transitions, durations, and exit codes.

## HTTP forwarding

Extract the existing llama HTTP forwarding mechanics into one shared `InferenceForwarder` in Core.
It owns hop-header removal, streaming copy, request cancellation, bounded body reads,
and queued SSE wait comments. The llama app and Splash app supply their routing and admission policy.
The Tensor app uses the same extracted wait-comment helper without forwarding its in-process results.
Delete duplicated mechanics as their consumers move.
Do not extract Tensor wire-to-capability converters into this adapter.

The Splash boundary parses JSON once to validate a bounded body, a configured string `model`, and `stream`.
All other provider fields remain opaque JSON. This preserves native validation and prevents silent feature loss.
Configured public IDs are passed as Splash's `--model`, so bodies and responses need no model-name rewrite.
Missing or malformed model input returns 400; an unknown model returns 404 before admission.
The proxy forwards native response bytes, status, and eligible headers unchanged when headers have not started.
Cookies, redirects, and upstream system proxies are disabled.
Public credentials and hop-by-hop headers are not forwarded to the private Splash endpoint.
The wrapper adds no public authentication scheme and defaults to loopback exposure.

Supported routes in the installed 1.1.0 server are:

| Method and path | Admission |
| --- | --- |
| `GET /v1/models`, `GET /v1/models/{id}` | Configured catalog; no model load |
| `POST /v1/chat/completions`, `POST /v1/responses` | Requested-model lease |
| `POST /v1/messages`, `POST /v1/messages/count_tokens` | Requested-model lease |
| `POST /tokenize`, `POST /apply-template` | Requested-model lease |
| `POST /v1/judgments`, `POST /v1/systemone` | Requested-model lease; native protocol |
| `GET`, `DELETE /v1/responses/{id}` | Resident-model lease; no load |
| `GET /gpu`, `POST /models/load`, `POST /models/unload` | Existing coordinator controls |

Unsupported routes return an explicit unsupported-operation error.
Legacy completions, embeddings, audio, and image generation receive no fabricated implementation.
The public surface does not expose arbitrary private server routes.
Model listing advertises installed catalog identity and configured context; native modality details are included only when verified.

Add an atomic `AcquireResident` operation to the coordinator for stored-response reads and deletes.
It grants a lease only for the usable resident model with no pending handoff or unload barrier.
It never queues a model load or chooses a model from the response ID.
No resident instance returns 404; a handoff in progress returns 503.
The native response store resolves the opaque ID and supplies the response or 404.
The lease prevents that instance from unloading during the operation.

Native response continuation uses the selected live instance and its native validation.
Switching or stopping the instance invalidates its stored IDs.
The wrapper adds no durable response store or background job executor.
It forwards only execution modes accepted by the installed runtime.

Queued streaming calls receive SSE comments after two seconds.
Failures before headers start use the applicable OpenAI or Anthropic error envelope and HTTP status.
Failures after SSE starts use a protocol-appropriate error event; SystemOne and other non-streaming routes reject `stream: true`.
Non-streaming calls receive no response bytes before admission.

## Disconnects and native completion

Closing an upstream HTTP connection does not prove native cancellation has finished.
Each Splash forward retains its coordinator lease through native completion confirmation.
On disconnect, the proxy disposes the upstream stream to trigger Splash's native cancellation path.
Cleanup uses a bounded token independent of the disconnected caller.

The controller confirms idle execution through a fresh native status observation:
no admitted/waiting work, no active scheduler phases, and no pending transport work.
The exact schema-5 counters are validated against the installed runtime during implementation.
A stale status snapshot never proves completion.
This aggregate drain is conservative when other requests share the same model.
The last outstanding lease cannot disappear while native work remains active.

If completion cannot be confirmed within the cleanup timeout, the controller stops the owned group,
invalidates all active leases, and confirms process termination before making admission available again.
No Comfy handoff proceeds on an assumed cancellation.

## ComfyUI integration

Reuse `ComfyClient`, `ComfyGate`, and `ComfyWatcher`.
The dedicated proxy listener gates `POST /prompt` and forwards other routes and websockets through YARP.
The application does not start or stop ComfyUI.
Direct submissions retain the existing exceptional reconciliation behavior.
That behavior drains Splash and confirms its group exit without treating ComfyUI's allocations as idle memory.
An unreachable ComfyUI service has the existing empty/unreachable observation semantics.
A failed `/free` on a reachable service blocks Splash acquisition.

## Extraction classification

| Existing area | Classification |
| --- | --- |
| Coordinator, Comfy gate/client/watcher, operational file logger | Retain shared ownership; convert the release seam |
| llama HTTP transport and queue wait comments | Convert to shared forwarding mechanics and wait helper |
| Windows Job Object and llama supervisor | Exclude from the Mac adapter |
| Tensor DTO conversion and model preparation | Exclude; Splash owns its protocol and installed pack |
| Splash lifecycle and Mac process-group adapter | Write within the new app |
| Splash installed Python/native source | External dependency; do not copy or modify |

## Verification and task checklist

- [x] Convert the release seam and configuration in Core, llama, Tensor, and affected tests; preserve Windows thresholds and fairness.
- [x] Extract shared forwarding and SSE wait mechanics; verify existing llama transport behavior with focused tests.
- [x] Implement configuration, installed catalog, Mac process containment, and Splash model lifecycle with controlled process/HTTP doubles.
- [x] Implement Splash routing, resident-response leases, errors, and native completion confirmation.
- [x] Wire the Comfy proxy and publish truthful Mac GPU observations.
- [x] Verify load failure cleanup, intended unload versus crash, timeout escalation, surviving native descendants, and recovery without request replay.
- [x] Verify opaque tool/image/structured-output payload forwarding, streaming, stored responses, unsupported routes, waiter cancellation, and unload barriers.
- [x] Restore and build App with zero warnings; run focused affected tests in runs under one minute; verify format and audit dependencies.
- [x] Run a foreground live Mac smoke: list without loading, short chat, streaming, Responses create/read/delete, unload/reload, and verify no owned child survives shutdown.
- [ ] Exercise Comfy handoffs live if a Mac Comfy instance is available; otherwise record that live check as unavailable and retain controlled coverage.
- [ ] Compare the existing short prompt through the wrapper with the prior Splash profile, recording load and inference separately; verify no significant transport regression.
- [ ] Update canonical App ownership and application README documents; commit and push the coherent units.

No NuGet publication, main-branch merge, service installation, or automatic startup registration is part of this checklist.

## Evidence sources

The route and lifecycle contract is based on the installed Splash 1.1.0 `install/launcher.py`,
`server/server.py`, and `server/backend.py`.
The schema-5 status fields are verified in the saved Mac benchmark status observations.
The prior benchmark records live in the Tensor arbiter worktree's ignored `tmp/mac-three-engines` directory.
The shared admission contract is documented in `App/README.md` and `docs/design/gpu-arbiter-tensor-tech.md`.

## Implementation verification

The App solution builds in Release with zero warnings.
The 28 Splash adapter tests pass, including real Mac process-group termination without model weights.
The 21 coordinator/llama tests and 23 Tensor adapter tests pass after the shared release and forwarding extraction.
The seven llama HTTP proxy tests also pass after the private-header forwarding policy change.
The dependency audit reports no vulnerable packages in the App solution.
