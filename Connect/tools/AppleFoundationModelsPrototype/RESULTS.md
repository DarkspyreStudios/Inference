# Apple C bridge prototype findings

**Date:** 2026-09-27. **Ticket:** [DSA-59](https://darkspyre.atlassian.net/browse/DSA-59).
**Phase:** Research prototype; production connector design remains open.

## Environment and provenance

- macOS 26.7, arm64; Xcode 26.6 (17F113), Swift 6.3.3.
- Portable .NET 11 console using the repository's `11.0.100-rc.1.26425.128` SDK.
- Unmodified Apple C bridge at commit
  `e868e60811aa0706feb2ccb33cfe7e27626287b7`.
- Upstream source archive SHA-256:
  `354c0178acc43c5c62eb550aad3ddb552de10e2a7487680096fa0cf6a7a5ac0f`.
- Apple Intelligence was already enabled; the system model reported available.
- Every prompt was synthetic and used `SystemLanguageModel.default` through the
  bridge. Inference used no Python runtime, MAUI, API key or HTTP server; Python
  was used only to prepare source and collect research evidence.

The native package built successfully without modifying upstream source. The
managed harness built with zero warnings and zero errors. Formatting verification
and the dependency vulnerability audit passed; the harness has no NuGet package
dependencies. No product package was created or released.

## Observations

The first suite exercised all 13 cases. It exited 2 because stream cancellation
returned success, contrary to the initial probe expectation. A targeted follow-up
added pre-cancellation state observations and confirmed that this happened while
the session was still responding. That behavior is now classified as a limitation,
not treated as a successful cancellation status. The full suite was not repeated
solely to turn its historical exit code green.

| Experiment | Observation | Design consequence |
| --- | --- | --- |
| Availability | .NET loaded the C bridge and obtained available=true and context size 4096. | Direct portable .NET interop is feasible on this environment. This is not an OS compatibility matrix. |
| Text | The callback returned `café 🦊` intact; the session was no longer responding afterward. | UTF-8 text and instructions work through the bridge. |
| Streaming | Three cumulative snapshots retained previous text as a prefix; one success terminal callback followed. | Conversion to text deltas looks feasible; arbitrary snapshot monotonicity is not proven by one run. |
| History | Exported instructions/user/assistant transcript JSON survived an exact semantic JSON round trip; the restored session recalled `LANTERN`. | Native transcript restoration works for this basic case. A full Common-message projection is still needed. |
| Flat schema | Strings, bounded integer, boolean, number, two-element string array, string enum and optional field generated valid tested shapes. | These constructs are candidates for an explicit supported subset. The harness builds them directly; it is not a general schema converter. |
| Nested schema | A referenced object and an array of referenced objects generated valid tested shapes. | Nested objects and object arrays are plausible initial support. |
| Ordinary JSON Schema | An object schema without Apple's `x-order` field was rejected with status 255 and a missing-key decoding error. | Standard schema input cannot simply be forwarded unchanged. |
| Output cap | With an eight-token cap, the response was `Nestled at the edge of a tranquil`, with status 0. Re-encoding that response as a prompt counted 16 tokens. | Success alone does not communicate truncation. Prompt token counting is not output-generation usage. |
| Cancel before output | Native task cancellation returned status 255, `Operation cancelled`, about 8 ms after the request; one terminal callback and idle session. | Basic cancellation is callable; status 255 is not a dedicated cancellation code. |
| Cancel stream | Follow-up: the completion task was incomplete and the native session was responding before releasing the stream. It stopped in about 0.4 ms with status 0 and an empty terminal payload; one terminal callback. | Cancellation can end the iterator as a successful stream. The adapter must preserve cancellation intent; status alone is insufficient. |
| Delayed tool result | The host completed `lookup_code` after 100 ms; generation consumed `ORCHID-729`. The transcript contained native tool-call and result entries. | In-turn host callbacks work in the control case. |
| Immediate tool result | Completing synchronously inside the callback left generation waiting for five seconds. Re-sending the identical result afterward allowed generation to finish. | The suspected continuation-registration race is reproduced. Delaying or retrying results is not an acceptable production remedy. |
| Cancel while tool pending | Cancellation did not produce a terminal callback within two seconds. Supplying the withheld tool result then produced status 255, `Operation cancelled`. | Pending-tool cancellation does not independently settle in this bridge. |

The schema prompt did not provide a note, but the model supplied one in the optional
field. Schema validity does not establish extraction accuracy or faithful omission.
No quality benchmark was performed.

No duplicate terminal callbacks or callbacks into removed operation contexts were
observed in these runs. This bounded observation does not establish absence of
native leaks, safe disposal under every interleaving or long-term stability.

## Tool failures and source evidence

The live immediate-result experiment agrees with the reviewed source:
`BridgedTool.call` invokes `foreignCall` before adding its continuation to
`outputContinuation`. `FMBridgedToolFinishCall` returns without storing a result
when the continuation is not present. The pending continuation also has no
cancellation handler in the reviewed method.

See Apple's [pinned tool implementation](https://github.com/apple/python-apple-fm-sdk/blob/e868e60811aa0706feb2ccb33cfe7e27626287b7/foundation-models-c/Sources/FoundationModelsCBindings/FoundationModelsCBindings.swift#L1740-L1831).
The prototype leaves that implementation unchanged.

This blocks using the unmodified bridge for dependable host-tool execution and
pending-tool cancellation. Resolve the native wrapper strategy in technical design
before implementing the connector. An upstream correction or an owned native
implementation would need to satisfy the same tool and cancellation requirements.

## Loading and distribution evidence

Copying the managed DLL, runtime/dependency metadata and compiled dylib to a separate
consumer directory also succeeded in the UTF-8 text experiment. The explicit
library path and system dependencies were sufficient for that local run.

`otool -L` showed Apple system frameworks and Swift runtime libraries.
`codesign -dv` reported an ad hoc, linker-signed arm64 dylib without a team identity.
This is a development artifact, not a validated shipping/signing strategy.

The clean consumer folder was on the same Mac with Xcode installed. It does not
prove operation on a Mac without developer tools, hardened-runtime loading,
notarization, a fresh user account, sandboxed applications, older macOS versions or
other machines. Existing product rules still require an agreed native dependency
distribution strategy; no native binary is added to a product package here.

## Process boundary and remaining questions

The bridge itself is loaded inside the .NET worker and links FoundationModels.
`modelmanagerd` was observed as a separate system process. Its presence alone does
not prove the precise execution location of this request's inference engine.
Connect's execution boundary therefore remains an open design gate; this prototype
does not reclassify the provider as Tensor or select a helper architecture.

Other unresolved items:

- Connect chat returns tool calls for later host result submission. The successful
  native callback test operates within one request. Chat round-trip semantics and
  the existing agent-session lane still need separate decisions.
- Failure propagation from a host tool, multiple/parallel calls, forced tool choice
  and rich tool results were not tested. The demonstrated native lifecycle defects
  should be resolved before extending those experiments.
- Common-to-Apple transcript conversion, cross-version replay, arbitrary JSON Schema,
  unsupported constraints and strict fallback policy remain unimplemented.
- The reviewed C exports lack structured streaming; neither adding that export nor
  representing it as buffered output has been approved.
- Exact truncation/usage reporting, refusals, context overflow, unsupported-platform
  discovery, readiness failures and production resource lifetimes remain to verify.

## Reproduction and local evidence

Use the commands in [README](README.md). The committed harness includes the exact
synthetic inputs. Generated source/build files and raw observations are ignored.
For this run they are in:

- `.artifacts/upstream/source.json`: dependency revision and archive digest.
- `.artifacts/results.json`: original 13-case suite, including its unexpected
  stream-cancellation result.
- `.artifacts/cancel-stream-followup.json`: targeted cancellation state observations.
- `.artifacts/consumer-result.json`: isolated consumer-directory text result.

These paths refer to this prototype directory. They are local evidence, not required
checked-in or shipped assets. This document records the durable findings.
