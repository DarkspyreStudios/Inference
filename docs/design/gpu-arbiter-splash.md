# GPU arbiter with Splash

## Functional requirements

The `App/src/gpu-arbiter-splash` sibling application runs on Apple Silicon macOS.
It wraps the installed Splash runtime and uses the existing arbiter admission and ComfyUI controls.
The initial model is the installed Qwen3.8-27B-Splash pack.

The host must expose Splash's OpenAI Chat Completions and Responses operations.
It must preserve streaming, tools, structured output, supported image input, and native response operations.
It must forward supported Anthropic Messages operations.
It must not fabricate operations that the installed Splash runtime does not support.

The host must expose model discovery, explicit load and unload, GPU ownership status, and the ComfyUI proxy.
Model discovery must not load a model or download weights.
Inference requests must select a configured, installed model.
The host must retain queued admission, waiter fairness, unload barriers, and streaming wait comments.
Active inference must finish or reach confirmed native cancellation before a GPU handoff.

Splash serves one model per process.
Loading must start its server and native engine and wait for readiness.
Unloading must stop the owned server and native engine and confirm their termination.
An idle model remains resident until another target needs the GPU or an explicit unload occurs.

ComfyUI remains independently operated.
The host must gate prompt submission, forward other ComfyUI routes and websockets, and observe exceptional direct submissions.
The host must free idle ComfyUI model caches before loading Splash.
Only one arbiter may own admission on this machine.

GPU status must distinguish host ownership, Splash-owned allocations, and Mac memory pressure.
Mac release confirmation must use owned-process termination and acknowledged ComfyUI cache release.
It must not claim to measure machine-wide Metal memory or return a fabricated NVIDIA VRAM value.

Stored Responses belong to the running Splash instance.
They disappear when that instance stops, switches models, or crashes.
Retrieval and deletion must not start a fresh model instance.

The application must remain a separate C# host within the App solution.
It must use the installed Splash runtime without copying or modifying its source.
It must use the existing local model pack without automatic downloads.
Request bodies, model output, credentials, and secret headers must not enter operational logs.

## Scope boundaries

The feature includes a foreground Mac application and verification against the installed runtime.
Service registration and automatic startup are separate deployment actions.
The feature does not change TensorSharp kernels, GPU-box deployment, model weights, or inference algorithms.
