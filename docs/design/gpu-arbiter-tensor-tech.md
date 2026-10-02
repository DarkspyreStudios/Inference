# GPU arbiter with Tensor engines

## Functional scope

The Windows GPU arbiter hosts the engines in `Darkspyre.Inference.Tensor` in process.
The host exposes the OpenAI operations that the public Tensor and Common interfaces can express.
It retains GPU observation, explicit model load and unload, queued admission, streaming wait
notifications, and the ComfyUI proxy and GPU handoffs.

The application consumes the published Common and Tensor packages. TensorSharp implementation
types remain inside Tensor's adapters. The host neither copies library source nor bypasses a
capability contract to implement a wire feature.

| HTTP operation | Public capability | Behavior |
| --- | --- | --- |
| `GET /v1/models`, `GET /v1/models/{id}` | Engine registry and configured packs | List and describe configured, installed models without loading them |
| `POST /v1/chat/completions` | `ITensorLlmInference` | Text, conversation history, function calls and results, structured output, and streaming |
| `POST /v1/completions` | `ITensorLlmInference` | Text prompt mapped to a user message; completion-shaped response and stream |
| `POST /v1/responses` | `ITensorLlmInference` | Stateless input messages and function items, instructions, structured output, function calls, and semantic streaming events |
| `POST /v1/images/generations` | `ITensorImageInference` | Supported sizes and counts; return generated image bytes as base64 |
| `POST /v1/audio/transcriptions` | `ITensorSpeechToTextInference` | Decode an uploaded audio file into PCM and return text or JSON transcription |
| `POST /v1/audio/translations` | `ITensorSpeechToTextInference` | English translation only when the selected engine advertises it |
| `GET /gpu`, `POST /models/load`, `POST /models/unload` | Host GPU coordinator | Retain admission, observation, and explicit load/unload behavior |
| Comfy proxy, including `/prompt` and websockets | Existing Comfy adapter | Retain gated submission and direct-job reconciliation |

Laya remains discoverable through the engine registry. Its decision contract has no corresponding
OpenAI operation. It does not acquire a fabricated OpenAI endpoint.

Embeddings, speech synthesis, image edits, file storage, Assistants, batches, realtime sessions,
and stored Response retrieval, cancellation, deletion and continuation have no matching Tensor
capability. Requests for those operations return an explicit unsupported-operation error.
No request silently drops a requested feature. Features represented by a common contract still
require support from the selected engine. Qwen currently accepts reasoning `None` and `High`;
other reasoning levels and unsupported tool-choice controls fail validation.

## Composition and dependencies

`App/` retains its independent solution and version. The Tensor host targets .NET 11 because the
published Inference interfaces target .NET 11. Pin Common and Tensor to the same published
Inference version in `App/Directory.Packages.props`. The host selects registered engines through
`AddTensorEngines` and `TensorEngineRegistry`.

Configuration contains an absolute pack-store root and an explicit list of public model aliases.
Each alias identifies a registered implementation, immutable pack manifest/key, and backend.
The registry supplies the implementation identity and pack namespace. The host creates one pack
manager per namespace beneath its root. Unknown implementations, duplicate aliases, unsupported
backends, and uninstalled packs fail configuration validation. Listing never initiates a download.

Model preparation is an explicit administration operation. It uses the existing resolvers and
pack-manager install/import methods, as ConsoleTester does. An inference request must not download
weights. Existing local GGUF files can be imported with their license and checked manifest.

## Model lifetime and GPU ownership

One host coordinator owns admission, waiters, resident-model selection, request counts and Comfy
handoffs. Tensor's execution coordinator continues to serialize execution inside an engine; it
does not become a second owner of host admission or Comfy state.

Replace the llama-router lifecycle seam with a model-host seam implemented over
`ITensorModelInstance`. Remove llama-server supervision and HTTP forwarding from this version.
The host starts engine instances through `StartAsync`. It subscribes before initiating a lifecycle
operation and waits for the target `EngineStatusChanged` state, or `Failed`, with cancellation and
the configured timeout. Acceptance of `LoadAsync` or `UnloadAsync` is not completion.

