# Apple Foundation Models connector design

**Ticket:** [DSA-59](https://darkspyre.atlassian.net/browse/DSA-59), under DSA-57.
**Date:** 2026-09-27. **Status:** source implementation complete; runtime/release qualification
remains open. The user explicitly authorized implementation after reviewing the design.
The managed connector was published in the authorized four-package `11.11.0-rc.1` release.
Signed native helper distribution remains separately authorized.

This is the approved implementation design. Earlier prototype alternatives remain evidence,
not competing implementation paths. See the [experiment record](../tools/AppleFoundationModelsPrototype/RESULTS.md)
for observations and their limits. Each additional Connect provider remains its own ticket.

## 1. Runtime and ownership

Use an owned Swift executable, `darkspyre-apple-fm`, called through a versioned private
stdio protocol by a managed Apple adapter in `Darkspyre.Inference.Connect.LLM`.
The managed adapter uses no Apple SDK or native dependency, so it follows the existing
process-adapter assembly convention. Swift source belongs under
`Connect/tools/AppleFoundationModelsHost/`.

```mermaid
flowchart LR
    H[Host and host tools] --> C[Connect provider contracts]
    C --> A[Apple process adapter]
    A -->|private stdio protocol| S[Owned Swift helper]
    S --> F[Apple Foundation Models]
    F --> M[Installed Apple system model]
```

This gives Connect an explicit external execution boundary irrespective of Apple's
internal service topology. It also exposes typed Swift errors, transcript identity and
structured snapshots directly. The C bridge remains research evidence; its Python package,
foreign callback ABI and upstream source are not production dependencies. The owned
prototype's cancellation invariants inform the helper's implementation and tests.

Run one helper process per model operation. A managed agent session reuses Connect's
existing `ManagedAgentSession`; successive model steps create fresh helpers and native
sessions. Host history is authoritative. There is no native conversation cache, daemon,
background work or persistent helper session to recover. Startup cost is a measured
implementation acceptance concern; pooling would require a later design change.

The host executes and authorizes tools. The helper only captures requested tool calls.
Apple owns model provisioning and updates. Connect never loads custom weights or falls
back to cloud inference.

## 2. Distribution and platform

- Keep exactly the existing four NuGets. Connect contains the managed adapter only.
- Distribute the helper as a separately versioned, immutable macOS arm64 release artifact.
  Use a Developer ID Application signature with hardened runtime, a signed disk image,
  notarization and a stapled ticket. Publish its checksum and supported protocol major.
  Hosts may install the helper or include it in their own correctly signed application.
  Connect receives an executable path; it does not install or download executables.
- A process is launched directly with argument lists, never through a shell. The helper
  links the system FoundationModels framework and OS Swift runtime; users do not compile
  it and do not need Python or Xcode as a designed runtime prerequisite.
- Deployment target: macOS 26.0, Apple Silicon. The new direct Swift probe
  compiled with `-target arm64-apple-macos26.0`, Swift 6 and warnings as errors. Live
  evidence is macOS 26.7 only. A clean-machine test on the oldest supported OS must pass
  before publishing that compatibility claim. No 26.4 or 27-only API is required.
- macOS 26.4 token-count APIs may be used behind availability checks for diagnostics;
  they are not a reason to fabricate full request accounting on earlier systems.
- The ordinary desktop/console host is the initial deployment environment. App Sandbox
  and Mac App Store distribution require their own host integration validation and are
  not advertised from this prototype.

Apple documents the signing identities and distribution-container/notarization sequence
in [distribution signing](https://developer.apple.com/documentation/xcode/creating-distribution-signed-code-for-the-mac),
[packaging](https://developer.apple.com/documentation/xcode/packaging-mac-software-for-distribution),
and [notarization requirements](https://developer.apple.com/documentation/security/resolving-common-notarization-issues).
The current development binaries are not proof of signed distribution. Actual release
signing requires the owner's Developer ID identity and notarization credentials at that
stage; none were requested or accessed during design or implementation.

## 3. Provider discovery and setup

Stable identity:

| Field | Value |
| --- | --- |
| Provider ID | `apple-foundation-models` |
| Display name | Apple Foundation Models (On Device) |
| New transport | `LlmTransport.AppleFoundationModels`, append without renumbering existing values |
| Default endpoint | `darkspyre-apple-fm`; a configured absolute executable path is supported |
| Default/only model ID | `apple-system-default` |
| Model display name | Apple System Language Model |

`ProviderDefinition`, capabilities and setup steps come from the registered adapter,
and flow through `SupportedProviders`, `SetupDefinitions` and the existing settings
manager. Hosts must not duplicate this catalog. The logical model ID does not pin an
OS-delivered model revision.

The Apple settings flow is credential-free: executable path, compatibility/readiness
check, model listing and an explicit synthetic generation test. It must bypass API-key,
OAuth and CLI-login actions; authentication is not a readiness test. Use the existing
external-prerequisite interaction for installation or system-setting steps. Check/test
actions declare local verification/external process effects, without remote authentication
or potential API billing. Saving remains the host commit target's responsibility.

Probe platform/architecture before launching the helper. Its handshake reports helper
version, protocol version, OS version and model availability. Missing helper, unsupported
platform, incompatible protocol, ineligible device, disabled Apple Intelligence and
model-not-ready must have distinct actionable setup results. Never change system settings
or trigger model provisioning on the user's behalf. Discovery itself runs no generation.
Provider metadata remains discoverable on unsupported systems so it can explain setup.
List the installed model only when available; recheck readiness before each generation.

The native readiness distinctions are documented by Apple in
[UnavailableReason](https://developer.apple.com/documentation/foundationmodels/systemlanguagemodel/availability-swift.enum/unavailablereason).

## 4. Process protocol and lifetime

The private protocol is UTF-8 newline-delimited JSON. The helper first emits a hello with
protocol name `darkspyre.apple-fm`, major `1`, helper version and readiness. The adapter
rejects an incompatible major before sending user content. Commands are `probe`,
`generate` and `cancel`; generation carries a request ID, semantic history, instructions,
tool definitions, output schema and generation options. All events carry that request ID
and a monotonically increasing sequence number.

Events are text snapshots, structured JSON snapshots, a complete tool-call batch and one
terminal result/error. A batch is committed only after native cancellation for handoff has
settled. stdout is protocol-only; diagnostics are separate, bounded and scrubbed of prompts,
tool outputs and raw provider errors. Reject malformed, oversized, duplicate-terminal or
wrong-request frames. Default limits: 1 MiB per frame, 64 KiB captured stderr, 10-second
startup handshake, 120-second request deadline and 5-second cancellation grace. These
limits must be visible in the adapter configuration/documentation and validated before use.

One operation owner controls task creation, user cancellation, handoff cancellation,
terminal commitment and disposal. Never use native `isResponding` as the authoritative
busy/disposal flag: the overlap probe found it false while a request was still active.
Host cancellation observed before terminal commitment wins over an unfinished handoff.
Internal cancellation to return tools produces `RequiresAction`, not host cancellation.
Drain native work before committing a terminal message. On timeout, broken pipe or failure
to settle, terminate/reap only the owned helper process tree. Never automatically retry a
turn: the host may already have executed tools in an earlier step.

## 5. Chat tools, history and managed agents

### Batch capture

At the first native `Tool.call`, snapshot the transcript before cancellation. Find the
current `.toolCalls` entry containing the callback argument's `GeneratedContent.id` by
Swift equality. Exclude IDs with matching `.toolOutput` entries in the supplied history.
Require nonempty unique call IDs, recognized tool names and complete argument objects
that pass the host's tool schemas. Capture the entire current entry, rather than waiting
for a requested number of callbacks or treating a quiet interval as a batch boundary.

The first-callback snapshots contained complete one-, two- and three-call identical-argument
batches in the focused probe. Apple's public type is a collection, with no separate
batch-complete signal. This observed ordering is therefore a checked adapter invariant,
not a documented cross-version guarantee. Missing/ambiguous callback identity, incomplete
arguments, or later callbacks outside the captured set fail the turn before host execution.
All callbacks remain suspended until the native task is cancelled; settle all pending
continuations before returning the captured batch. Never correlate by argument text, call
order, hashes or a C bridge integer.

Return the captured native UUIDs as opaque Common tool-call IDs. Chat callers execute them
and submit results in the next request. A helper never invokes a host tool itself.
The existing managed agent loop executes the same returned calls via its host handler,
applies host concurrency/iteration policy, appends results and asks the adapter again.
This supports chat and agent tools through one mapping.

### Semantic history projection

Construct typed `Transcript` entries from Common messages each time. The rebuild probe
preserved prior user/assistant text, two identical calls with arbitrary host IDs and
reverse-ordered results, then consumed the values without a tool callback. It carried no
original transcript JSON, GenerationIDs, asset IDs or Connect-only response state. This
keeps `LlmProviderService.Bind` fully usable without introducing a hidden cache or exposing
Apple payloads to applications.

- `Instructions` maps to native instructions, with current active tool definitions.
- User/assistant text maps to prompt/response entries in order. Existing empty host
  messages remain present. Native response asset IDs are empty for reconstructed history.
- Assistant tool calls map to a native tool-call entry; result IDs map exactly to the
  corresponding tool outputs. Preserve result order as supplied. Reject duplicate,
  unknown or missing results before another generation; do not fabricate a result.
- A final plain user message becomes the new prompt. A request ending with complete tool
  results uses the accepted empty internal prompt. It is absent from host history because
  the adapter returns semantic output and never returns a native history replacement.
- Preserve assistant text accompanying tool calls and user text accompanying results as
  separate typed entries. Their exact ordering must be covered by converter fixtures.
- Arbitrary mid-history system/developer roles have no equivalent native role. The initial
  mapping accepts canonical `Instructions` plus user/assistant messages; reject these
  unsupported role combinations explicitly rather than reordering them or flattening them
  into user text. This limit must be documented for callers.
- Transcripts are rebuilt for each OS/model version. Cross-version replay of Apple's private
  serialized transcript is not part of the contract.

The additional-batch probe separated two settled calls from a new pending call in one run,
but the model chose an unintended argument; a follow-up produced no new call. Both are
recorded as inconclusive overall. Deterministic converter/hand-off tests must cover any
number of successive batches. Live tool selection remains model-controlled on this baseline;
the adapter cannot promise a prompt will cause a particular call.

### Tool errors and controls

Reuse Connect's `ToolOutputProjector` and text route for ordered text, structured content,
error flags, text documents and resource references. Advertise the actual text conversion
and retain the existing omission/projection report for unsupported binary parts. Resources
are not fetched. An `IsError` output is data the model can read; it is not a thrown Swift
exception. A probe consumed an explicit synthetic error result and reported it without
rollback. Throwing from `Tool.call` instead produced a typed `ToolCallError` and rolled back
the current request. Internal transport/protocol failures fail the operation; host exceptions
follow the managed agent's existing safe failure policy.

On the macOS 26 baseline, support tool choice `Auto` and `None` (`None` registers no active
tools). Keep historical tool results even when no tools are active. Reject `Required` and
`Specific` before generation; prompt instructions cannot enforce them. A null/true
`AllowParallelToolCalls` permits Apple's normal parallel choice; reject false because the
baseline exposes no verified model-side control. Host execution can still be serialized by
the managed agent's tool policy. Newer tool-calling controls are not silently enabled merely
because a machine has a newer OS.

## 6. Structured generation and streaming

Translate standard schemas with public `DynamicGenerationSchema` constructors. Do not feed
ordinary JSON Schema into Apple's Codable representation or make its private `x-order`
format a maintained wire contract. Use one converter for output schemas and tool arguments.

Initial strict subset:

| Schema construct | Mapping/limit |
| --- | --- |
| Objects | Named properties, required/optional fields, nested objects; closed objects with `additionalProperties:false` |
| Scalars | String, boolean, signed 64-bit integer and finite double; reject bounds outside the chosen native numeric domain |
| String enum/const | Native string choices/constant guide |
| Numeric bounds | Inclusive minimum/maximum/range representable in the native numeric type |
| Arrays | One item schema; primitives or objects; min/max/exact element count |
| References | Acyclic local `$defs`/`$ref`, resolved before generation; deterministic generated type names |
| Annotations | Preserve title/description as schema guidance; they do not change validation |

Reject open objects, pattern/property-name rules, tuple arrays, uniqueness, arbitrary regex,
formats requiring enforcement, exclusive bounds, multiples, recursion, external references,
nullable/union/composition/conditional schemas and unknown validation keywords in strict mode.
Do not drop a constraint. Evaluate the final result against the original schema using the
existing `StructuredOutput.Validate`. Initial live probes cover representative shapes;
every admitted constructor/constraint needs deterministic conversion tests before advertising
schema support. This is a declared subset, not general JSON Schema compatibility.

For an unsupported schema, `Fallback.None` fails before generation. `JsonObject` also fails
because this design advertises no arbitrary-object generation mode. Explicit
`LocalValidation` permits unconstrained text generation plus original-schema validation and
reports `LocalValidationOnly`; it does not claim native enforcement or retry invisibly.

Direct Swift structured streaming works on the 26.0 API surface. The focused run produced
parseable partial objects whose serialized JSON changed earlier content/key order. Therefore
add a provider-neutral `LlmStructuredOutputSnapshotEvent(JsonElement Content)` to Common:
each event replaces the previous structured snapshot and is not final-schema-validated.
Add the equivalent agent event with thread/turn/item identity, and propagate it through
the managed loop. Emit no JSON text deltas for these snapshots. Terminal `Text` remains the
complete JSON representation with final validation. Text-only fallback uses normal text
events. Existing `CompleteAsync` consumes the same single terminal result.

Ordinary text snapshots become deltas only after ordinal prefix validation. A revision of
already-emitted text fails with `ProtocolFailure`; do not duplicate text or rewrite host
output silently. Test Unicode boundaries, repeated snapshots and final equality.

## 7. Completion, errors and accounting

Apple explicitly allows an output cap to stop generation without throwing. The direct
probe also found `rawContent.isComplete=true` for visibly truncated text. See
[maximumResponseTokens](https://developer.apple.com/documentation/foundationmodels/generationoptions/maximumresponsetokens).

Add `LlmCompletionStatus.Unknown` as a terminal response whose natural-completion/truncation
classification is unavailable. Append the value; do not renumber existing values. A normal
Apple terminal text/schema response uses this status, `FinishReason=null`,
`IncompleteReason=null`, and `Usage=null`. Valid JSON alone does not prove natural completion.
`RequiresAction` and known refusal/failure classifications remain explicit. Output-limit
enforcement is `ProviderEnforced`; enforcement and reporting are independent capabilities.

Add terminal `Unknown` values to `LlmAgentCompletionReason` and `LlmAgentTurnStatus` and map
them explicitly. The managed loop must retain the final assistant response, finish the
turn once and report unknown termination, without treating it as provider failure, known
truncation or an instruction to continue. Update every in-repository switch, renderer,
example and consumer test for these values and for structured snapshots in the same unit.
These are required public contract additions, not Apple types escaping the adapter.

| Condition | Mapping |
| --- | --- |
| Caller cancellation | `OperationCanceledException` after cleanup; preserve cancellation intent |
| Internal tool handoff cancellation | `RequiresAction` with the captured batch after native work settles |
| Apple refusal or guardrail rejection | `Refused` with a safe category; do not generate a second response just to obtain an explanation |
| Context window exceeded | Existing `context_length_exceeded` provider code, so managed agents can request compaction; no silent history deletion |
| Unsupported guide/language/feature | Explicit unsupported-feature failure; never downgrade without the request's fallback permission |
| Assets unavailable or model not ready | Model-unavailable/readiness failure with an actionable setup reason |
| Rate limited | Capacity-exhausted failure; no automatic retry and no invented HTTP status |
| Decoder failure | Execution/schema failure, not a valid structured result |
| Unexpected concurrent native request | Internal protocol/lifetime defect; active-operation state is owned by the adapter |
| Broken helper/protocol/deadline | Backend/protocol/timeout failure respectively, using existing Common failure codes |

Use typed Swift cases and bounded stable protocol codes rather than matching localized
error text. Preserve the existing provider overflow code for agent handling. Portable Bind
must retain meaningful failure categories; update its exception mapping where needed.
Local-success diagnostics use the existing process-adapter convention `Diagnostics(200,
null, null)`; document that 200 is a compatibility sentinel in this field, not an HTTP
exchange. Do not include raw Swift errors or transcripts as safe diagnostics.

List context capacity from the native model (4,096 on the tested runtime). Leave output
capacity, generation usage and full input occupancy unknown when unreported.
`ILlmContextEstimator` is synchronous and performs no I/O: the Apple implementation validates
the same projection but returns null with an explanatory diagnostic. Do not hide a subprocess
or async token-count call inside it. Native overflow is authoritative; agent compaction
handoff remains supported even when proactive occupancy cannot be measured. Temperature is
finite in [0,1], matching [Apple's documented range](https://developer.apple.com/documentation/foundationmodels/generationoptions/temperature).

## 8. Advertised initial surface

Implemented initial surface:

| Capability | Design |
| --- | --- |
| Text, instructions, supported semantic history | Supported through both Connect and portable Bind |
| Text streaming | Supported with checked delta conversion |
| Structured output/streaming | Declared schema subset, real snapshot events, original-schema final validation |
| Local model listing/setup | One logical installed model with readiness checks |
| Temperature/output cap | Supported with the limits above |
| Chat tools | Supported; host executes returned complete batches |
| Agent tools/sessions | Existing host-owned managed loop; context capture/replacement, interruption, steering and compaction handoff |
| Tool results | Existing text route and explicit rich-content projection reports |
| Usage/account usage/reasoning controls | Unavailable; do not synthesize values |
| Prompt images/documents, custom weights/adapters, cloud/PCC | Outside this initial system-text-model connector |
| Native resume/fork/background resources | Unavailable; managed semantic context can be imported/exported |

The registered capabilities correspond to the implemented surfaces above.
Unsupported combinations must fail explicitly; a capability override cannot bypass a
native limitation or enable an unimplemented contract.

## 9. Implementation sequence and acceptance

1. Approve this design and explicitly enter implementation. DSA-59 remains the Apple
   provider unit; neither the design nor the prototype authorizes publication.
2. Add the neutral unknown-termination and structured-snapshot contracts and update all
   consumers together. Cover Bind and managed-agent terminal/event behavior.
3. Build the owned helper, typed schema/history conversion, checked batch capture and
   lifetime state machine. Use deterministic tests for cancellation races and batch
   selection, plus the existing optional live probes for OS behavior.
4. Implement the process adapter, provider registration, credential-free settings,
   model/readiness checks and managed-agent registration. Preserve host persistence and
   tool execution ownership.
5. Exercise a complete .NET host through both Bind and managed sessions: text, structured
   streaming, single/parallel/repeated/identical and successive tool batches, error outputs,
   no-tools replay, malformed protocol and timeout/cancellation at each lifecycle boundary.
   Schema fixtures must cover admitted constraints and rejected constructs; history fixtures
   cover missing/duplicate/out-of-order results and unsupported roles. Verify setup on
   unsupported platforms without loading native code.
6. Before release, validate the signed helper on a clean non-development Mac and oldest
   supported OS, including Gatekeeper/hardened runtime. Build/restore/test/format/audit
   active affected products with zero warnings. Package verification must still reject
   embedded native helpers in product NuGets. Pack/sign/notarize/publish only under the
   separately authorized release workflow.

## 10. Implementation and verification record (2026-09-27)

The source implementation follows the approved design: a registered managed process adapter,
owned Swift executable, credential-free settings and existing managed-agent integration.
Common and agent Unknown terminal values and replacement structured snapshots are implemented.
No Apple SDK, C-bridge source or helper binary is added to a NuGet, and no release was published.

The C# schema converter produces a private typed generation plan. Swift validates that wire
plan and constructs public DynamicGenerationSchema values; it does not interpret a second
standard JSON Schema representation. Title/description annotations travel as native property
metadata and path-qualified guidance, including primitive/array nodes with no native description
constructor. Conversion rejects depth beyond 64 or more than 16,384 expanded nodes, bounding
acyclic-reference expansion. The original schema still validates output and tool arguments.

Deterministic coverage includes the declared schema subset/rejections, semantic history and
reverse-ordered results, complete-batch validation, successive managed tool batches, Unknown
termination and snapshot identity, typed failures through Bind, credential-free setup, malformed
protocol/UTF-8, duplicate/out-of-order events, deadlines, process reaping, abandoned streams,
cancellation races and cancellation before handoff commitment. Optional live tests skip explicitly
unless APPLE_FM_HELPER is supplied. Commands and limits live in the
[helper README](../tools/AppleFoundationModelsHost/README.md).

The first end-to-end run exposed a blocking FileHandle pipe read that waited beyond a complete
request line. The helper now uses bounded POSIX reads; a deterministic test holds the writer open
and verifies that the complete command is consumed without waiting for EOF. The current Swift
suite also exercises 1,000 continuation-registration/cancellation races.

Live evidence on macOS 26.7 (25G229), arm64:

- Discovery reports the available logical model and native context capacity 4,096.
- Bind text returns Unknown without invented usage or finish classification.
- Structured generation emits replacement snapshots and passes original-schema final validation.
- A two-call batch with identical arguments retains distinct native IDs; reverse-ordered host
  results replay with no active tools and produce final text.
- Managed sessions execute the host tool, retain the settled call/results and final assistant
  entry, and finish once with Unknown on successful native continuation.
- Cancelling after the first text chunk drains/reaps the operation; the next generation works.
- Multiple complete live runs and five consecutive focused managed-agent runs passed. A separate
  intermittent native continuation failure also reproduced outside .NET: NSError domain
  FoundationModels.LanguageModelSession.GenerationError, code -1, with underlying domain
  com.apple.tokengeneration, code 10. Only error type/domain/code metadata was examined using
  temporary synthetic diagnostics; those diagnostics were removed from source. This is not
  attributed to a documented cause. Production maps it to ExecutionFailed and managed
  ProviderFailure, retains already-settled tools, and does not retry. A deterministic regression
  test covers that behavior. Successful reruns do not establish that the native issue is fixed.

Remaining release qualifications: investigate/characterize that intermittent native continuation
failure, validate the signed/notarized helper on a clean Mac and the oldest supported macOS,
and complete the separately authorized package/distribution workflow. These are not established
by the development binary. The supported semantic history mapping follows Apple's public
[Transcript](https://developer.apple.com/documentation/foundationmodels/transcript) and
[ToolOutput](https://developer.apple.com/documentation/foundationmodels/transcript/tooloutput)
constructors; those APIs do not document the observed token-generation error's cause.

Final deterministic verification:

| Gate | Result |
| --- | --- |
| Connect restore/build/test/format/audit | Zero warnings; 499 tests pass (including 31 Common); 3 opt-in live tests explicitly skipped in the normal suite; no vulnerable dependencies reported |
| Tensor restore/build/test/format/audit | Zero warnings; 345 tests pass (including the same 31 Common); no vulnerable dependencies reported |
| UI restore/build/test/format/audit | Zero warnings; 65 tests pass (including the same 31 Common); no vulnerable dependencies reported |
| Unique .NET tests across products | 847 passed |
| Owned Swift helper | Debug/release builds and 10 deterministic tests pass with warnings as errors; strict swift-format lint passes; release Mach-O reports macOS minimum 26.0 |
| Relocated continuation helper | Historical prototype's 4 deterministic tests pass; patched C-bridge builds with the current helper fingerprint |
| Package asset verification without packing | Pass: no repacked native assets, dependency shader copying preserved, output collisions rejected |

No package archives, signed distribution artifact, notarization submission or publication were
created. Opt-in live generation evidence and its unresolved intermittent failure are recorded above;
they are not represented by the deterministic pass count.

## 11. Console harness extension (2026-09-28)

The user selected `Connect/tools/Darkspyre.Inference.Connect.ConsoleTester` and authorized this
extension within DSA-59. It uses the existing console host and public Connect/Bind/managed-agent
contracts. No new shipping API, package or native protocol is required.

Provider discovery comes from registered `ILlmClient.ProviderDefinition` instances. The console
keeps only its command aliases and credential-store identities; display names, defaults and setup
instructions come from adapters. `providers` lists them; `setup --provider <id-or-alias>` renders
the current settings steps, fields, requirements, troubleshooting and action effects without
executing an action. Existing subscription and AWS sign-in flows remain available.

Apple accepts `--helper <executable-name-or-absolute-path>`. Its `status`, `models`, `prompt`,
`agent` and probe commands use the settings manager's prerequisite check, with no login or vault
credential. `prompt`/`agent` use the provider's default model when omitted. Agent availability
comes from capabilities; Apple uses the existing managed session without native runtime options.

`apple-probe` runs five independent cases through public APIs: chat/text streaming, structured
snapshots and final validation, two identical tool calls and reversed-result/no-tools replay,
managed tool continuation and settled context, and cancellation followed by a new operation.
The model must actually emit the requested shape and calls: an unmet live assertion is a failure,
not evidence inferred from advertised capability flags. Apple `Unknown` is an expected terminal
classification and never proof of natural completion.

The caller can select one case, repeat 1–20 times and choose a 1–600-second case deadline (default
180). Repetitions are explicit independent experiments. Cases run sequentially, own their
cancellation lifetime, retain each failure and never retry it automatically. Managed sessions
permit at most four model iterations and execute only a synthetic in-memory lookup. After a
managed failure, the harness checks captured settled tool results and reports execution/retention
counts and duplicate IDs when the session remains capturable.

Reports contain per-case PASS/FAIL/SKIP, repeat index, elapsed time and aggregate counts. Missing
prerequisites return exit 3; failures return 1; all passed returns 0; caller cancellation stops
remaining cases and returns 130. A failed readiness operation returns failure. Error diagnostics
use harness-owned assertions, typed failure categories or exception type names; raw provider
exceptions, prompts, tool results and generated content are not report fields. The existing
interactive prompt/agent commands continue to display their requested content.

The [console instructions](../README.md#apple-console-harness) are the runnable entry point.
Deterministic runner/scenario tests use controlled functions and inference doubles. Live runs are
explicit development experiments and remain separate from commit gates and signed distribution
qualification. No later successful run erases the previously recorded native continuation issue.

Verification for this extension:

- Connect restore/build: zero warnings/errors. Full solution tests: 515 passed, 3 opt-in Apple
  tests skipped. This includes 16 new console harness test cases (21 console cases total), covering
  metadata/aliases and credential isolation, invalid invocation, deadlines, cancellation, prerequisite
  skips, failure retention/error redaction, snapshot requirements, stream/final-text consistency,
  duplicate tool IDs and reversed-result replay. Formatting and the dependency vulnerability audit passed.
- Two full live repetitions on macOS 26.7 arm64: 8 passed, 2 failed. Both repetitions passed text,
  structured output, two identical tool calls/replay and cancellation/reuse. One managed run requested
  two host lookups instead of one; another ended in ProviderFailure with one execution, one retained
  result and no duplicate call ID. Neither failure was hidden or rerun automatically.
- Three explicit focused managed repetitions: 2 passed, 1 ProviderFailure. Each run
  executed and retained one tool result with no duplicate ID. Successful terminals were Unknown.
- CLI checks passed for provider/setup display, readiness, model listing, default-model prompt and
  agent calls, and streamed/final-text equality. A deliberately absent helper returned 3 with five
  skipped cases; invalid repeat count returned 2 before generation.
- Local evidence is in ignored `artifacts/apple-console-probe-2026-09-28.log`,
  `artifacts/apple-console-agent-2026-09-28.log` and `artifacts/apple-console-smoke-2026-09-28.log`.

## 12. Existing signing setup (2026-09-28)

At the user's direction, inspected the sibling `tools/agent-ds` project's
`scripts/build-macos-app.sh` and `scripts/macos-bundle.sh`. That workflow uses Developer ID signing,
timestamps and hardened runtime, followed by optional notary submission, stapling and Gatekeeper
assessment. Its configured identity is `Developer ID Application: Brandon Mathis (L469XNMGLQ)`;
its keychain profile is `agent-ds-notary`.

This Mac reports that valid signing identity and has `codesign`, `hdiutil`, `notarytool` and
`stapler`. A read-only notary history request authenticated successfully with the existing profile.
No credential values were displayed, copied or committed. These prerequisites are available for the
helper's approved distribution workflow. This inspection did not sign a helper, submit a new
notarization or establish clean-machine/oldest-OS compatibility; those acceptance checks remain open.
