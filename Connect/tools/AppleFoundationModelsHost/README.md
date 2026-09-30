# Apple Foundation Models host

Owned Swift helper for [DSA-59](https://darkspyre.atlassian.net/browse/DSA-59).
The [connector design](../../docs/apple-foundation-models-design.md) owns its architecture,
capabilities and release gates. The managed adapter is in `Darkspyre.Inference.Connect.LLM`;
this executable is distributed separately and is never embedded in a NuGet.

## Development

Requires an Apple Silicon Mac, macOS 26+, an installed Xcode SDK with FoundationModels,
and Swift 6. Generation additionally requires Apple Intelligence enabled and its system
model ready. Nothing here installs software, signs in, changes settings or downloads models.

From the repository root:

```bash
xcrun swift build --package-path Connect/tools/AppleFoundationModelsHost
xcrun swift test --package-path Connect/tools/AppleFoundationModelsHost
```

The development executable is `.build/debug/darkspyre-apple-fm` inside this directory.
The deployment target is macOS 26.0; runtime verification so far is macOS 26.7 arm64.
Warnings are errors. There are no third-party Swift dependencies.
`Sources/PendingToolCall.swift` also supplies the historical C-bridge prototype's isolated
patch experiment. That prototype is not a runtime dependency of this helper.

Run the opt-in synthetic .NET integration tests from the repository root:

```bash
APPLE_FM_HELPER="$PWD/Connect/tools/AppleFoundationModelsHost/.build/debug/darkspyre-apple-fm" \
  dotnet test Connect/tests/LLM/Darkspyre.Inference.Connect.LLM.Tests \
  --filter FullyQualifiedName~AppleLiveTests --logger 'console;verbosity=detailed'
```

Without `APPLE_FM_HELPER`, the normal suite explicitly skips these live tests. The ordinary
adapter/schema/history tests use controlled doubles; process tests use temporary owned
scripts and explicitly skip on unsupported platforms. Swift tests do not generate content.
Live tests make no paid API calls, but exercise a nondeterministic OS-provided model and
can report model/runtime failures. They are not the deterministic commit gate.

For interactive prompts and repeatable scenarios through the public connector, use the existing
[Connect console harness](../../README.md#apple-console-harness). Its `apple-probe` command can
repeat chat, structured output, tool replay, managed continuation and cancellation checks, and
keeps failed runs in its report.

## Protocol v1

Launch directly with `--max-frame-bytes N`; no shell, socket or listening service.
UTF-8 newline-delimited JSON is the only stdout content. Keep stdin open after the initial
command so the host can send cancellation. Closing stdin cancels unfinished generation.
The reader uses bounded POSIX pipe reads: a complete line must not wait for EOF or a full
4 KiB read buffer. Narrative content is opaque data; it is never executed or logged.

The first frame is `hello`, containing `protocol: "darkspyre.apple-fm"`, `major: 1`,
`helperVersion`, `osVersion`, `availability`, native `contextSize` and `supportsSamplingControls`.
Connect checks sampling support in this greeting before writing a generation command. A helper
without this advertisement cannot accept explicit top-p, top-k or seed. Discovery reports these
capabilities as false for that helper. Readiness values are
`available`, `deviceNotEligible`, `appleIntelligenceNotEnabled`, `modelNotReady` and
`unavailable`. `probe` returns terminal `ready` after this hello; the adapter separately
checks availability. Probe never generates content.

Commands contain `command` (`probe`, `generate`, `cancel`) and a nonempty `id`.
`generate.request` contains instructions, semantic messages, tools, a typed schema plan,
temperature, maximumResponseTokens and optional topP, topK and seed. Top-p and top-k select
alternative random sampling modes; a seed alone selects probabilityThreshold 1. Standard JSON Schema is interpreted once in C#;
Swift validates the private plan and constructs public DynamicGenerationSchema values.
The helper does not deserialize Apple's private schema JSON.

Response frames contain `type`, matching `id` and a strictly increasing `sequence`:

- `text.content`: cumulative text, checked for an ordinal prefix by the managed adapter.
- `structured.content`: a JSON string containing a complete replacement snapshot, possibly
  partial against the final output schema.
- `batch.calls`: the captured group of `{id, name, arguments}`; arguments is a JSON string.
- `terminal`: status `unknown`, `requiresAction`, `refused` or `error`, with cumulative
  content for non-errors and a bounded stable code for errors/refusals.

The helper cancels and drains its native operation before emitting a batch/terminal. The
managed adapter validates the entire batch, waits for clean process exit, then exposes tool
calls and one completion. The helper never executes a host tool. Cancellation intent has
one owner independent of Apple's `isResponding`; cancellation before commitment wins.

## Limits and failures

Configure these positive integer `LlmConnection.ProviderOptions` values. Unknown options
and out-of-range limits are rejected before launching the helper.

| Option | Default | Maximum |
| --- | ---: | ---: |
| `maximumFrameBytes` | 1,048,576 | 16,777,216 |
| `maximumStderrBytes` | 65,536 | 1,048,576 |
| `startupTimeoutSeconds` | 10 | 300 |
| `requestTimeoutSeconds` | 120 | 86,400 |
| `cancellationGraceSeconds` | 5 | 60 |

The request deadline includes startup; it never resets per snapshot. After a terminal,
exit has at most the cancellation grace within that overall deadline. stderr is drained,
bounded and discarded. No prompt, tool output, credential or raw Swift error is a safe
diagnostic. Timeouts, abandoned streams and cancellation stop/reap only the owned process.
There are no automatic retries.

Apple does not classify natural completion versus token-cap truncation. A normal result
is therefore terminal `Unknown`, with no invented finish reason or usage. Structured output
is finally checked against the original schema in the managed adapter.

## Distribution gate

Development builds are not distribution artifacts. Before release, build an immutable
arm64 artifact, apply Developer ID Application signing with hardened runtime, place it in
a signed disk image, notarize/staple, and publish a checksum and protocol compatibility.
Validate that artifact on a clean non-development Mac and the oldest supported OS. No
signing credential or publishing permission is needed for the development commands above.
The repository release workflow requires separate authorization for pack/sign/notarize/publish.