A handoff drains active inference streams, unloads the resident model, waits for the Unloaded
event, and confirms VRAM release. It then frees idle Comfy caches and loads the selected pack.
The existing exceptional handling for direct Comfy submissions remains authoritative. Only the
host loop changes resident models. A failed load fails its queued requests without granting a
lease. A disconnected waiter is removed. A disconnected active caller cancels and disposes the
inference enumerator; its host lease lasts until native execution has finished.

Shutdown rejects new admission, cancels queued requests, and drains or cancels active work before
awaiting engine Stop/Dispose. Native memory release completes before shutdown is reported.

## HTTP boundary

Dedicated request readers validate wire input once and produce public capability DTOs.
Response writers convert public results and stream events into OpenAI JSON and SSE.
The selected alias resolves to the real pack model ID before execution and remains the model
name in the HTTP response. Unknown aliases return 404; invalid input and unsupported fields
return 400; unavailable backends and failed GPU handoffs return 503.

Chat and Responses map system/developer instructions, message history, function definitions,
assistant function calls, tool results, temperature, output limits and supported reasoning levels.
Structured-output requests map only formats the selected capability can enforce. Attachments are
accepted only when the selected model advertises support. Sampling controls absent from the public
contract, including top-p, penalties, seed, stop strings and log probabilities, are rejected when
requested. Completion prompts use the LLM chat contract; raw token prompts and suffix/infill are
unsupported. Choice/prompt arrays execute independent requests under the admitted model lease;
their response choices retain stable indices and obey configured request-size and choice limits.

Chat streaming emits role, content, supported reasoning, function-call deltas, terminal finish
reason, optional usage, and `[DONE]`. Responses streaming emits the documented item/content
lifecycle and text/function deltas followed by the terminal response event. Request errors are
validated before headers start. Queued streams retain SSE wait comments; failures after headers
start become protocol-appropriate error events. Token usage is reported only when the engine
reports it; the host must not invent counts.

Responses are stateless. Clients supply full input history and function results. `store: true`,
`previous_response_id`, remote hosted tools and background execution are unsupported. Text and
function outputs use stable item/call IDs within each response. The host executes no client tool.

Image generation returns PNG base64 with the capability's actual limits. URL output requires an
image store and is unsupported. Audio uploads are bounded, decoded to the PCM contract, and
validated against the selected engine's sample-rate/channel limits. Unsupported codecs and
options fail explicitly. Transcription output includes only text, language and segment timestamps
the engine actually reports. Clip transcription is not treated as realtime token streaming.

Prompt bodies, responses, audio, images and secret headers are not logged. Operational logs contain
only identifiers, lifecycle transitions, timings and failures without protected payloads.

## Verification checklist

- Verify lifecycle acceptance versus completion, Failed events, cancellation and teardown with fake engines.
- Retain focused admission, unload-barrier, waiter fairness and Comfy handoff tests.
- Verify OpenAI request mapping, tool history, structured output and unsupported-field rejection.
- Verify chat and Responses SSE ordering, usage, finish reasons, disconnects and failure events.
- Verify model listing never loads or downloads and aliases resolve without exposing native objects.
- Verify image and PCM transcription adapters with controlled outputs and malformed-input cases.
- Restore, build with zero warnings, run focused App tests, verify formatting and audit dependencies.
- Prepare a Windows publish artifact and run a side-by-side live Qwen smoke test on an unused port.
- Deploy the replacement service only after explicit deployment approval.

## References

- `App/README.md` — existing admission and Comfy handoff contract.
- `Tensor/README.md` — engine registry, pack preparation and host-controlled lifecycle.
- `Tensor/tools/Darkspyre.Inference.Tensor.ConsoleTester/Program.cs` — resolver, packs and lifecycle setup.
- `docs/qwen35-selfhost.tech-design.md` — Qwen chat, tools, reasoning and context limits.
- [OpenAI streaming formats](https://developers.openai.com/api/docs/guides/streaming-responses).
- [OpenAI Responses and Chat mapping](https://developers.openai.com/api/docs/guides/migrate-to-responses).
- [OpenAI Images API](https://developers.openai.com/api/reference/cli/resources/images).
- [OpenAI Audio API](https://developers.openai.com/api/reference/cli/resources/audio).
