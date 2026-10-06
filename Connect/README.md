# Darkspyre.Inference.Connect

## SDK HTTP composition

SDK HTTP registrations use one generation-owned pool for the finite SDK named pipelines,
including ComfyUI's empty default name. Named `AddHttpClient` actions, public handler-builder filters,
handler scopes and selected meter factories configure the owned chains. Direct client and factory
constructors remain borrowed. The SDK never replaces the application's `IHttpClientFactory`.

`AddLlmConnect(configureHttp)` and `ConfigureInferenceHttpPipelines(configureHttp)` select
`SdkGenerationOwned` or explicit `BorrowHostFactory`. The latter delegates creation to the registered
host factory and does not dispose it. Configuration accepts only the SDK names and freezes
before client construction. Conflicting creation modes are rejected.
The speech pipelines are `XaiTtsProvider`, `XaiSttProvider` and `XaiRealtimeProvider`.
Their registrations disable redirects and set an infinite HTTP-client timeout; each operation
retains its explicit connection timeout. The same generation owns their HTTP handlers and leases.

Custom logger registrations in owned mode use `ConfigureLogging(name, logging => ...)` and its
`AddLogger` methods. Legacy framework `AddLogger` actions are not inspected or automatically migrated.
Logger factories run after the named handler-builder actions in the actual handler scope. Public
default logging, header redaction and meter filters remain active. HTTP configuration objects refuse
nonnull JSON serialization and contain no transferable credential authority.
Their attributes name one public closed converter factory for the two configuration types, so
nonfriend generated contexts preserve the same nonnull refusal and null handling.

Handler expiry stops new assignment to the old chain. Live issued clients and response/body leases
keep it alive; weak client registrations do not retain dropped typed consumers. A generation-owned
10-second cleanup timer reconciles collected wrappers while expired entries remain pending. Root
retirement stops and drains that timer, closes live wrappers and awaits owned chain cleanup. Cleanup
failures remain owned and are returned to all repeated disposers; borrowed host resources are untouched.

All SDK-owned response consumption and result/event publication use one internal helper. It fences
cached strings/bytes/copies and buffered streams as well as unbuffered reads, without changing HTTP
completion options or body values. Raw arbitrary caller use of .NET `HttpContent` is not transparently
intercepted: its nonvirtual cached APIs can bypass content overrides. Direct supplied clients and
`BorrowHostFactory` keep ordinary body and ownership semantics.

Mapped task results publish after response, body and request cleanup completes; retirement begun by
cleanup prevents late success without replacing an earlier operation failure. If an owned logger
fails after receiving a response, that unhanded response is cleaned up once. Refused cleanup remains
owned with its exact error; independent logger and cleanup failures are preserved together.

Comfy's in-transport reference materialization stays admitted through resource commit and cleanup.
Returned Image inference has a separate caller-owned lifetime across download, resource commit and
publication. Pool retirement alone does not drain that inference after copied download bytes return;
whole-generation retirement must also retire the returned inference owner. Committed and uncertain
resource facts remain actual observations, not rollback claims.

## Grok Image

`AddImageConnect` registers `XaiImagineImageTransport` alongside ComfyUI. A Grok image
connection borrows an `XaiFamilyBinding` backed by the selected LLM facade and its connection
access. Image discovery uses that facade's image catalog. Manual workflows carry their native
model in `TransportConfiguration`; discovery does not gate a manual model.

Generation and editing use one synchronous JSON request. The transport accepts up to ten outputs
and five ordered references, including explicit duplicates. Binary and logical references upload
through the same Files owner. Imported native file IDs receive a selected-token metadata check.
Null original filenames remain null; an unnamed upload gets a separate local multipart label.
Selected and Image additional headers retain their values without replacing selected authorization.

`SubmitAsync` returns a transport-local completed-result handle, not a provider job ID.
Copied equal handles remain usable on that instance until retirement. `GetJobAsync` returns its
ordered completed snapshot without polling or credential refresh. Completed cancellation returns
false without contacting the provider. Inline downloads use actual retained data; native file and
returned URL outputs use the selected Files owner or its unsigned SDK-generation download path.
Unknown model, MIME and dimensions remain unknown. The shared image decoder preserves raw source
representations and checks caller byte limits before decoding batch output.

The bound Image inference commits generated bytes through caller-owned resource access. Its
operation fence follows the entered binding and detaches after children and cleanup drain.
`InferenceResourceOperationFailure.GeneratedResources` retains ordered actual commit-and-selection
receipts when a later reference, output, progress callback or cleanup fails. It uses the existing
copied receipt graph and does not replace failed work with success or infer rollback.
The transport owns its local results and HTTP request/response cleanup, not the borrowed core,
HTTP client, resource access or source streams. Catalog health confirms discovery access only;
generation entitlement, OAuth media compatibility and runtime retirement remain unqualified.

## Batch and deferred contracts

`Darkspyre.Inference.Connect.Resources` declares `ILlmBatchClient` for explicit-connection batch
creation, incremental requests, one-page queries/results, cancellation, JSONL creation and deferred
submission/retrieval. Its closed operation union carries actual chat, image/workflow and video
requests. Results retain caller `CustomId` correlation, terminal video outputs and committed caller
resource descriptors. Counts and timestamps remain nullable observations; absent counts are not zero.
`LlmBatchVideoResult.Result` carries the actual terminal `VideoOutput` without requiring a native job
ID. Its optional `JobSnapshot` carries a separately observed real job only. Image and video batch
results have optional `Usage` for actual provider-reported costs and counts, not estimates.
`GeneratedImage.Selection` and `MaterializedVideoResult.Selection` carry optional full post-commit
membership receipts. An absent receipt remains unknown; neither result reads current membership.
`LlmChatResult.GeneratedResources` and Common `LlmInferenceResult.GeneratedResources` carry ordered,
copied actual commit-and-selection receipts for generated session artifacts. Every receipt includes
the generated resource in its exact successful selection snapshot. Workspace-owned resources can
have a null session; a reported session must match that snapshot. Duplicate observations retain their
order. These receipts grant no access and never substitute a later membership read. Current providers
leave the collection empty until they perform actual generated-artifact materialization.
Media materialization budgets are explicit positive caller policy, not provider defaults. Page limits
retain the caller's raw nullable integer; these data declarations impose no provider paging bounds.

`ConnectResourcesJson` exports 28 exact data-type getters with one generated closed Common/LLM/Image/
Video graph. Pages copy their ordered lists, including assignments through `with`. Nested existing
request/result objects retain their own contracts; constructing a batch wrapper does not admit a
provider operation or resolve content. The codec preserves fixed xAI hosted-tool and video-option
variants and mixed function/shell tool graphs. It composes the existing LLM-owned caller-service refusal
policy and refuses nonnull resource access, handles, authorization and batch clients without calling
them. Diagnostic strings hide resource, prompt and native receipt data.

`LlmResourceOperationObservation` carries ordered upload attempts, partial results and completed
commits. Independent dispatch facts report possible batch mutation, deferred submission or consume-once
retrieval; they do not prove remote success or authorize retry. Response status and request ID require
an observed response. Upload receipts require dispatch, file and image receipts are exclusive, and
operation-created JSONL belongs only to creation from requests. Collections copy their data, including
result bytes and opaque JSON. The owned codec rejects contradictory facts and undefined operations.
`LlmResourceOperationException` and `LlmResourceOperationCanceledException` expose a copied observation
with fixed diagnostic text. Cancellation preserves its actual cause and token. Their constructors do
not sanitize caller-supplied inner exceptions. Exceptions remain runtime objects, not JSON roots.

`LlmResourceProviderService(connectionAccess, providerService: null)` borrows an existing
`LlmConnectionAccess` and optionally an existing core `LlmProviderService`. A supplied core must borrow
the same access; construction validates that identity before allocating owned state.
`AddLlmResources()` registers one scoped concrete facade and its same-instance `ILlmBatchClient`
alias with the existing access and optional registered core. The caller registers connection access
separately; this extension creates no connection source or HTTP factory. With an implemented xAI core
route, creation, retrieval, one-page batch/request listing and cancellation use one acquired connection
snapshot and the core's selected HTTP owner. Cancellation and request listing first authorize the
literal batch ID through one nondestructive Get using that same snapshot. Receipt scope records the
fixed batches route and originally selected connection ID, not account or principal proof; imported
receipts can use another selected connection ID when the route matches and the current token authorizes.
Supplied page controls are forwarded without inferred limits or automatic paging. Response counts,
cursors and timezone-qualified timestamps retain actual observations; date-only timestamps are not
converted to invented instants. Mutation dispatch and validated prior/returned receipts survive late
cancellation and failure, with response facts belonging only to the current HTTP stage.
Deferred submission also uses that same selected snapshot, borrowed HTTP/Files owners and canonical
Chat Completions body. Typed controls, schema numeric guarantees, function-only tools and supplied
native history are checked before credential refresh or resource effects. Logical prompt and tool
resources use the existing chat preparation boundary. Tool messages carry ordered text and file
blocks with actual uploaded file IDs; text-only messages retain their string form. Distinct uploads
are observed at actual dispatch and receipt parsing before cleanup. The returned deferred receipt
preserves the actual `request_id`; its scope describes the fixed deferred route and selected
connection, not account authority.
Submission timestamps and expiry remain unknown when absent from the response. Chat continuation
state has a distinct xAI Chat-lane identity and is never replayed as Responses state.
Deferred retrieval performs one selected-token GET on the receipt's fixed route, including imported
receipts. It performs no preliminary consumption, polling or retry. An actual `202` returns Pending
without reading its body; an actual `200` uses the same canonical Chat decoder. Generated `output_files`
are downloaded incrementally through the same Files owner in their returned order. Each output uses
one owned resource write handle with actual observed metadata and commits with session selection before
successful publication. `GeneratedResources` contains the exact ordered commit receipts. A null resource
access is legal for artifact-free results; observed artifacts require access before any download or
write, while failure still reports the preceding consume-once dispatch and response. Earlier committed
outputs survive a later transfer, commit, cleanup or publication failure without deletion or retry.
Incremental batch requests and creation from requests share one ordered snapshot and the canonical
Responses, image and video body builders. Every row is validated before credential refresh or input
transfers. Chat selections are frozen once per prompt; media reads only its named sources. One batch
resolves each borrowed MCP authorization once. Upload attempts retain their actual row/source and
Files receipt without merging distinct transfers of the same resource.
Each native request is limited to 25,000,000 UTF-8 bytes. Add sends one inline POST to the authorized
existing batch; it does not impose that limit on the aggregate body, split requests or retry.
Creation from requests uses create then one add when the measured aggregate inline body is at most
25,000,000 bytes. Larger input uses one JSONL upload and file-based creation, limited to 200,000,000
UTF-8 bytes and 50,000 rows. Both representations use the same prepared bodies. Inline-created batches
remain open; file-created batches are sealed by the provider. An observed Create receipt survives
later Add, cancellation and cleanup failure with its original observed counts, not synthetic totals.
Supplied video audio uses its actual public URL or a bounded data URL from actual bytes. Unknown
MIME uses application/octet-stream as a wire label without changing content metadata. Native file
audio is unsupported; no signed-URL or library-side URL-fetch fallback is used.
Batch result retrieval returns one provider page in its actual order with the provider's pagination
token. Each result uses its actual custom row ID. Chat results use the canonical complete Chat decoder,
including tools, reasoning, citations, continuation and named generated files. Images use the canonical
image codec and commit/select every output into the borrowed Session/Workspace. Imported URL image
outputs use the same unsigned download path and retain their actual bounded bytes. Terminal video results
stream into the same named writer through Files or the core's unsigned media download pipeline. No
custom row ID becomes a native video job ID. That unsigned pipeline sends no provider headers or
cookies, follows no redirects and has no SDK loggers; caller-supplied HTTP factory behavior remains
the caller's obligation. Batch transfers keep the LLM route's existing caller-cancellation timeout
policy and do not change a borrowed client's Timeout.
The raw UTF-8 page and aggregate decoded inline image bytes each have the caller's independent
MaximumInlineResultBytes budget. Each image also has MaximumInlineImageBytes. Known lengths are
checked before allocation or writing. Nullable usage and media metadata remain unknown when absent.
ObservedResults and ObservedCommits preserve earlier output and exact commit/selection receipts after
a later transfer, cancellation, commit, publication or cleanup failure. Missing resource access refuses
artifact materialization without hiding the preceding result-page dispatch and response.
Disposal fences entry, drains admitted operations, shares retirement completion and releases borrowed
references without disposing them. Actual HTTP and preparation cleanup refusal retains the failed owner and is reported
by retirement; ordinary operation failures do not poison retirement. No operation retries or rolls back.
Caller cancellation retains the existing facade entry precedence. The owned codec refuses both
nonnull concrete and interface facade values; null runtime values remain portable.

This assembly activates no global provider registration and makes no OAuth or endpoint-qualification claim.
Caller workspace storage remains caller-owned. Constructing or serializing a receipt performs no
upload, polling, materialization, cache creation or tool execution.

## Video bindings

`VideoProviderService` binds the existing Common Video API to explicitly registered transports.
`AddVideoConnect` registers the family service and the Grok Video transport. The adapter uses the
selected xAI core and Files owner for generation, editing, extension, lookup and streaming download.
Registration does not establish provider entitlement or live compatibility.

The bound facade validates the complete native request, source metadata and known workspace
incompatibility before credential acquisition. Entered capability and submission bindings carry
the facade's synchronous retirement fence. The creator detaches that fence after the awaited
operation and cleanup settle. Selected authorization is composed before Video additional headers;
a collision with a selected secret header refuses before credential refresh.
Request timeout validation follows the .NET send-through-headers timer's range; it is not a
provider limit. Settings and direct binding enforce the same range.

Materialization streams actual downloaded bytes into one caller-owned write handle and commits
with session selection. It returns the existing resource descriptor. Actual full commit-and-selection
receipts remain in failure `GeneratedResources` if a later fence or independent download/writer
cleanup refuses. Image and Video attach those observations through one shared resource helper.
Only known conflicting session or MIME facts refuse; null metadata and missing MIME parameters
remain unknown. These source paths do not establish Grok Video entitlement or runtime qualification.

## Hosted STT bindings

`Darkspyre.Inference.Connect.STT` binds a host-selected `SttConnection` to the Common hosted
`ISpeechTranscriptionClient`. `SttProviderService` resolves registered `ISttProvider` adapters.
`AddSttConnect()` registers the facade and `XaiSttProvider` for file and streaming transcription.
Its setup catalog contains the registered adapter's definition. Registration performs no OAuth,
resource or provider request and does not prove provider entitlement. An unregistered transport
refuses before credential or resource effects. Hosted STT does not replace Tensor's model-instance
transcription API.

The connection carries a durable `XaiConnectionSelection`, optional borrowed authorization and copied
case-insensitive protected headers. `Bind(connection, xai, resources)` passes the matching borrowed
`XaiFamilyBinding` and caller workspace access to the selected adapter. These collaborators remain
caller-owned. Bound Common clients implement `IDisposable` and `IAsyncDisposable`. A successfully
handed-off streaming session owns its transport independently of the client.
Adapter operations require one validated snapshot and known-incompatibility validation before
credential refresh or source effects. Binding validates the existing fixed xAI regional API bases,
positive timeout and matching selection without acquisition. Direct binding and setup use the shared
header validator before provider or binding-factory callbacks. Reserved, malformed and newline-bearing
headers refuse; static authentication headers cannot override the borrowed authorization. The
five-minute request timeout is a library default, not a provider limit.

`ConnectSttJson` exports exact connection/transport/setup data getters with a complete generated graph.
Live authorization, family bindings, factories, services and sessions refuse nonnull portable JSON.
Diagnostics hide connection and header values. Setup preserves all nullable transcription/streaming
defaults and explicit stream budgets; omitted limits remain omitted. Commands still require explicit
valid budgets before opening a stream. Field edits are effect-free. Only registered adapters supply
capability checks. The disposable settings manager fences and drains admitted operations and saves,
then releases borrowed targets/factories/access without disposing them. Its own synchronous retirement
fence returns structured cancellation for admitted checks/queries even before linked cancellation
propagates. Admitted saves preserve the host's actual committed or uncertain outcome.
Checks retain matching borrowed authorization. Portable setup changes omit that runtime authority;
the selected connection ID remains data, and only explicit named header edits reach the commit target.

`XaiSttProvider` also supports explicit composition with a borrowed `HttpClient`. REST
transcription sends one multipart request with the file field last, or asks the provider to fetch the
supplied URL. Named workspace inputs use the existing resource reader without reading or changing
selected-resource membership. The adapter borrows input streams through their owned read handles.
It preserves actual transcript, channel, speaker and timestamp observations; absent response model
identity remains null. Known-invalid media headers, filenames and incompatible declared-container
raw controls refuse before connection acquisition. The adapter does not inspect payload bytes or
infer audio format from filenames.

The reusable WebSocket session waits for `transcript.created`, sends audio packets and ordered
`finalize`/`audio.done` commands, and exposes one bounded event feed with caller-supplied limits.
Opus packets remain indivisible; explicit accepted sample-rate controls are sent unchanged, but the
provider ignores them for Opus. Streaming refuses the independent text-normalization control;
language retains the provider's formatting semantics. Actual per-channel final observations remain
ordered as received. The convenience stream owns one temporary session and its input enumerator,
and client retirement cancels and drains that work without requiring a paused reader to resume.
Explicitly handed-off sessions remain caller-owned. Cleanup refusals retain actual unsafe owners
and failures; borrowed HTTP, authorization, resource access and input sources remain undisposed.
Diagnostics use fixed speech labels without copying provider error messages. OAuth endpoint
acceptance, the provider's ambiguous 500 MB/50-character limits and real-time behavior remain
provider qualification boundaries; the library does not invent byte or Unicode cutoffs.

`SttProviderService` and `VideoProviderService` implement `IDisposable` and `IAsyncDisposable`.
Retirement immediately fences new facade calls, drains admitted synchronous setup/binding callbacks,
then releases borrowed adapter references. It does not interrupt those callbacks or replace their
original results/exceptions. Adapters, handed-off clients/inference and settings managers keep their
independent caller ownership; facade disposal never disposes them. Concurrent retirement callers share
the same completion. Use asynchronous disposal while another thread is inside a facade callback.

> **Proprietary software.** Copyright © 2026 Brandon Mathis. All rights reserved.
> No permission is granted to use, copy, modify, or distribute this software
> without prior written authorization. See [LICENSE.md](LICENSE.md).

`Darkspyre.Inference.Connect` is the out-of-process inference NuGet used by Darkspyre applications.
It references `Darkspyre.Inference.Common`, which supplies the typed portable LLM and image inference
contracts implemented by both Connect and Tensor; it does not embed the Common assembly.
The `Darkspyre.Inference.Connect.LLM` assembly supports OpenAI Responses, OpenAI-compatible chat endpoints,
Anthropic Messages, direct ChatGPT subscription access and the official Claude and Antigravity CLIs,
host-executed tools, managed agents, structured output, streaming, discovery, account usage, and
embeddings. The `Darkspyre.Inference.Connect.Image` assembly supplies the image-provider contracts
and built-in ComfyUI HTTP/WebSocket transport. AWS Bedrock Converse and AgentCore Harness remain
isolated in the `Darkspyre.Inference.Connect.LLM.Bedrock` assembly.
Azure resource OpenAI v1 Responses and Chat Completions use the optional
`Darkspyre.Inference.Connect.LLM.Azure` assembly inside this same package.

The package also includes `Darkspyre.Inference.Connect.TTS` for external
PocketTTS HTTP synthesis and `Darkspyre.Inference.Connect.Audio` for external TangoFlux generation.
They are included in the Connect product project, not separate NuGets. See
[PocketTTS integration](#pockettts-speech) and
[TangoFlux integration](#tangoflux-audio). The coordinated source package version is **11.15.1-rc.1**.

The packages target .NET 11. Credentials
for direct API transports remain owned by the host through `ILlmConnectionSource`; the connector
never owns an application database or secret store. Wrapped CLI transports instead use the official
CLI's existing login, which Darkspyre.Inference.Connect.LLM never reads or copies. Optional Claude catalog OAuth is a
separate model-discovery credential; it never selects or enables the Claude CLI transport.

`LlmCredential` and `LlmOAuthTokens` return constant redacted diagnostic strings. Formatting either
record exposes no credential values or account metadata. Their properties, record copies and
protected-store `Serialize`/`Parse` wire format retain the original values. Serialized credential
JSON remains secret and is not a diagnostic representation.

ACP and Copilot runtime configuration uses closed typed profiles, state selections and
authentication selections. `ConnectLlmJson` exports the corresponding data and staged-login
observation metadata. Live launch inputs, login services, factories and runtime authentication
owners refuse non-null serialization without invoking borrowed callbacks. Null runtime inputs
round-trip. Copilot credentials use the existing protected Web/null-omit codec; their diagnostic
strings redact tokens. These declarations do not register an ACP or Copilot adapter, launch a
runtime, perform login, validate state-directory isolation or certify a runtime/SDK version.

Existing transports reject foreign ACP/Copilot configuration, explicit runtime launch inputs
(including an empty object), and dedicated Copilot credentials before refresh, catalog, process,
HTTP or host inference effects. Direct adapters and resolved facade calls use the same admission
rule. Credential refresh and login results also refuse foreign runtime credentials before a
conditional save. Settings report invalid foreign inputs without calling environments or the
commit target. Runtime profiles and launch inputs copy their collections on initialization;
admission does not remove supplied inputs or select a different transport. Diagnostic redaction
includes both the GitHub token and the separate runtime connection token.

`LlmAgentProviderService.BindRuntimeAuthentication` exposes saved-ID and explicit draft/identity
overloads. Both reject runtime authentication as unavailable before resolving a connection or
starting login. `CreateSettingsManager(target, agents)` optionally borrows the agent facade from
the same connection source. The settings manager fences new actions, choices, opens and saves
when retirement starts, drains admitted calls, then drops borrowed collaborators and its commit
target. It never disposes them. A save that settles during retirement retains its actual commit
state and version, including committed or uncertain outcomes.

Every returned `LlmConnection` includes a required opaque `Version`. The version must cover both
connection metadata and credential state. `SaveCredentialAsync` must atomically compare
`expectedVersion` and replace only the credential when it still matches; it must reject changed,
replaced, or deleted connections and must never upsert. A separate read/check/write sequence is not
sufficient because delayed OAuth completion or refresh could otherwise overwrite a concurrent provider
change.

## Packages

The coordinated release contains four NuGets. They always use the same version from the repository
`Directory.Build.props` and publish together. Version numbers use
`<net>.<release>.<minor>`: .NET generation, breaking-contract release, and compatible tweak/fix.
The direct Codex and host-owned managed-agent behavior from the former `Mathis.LlmConnect` 3.0.0 is
retained; Claude and AgentCore retain provider-owned sessions.
See the [Agent DS handoff](docs/agent-ds-contract-handoff.md),
[technical design](docs/enhancements.md), and [verification record](docs/managed-agent-verification.md).

The Apple on-device adapter is registered by `AddLlmConnect`. Its separately built Swift
helper and release gates are described in the [connector design](docs/apple-foundation-models-design.md).
This source implementation has not been published as a signed helper release.

Google Vertex AI accepts Gemini text and Claude model IDs through shared provider profiles.
Register `AddLlmConnectVertex()` alongside `AddLlmConnect()` and supply a typed `LlmConnection.Vertex`
with project, location and ADC or service-account authentication. `ApiKey` selects global projectless
express mode for Gemini only; `ProjectId` and `Location` are null and the protected credential holds
the Google Cloud API key. Express mode has no model listing. Catalog entries report unknown invocation
access, not regional eligibility or permissions. Unknown limits and Claude strict-schema support stay
unknown; an explicit schema capability override enables a native schema attempt. The adapter assembly
is included by the Connect package project. See the [technical mapping](docs/google-vertex-ai-tech-design.md).
Live cloud qualification remains; the shared contract requires host migration.

The Gemini API (Google AI Studio) adapter is registered by `AddLlmConnect` under the provider id
`gemini-api` and transport `GoogleGeminiApi`. It needs only an API key, sent in the `x-goog-api-key`
header, and a fixed endpoint. It shares the Gemini request, stream, schema and continuation code with the
Vertex Gemini route. Both routes use native `responseFormat` with their documented object/array
shapes; Vertex retains its narrower schema keyword allowlist. Provider options go into
`generationConfig` and cannot replace fields written by the canonical builder. See the
[Gemini API design](docs/gemini-api-tech-design.md) for model discovery, capabilities and limits. Live
qualification remains.

### Contract migration

Continuation state is now Common's opaque `LlmContinuationState`, carried by both portable and
Connect messages/results as `ContinuationState`. Continuation envelopes use format 2; context captures
use format 3. The owned context codec accepts the closed format-2 binary/function shape and upgrades
it without changing transcript revision, IDs, order, bytes, omissions or opaque continuation. Old
`ResponseState` JSON members and unversioned captures are rejected. Hosts must migrate stored records
before adoption; see [DSA-68 handoff](docs/agent-ds-contract-handoff.md). Explicit state omission is
reported by `OmittedContinuationStateCount`; default handling rejects unbound state.

### JSON metadata

Each metadata getter owns its options and type cache. Callers can retain the returned metadata for
their generation and release it at retirement. Rejected foreign types have no static metadata cache.

`ConnectLlmJson.PortableRequest` closes the Common request graph with the fixed `XaiXSearchTool`
variant, also supported by `ChatRequest` metadata. It adds no arbitrary CLR subtype registry and
does not change Common's metadata. Owned options compose Common's exact runtime-resource and
authorization refusal converters; live authorization cannot be serialized or reconstructed as
data, and serialization never invokes its callback. The separate credential codec keeps its
existing Web names and null omission.

The private xAI Responses adapter maps DocumentSearch, WebSearch, SocialSearch, CodeExecution,
ImageGeneration and RemoteMcp with positive typed hosted-turn limits. Document search sends exact
collection IDs and optional result limits; each descriptor accepts at most ten collections.
Actual built `input_file` content also enables implicit document-search observations. Both paths
request `file_search_call.results`. Hosted-tool include strings use primitive JSON nodes without
default reflection metadata. Web controls use flat domains and image controls. X controls
preserve handles and dates without an invented handle cutoff or date interpretation. Deferred MCP
adds the server-side `tool_search` descriptor and retains its ordered opaque call/output items.
Pure preflight counts all wire tools before effects. Authorization resolves once per distinct
borrowed authority during generation, never during estimation or serialization.
Other built-in adapters and unmapped hosted kinds reject unsupported options before provider effects.
Native start/resume also rejects these unmapped options. Existing Anthropic server-tool configuration
is separate and does not advertise the new hosted API.

The same Responses parser observes hosted calls in streamed and complete JSON responses. A generic
added item produces Started only with a usable actual provider ID. The first terminal output supplies
ordered Completed observations; it does not infer progress phases or successful execution. Every
ID-less occurrence remains distinct. Repeated usable item IDs identify one call within that
response. Unreported identity, status, result collections and scores remain null; actual empty
collections, strings and numeric zero remain exact. The provider's documented omitted-score default
does not replace the shared API's unreported-score observation. Full terminal output remains unchanged
in opaque replay. Result-only `CompleteAsync` retains its terminal chat result; callers enumerate
`StreamAsync` for hosted observations. This mapping does not qualify live OAuth acceptance or
provider-specific file-search phase events. X custom calls are classified only by the published
search names and retain actual call IDs separately from opaque item IDs. Image progress uses only
documented lifecycle events. Unpublished understanding output/event shapes remain unmapped.

Citations preserve first-observed annotation order and suppress terminal copies of the same
occurrence. The adapter requests `no_inline_citations`. Offset units are unqualified, so positions
remain absent. Code outputs preserve actual code, logs and image URLs without fetching remote
artifacts. MCP arguments, output and error remain actual JSON values, including JSON strings.
Generated images decode bare base64 and identify JPEG/PNG from bytes. Without Resources they carry
copied bytes. With Resources they use the existing admitted scope and staged resource writer,
atomically commit-and-select, and release the owned write handle before hosted completion. The
final result carries actual ordered commit receipts. Cleanup errors preserve original failures and
uncertain mutation facts. Operation-local MCP secrets reach existing HTTP, SSE, protocol, model and
terminal diagnostic sanitization; retained opaque terminal output is not rewritten.
Managed sessions snapshot hosted data, borrow authorization and preserve actual streamed hosted
and citation observations under the invocation RequestId; they never route hosted observations to
the host function handler. Terminal chat/portable results preserve ordered citation lists.
Completed hosted calls preserve independently nullable observed IDs and status through the same
snapshot and nested agent/realtime metadata. Actual empty or whitespace strings remain unchanged;
they are not usable lifecycle correlation IDs. Owned portable JSON requires both nullable members
explicitly. Started/progress events still require usable actual IDs and observed lifecycle facts.
Completion reports an actual terminal item, not successful tool execution or an inferred start.
Facade generation, input counting and request measurement freeze hosted descriptors before the
first connection-source await. Later caller mutations cannot change admitted filters or headers;
authorization stays borrowed. Owned hosted-event metadata requires actual scope and observation
facts, including explicitly reported compaction revisions. Nested hosted image output retains
Common's property-specific null-byte refusal and its new closed phase/action enum policies.
Managed disposal shares its completion across callers, drains the producer even after cancellation
callback failure, then releases borrowed hosted authorization without disposing it. Cleanup failures
remain visible to every disposer. These controlled contracts do not qualify provider wire mappings
or native execution. Each accepted terminal model result projects one
`LlmAgentCitationsSnapshotEvent` with its actual request and turn IDs and a copied ordered citation
list, including non-successful result statuses. The snapshot precedes tool execution and turn
completion. It is a terminal-collection observation, not an append, deletion or clearing instruction.
An empty terminal collection does not prove provider-reported absence. Streamed individual citation
observations are not compared or deduplicated against terminal snapshots. The new concrete event has
an exact type-name getter on `ConnectLlmJson`; it adds no portable base-event union.

`ConnectLlmJson` exposes owned source-generated metadata for closed LLM connections, saved setup
configuration, chat messages/requests/results and captured agent context. Pass the matching typed
metadata to `JsonSerializer`. Credential `Serialize`/`Parse`, context-entry equivalence and continuation
scope hashing use owned metadata. Credentials retain their camel-case Web format and null omission;
captured context retains its property names, Common tool-output discriminators and
binary/null-versus-empty representations. Resolved internal services are never serialized.
Connection and setup metadata retain typed Vertex, Azure, Chat Completions and xAI options, including
unknown numeric modes for validation by their owning adapter; writing them does not enable a provider.

Format-3 context requires an explicit `SelectedResources` list. Its optional
`ResourceSelectionVersion` records the caller's workspace, logical session and selection revision,
independently of transcript revision. The codec copies and validates this data without accessing
storage. Legacy captures upgrade to empty membership with unknown provenance, not revision zero.
Byte-excluded captures retain logical references and the selection snapshot. Current session
implementations reject selections they cannot bind; import never silently installs or deletes caller
membership.

Portable context and its owned codec preserve a final incomplete mixed tool-call group with each
actual completed same-kind result. No later transcript message can follow an unresolved group.
Missing results do not imply an exit code, timeout, rollback or an unstarted call. Runnable imports,
next turns, provider projection and `ReplaceContextAsync` still require settled matching groups.
Callers reconcile with actual results and revision-checked replacement; the library does not replay
an executor, manufacture outcomes or prune the transcript. This data contract does not enable managed
shell execution.

Chat messages and turns use the Common ordered binary/native-file/workspace-resource attachment
union. `LlmChatRequest.Resources` and its Common projection preserve the same borrowed authority.
Owned request JSON refuses non-null authority and request diagnostics never traverse it.
Current built-in routes reject unimplemented references and authority before refresh, catalog,
provider, process or model execution. `SupportsFileReferences` defaults to false; capability overrides
cannot enable a missing native-file route.

Chat admission copies each ordered attachment list, binary payload and omission list before
connection resolution, refresh or catalog work. Direct Vertex, Bedrock Converse and SelfHost
requests use the same snapshot before authentication, model resolution or engine acquisition.
Native Claude turns snapshot input lists and attachments before control acknowledgments and turn-start events;
managed turns use the same copier before their asynchronous work.
Caller mutation after admission cannot change the attachment graph used by the request builder.
Runtime resource authority stays borrowed; snapshotting does not fetch or materialize references.
The same admitted request freezes string stops, provider-option JSON and structured-output schemas
before connection resolution; disposing the caller's JSON document does not invalidate that snapshot.

Gemini API and Vertex local text estimates serialize requests with `ConnectLlmJson.ChatRequest`.
The projection retains default property names/order, numeric enums, explicit nulls and escaping.
Opaque continuation state and media still produce unknown local occupancy; this metadata does not
turn a local estimate into a provider count or admission check.

Contexts and scope helpers remain internal. The package installs no reflection fallback for these
families. Runtime settings, one-way secret edits, executable handlers and undiscriminated agent/event
bases are not persisted snapshots. Provider CLI/HTTP protocol writers remain separate owners;
metadata for these closed families does not establish retirement of an active provider session.

Retained chat protocol builders write stop arrays, model aliases, numeric/string control IDs and
Responses output joins through structured JSON nodes and elements. Vertex argument-path identity
uses ordered string/index JSON segments and the BCL parser for escaped names. These wire paths
do not call the unconfigured serializer or depend on its reflection defaults. Portable contracts
continue to use their existing owned metadata; protocol projection adds no public metadata roots.
Retained JSON-object HTTP requests use the same `RequestJson` owner with private DOM metadata
generated by the existing internal context. Request bodies remain streamed by `JsonContent`; the
helper does not copy them into an extra full-body string or byte buffer or use reflection defaults.

Apple helper generation and cancellation commands use their own internal generated metadata,
including the recursive typed schema plan. The helper protocol retains its camel-case Web names,
null omission, escaping, sampling fields and maximum JSON depth of 128. No helper/session lifetime
behavior or Apple private framework format is changed by this serialization boundary.

PocketTTS voice responses and JSON bridge requests, and TangoFlux generation requests, use their
provider-owned internal generated metadata. PocketTTS retains explicit null voice/speaker fields;
TangoFlux still omits absent seed/cache fields while preserving explicit zero/false values. These
private wire types do not add public request contracts or change HTTP/cancellation behavior.

ComfyUI output state retains its existing Pascal-case persisted format through separate owned
metadata. Prompt/delete requests and diagnostic payloads use their own Web-format metadata; workflow
JSON remains opaque DOM data, including nulls and unknown node fields. Serialization does not change
workflow interpretation or progress/cancellation lifetime.

Claude and Codex's private OAuth JSON requests are written as DOM data, not reflection over anonymous
library types. Their field names/order, escaping and explicit null fields remain unchanged; Codex's
authorization-code exchange remains form encoded. This does not change either authentication flow.

Bedrock serializes AWS `Document` values through the SDK's public `DocumentMarshaller`, without
reflection metadata. Recursive and numeric JSON retains the SDK's representation. An absent nullable
document writes `{}`; an explicit JSON `null` remains distinct. Bedrock and AgentCore read documents
through the SDK's public `DocumentUnmarshaller` after strict BCL JSON validation. A private envelope
supports primitive roots through the SDK's container-oriented context and is removed before use.
The complete captured input supplies the reader buffer size; EOF returns its rented buffer even
when mapping fails. These paths do not use attributed reflection deserialization or copy a parser.

Claude CLI input envelopes and Antigravity chat/agent stdin envelopes use DOM writers with the same
field order, escaping, explicit null fields and final newline. Claude input keeps text before ordered
attachments. Missing Antigravity tool parameters and failed managed-tool parameters remain empty JSON
objects. These writers do not serialize library-owned anonymous types or change process/session lifetime.

Native Claude agent initialization, controls, user turns and MCP envelopes use private DOM writers.
Tool manifests retain ordered schemas and boolean `anthropic/alwaysLoad` metadata. MCP responses keep
numeric and string request IDs distinct; input/tool parts retain their ordered media/resource fields,
explicit nulls and omitted optional fields. The process wrapper owns the final newline. JSON writing
does not change host-tool cancellation, response routing or session retirement.

`LlmToolCapabilities.StrictInputSchema` reports Supported, Unsupported or Unknown. Unknown allows an
explicit `Strict=true` request unchanged so the host can try it and handle the provider result.
Known Unsupported rejects that request. Ordinary tools default to `Strict=false` independently.
Cache-read/write and reasoning usage details are optional subsets of normalized input/output totals.

### Vertex ConsoleTester

Use the existing console application with ADC configured on the machine:

```bash
dotnet run --project Connect/tools/Darkspyre.Inference.Connect.ConsoleTester -- vertex-probe --provider vertex --google-project YOUR_PROJECT --google-location global --model google/gemini-3-flash-preview --case all --repeat 1 --timeout-seconds 120
```

Use `anthropic/claude-sonnet-4-6` to exercise the Claude route. Cases are `text`, `structured`, `tools`,
`agent` and `cancel`. Calls are explicitly metered; failed cases are not retried. Unavailable auth or
catalog discovery produces a visible skip. The existing `prompt --image` and `prompt --document`
commands cover attachment experiments separately. These live probes are not normal test gates.

Express mode reads `VERTEX_API_KEY` during explicit login and stores it in the operating system vault.
The key never appears in a URL or command argument. Do not pass a project or location in this mode:

```bash
dotnet run --project Connect/tools/Darkspyre.Inference.Connect.ConsoleTester -- login --provider vertex --google-authentication ApiKey
dotnet run --project Connect/tools/Darkspyre.Inference.Connect.ConsoleTester -- vertex-probe --provider vertex --google-authentication ApiKey --model google/gemini-3-flash-preview --case all --repeat 1 --timeout-seconds 120
```

The express status check validates configuration only. Generation verifies remote model access.

### Gemini API ConsoleTester

Store the key once from the environment, so it never appears in a command argument, then run the probe:

```bash
GEMINI_API_KEY=YOUR_KEY dotnet run --project Connect/tools/Darkspyre.Inference.Connect.ConsoleTester -- login --provider gemini
```

```bash
dotnet run --project Connect/tools/Darkspyre.Inference.Connect.ConsoleTester -- gemini-probe --provider gemini --model gemini-3-flash-preview --case all --repeat 1 --timeout-seconds 120
```

The cases match the Vertex probe; both commands run `GoogleProbe`. Calls are metered against the key's project.

| Package | Purpose |
|---|---|
| `Darkspyre.Inference.Common` | Typed LLM, image, and decision requests, results, capabilities, streaming events, provenance, and failures shared by Connect and Tensor |
| `Darkspyre.Inference.Connect` | LLM, Bedrock/AgentCore, ComfyUI, PocketTTS and TangoFlux connections |
| `Darkspyre.Inference.Tensor` | In-process LLM, image, typed-decision and Whisper speech transcription |
| `Darkspyre.Inference.UI.Settings` | Host-placed Blazor/Lumeo editor using only Common setup contracts |

Connect, Tensor and UI.Settings depend on the exact synchronized Common NuGet; none carries
another product's DLLs. UI.Settings does not depend on Connect or Tensor. The release-candidate
suffix reflects the .NET 11 RC ASP.NET dependency, including on the private package feed.

There is no `Darkspyre.Inference.Connect.Common` package or assembly. Connector-only endpoint,
authentication, provider-management, and job contracts remain in their capability assemblies. Only
semantic inference contracts that are also implemented by Tensor belong in
`Darkspyre.Inference.Common`.

Connect's chat lane uses Common's LLM primitives directly: the stream events, tool definitions, tool
choice, tool calls, tool results with `LlmToolOutput`, attachments, structured-output types, completion
status and `LlmUsage`, all in `Darkspyre.Inference.Common.LLM`. Connect adds only what carries
Connect-specific data: `LlmChatMessage` and `LlmChatResult` with provider diagnostics and the shared continuation envelope,
`LlmChatCompletionEvent` for the final result, and `LlmModel` with provider capabilities. An attachment's
`MediaType` is used as given; without one, the adapter detects the type from the bytes.

## Portable inference lane

Bind a configured Connect implementation when an application wants to select Connect or Tensor
without changing its request and result types:

```csharp
ILlmInference llm = providerService.Bind(connectionId);
LlmInferenceResult result = await llm.CompleteAsync(new LlmInferenceRequest
{
    ModelId = modelId,
    Messages = [new LlmMessage("user", prompt)]
}, cancellationToken);
```

Image workflows expose the same boundary through `ImageProviderService.Bind(connection, workflow)`.
Binding is explicit and retains exactly one backend. Neither adapter retries through a different
provider nor falls back from Connect to Tensor.
The returned `IImageInference` exposes asynchronous disposal directly. Callers own its retirement;
the bound instance still borrows its collaborators and does not dispose them.

`LlmChatResult.ModelId` and `ModelRevision` carry actual adapter observations. Both are nullable.
The portable result preserves them in Common provenance; it does not substitute the requested
model. A self-hosted result preserves its actual loaded model identity and separately reported
revision. Existing result metadata and nested batch/deferred graphs carry both optional fields.

HTTP chat adapters read the returned `model`. Gemini and Vertex Gemini read the actual
`modelVersion` unchanged as the model identity, without deriving a separate revision from its name.
Missing observations remain unknown. Conflicting observations within one HTTP response, malformed
values and protected credential/header values refuse publication through a fixed safe error.
Claude streaming reads the most recent actual main assistant message or message-start observation;
init selections, subagent messages and aggregate usage keys do not supply identity. Its non-streaming
terminal format and the current Apple, Antigravity and Bedrock protocols report no model identity.

### Reasoning levels

`LlmInferenceRequest.ReasoningLevel` takes an `LlmReasoningLevel`: `None`, `Minimal`, `Low`, `Medium`,
`High`, `ExtraHigh` or `Maximum`. On one model, each level asks for at least as much reasoning as the
level below it; a level does not cost the same on two different models. Null leaves the provider's
default in place. Each model reports the levels it accepts in `LlmCapabilities.ReasoningLevels`, and
`SupportsReasoning` is true when that set contains a level above `None`. A request carrying a level
the model does not accept fails with `InferenceFailureCode.InvalidRequest` before any provider call;
no adapter substitutes a nearby level. Each adapter owns the mapping to its provider's wire form.
`LlmCapabilityOverrides.ReasoningLevels` replaces a transport's set for a connection or one model,
and the setup editor exposes it as one Default/Enabled/Disabled choice per level. The design is
`docs/llm-reasoning-level.tech-design.md` in the repository root.

Anthropic API-key, Gemini API and ChatGPT subscription connections read each model's levels from the
provider's model catalog, which `LlmModelListCache` keeps. The first capability query or generation on a
new connection version therefore lists the connection's models once. When that listing fails, the adapter
uses its own model-family table and the operation proceeds.

Codex also reads `supports_reasoning_summary_parameter` and `input_modalities` from the selected
model's catalog entry. A false summary flag disables public reasoning and summary requests without
changing effort or retained state. An explicit modality array without `image` disables prompt images
and image tool results in chat and managed-agent sessions. Missing fields retain the adapter defaults.
Connection and model overrides keep their existing precedence over these catalog facts. Other tool
content and prompt documents keep their existing support.

OpenAI Responses levels are per model, from a table of verified model pages, because the OpenAI Models
API reports no capabilities. `None` sends `reasoning.effort: "none"` and `Maximum` sends `"max"`. A
verified reasoning model accepts `temperature` only with `ReasoningLevel = None`; a request without a
level counts as reasoning, because these models default to effort `medium`. A model without a table row
offers `Minimal` to `ExtraHigh` and accepts temperature at any level.

## PocketTTS speech

`ITtsClient` accepts `TtsRequest` and returns `TtsResult` with WAV bytes and provenance. Register
`services.AddTtsConnect()`, then bind a host-supplied `TtsConnection` with `TtsProviderService`:

```csharp
await using ITtsClient speech = ttsProviders.Bind(new TtsConnection
{
    Endpoint = new Uri("http://127.0.0.1:8020"),
    PocketTts = new PocketTtsEndpointOptions { Protocol = PocketTtsProtocol.Multipart }
});
IReadOnlyList<TtsVoice> voices = await speech.ListVoicesAsync(cancellationToken);
TtsResult clip = await speech.SpeakAsync(new TtsRequest
{
    Text = "The next room is quiet.",
    VoiceId = selectedVoiceId,
    Format = TtsAudioFormat.Wav
}, cancellationToken);
// The host owns playback or storage of clip.Audio.
```

The connector never starts a bridge, installs models, bypasses TLS validation or manages playback.
The endpoint is external even on localhost. Multipart sends `text`, `output_format` and the
configured voice field (default `voice_url`). It rejects non-default speed. BridgeJson sends `text`,
`voice`, `speaker`, `speed` and `response_format`. PocketTTS advertises and accepts only WAV.
Voice discovery uses `/voices`, falling back to `/health` only after a successful empty list. It
preserves both voice-option objects and voice-name arrays; it never invents fallback voices.

`SetupDefinitions` and `CreateSettingsManager` use the shared typed setup editor. Endpoint, timeout,
protocol, paths, protected headers, voice and speech defaults are editable. `TtsSetupChanges`
contains the host's identity/version, the typed configuration with no header values, and explicit
protected-header sets/removals. Untouched headers are omitted from mutations, not deleted. Save
does not list voices or synthesize. Endpoint/voice verification and potentially billed synthesis
are separate actions; protocol changes update the speed constraints and mark test evidence stale.
The host retains its usual atomic settings/secret commit responsibility.

HTTP authentication/access errors, timeouts and malformed responses have distinct
`InferenceFailureCode` values; caller cancellation remains `OperationCanceledException`. Requests
are never automatically retried. Raw response bodies, prompts and protected headers are not logged
or copied into errors. Registration disables automatic redirects and gives each request its
connection timeout. If constructing `PocketTtsHttpProvider` with your own `HttpClient`, disable
automatic redirects yourself so custom secret headers cannot follow a redirect to another origin;
the host owns that client's lifetime. Endpoint paths cannot select a different origin.

### TTS data and streaming contracts

`TtsRequest` and `TtsSetupConfiguration` carry optional language, sample rate in Hz, bit rate in
bits per second and timestamp selection. `TtsConnection.XaiDefaults` owns saved typed
`XaiTtsOptions`; request options inherit each null member, while false, zero and empty maps
override it. The owned setup codec rejects the removed setup-level `Xai` field instead of
silently discarding saved values. The xAI options preserve nullable
latency/normalization controls and an ordinal text-replacement map. Absent and explicit empty
maps remain distinct. PocketTTS rejects every explicit new control, including `false`, before
HTTP. Its settings preserve the controls but refuse unsupported test/save actions.

`TtsResult` carries nullable duration and ordered `TtsTimestamp` observations. Each timestamp
retains actual text and the observed start/end timeline; it does not invent text offsets.
Streaming timestamps use the observed session timeline, not a per-chunk reset. Missing timing
stays null; observed empty remains empty. Audio, timestamp lists and replacement maps are copied
at their data boundary, including `with` updates. Diagnostic strings omit content and credentials.

`ConnectTtsJson` exports exact type-name metadata getters for TTS connection, setup, request,
capability, result, option and concrete event data. Its closed generated graph composes Common
provenance/failure data without reflection fallback. Non-null runtime clients/providers/sessions
refuse serialization and restoration without inspecting their callbacks; null remains null.
The provider-private Pocket wire codec remains separate and preserves its existing request bytes.
`ConnectTtsJson.TtsStreamEvent` owns the closed audio, completion, error, replacement-update and
utterance-clear union. Replacement updates and clear commands complete after send; their
observations preserve the actual acknowledgment. The convenience stream returns all events.
Replacement updates copy caller maps inside session admission before waiting to send. Events
reader admission refuses a retired session without holding a lease across event yields.

Bound `ITtsClient` instances support synchronous and asynchronous disposal. Pocket clients
fence new calls, cancel and drain admitted work, then drop borrowed HTTP/provider/resource
references. A nonnull resource argument selects artifact mode: Pocket commits validated WAV
bytes and session membership atomically before returning `TtsResult.Resource`. It does not
invent a filename. Null resources preserve standalone bytes. The caller owns the access and
returned clients; facade retirement does not retire handed-off clients. Settings retire their
temporary clients and do not bind resources for readiness checks or sample clips.
Pocket exchanges own request bodies before the first send fence and clean response and request
owners once. Refused cleanup retains the actual unsafe owner and reaches the client's cached
retirement failure. Independent operation and cleanup errors remain ordered and intact.
TTS and hosted-STT settings retire every temporary client, including a binding rejected by a
late owner fence. They preserve genuine cleanup failures across repeated disposal after draining
work and dropping borrowed collaborators. Ordinary action failures do not poison retirement.
Retained cleanup failures can retain the refused client's own collaborator and metadata graph.

`ITtsStreamingClient` and `ITtsStreamingSession` declare reusable disposable streaming and one
ephemeral iterator wrapper. Opens and wrappers require explicit positive `InferenceStreamLimits`
for event count, buffered payload bytes and assembled message bytes. These caller-local budgets
are not provider limits. Session commands complete after ordered transport send, not terminal
acknowledgment or event consumption. One event consumer drains the feed; disposal closes the
feed before draining admitted work. No automatic replay/reconnect is part of these contracts.

PocketTTS implements neither streaming interface, advertises all new capability flags false and
leaves unknown rate lists null. `AddTtsConnect()` registers PocketTTS and `XaiTtsProvider`.
The xAI adapter implements REST synthesis and reusable streaming sessions; OAuth and actual
provider readiness remain unqualified. `Bind(TtsConnection, XaiFamilyBinding?,
IInferenceResourceAccess?)` borrows the entered owners without acquiring credentials or making
requests. Connection headers are copied, and xAI selection/authorization must match the binding.
PocketTTS refuses the new xAI authority fields before HTTP; existing static-header workflows
remain supported. Runtime authority, resource access, binding factories and settings owners refuse
non-null serialization through the owned codec without invoking callbacks.

TTS settings managers fence new admissions synchronously, drain admitted actions and conditional
commits, then release borrowed collaborators without disposing them. Opening settings checks the
owner fence after reading the borrowed setup definition. Late check/test/query results
cannot verify an edited or retiring draft. Portable changes omit runtime authorization and retain
protected-header patches separately. Typed xAI setup controls preserve explicit false, zero and
empty values; setup does not automatically register an xAI provider.

The explicit xAI client acquires one OAuth snapshot per REST operation or stream opening. Voice
listing requires the binding's optional borrowed LLM facade and uses its selected HTTP/admission
owner without a second resolve or refresh. Voice observations remain ordered actual provider data,
not a separate cached model catalog. Without that facade, voice-listing capability is false and the
operation refuses before resolution or HTTP. REST timestamps preserve graph text and timeline
pairs; raw responses preserve their observed media type. Speech with no reported model has required
`Provenance.ModelId = null`, rather than a fabricated model identity.

Streaming commands map `text.delta`, `text.done`, `text.clear` and `session.update`; observations
preserve actual `audio.delta`, `audio.done`, `audio.clear`, `session.updated` and sanitized errors.
Resources use one incremental handle per utterance and commit bytes plus selected membership before
completion publication. Pocket and xAI speech use the same internal `SpeechResourceWriter` in the
LLM assembly for scope validation, commit/select receipts and owned-handle cleanup. Its selection
version exposes only the actual observed commit receipt; it does not query the store again.
REST synthesis and streaming utterance completion retain the complete validated
commit-and-selection receipt in failure `GeneratedResources` if writer cleanup, cancellation
or final result publication fails.
The receipt contains the actual selected membership snapshot, not a later store read.
Chunks remain transient; clear discards uncommitted staging. The handed-off
session is independently caller-owned and survives client retirement. Client and session retirement
fence, cancel, drain and detach borrowed references; genuine refused cleanup owners and failures remain
retained without retry. The convenience iterator owns its temporary session and input enumerator;
client retirement closes that session and drains input even while the public iterator is paused.
The wrapper uses the same bounded event feed. It never disposes the borrowed input enumerable.
Session retirement closes observations promptly without publishing late completion. Its cached
retirement result preserves actual late artifact commit/selection facts, including unknown acknowledgment,
and any independent cleanup refusal. Ordinary receive cancellation remains healthy.
The convenience result preserves the original resource failures once, including typed cancellation
from borrowed input; input-only operation failures do not poison client retirement.
No automatic reconnect/replay occurs. OAuth endpoint acceptance and real
voice/latency qualification remain open: the guide's latency value 2 is passed through unchanged despite
the standalone schema's narrower 0/1 declaration. Controlled tests do not qualify live provider access.
Failed BCL WebSocket upgrades use only collected actual HTTP status: 401/402 retain the shared
authentication/entitlement status rules, while 400/403 cannot imply hidden body-dependent facts.
Unobserved status remains a safe transport failure. Raw bodies, headers and transport diagnostics
are not public errors; original transport evidence remains internal. Cancellation is not reclassified.

`TtsProviderService` supports synchronous and asynchronous disposal. Retirement fences new facade
calls, drains admitted synchronous setup/binding callbacks, then releases borrowed provider references.
It does not interrupt those callbacks or replace their original result or failure. Returned clients
and settings managers keep independent caller ownership; facade disposal never disposes them or
the borrowed providers. Concurrent disposal callers share the same completion. Use asynchronous
disposal while another thread is inside a facade callback.

## TangoFlux audio

`ConnectAudioJson` exports exact type-name metadata getters for the nine public Audio
connection, generation, capability and setup data roots. Its finite generated graph preserves
optional defaults, explicit zero/false values and unknown measured duration. Observed capability
and result facts cannot be omitted or replaced with null. Unknown transport/member values refuse.
Use these getters for portable data; the private TangoFlux wire codec retains its own Web naming
and seed/cache omission policy. Audio-owned options refuse non-null live clients, providers,
settings, managers, commit targets, streams and authorization. Null runtime values round-trip;
metadata does not invoke callbacks or acquire provider resources.

`IAudioGenerationClient` accepts typed generation requests and returns WAV bytes and provenance.
Register `services.AddAudioConnect()`, then bind a host-supplied `AudioConnection` with
`AudioProviderService`:

```csharp
IAudioGenerationClient audio = audioProviders.Bind(new AudioConnection
{
    Endpoint = new Uri("http://127.0.0.1:8769"),
    RequestTimeout = TimeSpan.FromMinutes(2)
});
AudioGenerationResult clip = await audio.GenerateAsync(new AudioGenerationRequest
{
    Prompt = "Rain tapping on a wooden roof.",
    Duration = TimeSpan.FromSeconds(5),
    Steps = 20,
    GuidanceScale = 4.5,
    Seed = 42,
    UseCache = false
}, cancellationToken);
// The host owns playback, storage and its own cache. MeasuredDuration may be null.
```

The adapter sends the donor `/generate` protocol: `prompt`, duration in seconds, `steps`,
`guidanceScale`, and optional `seed`/`useCache`. Duration must be 1–30 seconds, steps 10–100 and
finite guidance 0.1–20. Invalid values fail before HTTP; they are not clamped. Omitted steps/guidance
use 10/4.5. An omitted seed remains omitted, including when `UseCache` is false. No application cache
policy, random seed, model installation, provider process, playback or soundscape logic is included.
WAV framing is validated; decoding and duration measurement are not performed, so `MeasuredDuration`
is null rather than copied from the requested duration. An absent content type is accepted only
with valid WAV bytes; an explicitly unsupported content type fails.

`SetupDefinitions` and `CreateSettingsManager` expose typed endpoint, fractional timeout, relative
generation path, protected headers, duration, steps, guidance, optional seed and a three-state
provider-cache choice. `AudioSetupChanges` carries create/edit identity and expected version, typed
settings without header values, and explicit protected-header sets/removals. Keep and omission do
not delete existing headers. The host owns the atomic configuration/secret commit. Save performs no
network request; tests use the settings' current values, and an edit marks their result stale.

TangoFlux advertises no model listing or independent health check. Its explicit test sends a real
generation request and declares network/potential-billing effects. Listing implementations and
reading capabilities do not contact the server. Audio generation and PocketTTS speech synthesis
remain distinct typed contracts grouped under the shared Audio capability. The compiled
`Common/examples/Darkspyre.Inference.Setup.Examples/AudioSetupExample.cs` demonstrates both with the
same generic settings renderer and a host mapping saved audio defaults into a request.

Authentication/access errors, timeout, caller cancellation, HTTP failures and malformed results
remain distinct. No potentially accepted generation is retried, and errors contain neither prompts,
protected headers nor raw provider bodies. Registration disables automatic redirects. Hosts passing
their own `HttpClient` to `TangoFluxHttpProvider` must also disable redirects so protected custom
headers cannot follow them across origins; the host owns that client's lifetime.

## Restore from GitHub Packages

Add the private source to the consuming repository's `NuGet.Config`:

```xml
<add key="DarkspyrePrivate" value="https://nuget.pkg.github.com/DarkspyreStudios/index.json" />
```

Keep credentials outside source control. NuGet recognizes this environment variable:

```bash
export NuGetPackageSourceCredentials_DarkspyrePrivate='Username=brandonm4;Password=YOUR_GITHUB_TOKEN'
```

The token needs `read:packages`; publishing also requires `write:packages`.

## Host integration

Register the connector and provide the host-owned connection source:

```csharp
services.AddLlmConnect();
services.AddScoped<ILlmConnectionSource, MyProtectedConnectionSource>();

services.AddLlmConnectBedrock();
```

Call the provider facade with an opaque connection ID and a model selected by the host:

```csharp
var result = await provider.CompleteAsync(
    connectionId,
    new LlmChatRequest
    {
        ModelId = modelId,
        Instructions = "Return a concise answer.",
        Messages = [new LlmChatMessage("user", prompt)]
    },
    cancellationToken);
```

### Credential access

`LlmConnectionAccess.AcquireAsync` resolves a connection and refreshes its OAuth credential through
the registered authenticator. A changed credential is saved through `SaveCredentialAsync` with the
resolved expected version. A rejected save fails the operation without retry or fallback. Adapters
that ignore stored credentials retain that policy. Non-OAuth credentials remain unchanged.

`AddLlmConnect` registers one scoped access owner shared by chat and embeddings. A facade built by
hand without an injected access owner creates and retires its own. An injected owner must use the
same connection source and remains borrowed. Retiring a facade cancels its credential operations
without retiring a shared owner.

The embedding facade accepts an optional borrowed `LlmProviderService` for the shared xAI family
catalog. Supplying it requires an explicit credential-access owner shared by both facades.
Construction rejects a mismatched pair before allocating private access. `AddLlmConnect` passes
the same scoped access and provider facade to embeddings.

Access retirement fences acquisition, cancels and drains resolution, refresh and saving, and clears
borrowed source and authenticator references without disposing those collaborators. Late refreshes are
not saved; late save results do not return credentials. Cancellation during a save can race a source
commit, so retirement waits for that save and does not undo or retry it. The source remains responsible
for atomic version checks and rejecting writes to changed or deleted connections.
Entered acquisition and refresh operations check the synchronous owner fence after awaited work,
before saving and before returning. They reject late results while linked cancellation callbacks
are pending. New operations reject an already retired owner.

`LlmConnectionAuthorization` binds a connection ID to a borrowed access owner. Its
`IProviderAuthorization.GetAsync` returns the current OAuth Bearer token after acquisition. Missing
OAuth fails; API keys are not substituted. The returned header's diagnostic text redacts its fields.

### Model lists

OpenAI-style discovery preserves positive integer `context_length`, `top_provider.context_length`,
`top_provider.max_completion_tokens` and `max_model_len` limits when reported. Missing or invalid
values remain unknown. The adapter does not invent limits for OpenAI model-list entries.

`LlmProviderService.ListModelsAsync` keeps each connection's model list in `LlmModelListCache`, which
`AddLlmConnect` registers once for the registration owner's lifetime. A list stays until the exact
tuple of connection `Version` and nullable resolved continuation scope changes, or until
`RefreshModelsAsync` lists again and replaces it. Every connection or credential edit changes
`Version`. The tuple compares both components separately and distinguishes null from empty scope.
For xAI, an epoch also compares an allowlisted immutable copy of the actual admitted credential,
endpoint, region, service tier, agent count, headers, provider options and capability overrides.
This catches a refreshed credential returned with its original source version; equality is local
change detection, not account or principal proof. Unrelated runtime and authority graphs are not
retained in this catalog projection.
Concurrent requests for one connection share a single listing, request accounting and managed sessions
read the same list, and a failed listing is not kept. Common's `ILlmInference` carries the same pair:
`ListModelsAsync` may answer from a kept list, and `RefreshModelsAsync` lists again. A service built by
hand without the shared cache keeps its own.

A tuple change or explicit refresh fences publication from the replaced in-flight listing.
Its waiting callers receive cancellation if it returns after replacement. The current entry remains
unchanged. Replacement does not cancel the provider's entered call. An already-cancelled request
does not start or replace a listing.
An xAI epoch shares one generic REST listing and lazily reads only requested chat, image, video or
embedding family listings. Replacement fences each subsequent route and publication. Exact model
IDs take precedence; ambiguous aliases do not enrich facts or choose a profile. Missing fields remain
unknown, and manual model selection does not imply entitlement. This internal family catalog does not
register or enable unfinished media adapters. Voice listing remains an uncached TTS projection.
The optional same-access LLM facade on `XaiFamilyBinding` now supplies this internal cached family
path using its explicit selection ID and already acquired snapshot. It does not resolve or refresh
again, and its caller fence belongs only to the waiter, never to shared catalog production.
Publication copies the ordered list, catalog JSON and mutable capability sets. Adapter-owned lists,
sets and JSON documents do not remain authoritative after publication. Every caller reads the same
read-only snapshot until replacement.

`AddLlmConnect` also registers one disposable `LlmAccountUsageObservations` for its owning service
provider. That provider owns one dependency generation, not an application-global store shared across
reloads. Chat and native-agent facades in that provider share the observation store.

`LlmProviderService` and `LlmModelListCache` implement `IDisposable` and `IAsyncDisposable`. Facade
retirement fences new calls, cancels active calls and waits for them to leave. It retires a privately
created cache and credential-access owner, but not injected shared owners. Clients, authenticators
and the connection source remain borrowed and are not disposed by the facade.

The chat producer owns its provider enumeration through cleanup. Independent provider or projection
and enumeration-cleanup failures remain together. Failed cleanup retains the actual enumerator through
the cleanup failure, which remains visible to repeated facade retirement callers without retry.
Abandoning a chat stream reports typed cleanup failures even when provider cleanup fails during
the final pull, before outer enumeration disposal. Retirement also retains those final-pull cleanup
causes without treating the provider failure as a retirement failure.

Credential access and the chat, embedding and agent facades release their borrowed source references
after admitted work drains. Chat and embedding facades also release credential-access references;
chat releases setup environments and adapter collections. Cleanup failures do not skip this release.
Concurrent disposal callers share the same completion and retained failure. Chat disposal preserves
failures from its operation, credential and privately owned catalog retirement. Embedding disposal
preserves admitted cleanup outcomes and owned credential-retirement failures. Agent disposal preserves opening and
every owned session-retirement failure. Each waits for all owned cleanup before reporting failure.
Agent retirement includes sessions returned after its initial snapshot. Failed session cleanup keeps
the actual session owner; shared disposal reports that failure without retrying its release.
Cancelled opening retains its cleanup failure as the cancellation cause.
A single original error remains unchanged; multiple errors are retained in an `AggregateException`.
Repeated disposal returns the same cached failure instance. Synchronous settings
manager creation and Claude login participate in admission and drain. An admitted synchronous login
keeps its existing result or exception; disposal waits for that call to leave.
Shared catalog producers use the cache's admission, immutable snapshot and cancellation fence, not
the originating facade's fence. An injected cache can finish and publish after that facade retires.
Cache retirement drains producers and clears executable client references; borrowed clients and HTTP
clients are never disposed. Refused owned catalog request/response cleanup retains its safe failure
and actual owner, and repeated cache retirement reports the same failure without retrying cleanup.
The exact cleanup cause is retained as private evidence without exposing its untrusted text through
the public exception graph; canceled cleanup retains its actual cancellation token. Applied request
header values join credential redaction for catalog error and malformed-body diagnostics.

A disposed default-context facade or authorization object can remain retained without keeping a
separately collectible source or adapter generation alive through those released references.
Retained caller metadata, live settings objects and genuine foreign exception objects remain separate
roots. This does not establish retirement of an active provider, native runtime or whole SDK graph.

Cache retirement clears kept models, cancels provider listings through an owner token, and waits
for every listing, including entries replaced by an edit or refresh. A cancelled caller only stops
its own wait. A provider that ignores owner cancellation keeps retirement pending until its call
ends; its late result is not published. Abandoned provider faults are observed.

LLM settings actions and model-choice queries own the temporary provider facade they create. Each
operation retires that facade and its private catalog cache before returning, including cancellation
and failure paths. Cancelled discovery waits for its provider listing to drain; an ignoring provider
keeps the operation pending until it returns. Injected clients and authenticators remain borrowed.
Another settings operation creates a fresh facade and can reuse those clients.

Use asynchronous disposal while provider work is active. Chat streams use an ordered, non-dropping
32-event buffer. Their producers own active operations through provider enumeration cleanup.
Disposal cancels blocked writes and drains producers without waiting for a paused event reader.
An operation that ignores cancellation still keeps disposal pending. A registration owner disposes
its shared cache; sharing a cache across
containers does not transfer that responsibility to a scoped facade.

### Explicit cache binding

`LlmProviderService.BindContextCacheAsync(connectionId, ct)` is the asynchronous entry point for
`ILlmContextCacheClient`. It resolves one versioned connection, snapshots its data and validates that
the selected transport is registered. Binding does not refresh credentials, prepare a provider,
discover models or create a cache. Gemini API and standard-project Vertex Gemini implement these
operations. Other adapters and projectless Vertex express mode report `NotSupportedException`.

The facade's admission and retirement cover the source read. Cancellation waits for a source that
ignores its token to return, then rejects its late result. Facade retirement also drains that read
before releasing borrowed collaborators. The returned client borrows the facade. Later operations
perform explicit cache mutations or validate receipt scope through that same owner; binding itself
does not create a paid resource.
Receipt admission checks the facade's cancellation and retirement fence after acquiring the
binding gate, before installing receipt evidence. Publication checks again after gated work.
A late create, expiry update or delete keeps its actual observed receipt in the existing
possible-mutation failure; it does not publish a successful result into a replaced binding.
Failure-side uncertainty recording remains available after cancellation and performs no
provider mutation or automatic retry.

Exact provider-token counting rejects explicitly cached Vertex requests before credential refresh,
authentication, connection preparation, model discovery or HTTP dispatch. This restriction is
independent of generation-cache capability. Uncached counting and local estimated accounting
retain their existing behavior.

### Files and Skills facade

`LlmProviderService` exposes `ILlmFileClient`, `IXaiFilePublicUrlClient` and
`IXaiSkillClient`. `AddLlmConnect` registers those interfaces as aliases of the same scoped facade.
The facade owns their admission and retirement; resolving an interface does not create another
connection source or credential owner. Skills methods use the `IXaiSkillClient` interface to
distinguish skill IDs and results from file IDs and results.

`LlmProviderCapabilities.Files` and `.Skills` report separate resource-operation facts. The
builtin xAI Responses client exposes the seven implemented native Files operations through
the facade. The facade resolves one selected connection, snapshots its configuration and validates
arguments and headers before its existing credential owner refreshes that connection. The private
transport borrows the same HTTP client as chat. `AddLlmConnect` includes the Grok adapter.
Skills list, metadata retrieval, raw ZIP download and deletion use the same selected resource
transport and facade lifetime. Skills pages preserve the actual `has_more`, `first_id` and `last_id`
facts; caller-directed next pages send `after`, not the Files `pagination_token`. Version strings
and Unix creation times remain actual receipt values. ZIP downloads copy bytes to the borrowed
destination without extraction, rewinding or implicit flushing. Skills upload and generation
attachment remain unavailable. `Skills.ResourceOperations` stays false because the complete
operation set is not implemented.
Both borrowed Skills upload variants require positive caller-selected `MaximumUploadBytes`,
`MaximumEntries` and `MaximumExpandedBytes`. Input bytes exclude multipart framing. ZIP entry counts
include directories; expanded bytes count uncompressed file content. For explicit directory uploads,
entries count supplied files and both byte limits apply to their total content. The library supplies
no defaults or numeric provider limits. These contracts carry policy for the unavailable upload path;
they do not perform stream reads, archive inspection or provider calls. Grok's directory multipart
path encoding, accepted ZIP layout and uploaded-skill generation-attachment mapping are unknown.
Upload and generation attachment remain unavailable; the library does not guess those protocols.
Other Files routes remain unavailable. When no builtin Files adapter is
composed, refusal precedes source resolution and stream inspection. Caller cancellation and
retired-facade admission retain the existing lifetime behavior.
Facade capability queries suppress Files flags for clients without an implemented facade route.
They fence publication after the adapter's capability getter returns.
Upload and download streams remain borrowed, including on rejection and facade disposal. These
resource interfaces alone do not enable native-file prompt references or skill generation attachment.

The private xAI Files transport implements upload, bounded one-page listing, metadata retrieval,
raw download, deletion and explicit public-URL creation/revocation against an admitted connection
snapshot. It shares the chat adapter's OAuth-only regional header policy. Upload sources and
download destinations remain borrowed; transfers do not seek, flush or close them. Download receipts
follow complete copy and owned response cleanup. Premature EOF against an observed HTTP content length
is a failure; text extraction is not compared with original file metadata size. Partial failures can
leave written destination bytes. Observed public URLs must parse as absolute URIs without literal
controls. Their accepted strings remain unchanged; the transport does not fetch them or impose an
undocumented scheme/host policy.
The transport fences each awaited boundary and drains entered work before dropping its borrowed HTTP
client. Owned response and request cleanup each runs once. Independent operation and cleanup failures
remain visible together; a lone failure retains its original identity. Publication follows cleanup
and the final owner fence. Its protocol errors retain actual status and authentication/entitlement
classification, but exclude provider response bodies and capability URLs from diagnostics. The facade
drains admitted operations before retiring its private Files helper. Borrowed clients, authenticators,
sources and streams are not disposed. Logical Session/Workspace materialization remains a separate
resource path; native file IDs and public URLs are not portable resource identities.

The builtin xAI Responses route accepts native file prompt references and materializes logical
document resources through one borrowed `LlmChatRequest.Resources` authority. It freezes one selected
membership snapshot after continuation projection. Explicit history occurrences retain their order
and duplicates. Selected references absent from the supplied graph append to the current user prompt.
The route reads authorized committed bytes through owned handles and uploads them through the same
private Files transport. Unknown byte counts remain unknown; missing filenames refuse the file
destination. Native receipts belong to the admitted request and do not replace logical captured history.
After a connection change, the caller reattaches its resource authority to the portable context.
The destination uploads the named committed content again; it does not reuse another connection's file IDs.
Local accounting does not read or upload selected resources. `AddLlmConnect` includes Grok registration.
Managed sessions created through the shared internal factory validate resource-bound imported
contexts against the caller's exact scoped selection snapshot. Only a new session without an imported
context adopts the current selection; an unbound manual or upgraded legacy context must be explicitly
bound first. Capture freezes transcript state, then reads selection outside the session lock and
rechecks transcript revision before publication. Replacement and continuation similarly recheck idle
state and revision after caller callbacks, without mutating the store. Committed caller-tool resource
outputs are authorized before settlement; they remain logical history, not provider file IDs.
Resource-backed requests do not automatically retry a failed model request after upload.
Typed resource cancellation reaches a live reader even when session retirement cancels the producer.
Healthy cleanup leaves session disposal successful. Actual owned handle or HTTP cleanup refusal uses
`InferenceResourceOperationPhase.Cleanup` and remains the same cached reader/disposer failure;
independent operation and cleanup causes remain visible. Borrowed authority detaches after callback
drain, while refused owned resources remain reachable through the actual retained cleanup failure.
SSE parsing borrows its caller-owned stream. A refused async body-stream release skips dependent
response/content cleanup rather than retrying that same stream synchronously; the failed stream and
dependent response stay with the cleanup error until that error's references are released. This is
not process-exit quarantine. `AddLlmConnect` includes the Grok agent opener;
registration does not qualify OAuth, provider behavior or other destinations.

### Embedding lifetime

`LlmEmbeddingProviderService` implements `IDisposable` and `IAsyncDisposable`. Its registration owner
retires the facade. Embeddings refresh OAuth through the same access implementation as chat before
dispatch. Every `ILlmEmbeddingClient` supplies an effect-free `ValidateRequest`; the facade calls it
before refresh, and the direct adapter validates before HTTP. `LlmEmbeddingRequest.Dimensions` is
nullable and positive when supplied. Raw `dimensions`, `model` and `input` are reserved in both
connection and request options. Unknown model-specific limits remain provider decisions. Requests
snapshot inputs and JSON options before asynchronous work; connections snapshot embedding options
and headers before refresh. OpenAI-compatible responses require one unique in-range index per input,
finite nonempty vectors and the requested dimensions. Results retain the reported model ID when
present; an absent ID stays null and is not replaced by the requested alias. Protected values in
model metadata refuse publication. Diagnostics redact credentials and additional header values.
The three embedding data types expose owned metadata through their exact type-name getters on
`ConnectLlmJson`.

`LlmEmbeddingProviderService.ListModelsAsync(connectionId)` lists observed xAI embedding-family
models through the explicitly borrowed LLM facade and its sole model cache. Other transports and
missing catalog composition refuse before credential refresh. The xAI embedding path consumes one
resolved and refreshed connection snapshot for catalog and POST; nested catalog work does not
reacquire credentials. The returned facts are installed only on that admitted internal snapshot.
An unknown manually selected model remains unknown, not unsupported solely because a listing omits
it. Without the shared facade, catalog-dependent xAI embedding calls refuse before refresh.
Non-xAI embedding calls retain their existing construction without a catalog facade. Live embedding
facades are runtime composition, not portable data: owned options preserve null and refuse non-null
values without serializing their collaborators. No runtime facade getter is exported.

`AddLlmConnectSelfHost` registers the SelfHost embedding client. It embeds through the host's
`ILlmSelfHost.GetEmbeddingInferenceAsync(engineId)`, where the connection's endpoint names the
engine. It shares chat's connection refusal policy for provider options, HTTP headers and capability
overrides, and refuses request provider options before acquiring the engine.
The engine formats queries: `LlmEmbeddingRequest.Kind` states
whether inputs are queries or documents, `Instruction` states a query's task, and `Truncate` decides
whether an over-limit input is cut or fails the request with `EmbeddingInputTooLongException`. The
result reports `ModelId`, `ModelFingerprint` and `TruncatedInputs`. Remote adapters send inputs as
given; they ignore `Kind`, `Instruction` and `Truncate` and report no fingerprint.

The internal xAI embedding adapter implements ordered text inputs, float vectors and typed dimensions.
It accepts at most 128 non-empty inputs and preserves supplied `user` and boolean/null `preview`
options, including explicit nulls. Its direct path consumes the supplied OAuth connection and the
stateless embedding catalog reader; it does not refresh credentials or own another model cache.
It refuses non-default typed service tiers and raw `service_tier` options before HTTP because it
does not implement an embedding tier mapping. Raw `encoding_format` accepts only `float`.
Responses require complete unique input indexes, finite consistent vectors, actual model provenance
and nonnegative observed usage. The validated response model populates `LlmEmbeddingResult.ModelId`;
the requested alias never substitutes for that observation. Validated float values use primitive
JSON nodes without default reflection metadata. Successful diagnostics serialize only validated
model and numeric facts. A model string containing a known credential or protected header value causes a local
confidentiality refusal after the response; it is not provider rejection or remote rollback proof.
Input text is not a secret-replacement authority, and numeric JSON is not text-redacted.
HTTP request/response cleanup preserves independent failures and
retains actual refused owners behind safe diagnostics. The adapter is not registered. Subscription
OAuth, model entitlement, preview format and the published input-schema/example disagreement remain
unqualified; no default embedding model or working xAI availability is advertised.

Retirement fences new requests, cancels
connection lookup and provider calls, and awaits all active requests before releasing its adapter references. Supplied adapters and the
connection source remain borrowed and are not disposed by the facade. Caller cancellation affects
only that request. A provider that ignores cancellation keeps retirement pending; its late result
is rejected. Use asynchronous scope disposal while embedding requests are active.
Post-await boundaries check the synchronous retirement fence even when a cancellation callback
delays propagation to a request's linked token.
The internal xAI dispatch also checks that same facade fence through send, read, parsing and
publication. Cache and embedding owners share one finite cleanup-outcome policy with separate
owner-scoped state. Only actual cleanup carriers affect retirement; ordinary provider, validation
and cancellation failures do not. Refused cleanup retains its actual safe carrier and resource,
and repeated disposal reports the same result without retry. Borrowed catalog facades are detached
after drain and are never disposed by embeddings.

### Agent-session lifetime

`LlmAgentProviderService` implements `IDisposable` and `IAsyncDisposable`. It owns sessions returned
by start, resume and fork. Facade retirement fences new openings, cancels active openings and
retires every returned session. Existing sessions retire without waiting for another opening to
finish. A session that finishes opening after cancellation is disposed before its opening call
ends. Supplied clients and the connection source remain borrowed.

Each returned session fences its methods on retirement, cancels and drains active calls and stream
producers, then disposes its adapter session. Turn and observation streams use ordered, non-dropping
32-event buffers. The producer retains its operation lease through adapter enumeration cleanup;
the public reader holds no lease while paused. Concurrent callers share one retirement completion.
Disposing one session leaves its siblings and the facade usable. Caller cancellation affects only
the named operation. Late results and events from cancelled work are rejected. A failed session
retirement remains visible when its owning facade retires; another session is not skipped.

Use asynchronous scope disposal while sessions are active. Retirement cancels full-buffer writes
and drains producers independently of paused readers. Disposing an enumeration cancels and drains
its producer. Consuming readers receive provider failures. Abandoned enumerations still report
cleanup and cancellation-callback failures. Independent read and producer failures are preserved;
actual enumeration cleanup failures remain visible to repeated retirement callers.
An adapter that ignores cancellation keeps retirement pending until its work leaves. Direct
`ILlmAgentClient` callers own the sessions they create outside the facade.

### Vertex authentication lifetime

`AddLlmConnectVertex` registers one authentication owner per container. Container disposal fences
new authentication calls, cancels active calls, and awaits their completion before clearing cached
credentials and credential-source callbacks. Concurrent disposal shares the same completion.
Use asynchronous container disposal while authentication calls are active. Caller cancellation
affects only that call. Tokens returned after caller or owner cancellation are rejected.

Service-account and ADC-file acquisition share one credential constructor per key. Failed
construction is removed from the cache and can be retried. ADC files are re-read on acquisition;
replacement changes the credential and observation scope. SDK discovery without an ADC file stays
inside its active call until discovery ends, because SDK cancellation only cancels the wait for
that shared task. SDK/parser errors never expose credential diagnostics or inner exceptions.

This owner releases its cache references. Google's shared ADC discovery cache and SDK-internal
HTTP/refresh lifetimes are separate from these authentication-call retirement guarantees.

### Bedrock SDK lifetime

`AddLlmConnectBedrock` registers one SDK client owner per container. It caches each runtime,
control-plane and AgentCore client by an opaque fingerprint of region, profile, access key, secret
and session token. Concurrent acquisition constructs one client; a failed construction can be
retried. Changes to configured credential fields use a different client and logging-verification key.

Every SDK call holds an owner lease. Streaming leases cover response reading and disposal, not
just response creation. Owner retirement fences new acquisition, cancels active calls, waits for
their leases, and disposes every cached client and explicitly resolved disposable credential.
Response cleanup failures remain visible to the SDK owner. One disposal failure does not skip
another resource. Logging verification also fences and drains its lookups and clears its cache.
Use asynchronous container disposal while work is active.

SDK acquisition, awaited results, catalog follow-on calls, logging verification and each published
Converse response event check the synchronous owner fence. Delayed linked-token propagation cannot admit
a late successful result. Successful retirement detaches client-creation callbacks and disposed
stream response references. The owner retains exact resources whose cleanup fails and the original
errors; repeated disposal shares that failure and does not retry cleanup. A failed stream opening
preserves both its original operation error and a response-cleanup error when both occur.

Model discovery preserves every successful catalog lane, including an empty response. A failed lane
logs a warning through the standard host logger. Two failed lanes also log an error and return ordered
safe failures, not an empty catalog. An optional resolution failure logs a warning and retains the exact
requested model ID for invocation. Diagnostics contain fixed operations, observed status and protected
SDK error-code/request-ID fields, never raw exceptions or provider content. A refused logging sink
preserves the discovery failure and the separate logging failure behind safe diagnostics.

Clients use a private default AWS credential resolver and SDK-client-local HTTP caches. Retiring
the SDK owner disposes the resolver and the SDK pipelines' HTTP pools. Runtime and AgentCore HTTP
clients have infinite generation timeouts; control-plane clients retain their SDK timeout.
An adapter that ignores cancellation keeps retirement pending until its call leaves. Consume or
dispose open streaming responses and enumerators so their leases can leave.

### Streaming, reasoning text and usage

`LlmProviderCapabilities.SupportsStreaming` says whether the adapter streams provider output as the model
generates it. `LlmCapabilityOverrides.SupportsStreaming = false` selects the provider's non-streaming request:
the adapter sends it and emits the complete response as events after it arrives. A transport whose provider
has no non-streaming request rejects every request while streaming is off. Common's
`LlmCapabilities.SupportsStreaming` carries the same flag to `ILlmInference` hosts.

Compatible Chat Completions reports `Unknown` for an unrecognized or missing stop reason unless
the response contains an explicit refusal. Tool calls alone do not establish a recognized terminal
reason. Anthropic also keeps an unrecognized or missing stop reason `Unknown` even when calls are
present. Both retain the observed calls and publish no successful continuation from that unknown
outcome. Each HTTP event publication checks caller cancellation, including buffered
non-streaming events and adapters using a borrowed HTTP client.
Claude CLI and Antigravity also check caller cancellation before each buffered non-streaming event.

Bedrock Converse and ConverseStream also retain decoded tool calls for an unknown stop reason.
Their terminal result preserves partial text, reported usage and the observed stop reason without
continuation state or structured-answer evaluation. Observed calls do not authorize tool execution.

AgentCore chat classifies both `content_filtered` and `guardrail_intervened` as `Refused`.
It preserves returned text and reported usage without evaluating that text as a structured answer.
Both stop reasons map to `content_filter`, matching the Converse refusal mapping.

Compatible Chat Completions skips structured-output validation for a refused result, including
`content_filter` without refusal text. Its partial text, reported model, usage and finish reason
remain available. Filtered partial JSON is not published as an evaluated or valid structured answer.

Gemini API and Vertex Gemini retain complete decoded tool calls when the actual terminal reason
is unrecognized. The result remains `Unknown` and carries no continuation state. Open streamed
arguments do not become complete calls. Existing known refusal, truncation and malformed-call
outcomes keep their classifications.

Vertex typed function-argument streams accept dot names and single- or double-quoted bracket
names in their JSONPath property selectors. Both quoted forms preserve the exact decoded property
name and share indexed-array reconstruction. Returned calls, portable results and retained
thought-signature continuations carry the same reconstructed arguments.

Responses reports `Unknown` for an unrecognized native status and does not publish continuation
state from that outcome. Tool calls alone do not establish successful completion. Direct Codex
also accepts the actual terminal SSE event type when its response omits `status`, matching the
selected CLI protocol. An explicit unknown status is not replaced by the event type. Other
Responses routes require their native status; a missing status remains unknown. Managed sessions
retain tool calls from an unknown outcome in their context but end with `Unknown` without executing
the calls or starting another model step. Structured-output validation does not discard those
unknown tool observations or replace their terminal reason.

`SupportsReasoningText` describes public reasoning separately from reasoning-level control. Common
mirrors the flag. The **Reasoning text** override disables public reasoning events and provider
summary requests without dropping opaque continuation state. Model overrides take precedence over
connection overrides. An override cannot enable a producer the adapter does not implement. Apple
Foundation Models and Antigravity report false. SelfHost reports the loaded engine's flag.

Responses requests `reasoning.summary = auto` when a positive level is selected or the resolved
profile says the model reasons by default. An unprofiled API-key model gets no implicit summary
request. An explicit `None` sends no summary request. Codex uses the same builder and display switch;
its catalog supplies reasoning support, with the subscription adapter's default when absent.
Gemini and Vertex Gemini request `includeThoughts` for active
reasoning, including a thinking profile with no explicit level. Turning display off leaves effort,
thinking levels and budgets unchanged. Claude CLI chat maps `thinking_delta` to public summaries
and ignores signature deltas. AgentCore chat emits reasoning events, not activity text. Native
Claude and AgentCore sessions honor the same display override. Streaming Claude CLI chat writes
`showThinkingSummaries: true` to a per-call settings file and passes its path through `--settings`
when display is enabled. Streaming chat also supplies the official SDK's `--thinking-display`
argument: `summarized` when display is enabled and `omitted` otherwise. The file is removed with
the call's working directory on completion, cancellation, failure or reader disposal.
Authentication probes and non-streaming calls receive no summary settings file or display argument.
The request does not guarantee that the CLI returns a summary.
Native Claude sessions pass the resolved initial `showThinkingSummaries` boolean as inline
`--settings`. Before each host turn, the existing `apply_flag_settings` control carries that turn's
resolved display choice and any explicitly supplied effort. Session initialization and each host
turn also await the headless `set_max_thinking_tokens` control with only `thinking_display`.
The budget field is absent. This requires the current official headless protocol's display-only
update semantics, present in Claude 2.1.288. An omitted effort does not reset the CLI's existing
effort, and a display-only update preserves its thinking configuration. A rejected control fails
the operation before the prompt is written. The session keeps the last model acknowledged by
`set_model`; later omitted model choices and provider-originated turns use that model's capability
overrides. An explicit return to the startup model sends a model switch when the CLI has selected
another model.
Native effort validation reads the same resolved model profile before any CLI control is sent.
Chat and native-session thinking deltas carry `IsSummary = true`; neither path exposes signature
or encrypted state as reasoning text.

Anthropic Messages and Vertex Claude share model profiles. Positive adaptive reasoning requests
`thinking.display = summarized` on families whose default display is omitted. A null level requests
only that display with adaptive thinking on the named default-thinking Opus 5, Sonnet 5, Fable and
Mythos families; it invents no effort or budget. Explicit `None`, budget models, effort-only models
and older adaptive models retain their existing controls. Disabling display omits the summary request
without changing explicit reasoning controls. Catalog evidence remains authoritative for thinking
support. Vertex Sonnet 4.6 receives no new display field.

Claude profiles reject temperature and forced tool choice while adaptive or budget thinking is
active, including a profile's default thinking. Profiles that prohibit forced tool choice reject it
at every level. Explicitly disabled thinking and effort-only reasoning keep the model's supported
sampling/tool controls. Anthropic Messages, Vertex Claude and Bedrock Claude use this validation.

Anthropic Messages and Vertex Claude report `pause_turn` as `Incomplete` with its native reason and
opaque continuation state. Ordered server-tool calls, their result blocks, caller metadata and
citations remain replay data; they do not become host tool actions. Streaming retains partial
server-tool argument deltas before constructing that state. Calls from native code execution retain
their `caller` metadata, including client calls that still require a tool result. A caller replays a
paused assistant message with its unchanged text and continuation state. Vertex accepts that opaque replay without
allowing ordinary assistant prefilling. Unknown stop reasons stay Unknown;
`model_context_window_exceeded` and `max_tokens` are Incomplete without paused-turn state.

The private Anthropic continuation payload contains ordered content and an optional opaque
code-execution container ID. The ID shares the content's account/model binding and survives context
capture/import. Replay sends the most recent retained ID as the top-level `container` field.
It retains no container expiry, skills or arbitrary response metadata. Raw provider options cannot
replace a retained ID. Container errors propagate without retry, recreation or silent identity removal.
The count-tokens operation rejects requests containing `container`, which its API cannot represent.
Legacy array-only Anthropic payloads are not accepted; other providers' payload shapes are unchanged.

Mixed server/client responses with pending client tools report `tool_use` and RequiresAction.
Only client calls emit tool events and reach a managed session's handler. Their results accompany
the retained ordered server/client content and container on the next request. A contradictory
`pause_turn` response with pending client calls fails without returning a resumable completion.

Anthropic Messages and Vertex Claude consume `ProviderOptions["anthropic_tools"]` as an adapter-owned
object. `server_tools` is an array of native server definitions. `client_tools` is an object keyed
by typed `Tools` names; its values accept `allowed_callers`, `defer_loading` and `input_examples`.
Typed client schemas, names, descriptions and strictness remain authoritative. The request option
replaces the entire connection option; an empty object clears it. Neither source is mutated.
The adapter emits one combined `tools` array, not the `anthropic_tools` wrapper, for generation and
provider counting. Typed tool choice and parallel controls apply to the combined names. Raw `tools`,
`tool_choice` and `temperature` options fail even when the typed fields are absent. The temperature
guard applies to generation and counting before Vertex authentication. Vertex Gemini rejects
`anthropic_tools`.

Native definitions accept the documented web search/fetch, code execution, tool search and advisor
versions. Unknown types, client-executed native built-ins, duplicate names and native schema/strict
overrides fail locally. Other native configuration stays opaque and unchanged; provider errors
determine route/model availability and tool-specific parameter support. The adapter never rewrites
definitions, retries an invalid configuration or switches tools. Advisor results, including encrypted
advice, remain opaque ordered continuation content, not public reasoning or host tool events.
Beta tools require the caller's provider beta header; the adapter does not enable paid tools implicitly.

Client `allowed_callers` accepts nonempty unique lists of `direct`, `code_execution_20260120` and
`code_execution_20260521`. A programmatic client requires a declared code-execution tool at either
of these two versions. It cannot be strict or disable parallel tool use. A forced client choice
must allow direct calling. Deferred clients require a declared tool-search tool. These checks do
not establish live tool provisioning or complete tool-specific compiler validation. See the
[programmatic calling contract](https://platform.claude.com/docs/en/agents-and-tools/tool-use/programmatic-tool-calling).

Programmatic client schemas reject proven local ordinary-`$ref` cycles before generation or provider
counting; Vertex rejects these cycles before authentication. Direct-only recursive schemas remain
unchanged, including in mixed requests. The guard indexes draft 2020-12 schema locations, local
resource IDs and anchors without fetching references or evaluating instances. It handles JSON Pointer
escaping and shared acyclic references. `default`, `const`, `enum`, `examples` and unknown annotations
remain opaque data. Unresolved external references, unknown dialects and ambiguous identities remain
provider validation. The guard never rewrites schemas or adds strict-schema restrictions. Dynamic
`$dynamicRef` evaluation is outside this ordinary-`$ref` cycle check.

The canonical Claude builder validates scoped, projected continuation before HTTP dispatch. Mixed
turns with waiting native tools and programmatic turns require an immediate user message containing
only results, exactly once for every waiting client call. Results can arrive in a different order.
Programmatic results accept strings or text blocks only; direct client results retain supported rich
content. Programmatic caller IDs must refer to retained code-execution invocations. An active result
request requires its native tool definitions and a retained container for programmatic calls. A terminal
assistant message with waiting native calls and no client calls requires those native definitions too;
a client tool with the same name cannot substitute. Bash and file-operation invocations use their
owning code-execution definition. The documented dynamic-filtering web tools also provide native code
execution without an extra definition. Native results match by `tool_use_id`, not position. Completed
historical exchanges do not require those tools or a container to remain configured now. The adapter
does not rewrite history, strip extra input or binaries, synthesize results, execute native tools or retry.
This validation follows principal resolution and explicit unbound-state projection; Vertex resolves
authentication before checking principal-bound history. Raw `thinking` cannot override typed reasoning,
even when the typed setting leaves the native field absent.

Managed sessions retain an Incomplete reply that carries continuation state and no host tool calls.
They emit its message, finish the turn with Truncation, and preserve the state through capture/import
for a caller-directed later turn. They do not automatically retry or execute server tools.

`SupportsStreamingUsage` describes usage reported by completion. `SupportsUsageDeltas` describes
running `LlmUsageDeltaEvent` observations and is mirrored on Common's capabilities. Each observation
replaces the previous value for the entire call. Null counts remain unknown. Duplicate observations
are suppressed. Only completion usage is authoritative, including when it corrects an earlier value.
`LlmUsage` also carries nullable `ProviderReportedCostUsd`, `HostedToolCallCount`, `HostedSourcesUsed`
and a frozen `HostedToolCalls` map keyed by `LlmHostedToolKind`. These are independent provider facts;
no local price calculation or named-count sum invents an unreported total. Distinct completed calls
sum a field only when every contributor reports it without overflow. Named counts use the intersection
of reported valid keys; null maps remain unknown and empty maps report no named detail. Assignment and
owned JSON import normalize negative hosted counts to unknown, retaining explicit zero. Interim usage
replaces the current request's observation. These shared fields do not enable a hosted-tool capability
or claim a provider mapping that has not been implemented.
Anthropic and Vertex Claude report observations from `message_start` and `message_delta`. Gemini
and Vertex Gemini report normalized `usageMetadata`, including reasoning output tokens.
Anthropic's initial output count is retained when reported. Later output counts replace it;
cache-read and cache-creation counts join uncached input in the normalized input total.
Claude CLI chat and native sessions share a ledger keyed by API message ID. It sums distinct calls
and ignores replayed calls and subagent events. Its observations and final `result.usage` cover the
main loop for the current turn, as described in the [Claude usage contract](https://code.claude.com/docs/en/agent-sdk/cost-tracking).
Native sessions use one `LlmAgentUsageEvent.RequestId` for the turn's running and final observations.
A final result without usage replaces the running estimate with unknown usage.
Antigravity chat and native sessions share completed-step accounting. Model-response and checkpoint
usage replaces earlier usage for the same step. ACTIVE snapshots, user-input steps, native-tool steps
and previously accounted process-generation steps add no tokens. Native sessions reconcile final
usage against a trusted same-process cumulative baseline without adding it to the step ledger.

OpenAI-compatible connections default running usage off. The **Running usage** capability override
opts a vLLM-compatible endpoint into `stream_options.continuous_usage_stats` when streaming usage
is enabled. Other compatible endpoints do not receive that extension by default. Non-streaming
responses emit no running usage events. Managed agent loops project observations into their existing
`LlmAgentUsageEvent`: a repeated `RequestId` replaces that step's observation, and `RunTotals` includes
completed steps plus the current estimate. Only a step's completion updates the settled run total.
An override that enables running usage on an adapter without a producer is rejected. A model override
takes precedence over the connection override.

A response that arrives complete produces its events in a fixed order: reasoning text, the answer text as
one `LlmTextDeltaEvent`, the refusal text when it differs from the answer text, then for each tool call its
`LlmToolCallStartedEvent`, `LlmToolCallArgumentsDeltaEvent` and `LlmToolCallCompletedEvent`, and last the
completion. The completion carries the same result a streamed response of the same content produces. Gemini
and Vertex Gemini read a `generateContent` response with the streaming reader, so their events follow part
order in both modes.

A non-streaming response sends its HTTP headers only when generation ends. The generation HTTP clients
(OpenAI Responses, OpenAI-compatible, Anthropic, Gemini API and Vertex) therefore have no client timeout; the
caller's cancellation token ends a request. Anthropic's SDKs refuse non-streaming requests they expect to run
longer than 10 minutes, and the API can end a long non-streaming request with `504 timeout_error`. Connect
does not pre-validate `max_tokens` against that limit, so a long non-streaming Anthropic request can fail
where the same streamed request succeeds.

| Transport | Streams | With `SupportsStreaming = false` |
| --- | --- | --- |
| OpenAI Responses | `stream: true` | `stream: false` |
| Codex | `stream: true` | Rejected |
| OpenAI-compatible | `stream: true` | `stream: false`, no `stream_options` |
| Anthropic Messages | `stream: true` | `stream: false` |
| Claude CLI | `--output-format stream-json` | `--output-format json`, prompt as text on standard input, no attachments |
| Bedrock Converse | ConverseStream | Converse |
| AgentCore | InvokeHarness | Rejected |
| Vertex Gemini | `streamGenerateContent` | `generateContent` |
| Vertex Claude | `streamRawPredict` | `rawPredict` |
| Gemini API | `streamGenerateContent` | `generateContent` |
| Antigravity | `--output-format stream-json` | `-p` with `--output-format json` |
| Apple | `streamResponse` | `respond(to:)` |
| SelfHost | Engine-defined | Engine-defined |

### Wrapped CLI processes

Three transports start a local process: `AnthropicClaudeCli` starts the Claude CLI,
`GoogleAntigravityCli` starts the Antigravity CLI, and `AppleFoundationModels` starts the Apple helper.

On macOS and Linux each process leads its own session and process group and has no controlling
terminal. A Ctrl+C typed in the terminal of a console host does not reach it. The host's cancellation
and disposal paths end it.

The Antigravity CLI and the Apple helper end by `SIGKILL` to the process group on cancellation and
disposal. Every descendant that stays in the group ends with the process. A descendant that calls
`setsid` or `setpgid` leaves the group, and the kill does not reach it. On Windows, cancellation and
disposal kill the process tree.

A Claude CLI is never killed early. The CLI refreshes its OAuth tokens and saves the rotated pair to the
keychain; a CLI killed between the refresh and the save leaves a spent refresh token behind, and the next
refresh signs the user out. Every Claude CLI process (chat, `auth status`, `/usage`, model listing and
agent sessions) therefore ends by standard-input EOF. A one-shot run whose input is already closed also
receives `SIGTERM`, then `SIGCONT`, on its process group. The process exits on its own; its process group
receives `SIGKILL` only after 30 seconds, and a Warning log entry records that kill. A caller that cancels,
stops a stream early or disposes a session returns at once. The exit continues in the background, which
drains the process's output and reaps it, and logs any failure. The `ClaudeCliExits` singleton owns these
background exits; disposing the service provider waits for all of them, so host shutdown takes up to
30 seconds plus the 5-second kill bound for a Claude CLI that ignores EOF and `SIGTERM`. On Windows a
Claude CLI receives no `SIGTERM`, so a one-shot run ends on its own or at the limit.
The exit owner retains actual background failures after logging them and reports them on disposal,
including repeated disposal. Successful exits leave no retained outcome. A refused warning does not
skip forced termination. Failed group signals remain failures even if killing the root succeeds;
the existing bounded exit wait still runs. Independent signal, exit, pipe-drain and release failures
remain visible together. Cancelling an owned pipe drain during retirement is not a failed drain.

After a kill, Connect waits at most 5 seconds for the runtime to report the exit. When the bound
expires, the operation fails with a message that names the process ID:

| Lane | Failure |
| --- | --- |
| Antigravity CLI chat | `LlmProviderException` with `ProviderCode` `antigravity_cli_exit_timeout` |
| Antigravity native agent session | The cancelled operation keeps its own outcome, and the session faults. Later calls throw `InvalidOperationException` with the reason. |
| Apple helper | `InferenceException` with `InferenceFailureCode.ExecutionFailed` |

Claude CLI chat owns the process during stdin writes as well as output reads. Cancellation during a
blocked stdin write and early disposal of its output stream both close standard input and hand the
process to the background exit. Its stderr reader enforces a 16 MiB character limit while reading. A stderr read failure interrupts
blocked stdin/stdout operations. Retirement cancels and observes that reader with a 5-second bound.
Its stdout reader rejects a decoded line above 16 MiB characters before receiving a newline.
Total captured stdout is checked before a line and its normalized newline are appended.

Claude and Antigravity native session processes share one pipe-operation owner. Retirement fences new
reads, writes and exit waits, cancels active and queued operations, and waits for them to leave before
disposing the process. Concurrent disposal calls await the same completion, including its failure.
Caller cancellation affects only that operation. Each write flushes before completing. Closing stdin
discards buffered input from an abandoned write; it does not flush that input during retirement.
Antigravity rejects closing stdin while another write is active.

Native Claude stderr capture rejects input above 16 MiB characters during reading. Native Antigravity
keeps the first 64 KiB characters and drains the remaining stderr without retaining it. A capture
failure interrupts pending pipe operations. Both wrappers and Claude chat share a stdout line reader
that rejects decoded lines above 16 MiB characters during receipt. It preserves the pipe's encoding,
CR/LF/CRLF delimiters and an unterminated final line at EOF. Caller cancellation retains received
partial text for the next read. An oversized line faults the reader; later reads report the same
failure without truncating or resynchronizing its content.

Native-session disposal allows up to 2 seconds for graceful exit after closing stdin, then signals an
owned process that remains alive. The process exit and stderr EOF share a subsequent 5-second bound.
Cleanup cancels and observes the stderr reader with a separate 5-second bound. An exit wait or
disposal that cannot drain stderr fails with `IOException`; a capture-limit failure remains visible
as `InvalidOperationException`. A parent that already exited is not signalled through its former group
ID. Descendants holding its stderr pipe open therefore produce a drain failure, not successful
retirement. Antigravity drains its session reader even when process disposal fails.
Antigravity session disposal also shares one retirement completion across callers. It reports
termination and pipe-drain failures to those callers and closes its observation feed even on failure.

Direct Claude native sessions fence operational calls when retirement starts. Retirement cancels
and drains active turns, observation enumerations, control requests and host tool callbacks before
disposing their process, reader, resource poller and gates. A host tool remains owned through result
projection and the response write. An ignoring callback or write keeps retirement pending until it
returns; a cancelled late callback result is not emitted. Bounded turn and observation producers
drain without requiring a paused public reader to resume. Concurrent disposers share the completion
and its cleanup failures. A failed
process disposal does not skip the reader or resource poller. Cancellation and cleanup failures are
retained together. Tool cancellation matches the typed JSON-RPC ID; numeric and string IDs remain
distinct, and repeating an active ID fails the session without abandoning its first callback.
If a tool's cancellation callback throws when the process exits, the session retains that failure
for retirement, cancels the remaining tools, ends reported resources and closes its observation feed.

Claude and Antigravity share one launcher for chat and native sessions. Their default commands are
`claude` and `agy`. On Windows the launcher searches absolute PATH directories in order for `.exe`,
`.com`, `.cmd` and `.bat` entries. An explicit executable path keeps its configured target.
Native executables receive structured arguments through `ProcessStartInfo.ArgumentList`.

For a complete argless Node wrapper generated by [npm/cmd-shim](https://github.com/npm/cmd-shim/blob/main/lib/index.js), the launcher resolves its literal
entry path and the adjacent `node.exe` or PATH Node executable. It invokes Node directly with
structured arguments and preserves the wrapper's `.JS` PATHEXT removal. Modified wrappers,
environment-setting wrappers and wrappers with Node options are not translated. On Windows, an
explicit `.js`, `.mjs` or `.cjs` entry also uses Node. The launcher
never installs or downloads Node or a CLI.

Other batch wrappers run through the system `cmd.exe`, with AutoRun and delayed expansion disabled.
Every argument is a quoted literal. Quotes, percent signs, exclamation marks, CR/LF, NUL and a
trailing backslash are rejected before starting the batch command. Spaces, Unicode, empty strings
and quoted shell metacharacters are retained. The complete command line, including the interpreter
and switches, cannot exceed [8191 characters](https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/command-line-string-limitation). These batch restrictions do not apply to native or
resolved Node arguments. An AGY prompt still has its separate platform argument-size bound and
process-list exposure; a batch launch can hit the smaller command-line bound first.

Launcher tests exercise planning with filesystem fixtures and actual managed argument round trips.
Windows batch execution, cancellation and installed-Node round trips are conditional tests. A
non-Windows run reports them as skipped; it does not qualify Windows invocation.

## Document search contracts

`IDocumentSearchClient.SearchAsync` declares a connection-selected search over existing provider
collections. `LlmProviderService` implements that exact operation. `AddLlmConnect` resolves the
interface to the same scoped facade. Search dispatches only when the selected client is the built-in
xAI Responses adapter. Other selected clients refuse before credential refresh or HTTP. The facade
copies the request before connection resolution and uses one frozen connection for validation,
credential refresh and search. Existing admission preserves caller cancellation and refuses calls
after retirement. Supplied sources, authenticators and the selected HTTP client remain borrowed.

Connect supplies the data contract and owned `ConnectLlmJson` metadata. The facade route does not
establish OAuth or collection entitlement. Owned options
reject non-null live `IDocumentSearchClient` values on read and write, including nested consumer-generated metadata;
explicit null remains null. No runtime client is exported as portable data.

The internal stateless xAI protocol maps typed search controls to `POST /v1/documents/search`.
It uses a supplied immutable OAuth connection and the existing SDK consumption admission; it does
not resolve or refresh credentials or register an adapter. Chat
`ProviderOptions` are not copied into search JSON. Explicit non-default service tiers refuse
before HTTP because the search route has no mapped tier control. Requests and responses have
one cleanup owner; independent failures retain fixed-safe diagnostics and actual refused owners.
The facade applies the same connection validation before refresh and before saving refreshed
credentials. It drains admitted search and retains actual cleanup refusals in its shared retirement
result. Subscription OAuth acceptance and collection entitlement remain unqualified.

`DocumentSearchRequest` carries ordered collection IDs, an opaque filter, optional grouping and a
closed hybrid/semantic/keyword retrieval selection. Grouping supports `DocumentMinK` and
`DocumentMaxK`. Hybrid retrieval accepts model or RRF rerankers; semantic and keyword retrieval
accept model rerankers only. Optional values remain absent instead of inserting provider defaults.
The typed xAI ranking and RAG options stay in Connect, not Common.

The records copy supplied lists and metadata dictionaries, including assignments through `with`.
They preserve order, duplicate collection IDs, raw strings and case-sensitive metadata keys.
The native builder preserves collection, grouping and aggregate keys as string arrays without
default reflection metadata.
Grouping requires at least one key; aggregate keys may be empty. Aggregate K is positive,
search multipliers are 1 through 100, and weights are finite values from 0 through 1.
Weights are not normalized, and RRF K has no local lower bound.

`DocumentSearchMatch` reports finite actual scores without range normalization. Missing `Fields`
remains null; an observed empty map remains empty. JSON-looking field values remain strings, and
page zero remains an actual value. Native file/chunk IDs and snippets do not establish workspace
resource ownership or access to a complete source file. Serialization does not perform search,
resolve credentials, fetch files, group results or evaluate filters.

## Image provider integration

`ImageTransportJobSnapshot.ModelId` and `ModelRevision` are optional observed execution facts.
The portable image result preserves them in Common provenance. Workflow IDs and versions remain
workflow selection facts, not model provenance. ComfyUI history currently reports no execution-model
identity or revision, so both remain null.

Resource-bound generated images carry the validated resource descriptor and full selection receipt
returned by each output's own commit. Each selection preserves the actual workspace, session, revision
and ordered membership. The image path does not reread membership or manufacture a selection.

Register the image capability and resolve its provider-neutral transport through
`ImageProviderService`:

```csharp
services.AddImageConnect();

ImageProviderService images = services.GetRequiredService<ImageProviderService>();
IImageTransport transport = images.GetRequiredTransport(ImageTransport.ComfyUi);
```

The host owns connection persistence, credentials, workflow storage, durable job scheduling, media
storage, retry policy, and user authorization. It maps those records into short-lived neutral values:

```csharp
var connection = new ImageConnection
{
    Transport = ImageTransport.ComfyUi,
    Endpoint = "http://127.0.0.1:8188",
    RequestTimeout = TimeSpan.FromMinutes(10)
};

ImagePreparedReference uploadedReference = await transport.UploadReferenceAsync(
    connection, reference, content, cancellationToken: cancellationToken);

PreparedImageTransportRequest prepared = transport.Prepare(new ImageTransportPreparationRequest
{
    Workflow = workflow,
    PositivePrompt = prompt,
    NegativePrompt = negativePrompt,
    Width = 1216,
    Height = 832,
    Seed = seed,
    CacheKey = cacheKey,
    References = [uploadedReference]
});

string providerJobId = await transport.SubmitAsync(connection, prepared, cancellationToken: cancellationToken);
ImageTransportJobSnapshot status = await transport.GetJobAsync(
    connection,
    providerJobId,
    workflow,
    cancellationToken: cancellationToken);
```

`ImageWorkflow.TransportConfiguration`, `PreparedImageTransportRequest.ProviderRequest`, and
`ImageTransportOutput.ProviderState` are opaque adapter-owned data. `UploadReferenceAsync` returns
an immutable receipt; `Prepare` copies that receipt without authentication, reads, uploads, or native
ID lookups. Consumers may round-trip prepared data but it does not prove authority to reuse a native
reference. ComfyUI accepts native receipts only on the same unchanged transport/connection owner.
Changed static headers, a different connection, or a different transport owner require reupload of
logical content. The package does not own an image
database, media library, workflow catalog, cache, or background worker.

ComfyUI reference uploads borrow the input stream. Upload reads from its current position without
rewinding, flushing, or disposing it, including cancellation and failed uploads. The caller owns
the stream's eventual disposal.

ComfyUI accepts static protected headers. Its newly optional dynamic `Authorization` and xAI binding
members remain unqualified and refuse before callbacks or HTTP. Image generation accepts one binary
reference or one logical resource resolved through caller-owned access. URL, native-file, multiple
references, step/guidance overrides and Count greater than one refuse before effects. All observed
outputs are retained. Download MIME comes from actual HTTP metadata, not a workflow default.
Composite admission copies binary reference bytes before credential or capability awaits. Constructing
a BinaryMediaSource still borrows caller memory; the operation owns its entered byte snapshot.

When resource access is supplied, a binary reference is committed and selected for the caller's
session before ComfyUI uploads the authorized logical resource. Resource reads and uploads retain
the endpoint/header snapshot admitted before the first callback. Generated outputs are likewise
committed and selected before the composite publishes completion. Typed failures preserve known
content/selection outcomes; a dispatched commit with no matching response remains unknown.
Resource callbacks retain the admitted workspace and session scope. Returned content and selection
receipts must match the actual artifact. Successful owned-handle cleanup is followed by a final
scope and retirement fence before publication. Refused cleanup uses the typed Cleanup phase and
retains the actual unsafe handle. Independent operation and cleanup errors remain ordered. Bound
inference retirement reports the same retained cleanup failure to repeated disposal callers,
including reader refusals before job submission. Healthy borrowed collaborators detach after drain.

Image facades and settings managers fence new calls on disposal and drain admitted operations before
releasing borrowed transports, workflow catalogs, binding factories, resource access and commit
targets. Those collaborators are never disposed. Settings admission also applies through base and
interface references. Draft edits invalidate pending health, choice and workflow results; an admitted
conditional save still preserves the commit target's observed committed or uncertain result.
`ImageProviderService` also supports synchronous and asynchronous disposal. Retirement cancels
workflow listings, drains providers that ignore cancellation, and checks its own retirement fence
before publishing a completed listing. Provider failures retain their original identity. Admitted
synchronous facade calls keep their original outcomes; returned inference objects and settings
managers keep independent caller ownership. Concurrent disposal callers share completion, including
cancellation-callback failures. Disposal releases the borrowed transport map without disposing it.

`ConnectImageJson` exports exact type-name metadata for the complete public Image data graph.
Runtime authority, bindings, resource handles and binding factories refuse non-null serialization.
Download bytes reuse Common's exact required-data property converter: explicit JSON null is refused,
while empty bytes and unknown MIME remain valid observations. This is not a global byte-memory policy.
Image reference receipts, prepared requests, output receipts and job snapshots use fixed `ToString()`
labels that omit opaque provider data.
Workflow selection stores both `ImageWorkflowOrigin` and the exact ID. Host and built-in origins
remain separate; ComfyUI does not advertise a built-in workflow catalog.
Owned workflow-origin and selection metadata preserve `Host=0` and `BuiltIn=1` and reject undefined
numeric origins. This guard does not change other Image enum policies.
Each present selection requires an explicit origin; an omitted origin cannot silently select Host.

The shared [`XaiFamilyBinding` contract](docs/media-bindings.md) uses explicit selection and the
existing credential-access owner. It does not register or qualify an xAI Image transport.
Family acquisition rejects incompatible credentials before refresh and incompatible refresh results
before credential save; the Responses client uses the same OAuth-only authorization validator before HTTP.
The chat facade applies that selected adapter policy before credential refresh, credential save and catalog callbacks.

Private xAI media codecs now map native image generation/edit bodies and typed video
generation/edit/extension bodies. They retain ordered references, explicit false controls, actual
submission IDs and one-poll status/model/output observations without requested-model or MIME
fallbacks. The image codec copies ordered native `url` or `file_id` objects without inferred IDs or
metadata. It validates HTTP(S) provider-fetch URL data without fetching content and preserves
single-image edit `aspect_ratio: null`. Storage and nullable usage facts use finite owned projections
rather than raw response DTOs. These codecs alone perform no HTTP, upload, credential acquisition or receipt authorization.
The Image and Video transports consume these codecs through the selected core and Files owner.
Their registration does not establish OAuth entitlement or runtime qualification. Synchronous media
usage handoff remains unavailable.

## Video contracts

`Darkspyre.Inference.Connect.Video` supplies `VideoConnection`, `IVideoTransport`,
`VideoProviderService.Bind(connection, xai, resources)` and the five-operation Common
`IVideoInference` facade. Capability lookup, submit, one-poll lookup and owned download
remain explicit calls. Materialization streams actual downloaded bytes into the borrowed
caller resource store and atomically commits content with session selection. It retains
actual nullable MIME, duration and expiry instead of inferring them from request policy.
Returned download streams hold their operation lease until closure. Facade disposal closes
owned downloads, fences new operations and drains admitted work before detaching borrowed
transports, bindings and resource access. Callers can dispose the bound facade through
`IDisposable` or directly through `IVideoInference.DisposeAsync`. Returned-stream operations check
the synchronous owner
fence before effects and before returning, even when linked cancellation propagation is delayed.
Cancellation during successful materialization cleanup retains the observed committed reference,
session-selection outcome and revision in `InferenceResourceOperationCanceledException`.
Materialization validates admitted workspace/session scope and actual content/selection receipts.
Owned download and writer cleanup each runs once, including independent refusals. Cleanup failures
use the typed Cleanup phase and retain the actual unsafe owner. Bound retirement preserves those
failures even when a returned stream is disposed before the facade. Repeated disposal shares the
same outcome after drain and borrowed detachment; ordinary operations do not poison retirement.
`VideoJobSnapshot.ModelId` preserves an actual nullable provider model observation through the
existing Video and nested batch metadata. No requested model or job scope supplies a fallback.

`VideoConnection.MaximumEncodedReferenceAudioBytes` is a nullable caller-selected limit with no
default. A supplied value must be positive. Submit preflight requires it for binary or workspace
reference audio before opening content or acquiring credentials. Preset voices and ordinary
reference URLs do not require it. The encoder bounds combined data-URL bytes, including MIME prefixes,
base64 and repeated reference occurrences. It reads incrementally and refuses excess input. Video
settings preserve the value through load, edit and save; the existing connection JSON metadata includes it.

`XaiVideoOptions` is the fixed Video-owned subtype for Files-owned storage controls, signed
upload destinations and explicit user data. Common metadata refuses these provider options;
`ConnectVideoJson` owns their complete generation/edit/extension and scoped receipt graph.
The private Video codec validates all implemented request controls before resolving any media source.
It preserves supplied aspect ratios without establishing their effective provider behavior.
Serialized job/output scope is data, not proof of authority. Each implemented transport owns
receipt validation and download-origin authorization before effects. Lookup and download carry a
private view of the same borrowed binding, with the facade's retirement fence, before acquisition or refresh.
Materialization performs one download, not a second credential acquisition. Extension requests
refuse an explicit `User` instead of dropping it. New settings carry an explicit xAI connection selection;
rendering, opening and saving do not acquire credentials. Only the explicit capability action
uses the supplied borrowed binding factory. Header changes use the typed protected commit target.
The existing LLM header parser rejects reserved or malformed explicit and acquired-snapshot
headers before credential refresh. The bound facade copies source bytes at operation admission;
the shared `BinaryMediaSource` data constructor still borrows its input.

`AddVideoConnect` registers the internal xAI transport. Capability lookup reads the selected connection's
Video model catalog. Submission retains the actual native request ID. Lookup performs one poll and
retains actual nullable progress, model, output and sanitized provider failure facts. Native downloads
authorize a Files receipt or freshly retrieve the output through its authenticated native job before
opening the same unsigned media pipeline. An imported arbitrary URL is not authority. Terminal batch
outputs with a committed logical resource use its named reader, without inventing a native job ID or
falling back to a provider URL. These implementations remain unqualified against a live provider.

Files and unsigned media transfers share one internal HTTP download owner. It exposes response
metadata before body opening, streams incremental copies, and caches one response/request cleanup
outcome. The response alone disposes content. A refused cleanup retains the actual response/body or
request and remains visible to the owning Files/core retirement. Borrowed destinations remain open.
The send-through-headers timer disarms before body processing; borrowed HTTP timeouts do not change.
Core-owned download opening retains the selected core, Files and HTTP generation operations until
its caller releases them after closure and read drain. Video uses this opening path for returned live
streams. Response closure starts before read drain; leases and creator fences release only after both
settle. Actual cleanup failures remain visible to the transport and facade as well as the core owner.

## Host-executed tools

For complete, compile-checked implementations for every supported provider, including connection
shapes, SecureStore ownership, and the separate chat and agent loops, see the
[`Darkspyre.Inference.Connect.LLM.Examples` guide](examples/Darkspyre.Inference.Connect.LLM.Examples/README.md).

`SupportsToolUse` describes the chat request/response contract: the provider can request a
host-defined function, return its arguments, and accept the host's result on a later request.
`SupportsAgentSessions` and `SupportsAgentToolUse` describe the live session contract, where the
provider invokes an `LlmAgentToolHandler` and receives its result during the same turn. Darkspyre.Inference.Connect.LLM
never grants a model direct access to host resources. The host must validate every call and apply its
own authorization and side-effect policy.

| Transport | Chat tools | Agent tools | Prompt images | Prompt documents |
|---|---:|---:|---:|---:|
| OpenAI Responses | Yes | Yes | Yes | Yes |
| OpenAI-compatible Chat Completions | Yes | Yes | Override | Override |
| ChatGPT Codex subscription | Yes | Yes | Yes | Yes |
| Anthropic Messages with API key | Yes | Yes | Yes | Yes |
| Grok OAuth Responses | Yes | Yes | Catalog | Yes |
| Claude subscription via official CLI | No | Yes | Yes | Yes |
| AWS Bedrock Converse | Yes | Yes | Yes | Yes |
| AWS Bedrock AgentCore Harness | No | Yes | No | No |
| Google Vertex AI (Gemini and Claude) | Yes | Yes | Yes | Yes |
| Gemini API (Google AI Studio) | Yes | Yes | Yes | Yes |
| Antigravity CLI | No | No | No | No |

Tool-result content support is declared per route and lane; see [Tool output](#tool-output).

OpenAI-compatible calls emit their tool start before argument deltas. Missing provider tool-call IDs
receive distinct Connect-generated IDs. Preserve these IDs unchanged in assistant history and tool
results; a late provider ID does not replace an already published generated ID.

Define tools on the request and branch on `RequiresAction`:

```csharp
var weatherTool = new LlmToolDefinition
{
    Name = "get_weather",
    Description = "Get the current weather for a city.",
    InputSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            city = new { type = "string" }
        },
        required = new[] { "city" },
        additionalProperties = false
    })
};

LlmChatResult result = await provider.CompleteAsync(
    connectionId,
    new LlmChatRequest
    {
        ModelId = modelId,
        Messages = [new LlmChatMessage("user", "What is the weather in Chicago?")],
        Tools = [weatherTool]
    },
    cancellationToken);

if (result.Status == LlmCompletionStatus.RequiresAction)
{
    LlmToolCall call = AssertSingleAuthorizedCall(result.ToolCalls);
    string toolOutput = await ExecuteWeatherToolAsync(call.ArgumentsJson, cancellationToken);

    result = await provider.CompleteAsync(
        connectionId,
        new LlmChatRequest
        {
            ModelId = modelId,
            Messages =
            [
                new LlmChatMessage("user", "What is the weather in Chicago?"),
                new LlmChatMessage("assistant", result.Text)
                {
                    ToolCalls = result.ToolCalls,
                    ContinuationState = result.ContinuationState
                },
                new LlmChatMessage("user", string.Empty)
                {
                    ToolResults = [new LlmToolResult(call.Id, LlmToolOutput.FromText(toolOutput))]
                }
            ],
            Tools = [weatherTool]
        },
        cancellationToken);
}
```

During streaming, tool calls arrive as `LlmToolCallStartedEvent`, one or more `LlmToolCallArgumentsDeltaEvent` values, and `LlmToolCallCompletedEvent`. The terminal `LlmChatCompletionEvent` repeats the complete calls in `LlmChatResult.ToolCalls`. `ArgumentsJson` is deliberately raw JSON; the host should deserialize it into its own validated input type before execution.

### 2.0 attachment migration

Version 2.0 replaces `LlmToolAttachment` with `LlmAttachment`, removes caller-supplied
`MediaType`, and adds `Attachments` to `LlmChatMessage` and `LlmAgentTurnRequest`. Replace the old
type name, delete `MediaType` initializers, and gate prompt inputs with `SupportsPromptImages` or
`SupportsPromptDocuments`.

The binary variant of a prompt attachment is a provider-neutral value. The host supplies the
high-level kind, filename, and bytes; Darkspyre.Inference.Connect.LLM detects the media type and owns base64 encoding,
temporary-file staging, sandbox roots, and every provider wire shape:

```csharp
var promptWithFiles = new LlmChatMessage("user", "Compare the chart with the report.")
{
    Attachments =
    [
        new LlmAttachment
        {
            Kind = LlmBinaryKind.Image,
            FileName = "chart.png",
            Data = chartBytes
        },
        new LlmAttachment
        {
            Kind = LlmBinaryKind.Document,
            FileName = "report.pdf",
            Data = reportBytes
        }
    ]
};
```

Check `SupportsPromptImages` and `SupportsPromptDocuments` before attaching prompt input. Prompt
attachments belong on user messages only. Adapters reject an unsupported kind, unrecognized content,
path-bearing filename, or empty payload instead of silently turning it into text.

OpenAI Responses, direct Codex, Anthropic Messages, Claude CLI, and Bedrock use native
image/document content blocks for prompt attachments. Direct Codex sends prompt attachments and
tool-output binaries inline through Responses, with no CLI staging directory. The caller supplies kind,
filename, and bytes; adapters own MIME detection, encoding, and wire representation.

### Files and skills contracts

`ILlmFileClient`, `IXaiFilePublicUrlClient` and `IXaiSkillClient` declare explicit native resource
operations. Their metadata, options, pages, deletion results, transfer receipts and capability data
have exact type-name getters on `ConnectLlmJson`. The provider facade implements these interfaces,
with the selected routes described in [Files and Skills facade](#files-and-skills-facade).
Borrowed Skills upload inputs carry explicit caller limits and remain runtime-only, not data-codec
roots. Their nonnull serialization refuses without reading their streams or exposing their policy.
Unimplemented operations retain false capability flags.

Files pages preserve their returned token and an explicit `IsKnownLastPage` fact. Skills pages
preserve their distinct first/last-ID and `HasMore` observations. Required response values do not
become zero or false when absent. File bytes may be zero; missing MIME and expiry observations stay
null. Public URLs retain their actual strings in data serialization but are absent from diagnostics.
Files and Skills list limits are 1 through 100, matching the
[published parameter schemas](https://docs.x.ai/openapi.json). Optional file/URL lifetimes retain
whole seconds between one hour and thirty days; absent values remain absent.

The private xAI Files adapter uses one HTTP exchange path for upload, list, metadata, download,
delete and public-URL operations. It attempts owned response and request cleanup independently.
An actual disposal refusal uses the typed Cleanup phase and retains the refused message through
the operation error and the adapter's retirement result. Original operation failures remain first;
response and request cleanup failures follow in that order. Retirement drains admitted operations,
detaches borrowed HTTP and reports the same cleanup failures without retrying disposal. Upload
content detaches its borrowed source and fence after the operation settles, including when a
refused request retains its multipart content. Caller streams are not closed, rewound or flushed.
Partial destination bytes and remote mutations are not rolled back by cleanup. This private
implementation does not enable the public provider route or establish live OAuth qualification.

`XaiFileStorageOptions` is separate provider-storage data. It preserves a required filename,
optional whole-second expiry capped at thirty days, and explicit public-URL intent. It does not
apply the standalone upload's one-hour minimum to storage. The closed public-URL union uses
`flag` and `configuration` discriminators: absence, explicit false, explicit true and configured
publication remain distinct. Configured publication reuses `XaiPublicUrlOptions` and its own
expiry bounds. All four storage roots have exact `ConnectLlmJson` getters. These declarations
do not upload content, publish a URL, authorize a request or enable a provider route.

Skill uploads carry explicitly supplied borrowed streams and ordered directory entries. They do
not enumerate paths, inspect archives, read streams or attach a skill to generation. Their runtime
inputs and service interfaces have no public data-metadata getters; the owned serialization
options reject non-null values without inspecting collaborators. Diagnostic strings omit paths,
stream details and resource payloads. Operation-time path/duplicate/page consistency checks,
transfer ownership, facade routing, native scope and provider qualification are not implemented
by these declarations.

### Explicit cache contracts

`ILlmContextCacheClient` declares explicit create, get, one-page list, expiry update and delete
operations. Its request, correlation, receipt, usage, page and delete-result records have exact
type-name getters on `ConnectLlmJson`. `BindContextCacheAsync` validates a selected connection
without remote effects and binds Gemini API or standard-project Vertex Gemini; other routes remain unavailable.
`LlmChatRequest.ExplicitCache` and Common's request carry the same data-only selection.
`SupportsExplicitCache` defaults to false on both capability shapes; overrides cannot enable it.
The Google facade advertises its owner-admitted selection path; a raw direct adapter without this
internal admission remains unavailable. Other built-in routes refuse a supplied selection before
credential refresh, catalog discovery, counting, remote requests or engine/process entry. Registered implementations own their declared
capability and selection validation. Vertex exact cached counting refuses independently of
generation support. SelfHost does not forward native provider cache receipts. Portable projection
preserves the selection; advisory `PromptCache` remains separate. No operation creates, renews,
transfers or falls back from a paid cache implicitly.

Each Google cache operation resolves one selected connection snapshot. The same facade retains
only derived provider-validated receipt facts, swept at observed expiry and cleared at retirement;
it retains neither prefixes nor complete listings. Scope IDs are data, not account authority.
Matching owned record/JSON copies can reuse that receipt evidence. Unknown or new-owner imports
require explicit `Get`; observed credential/configuration replacement revalidates a known receipt
through the current fixed-route `Get` before use. Credential equality is only change detection,
never a claim of rotation-stable principal identity. List does not authorize generation.

Vertex requests use the fixed configured project/location route. An authorized create, get or
consistent list response can establish that route's observed numeric project parent on the same
binding. Conflicting project, location, model or native-name facts refuse. Short `models/gemini-*`
response names preserve model identity but do not establish a project association. Replacement
configuration clears this derived association; imported names never select a request endpoint.
Express mode and Claude explicit caches remain unavailable.

Google generation and exact Gemini API counting use the same canonical cached-content suffix body. Cached
suffixes cannot replace system instructions, tools or tool configuration. Advisory occupancy is
unknown rather than a suffix-only total, and it never performs cache revalidation. Actual usage
remains the provider's observation. Create, expiry-only update and delete are explicit operations;
dispatched acknowledgement failures preserve mutation uncertainty without retry. A same-owner
successful-delete tombstone permits repeat deletion only under unchanged configuration, and
`AlreadyAbsent` requires an actual authorized 404. Expired selections and fixed-route not-found
reads use distinct typed unavailable reasons; authentication failures do not imply absence.

Cache creation materializes only explicitly named logical prefix resources, not the session's
selected chat resources or the workspace catalog. The shared scope/descriptor/read-handle owner
validates immutable metadata and counts streamed bytes. Gemini API Files staging uses a fixed-origin
multipart route, validates returned native identity/MIME/observed size, and observes PROCESSING via
fixed-route readiness reads under the same connection until ACTIVE. FAILED or contradictory facts
refuse; cancellation drains admitted work. Opened handles and responses are owned; workspace
access, source content and HTTP are borrowed. Derived staging copies follow Google's documented
expiry without implicit deletion, reupload or retry. Unknown-length multipart acceptance, staging
expiry versus cache lifetime and real model entitlement remain provider qualification gates.

Vertex's optional typed `LlmVertexCacheStaging` selects a Cloud Storage bucket and object prefix
under the same acquired Google authorization. It creates no bucket and accepts no separate staging
credential. Named and raw file media stream to derived objects with observed bucket, object name,
generation and size. Any present response MIME is validated; absent response MIME remains unknown.
Duplicate named references open and upload once while every
sent inline occurrence counts its raw bytes. Without staging, inline media preparation uses a
10 MiB aggregate raw-content budget; this is a local preparation bound, not a claim about the
backend's encoded-body measurement. The 10,000,000-to-10,485,760-byte interval reaches the provider
unchanged. System instructions, tool schemas, text, base64 and metadata have no added body-size cap.
Unknown-length named content reads once through the remaining occurrence budget plus a boundary
byte; oversize content refuses without truncation, reopening or automatic staging.

Cloud Storage upload query parameters encode the exact object name once. Native `gs://` file URI
values preserve slash, plus, percent and Unicode text without normalization or percent decoding.
The local unqualified-URI representation rejects literal `#`, `?` and controls in the configured
prefix; this is not a Cloud Storage object-naming prohibition. Insert-only upload preconditions
do not make an object immune to later external replacement. The caller maintains derived object
availability and unchanged content for cache use. The adapter does not append a generation fragment,
delete objects, retry uploads or recreate caches implicitly. Partial staging effects remain distinct
from dispatched cache-create uncertainty. Unknown-length upload acceptance, native URI character
interpretation, external object retention and actual model entitlement are not established by local
validation or controlled transport fixtures.

Upload requests own their content and receive one cleanup attempt after response cleanup. Media
content detaches its borrowed source and fence after admitted upload work drains, including when an
outer request-content wrapper refuses cleanup. The source stream remains borrowed.
Gemini and Vertex cache-prefix preparation use the shared named-resource failure boundary.
Existing typed failures keep their actual resource, phase, commit/selection receipts and cancellation
token. Untyped failures carry the current phase and known read bytes. Independent read-handle
cleanup failures remain alongside the original failure; prefix preparation does not manufacture
a dispatched cache-create outcome.
Cache CRUD exchanges also own their authorized requests. They attempt response then request cleanup
before result publication or typed uncertainty, preserving validated late receipt evidence. Admission
failures still clean requests once and do not imply a dispatched mutation.
If owned request, response or read-handle cleanup refuses, the same facade retains the actual refused
owner through a safe cleanup carrier. Retirement drains admitted operations, attempts independent
owned cleanup and reports its cached cleanup failure to every disposal caller; it does not retry
the refused release. Ordinary provider/transport/receipt failures do not fault retirement, and
healthy borrowed references detach even when a distinct owned resource cannot be released.

Receipts require observed expiry and preserve unknown usage/timestamps as null. List page size is
explicit and positive; a short page with a continuation token remains nonterminal. Collection
properties copy their lists, without claiming an admitted operation's deep prefix snapshot.
Create's optional `Resources` is borrowed runtime authority: non-null serialization fails and
diagnostics do not inspect it. The request does not validate a provider's lifetime or scope.

The private Google cache data boundary freezes prefix lists, binary bytes and tool schemas, requires
one positive, Protobuf-representable TTL or future expiry, and preserves the exact reconciliation
display name. It converts CLR ticks without floating-point rounding and refuses observed timestamps
finer than representable CLR precision. Its receipt parser preserves independent unknown usage facts, validates required
observed expiry and returns one ordered page with its actual continuation token. These effect-free
helpers do not establish native resource/principal authority or enable cache dispatch.

The shared Gemini request codec can construct immutable cache-prefix contents, system instructions,
function declarations and tool configuration without generation-only settings; this internal
preparation shares the canonical contents/tool owner used by the Gemini lifecycle; it does not
independently authorize a native receipt or prefix resource.

The private Google cache create send boundary borrows an already-authorized request and client.
It disposes the response, checks the existing caller and HTTP-generation fences, and never retries.
Cancellation before send remains ordinary cancellation. Failed acknowledgement after invoking send,
including a non-success HTTP status, conservatively reports possible creation through the typed
reconciliation errors. A local acknowledgement does not prove rollback or permit retry. A validated
late receipt remains exception evidence when retirement or response cleanup prevents success. Fixed diagnostics preserve
observed status and request identifiers without retaining untrusted body or exception text. This
boundary does not authorize routes, issue receipt scopes or make a provider cache route available.

The private Google cache read boundary borrows an already-authorized GET request and client.
It owns the response and executes one receipt or page callback without retrying or chasing pages.
Existing caller and SDK-generation fences cover send, body read, parsing and final publication after
response cleanup. Ignoring send, read and cleanup work drains before the admitted operation exits.
Read failures retain safe status and request identifiers as ordinary provider or cancellation errors,
with both safe operation and cleanup facts when they fail together. They do not imply a mutation,
publish late receipts or establish resource/account authority.

The internal Gemini cache read adapter builds fixed GET and one-page LIST routes from an admitted
connection. It reuses the Gemini API endpoint and API-key rules, validates native resource names and
model IDs, and replaces imported receipt scope data with the caller-supplied scope after an exact
GET identity check. LIST preserves actual page order and continuation tokens. Requests are owned;
HTTP and owner fences are borrowed. The adapter issues no scope authority, retains no cache catalog
and does not independently enable public binding, generation selection or cache creation.

The private Google cache mutation boundary borrows already-authorized PATCH or DELETE requests and
the existing caller and SDK-generation owners. It validates update receipt identity and observed
expiry before publication. Delete success acknowledges `AlreadyAbsent=false`; only HTTP 404 can
invoke the borrowed effect-free absence-authorization callback, fenced before and after invocation.
That callback supplies already-established binding evidence. A false result remains an uncertain
failed acknowledgement, not absence. Rejections and deletion acknowledgements do not read response
bodies. Failures after invoking send use the typed update/delete uncertainty errors, preserving safe
operation and response-cleanup facts and optional validated late evidence without retrying or
publishing success. Cancellation retains actual observed response status, request ID and retry delay
as safe evidence within one cancellation failure, not an extra failure or successful acknowledgement.
These internal exchanges do not authorize routes, issue receipt scopes or
enable public cache operations.

`LlmContextCacheCreateCancelledException` and `LlmContextCacheCreateUncertainException` retain
correlation, the actual request ID and an optional observed receipt without declaring success or
rollback. They are runtime exceptions, not portable metadata roots. Their presence does not
implement dispatch tracking, automatic retries, reconciliation or paid cache creation.

`LlmContextCacheUnavailableException` distinguishes a selected route's observed missing cache from
elapsed selected or observed expiry. Missing permits an absent HTTP status or HTTP 404, never a
contradictory authentication or transport status. Expiry does not establish deletion or account entitlement.
`LlmContextCacheMutationUncertainException` and `LlmContextCacheMutationCancelledException` identify
an expiry update or deletion that may have committed. They retain the selected reference, actual
request ID and optional matching late update or deletion evidence without declaring success or
authorizing another mutation. Update evidence preserves the same resource, model and scope while
allowing changed expiry; a supplied reference expiry must match that receipt's observed expiry.
Cancellation retains its token. The existing create errors remain
distinct. These runtime exceptions have fixed diagnostics and retain already-safe inner failures;
the adapter boundary must sanitize untrusted failures before construction. They are not portable
metadata roots. `LlmContextCacheUnavailableReason` and `LlmContextCacheMutation` have closed,
numeric owned metadata. Constructor validation does not prove provider authority. These
contracts do not implement update/delete dispatch or make a cache provider available.

### Tool output

`LlmAgentCallerServices` groups borrowed permission, text-file, terminal-factory, shell-executor
and resource-access interfaces. The typed callback DTOs preserve actual session/turn scope,
informational content/diffs/locations, raw JSON, ordered permission choices and artifact descriptors.
New DTOs copy lists, binary data and JSON values on constructor/init/with assignment. Diagnostic
strings omit callback contents, paths and environment values. File read StartLine is one-based;
MaxLines=0 remains zero. Terminal OutputByteLimit preserves nullable/zero/64-bit values.
Absolute POSIX, Windows drive-rooted and server/share UNC path text survives across decoding hosts
without normalization or filesystem access. File URLs and relative paths are not operational paths.

Factories and caller services remain borrowed. A returned terminal handle is owned by its session;
Kill preserves readability, while DisposeAsync must kill/drain and invalidate that handle. These
interfaces contain no filesystem, process or shell implementation. Start/Resume borrow the typed
composition and reject unsupported supplied services before runtime work. The xAI Responses managed
lane maps shell execution through the supplied borrowed `ShellExecutor`; other built-in managed
adapters do not advertise that mapping. Managed sessions release their borrowed handler/service
references only after admitted work drains; they do not dispose them.
Caller-owned SelfHost portable shell support does not enable its managed shell executor or
caller-service mapping; supplied managed shell definitions/services still refuse before engine resolution.

`ConnectLlmJson` exports the callback data through exact type-name getters and closed fixed unions.
Runtime services have no public metadata getters. Their internal owned codecs refuse all non-null
read/write without invoking callbacks or diagnostic methods. Start/Resume are not data-codec roots.
Callback RuntimePolicy uses fixed `host-controlled` and `provider-configured` variants. Scope data
freezes caller-selected policy roots and does not grant filesystem or resource access.
The exact `LlmAgentRuntimePolicyKind` root and nested capability data reject undefined numeric values
and preserve both defined numeric values.

`LlmAgentToolPresentationEvent` carries a copied complete tool presentation without requiring a
tool name or turn ID. `LlmAgentContentObservationEvent` carries ordered typed user, assistant or
reasoning content. Both preserve actual nullable turn/item identities and explicit replay facts;
neither invents completion or invokes a tool. Append adds blocks. Replace requires an actual item
ID and represents the complete known item content. Changing an Append observation to Replace
through an initializer requires assigning its item ID first. Event payloads copy binary data,
JSON and nested collections. Diagnostic strings redact presentation contents.
`ConnectLlmJson` exports these two concrete events and their two closed enums through exact
type-name getters. Existing streams carry the events through `LlmAgentEvent`; there is no base-event
data codec. Resource links remain observations without fetching or artifact registration.
These contracts do not register an ACP/Copilot adapter or implement protocol replay and tool-update
merging.

`LlmAgentClientToolDefinition` is a closed function/shell data union. The existing
`LlmAgentToolDefinition` retains its schema, strictness, deferred-loading and sequential-execution
fields. `LlmAgentShellTool` wraps the Common shell definition. `ConnectLlmJson` exports these three
types with exact type-name getters and fixed `function`/`shell` discriminators. Start/Resume definitions
and Common/Connect request, history and terminal collections preserve one ordered function/shell
union. Unsupported shell definitions/history refuse before refresh, catalog, provider counting or
native entry. Facades freeze the new tool graphs before their first awaited resolution. Agent
Start/Resume/Fork freezes definitions before connection resolution. Managed openers freeze definitions
and supplied context before preparation or catalog awaits. Schemas and context bytes are copied;
handlers and caller services stay borrowed. Shell results
match actual preceding calls; output limits remain on that call, not duplicated on the result.
The xAI wire projection echoes the matched call's actual nullable output limit and preserves ordered
commands, output members and exit/timeout outcomes without truncation or inferred execution facts.
Terminal shell observations use the same Responses parser and opaque continuation codec; streamed
observations do not invoke the executor. Managed mixed function/shell calls execute once in terminal
order and retain each genuine completed result. A callback failure or interruption propagates and
leaves the unfinished tail unresolved for explicit reconciliation. Function-only scheduling and
error conversion are unchanged. Shell callbacks run outside the context lock with actual
thread/session/turn scope and no inferred directory or runtime authority. Retirement drains admitted
callbacks before detaching borrowed services. This source implementation is not OAuth/runtime
qualification. Skills directory multipart and uploaded-skill generation attachment remain separate
protocol gates; no local filesystem discovery, process runner or Skills upload is performed here.

`LlmAgentEvent.TurnId` is nullable for actual session-only observations. Concrete turn-scoped events
retain guarded non-null IDs, including construction and base-typed `with` assignments. Resource and
resource-output observations carry null, not an empty synthetic turn. The owned
`LlmAgentResourceSelectionChangedEvent` metadata preserves an observed revision and nullable actual
turn; its declaration does not implement selected-resource membership or hint publication.
Common shell-call observations and typed history remain distinct from function-shaped agent events.

A tool result carries one `LlmToolOutput`: ordered parts, optional structured JSON, and an error flag.
A chat request sends it as `new LlmToolResult(call.Id, output)`, and an agent `ToolHandler` returns it.

```csharp
var output = new LlmToolOutput
{
    Parts =
    [
        new LlmToolOutputText("The chart and report follow."),
        new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", chartBytes, "chart.png"),
        new LlmToolOutputBinary(LlmBinaryKind.Document, "application/pdf", reportBytes, "report.pdf"),
        new LlmToolOutputResource("https://example.test/spec") { Name = "spec" }
    ],
    StructuredContent = JsonSerializer.SerializeToElement(new { rows = 42 })
};
```

The host supplies each binary part's media type. A resource with neither `Text` nor `Data` is a link,
and Connect never fetches it. A resource with a byte body follows the carriage its media type selects,
and `BodyKind` reports that kind.
A text document is UTF-8 text with a `text/*` or `application/json` media type.

`LlmRichContentSupported` declares how a route carries each kind of content. `Native` sends the
provider's own content item, `Text` sends a text part that renders it, and `Unsupported` omits it.
`LlmProviderCapabilities.RichContentSupported` describes the chat lane, and
`LlmProviderCapabilities.Agent.RichContentSupported` describes agent-session tool calls. An override
with `SupportsToolResultImages` or `SupportsToolResultDocuments` set to `false` lowers those carriages.
An override never raises a carriage.

| Route and lane | Images | PDF | Text docs | Audio | Links | Embedded text | Structured | Error flag |
|---|---|---|---|---|---|---|---|---|
| Direct Codex (chat and agent), OpenAI Responses | Native | Native | Native | Unsupported | Text | Text | Text | Text |
| Anthropic Messages | Native | Native | Native | Unsupported | Text | Text | Text | Native |
| Bedrock Converse | Native | Native | Native | Unsupported | Text | Text | Native | Native |
| Claude CLI agent | Native | Unsupported | Text | Unsupported | Native | Native | Text | Native |
| AgentCore Harness | Unsupported | Unsupported | Text | Unsupported | Text | Text | Text | Native |
| OpenAI-compatible, Claude CLI chat, Antigravity CLI | Unsupported | Unsupported | Text | Unsupported | Text | Text | Text | Text |

Every route omits other binary content and keeps the supplied order. Direct Codex and the Claude CLI
agent rows are measured; the other rows follow each provider's protocol and are not live-verified.

The text renderings are fixed. A resource link becomes `[Resource link: name] uri`. An embedded text
resource becomes `[Resource: uri]` followed by its text. A text document becomes `[File: name]`
followed by its text, or an embedded text resource where the route carries those natively. Structured
content becomes its JSON as the final text part. A converted error flag becomes a leading
`[Tool error]` text part.

`LlmProviderService.ProjectToolOutputAsync` reports what one output sends for a connection, model and
lane, without a network call. Each part is `Delivered`, `Converted` to text, or `Omitted` with a
reason: `UnsupportedByRoute`, `UnsupportedByModel`, `InvalidContent` or `Empty`. Invalid content is
structured content that is not a JSON object, a resource with both a text and a byte body, text that is
not UTF-8, or a part over `MaxBinaryBytes`. Adapters serialize through the same projection, so the
report matches the request. An omitted part never fails the request, the turn or the tool, and never
changes `IsError`. Connect adds no notice for an omitted part. A host that wants one adds a text part
and projects again.

The facade checks caller cancellation and facade retirement after each route/model capability
callback and before publishing the projection report.

### Continuation state

`LlmChatResult.ContinuationState` is opaque, versioned adapter state. Preserve it unchanged on that
response's `LlmChatMessage.ContinuationState`, alongside its visible text and tool calls, on ordinary
turns as well as tool turns. Keep every assistant record, not just the latest one. Responses adapters
retain ordered reasoning/text/calls; Anthropic retains thinking/signatures/content. The adapter
replays state instead of duplicating the message's visible text/calls. State is bound to transport,
model and account/connection scope; incompatible state fails before sending unless the request opts
in to omission, as described below. The former request-level `ContinuationToken` has been removed,
with no compatibility shim. Bedrock Converse state carries the reply's reported token counts. When the
reply contains reasoning, it also carries the ordered reply with its reasoning signatures, and Connect
replays that reply. A Converse request accepts only state bound to its own connection and model.
Compatible Chat Completions retains `reasoning_content` and supported `reasoning_details` in
assistant state. Tool-enabled requests replay structured details when present, otherwise retained
reasoning content. Requests without tools omit both replay fields. The codec validates the visible
text and tool calls, connection identity, model and a 16 MiB UTF-8 payload bound before dispatch.
Encrypted reasoning remains opaque and never becomes a public reasoning event. The adapter reads
`reasoning_content`, `reasoning`, text details and summary details in that display order, so
overlapping wire fields produce one event. AgentCore and CLI chat do not import this opaque state.

### Carrying history to another model or provider

A conversation that moves to another model or provider starts a new provider-side conversation from
the host's retained history. Set `UnboundContinuationState = LlmUnboundContinuationState.Omit` on those
requests. Each message whose state does not bind to the target transport, model and account is sent
without it; its visible text, tool calls, tool results and attachments are kept. The result's
`OmittedContinuationStateCount` reports how many messages were sent without state. The default,
`Reject`, fails before any request. The caller's messages are not modified, so the original state
stays in host storage.

Gemini API and Vertex Gemini accept visible foreign tool-call groups with explicit Omit. Gemini 3
places the documented dummy thought signature on the first imported call; original bound native
signatures remain unchanged. A function call with omitted arguments exposes typed `{}` arguments;
its native continuation keeps the omission. Explicit null, array or scalar arguments are rejected.
Tool results follow their original call order. Gemini 3 carries PNG,
JPEG, WebP and PDF tool results as nested function-response media, bounded at 7 MiB per binary.
Earlier Gemini models omit those unsupported binary results through the existing projection report.
Vertex's supported Gemini 3 streaming profiles request preview path-based function arguments and
emit the tool name early. They retain complete reconstructed calls for replay; the route does not
advertise JSON-text argument streaming. Gemini API and non-streaming requests do not send that flag.
Array indexes and boolean argument values retain their JSON types through portable results and
native continuation replay.

Claude CLI chat and Antigravity CLI chat report `LlmProviderCapabilities.ToolHistory = Text`. Tool
calls and results already in the history reach their prompt as `[TOOL CALL id=… name=…]` and
`[TOOL RESULT id=… status=ok|error]` blocks. Offering tools to these routes still fails.

`LlmAgentCapabilities.ContextImport` reports how a session start takes `LlmAgentSessionOptions.Context`:

- Managed Codex and managed Bedrock Converse import it as host-owned context (`Structured`). With `Omit`, unbound state is removed
  and the first turn emits one `LlmAgentWarningEvent` with the count.
- Claude CLI and AgentCore sessions report `OpeningContext`. A start with `Omit` begins a new provider
  conversation and sends the history once, as a `CONVERSATION SO FAR` transcript ahead of the first
  turn's input. The first turn emits one warning that names what was not carried; AgentCore carries
  no history attachments. A start context with `Reject` fails on these runtimes.

`MeasureRequestAsync` measures the request as it would be sent. Claude CLI chat and Antigravity CLI
chat estimate it with the bundled `o200k_base` tokenizer and say so in the diagnostic; the estimate
excludes each CLI's own framing.

### Provider input-token counting

`LlmProviderService.CountInputTokensAsync(connectionId, request, ct)` explicitly calls the selected
provider's count API. `ILlmProviderTokenCounter` is the optional adapter capability. Anthropic
Messages, Gemini API, Vertex Gemini and Vertex Claude implement it. A route without it throws
`NotSupportedException`; counting never substitutes a local estimate or creates a completion.
`MeasureRequestAsync` remains local nonmutating accounting and does not call a count API.

Counting refreshes and prepares the selected credential, binds continuation state, and attaches the
same catalog as generation. It uses the canonical generation request builder. Anthropic excludes
generation-only controls but preserves system/messages, tools, tool choice, thinking and output
format. Gemini API wraps the complete request in `generateContentRequest`. Vertex Gemini sends
the count API's input fields and generation config without `toolConfig`. Fields a count API cannot
represent are rejected, not silently stripped from the prompt.

`LlmProviderInputTokenCount` carries the resolved model, provider-reported input count, nullable
cached-input and billable-character counts, modality breakdowns, and omitted continuation-state
count. Unknown modalities retain their counts as `Unknown`. Missing optional values stay null;
malformed, negative and overflowing counts fail. These are preflight provider counts, not actual
generation usage, billing records or guaranteed admission. No output-token usage is invented.

Each count HTTP operation has a 100-second deadline through response parsing. Caller cancellation
remains cancellation. Service disposal cancels and drains active counts and rejects late results.
The facade retains the adapter's failure as the cancellation cause when both occur together.
Failures expose safe status/retry information without response bodies or nested transport diagnostics.
Vertex Claude uses `count-tokens:rawPredict` with the bare model ID. Its count regions differ from
generation regions; an unsupported selected location fails without rerouting or changing the model.
Vertex accepts Gemini text and Claude model IDs without a local model or generation-location allowlist.
Express mode counts Gemini input with its projectless API-key endpoint.

### Host-owned managed agent runtime

**Accounting extension (11.14.0-rc.1):** the library includes
`LlmProviderService.MeasureRequestAsync(connectionId, request, policy, ct)` and
`ILlmAgentSession.MeasureContextAsync(proposedContext, ct)`, both returning `LlmRequestAccounting`.
These APIs are introduced in the coordinated `11.14.0-rc.1` contract release.
Standalone accounting resolves the selected connection read-only and may read its model catalog,
but never refreshes/persists credentials or runs inference. Session accounting uses base settings
while idle and the frozen executing model/instructions/tools while paused, including queued steering
without consuming it. Active execution is rejected. It never replaces context or consumes a pause.

The result distinguishes `IsSupported = false` from supported-but-unknown measurement, exposes raw
nullable model limits separately from the effective host ceiling, reserve, safety margin and usable
input budget, and preserves `LlmContextMeasurementSource`. Neither `IsSupported`, an estimate, nor
`NeedsCompaction = false` proves fit. Native-owned context accounting is unsupported. See the
[accounting design](docs/enhancements.md#public-nonmutating-accounting) for failure and concurrency semantics.

`OutputLimitEnforcement` is exposed by both `LlmProviderCapabilities` and Common's `LlmCapabilities`.
OpenAI Responses, Anthropic Messages and Bedrock Converse advertise `ProviderEnforced`; direct Codex,
Claude CLI and Antigravity advertise `Unsupported`. Generic compatible endpoints and the custom
AgentCore harness remain `Unknown`: forwarding a field is not evidence of enforcement. A reserve
does not enforce `MaxOutputTokens`. Bounded summarization must reject an unsupported or unverifiable
route, not switch models or silently omit its cap. No new inference retries or compaction orchestration
are introduced by these APIs.

Agent sessions report adapter-owned progress as `LlmAgentActivityEvent`; it is not assistant transcript
text. `LlmAgentToolEvent.Output` carries the tool result the runtime reported, or null when none was
reported. It is an observation: a tool the runtime owns never reaches a host handler through it. The
Claude CLI, AgentCore and managed runtimes set it on the completed or failed event of a host tool. The
Antigravity CLI sets it on native tool observations when AGY reports output or an error.

`LlmProviderCapabilities.Agent` declares context and tool-execution ownership, capture/replacement,
context status, host compaction handoff, native resume/fork/compaction, steering and interruption.
Check these before selecting a pathway. `SupportsAgentTurnInstructions` separately gates a complete
per-run instruction override: null uses base instructions, empty clears them for the run, and
nonempty replaces them. Native adapters currently reject any non-null override before starting.

The existing `ILlmAgentSession` now includes async capability/status queries, context capture,
idle compare-and-swap replacement, and streamed same-run continuation. `LlmAgentContext` is a
versioned list of stable `LlmAgentContextEntry` records, not a durable run scheduler. Agent DS owns
its existing session store, restore/fork decisions and compactor; Darkspyre.Inference.Connect.LLM adds no storage seam.

`LlmAgentContextStatus.NeedsCompaction` and token counts are nullable: unknown is not zero/false.
`MaxContextTokens` is effective context capacity; model discovery also exposes nullable context
and output limits. Context occupancy includes cached input and differs from accumulated usage or
account quota. A `LlmAgentCompactionRequiredEvent` ends an execution segment, **not the logical run**.
After host compaction, `ContinueTurnAsync` uses the same turn ID, pause ID and expected revision,
without resubmitting the prompt or re-executing settled tools. Managed clients cover direct
Codex, API-key OpenAI Responses, Anthropic Messages, OpenAI-compatible Chat, Gemini API, Apple Foundation Models
and Grok OAuth Responses through `AddLlmConnect`. Separate registration
extensions add Bedrock Converse and Vertex Gemini/Claude. SelfHost opens a managed session only after resolving the selected host
engine/model and checking its requested operation capabilities. Each managed client checks the
selected model/connection before opening. Each managed lane uses its actual request estimator,
continuation scope and facade stream as its chat route; it does not enable Azure agent sessions.
`LlmProviderException.IsContextOverflow` identifies recognized provider input-context failures.
Adapters classify their own HTTP, stream or typed SDK errors and keep `ProviderCode` unchanged.
Host-compaction managed sessions pause on this flag. Grok's provider-owned automatic mode ends
`ProviderFailure` without a host pause. Responses/Codex, compatible chat, Anthropic Messages,
Gemini/Vertex, Bedrock Converse and Apple recognize their supported overflow error patterns.
Bedrock Converse SDK failures surface the actual error kind and credential-redacted message,
with observed status and request identity. Unrelated host failures retain the managed generic
diagnostic; cancellation and independently failed owned cleanup remain distinct.
xAI input overflow raises this failure instead of returning an incomplete completion; output-token
truncation remains an incomplete completion.
Each model step permits at most two provider-overflow pauses, including across continued segments.
A third overflow ends the run with `ProviderFailure` and
"The context could not be reduced enough for this step." A completed model step resets the count.

Paused continuation retains protected records exactly or reduces only their function tool-output
parts. A reduction keeps the same ordered part count and replaces each part with text. Replacement
text cannot exceed the original text part's length, including any recovery notice. Binary and
resource parts can become caller-authored notices; Connect does not interpret notice grammar or
host storage IDs. Entry identities, role, call order/count, error flags, structured content and all
other fields remain unchanged. Shell results remain exact. The existing whole-context shrink/fit
checks still apply, including the fallback that requires removal of unprotected history when
occupancy is unknown. Idle `ReplaceContextAsync` retains its separate revision-checked replacement
behavior; it has no live protected set.

`CompactWithProviderAsync(LlmProviderCompactionRequest, ct)` declares a provider-created replacement
proposal for a separate host commit. Managed Codex advertises `SupportsProviderCompaction`;
other sessions reject this operation. The caller supplies the current revision and, when paused,
the exact turn/pause IDs. The request has an exact `ConnectLlmJson.LlmProviderCompactionRequest`
metadata getter. It uses the same fresh credential, model catalog, continuation scope and Responses
stream as generation. Visible tools remain present, parallel calls are allowed, and the output schema
is absent. Successful completion must contain exactly one encrypted compaction item; extra completed
text/tool items are ignored and never dispatched. The complete compaction item remains unchanged in replay.
The returned context keeps budgeted newest real user prompts in chronological order, then the opaque
compaction entry, then the paused protected tail. Protected records stay outside `RetainedUserTokens`;
unknown prompt cost stops optional retention. Actual resource selection/version remains separate.
The proposal retains the current revision, installs nothing and consumes no pause. The host commits it
through `ReplaceContextAsync` or `ContinueTurnAsync`, including their existing shrink/fit checks.

Grok uses the separate provider-owned automatic mode, not this proposal API. A known threshold triggers
one `/responses/compact` call before the model step. Known hard-window excess or provider overflow ends
`ProviderFailure` without a host handoff or over-window retry. The complete returned opaque context
replaces transcript state once after resource/revision admission; steering queued during compaction
follows it. Compacted and usage events retain actual request ID, nullable dropped-message count and
nullable usage. Compaction billing contributes to turn usage, not model iterations or context occupancy.
Priority compaction is unsupported pending qualification and refuses before refresh, resource callbacks
or HTTP. Ordinary Priority generation remains available and never silently downgrades its tier.

`CanCompactHistory` is true when unprotected history exists or a protected function result contains
a text part longer than 2,000 characters or a binary part. `ProtectedFits` is the nullable inverse of
the protected context's measured `NeedsCompaction`. Null means unknown, and compaction availability
does not prove that a particular replacement fits.

Capture defaults to omitting attachment bytes and recording `OmittedAttachments` metadata. For a
tool-output part, the entry's `Index` is the part's position in `Parts` and `ToolCallId` names the
result; the capture drops the binary part or the resource's byte body. Live
compaction must capture with `IncludeAttachmentData = true`; the host may strip bytes only from its
separate persistence projection. Omitted metadata cannot recreate an attachment or become model input.
See the [compiled host-context example](examples/Darkspyre.Inference.Connect.LLM.Examples/HostOwnedAgentContext.cs).

Text-only occupancy is estimated from the actual projected request using the bundled o200k
tokenizer, including instructions and schemas. It is not an exact current-model token count.
Anthropic and compatible Chat use the same validated plaintext generation mapper without provider I/O.
Actual media, selected resources and their opaque continuation make occupancy unknown.
Responses, direct Codex and Bedrock Converse record the provider's reported input
and output tokens on the continuation state (`LlmContinuationState.InputTokens` and `OutputTokens`), and
the estimate counts the history up to the last such state as their sum, tokenizing only what follows.
That estimate reports `Source = Mixed`. Codex without an applicable report uses the pinned 0.159.3
CLI's coarse encrypted-content length approximation plus actual plaintext and reports `Estimated`.
Any retained Codex compaction suppresses historical count anchors, including protected pre-compaction
reports. Compaction-request usage never becomes replacement occupancy. Other providers do not inherit
this heuristic; actual media and resources remain unknown. Hosts that keep continuation state keep the counts with it. Each
Bedrock model has its own tokenizer, so a Bedrock estimate of the unreported tail is approximate and its
diagnostic says so. Provider context-overflow errors trigger a handoff only in host-compaction mode. Catalog limits
are optional. Configure an explicit output reserve when the catalog provides none; without a
known limit, reserve and occupancy, `NeedsCompaction` stays null.

Tools execute sequentially by default. Parallel mode is opt-in and bounded; a tool marked
`RequiresSequentialExecution` serializes its whole batch. Schemas are validated before callbacks.
Interrupted or failed handlers produce error results, and all issued calls settle before capture.
Typed resource-operation failures remain operation failures. Parallel settlement preserves every
independent cause, and cleanup failures remain visible to the turn reader and repeated session
disposal callers. Retirement drains ignoring callbacks without waiting for a paused event reader.
A cancelled handler's external effect may be unknown: Darkspyre.Inference.Connect.LLM never automatically replays it.
Model retries are limited to three total attempts before any output, respect retry delays up to
30 seconds, and leave aggregate usage unknown if an attempt's usage was unreported. Streams with
exposed output are not retried. Consume/dispose an enumeration fully before capture or another run.

Interactive completion methods accept the connection version captured before authentication began.
The `SignInAsync` convenience method captures that version internally. In both cases, the final save is
still conditional, so a connection change during the external authentication round trip is rejected.

## Prompt caching

`LlmChatRequest.PromptCache` and Common `LlmInferenceRequest.PromptCache` carry the same advisory
`LlmPromptCacheHint`. `Prefix` asks to cache tools and instructions. `Conversation` also includes
the conversation prefix through its last message. `Default` selects the provider's standard retention;
`Extended` asks for its longer retention, which can increase cache-write charges.

`LlmProviderCapabilities.PromptCache` and Common `LlmCapabilities.PromptCache` declare automatic
caching, hint acceptance and supported retentions. Capability overrides do not replace this declaration.
Unsupported routes ignore the hint. Unsupported retention falls back to `Default`. SelfHost preserves
the loaded engine's declaration and forwards the hint without turning it into a required control.

Managed agent requests automatically include a `Conversation` hint with `Default` retention,
including tool continuations and accounting projections. A hint does not promise a cache hit, token
savings or a billed cache resource. Reported cache reads and writes remain in `CachedInputTokens`
and `CacheWriteInputTokens`.

Anthropic API and Vertex Claude share cache markers in generation and exact-count requests. Prefix
marks the last nonempty system block, or the last tool when no system text exists. Conversation also
sets the top-level automatic marker. Extended requests one hour; Default leaves the provider TTL
unchanged. Signed and opaque continuation blocks are not modified. A supplied hint conflicts with
raw `cache_control` options.

Bedrock Converse and ConverseStream share one exact model-ID cache fact lookup after the existing
geographic-prefix removal. Only documented Claude and Nova IDs receive markers or cache support.
Claude supports system, tool and message points; Nova supports system and message points only.
Default uses five minutes. Extended uses one hour only for documented supporting Claude IDs and
otherwise falls back to five minutes. Supported Claude and Nova IDs report implicit caching too.
Unknown IDs keep their prompt unchanged. No local token threshold or cache hit is inferred.

OpenAI Responses uses `prompt_cache_options.ttl = "30m"` for Extended on its supported GPT-5.6
and GPT-6 profile rows. Default and Scope add no field. Other OpenAI profiles, Codex, Claude CLI,
Gemini API and Vertex Gemini report automatic-only caching. Responses cache options never flow
into another transport through inheritance.

The recognized OpenRouter Chat Completions endpoint projects documented content markers for Claude,
supported OpenAI profiles, Gemini 2.5/3 and the listed Alibaba explicit-cache models. Claude
Conversation uses the top-level automatic marker; other mapped conversations mark their last text
block. Prefix marks a system/developer text block. A tool-only Prefix leaves the hint unused without
changing the prompt or tools. Claude Extended requests one hour; supported OpenAI Extended requests
30 minutes; other mapped retentions fall back to Default. Known automatic-only routes add no marker.
Raw options cannot override the supported neutral cache control. These declarations describe request
projection, not observed cache hits, access or provider qualification.

## Provider setup and editing

`LlmProviderCatalog.SetupDefinitions` describes providers without resolving connections or
credentials. `LlmProviderService.CreateSettingsManager` returns the manager that opens settings and
runs their actions against actual provider operations. `LlmProviderDefinition.SetupSteps` uses Common's
descriptive `SetupStep`. Render fields and run actions from the settings object, not by interpreting a
descriptive step or switching on a provider ID.

```csharp
LlmSettingsManager manager = llms.CreateSettingsManager(hostCommitTarget);
LlmSettings settings = await manager.OpenAsync(
    LlmTransport.OpenAiResponses,
    new SetupConfigurationIdentity(SetupChangeIntent.Create, connectionId, null),
    cancellationToken: cancellationToken);

// Render semantic field types and call their typed edit methods; edits change only `settings`.
// Inspect each action's declared side effects and obtain host authorization before
// manager.RunAsync(settings, action.Id, progress, cancellationToken).
if (settings.CanSave)
{
    SetupCommitResult saved = await manager.SaveAsync(settings, cancellationToken);
    // Handle Committed, Conflict, Invalid, or Uncertain; never blindly retry Uncertain.
}
```

For editing, supply the exact saved `LlmConnection`, its model selection and an Edit identity whose
expected version matches that connection. The host's `ISetupCommitTarget<LlmSetupChanges>` must
atomically compare that version over configuration and credentials. Authentication uses a draft-local
`ILlmConnectionSource`; neither device completion nor catalog OAuth replaces saved credentials
before commit. Normal runtime credential refresh still uses the existing host seam unchanged.
CLI-owned authentication remains external; optional Claude catalog OAuth does not establish CLI login.

Settings objects never expose saved secrets. API-key fields support Keep/Replace/Clear; provider options,
capability overrides and additional headers already on a connection are retained when editing.
Auth interactions, dynamic choices, validation and explicit model tests use one common contract.
`AddLlmConnectBedrock` registers AWS profile/region choices in the SDK-owning assembly.
OpenAI-compatible remains an advanced provider with explicit manual-model support and typed
default/enabled/disabled capability overrides. Disabling the `Streaming` override selects the endpoint's
non-streaming request, as [Streaming, reasoning text and usage](#streaming-reasoning-text-and-usage) describes.
The Chat Completions and Responses adapters each share one protocol implementation. Adapter hooks
select request routing, headers and model-specific sampling validation without replacing the wire model.
The internal complete Chat decoder accepts an absent original request. It maps the actual reported
model, usage and tool calls without inventing request-specific structured-output validation. The xAI
projection retains citation strings and generated file IDs/names for resource materialization, and
its shared body builder accepts Files-prepared native references. These internal mappings do not
enable batch or deferred operations by themselves.
Prompt-image overrides send `image_url` data-URL parts. Prompt-document overrides send PDFs as
`file` parts and valid UTF-8 `text/*` documents as text parts. Other document formats fail before
dispatch. Tool results remain text-only; prompt overrides do not enable binary tool results.

For OpenAI-compatible connections, `LlmConnection.ChatCompletions` stores two non-secret protocol choices. A null `OutputLimitField`
selects `max_completion_tokens` for `api.openai.com` and `max_tokens` elsewhere; an explicit
`MaxTokens` or `MaxCompletionTokens` wins. A null `UsePromptCacheKey` maps a nonempty
`ConversationKey` to `prompt_cache_key` only for `api.openai.com`. Other endpoints require explicit
true; explicit false disables the mapping on any endpoint. The advanced settings expose both
choices and require retesting after edits. Provider options cannot replace a standard field written
by the typed request. Conversation affinity is a hint, not an explicit billed cache resource.
Existing provider JSON, headers and model-specific overrides are retained; the editor does not turn them into
an arbitrary JSON settings payload.

`XaiResponses` defines the Grok OAuth connection and settings contract. `LlmConnection.Xai` holds
`XaiConnectionOptions`: `Global` selects `https://api.x.ai/v1`, and `UnitedStates` selects
`https://us.api.x.ai/v1`. `GetEndpoint()` derives that fixed URL. Setup offers bounded region and
`Default`/`Priority` service-tier choices, with no endpoint or API-key field. It retains unrelated
headers, provider options and overrides. A kept OAuth access token or a completed draft sign-in
satisfies Save; an API key or static secret header does not. Region edits replace the endpoint;
tier edits require retesting but do not request reauthentication.

Opening Grok settings preserves the supplied endpoint and typed options. Missing typed options or
a route/region mismatch is shown locally and blocks Save, Check and model discovery until an
explicit region selection fixes the draft. Read-only **Saved sign-in** and **Draft sign-in** fields
report credential configuration without exposing secret values or claiming a fresh authentication
check. A draft sign-in remains unsaved until Save commits; an uncertain save remains unknown.

`IXaiDeviceLogin` supplies the device grant to `StartXaiLoginAsync`, `CompleteXaiLoginAsync` and
`SignInAsync`. Completion performs one token poll; interactive sign-in owns the polling loop.
Setup continuation and interactive sign-in respect the device interval, provider-reported expiry,
and the five-second increase on RFC 8628 `slow_down`. `LlmDeviceCode.ExpiresAt` is null for a provider
that supplies no expiry. Device-code diagnostic text is redacted. Token saves use the captured
connection version. Facade retirement cancels and drains sign-in and suppresses late results;
it does not dispose the borrowed authenticator or undo a save already committed by the source.
Entered Grok sign-in operations check the synchronous owner fence after awaited work and before
saving. Retirement rejects late results even while linked cancellation callbacks are pending.
Grok authentication status and sign-in saves use the shared OAuth-only credential validator.
Status returns false for incompatible credentials or malformed bearer tokens; sign-in refuses them
before saving. Both paths retain their rejection of a nonempty API-key field, including whitespace.

The internal `XaiAuthenticator` implements the [RFC 8628](https://www.rfc-editor.org/rfc/rfc8628)
device grant against `auth.x.ai` with form-encoded requests and a public client. A successful token
exchange calls `GET https://api.x.ai/v1/me` once and requires a non-blocked account. Credentials contain
only OAuth tokens. Expiry comes from `expires_in` or the access-token JWT; account identity comes from
`/v1/me`, and the optional ID-token email is display metadata, not a locally verified identity claim.
The shared OAuth pipeline refreshes within 60 seconds of expiry. It preserves account metadata,
accepts rotated tokens and saves through `LlmConnectionAccess`'s version-conditional source seam.

`XaiErrorClassifier` supplies one sanitized HTTP failure policy. It maps 401 and the specified
token-validation 403 message to `AuthenticationRequired`, and 402 or a blocked account to the named
entitlement failure (`AccessDenied`). Unknown 403 responses remain provider errors. Subscription-specific
403 matching is not implemented or live-qualified. Errors never trigger a retry or refresh.
Named authentication and entitlement failures clear the setup device attempt without saving a credential.

The shared Responses pipeline retains xAI output as an ordered deep clone, including hosted-tool
items and encrypted metadata. Continuation is bound to the transport, endpoint, account and model;
token rotation on the same account does not break that binding. The terminal output is authoritative,
including an empty array. Function completions use terminal calls, and a call without streamed arguments
still emits its arguments once. Known terminal statuses map to completion, action, refusal, failure or
incomplete results. The [xAI response schema](https://docs.x.ai/developers/rest-api-reference/inference/responses)
names `max_output_tokens`, `max_prompt_tokens` and `max_time_limit`; each maps to `Incomplete`.
Unknown statuses and incomplete reasons map to `Unknown`. Recognized `context_length_exceeded`
codes or reasons raise `LlmProviderException.IsContextOverflow` before completion without retrying.
The named incomplete limits remain `Incomplete` when no recognized overflow code is present.
Encrypted context stays opaque; diagnostics redact its named values and
reflections, and malformed JSON does not retain a parser exception containing provider text.

The internal `XaiResponsesLlmClient` sends OAuth-only streaming or genuine non-streaming requests
to the typed region's fixed endpoint. It rejects mixed static secret headers before dispatch and retains
ordinary nonsecret additional headers. It sends the typed service tier and records the recognized applied
tier in diagnostics. Typed protocol controls cannot be injected through raw provider options, even when
the typed property is absent. The combined wire tools are limited to 350, including deferred MCP
tool search; `web_search` is a reserved hosted name.
The client joins the generic and language listings, offers only listed language models, and reads the
facade's published catalog. Catalog reasoning levels and image modalities override family fallbacks.
Unknown manually entered models still permit a request without fabricated discovery evidence.
Image prompts require catalog image support and cannot exceed 20 MiB. Temperature and top-p follow
the model profile; top-k, seed, penalties and string stops are unsupported on this Responses route.
The numeric schema preflight follows schema positions and local references, not literal data. Bounds
above 2048 string characters, 256 array items or 64 properties require explicitly authorized local
validation against the original schema. Strict tool schemas beyond those bounds fail without weakening.
This numeric check does not implement the provider's complete regex and schema subset.
The client does not interpret unqualified xAI headers as OpenAI account-usage evidence. Its usage
header names and units require separate provider qualification; no quota snapshot is fabricated.

The terminal Responses `usage` maps [reported integer cost ticks](https://docs.x.ai/developers/cost-tracking)
exactly to decimal USD at 10^-10 USD per tick. `num_server_side_tools_used` and `num_sources_used`
remain independent total-call and source observations. The documented
[`server_side_tool_usage_details`](https://docs.x.ai/developers/tools/x-search#usage-counts) maps
web, X, code-interpreter, MCP and image-generation call counts to the corresponding hosted categories.
Missing, malformed, negative or unrepresentable observations remain unknown; explicit zero and empty
detail remain observed. The client does not sum named detail into a total or count fetched X items as
calls or sources. It leaves the separate `file_search_calls`/`document_search_calls` pairing unknown
because their neutral aggregation is unqualified. Other Responses providers do not acquire these xAI
metrics. Terminal usage remains authoritative; no interim usage producer or capability is added.

`LlmChatRequest.ProviderControls`, its Common request projection and `LlmAgentTurnRequest`
carry `XaiMultiAgentControls`. An absent control inherits `XaiConnectionOptions.MultiAgent`;
a present control with `AgentCount = null` suppresses the saved count and leaves the provider
default unspecified. The existing `grok-4.20-multi-agent` profile supports 4 or 16 agents, encoded
as REST `reasoning.effort = "low"` or `"high"`, never `agent_count`. Profile-backed setup offers
those counts and a provider-default choice. Explicit unsupported models/counts, non-xAI routes,
neutral or raw reasoning combined with a multi-agent control, and raw agent-count injection
refuse before refresh, catalog access or generation. Managed turns retain their effective control
through tool iterations; Connect and batch Resources metadata preserve the fixed
`xai-multi-agent` discriminator, while Common metadata refuses this Connect subtype.

`AddLlmConnect` registers the Grok provider, authenticator, Responses client and managed agent through
the same finite SDK HTTP pipelines. Provider discovery performs no OAuth or model requests.
`AddLlmConnectXai` uses the same registration path; calling it again does not duplicate Grok services.
Registration is not live qualification; OAuth and provider qualification remain `NotRun`.
`LlmModelListCache` owns generic and lazy listings for all four model families. Agent counts are distinct from reasoning levels.
The region and tier premiums follow [xAI pricing](https://docs.x.ai/developers/pricing).

`LlmProviderService` implements Connect's `ILlmTextTokenizer`. `TokenizeAsync` accepts a connection
ID, model ID and one plain string. Empty text is valid. Unsupported adapters fail before credential
refresh. The Grok adapter sends only `model` and `text` to the selected region's
[`/v1/tokenize-text`](https://docs.x.ai/developers/rest-api-reference/inference/other) endpoint.
`LlmTokenization.Tokens` retains provider order, duplicate `uint` IDs, exact display text and copied
bytes. A token's bytes need not decode independently as UTF-8 and are never reconstructed from its
display text. Missing fields and invalid integer/byte representations fail with fixed protocol errors
without retaining raw bodies. `ConnectLlmJson.Tokenization` and `Token` own result serialization.
The facade drains active tokenization during retirement and rejects late results through its
synchronous owner fence. Injected access and clients remain borrowed. Cancellation that races a
provider failure retains the actual error as its cause. Tokenization does not count instructions,
tool schemas, media or protocol framing. It does not report request occupancy, generated
usage or billing, and it does not implement `ILlmProviderTokenCounter`.
OAuth access to its tokenizer endpoint has not been live-qualified.

`ImageProviderService.SetupDefinitions` and `CreateSettingsManager` provide the same editing surface for
ComfyUI. Supply `IImageWorkflowChoiceSource` for host-owned saved workflows and a typed
`ISetupCommitTarget<ImageSetupChanges>`. Header values remain protected, deletion is explicit, and
health checking is separate from a potentially costly generation test. Reference-required workflows
declare the generic generation test unavailable; their existing reference/upload/job APIs remain
first-class. Setup does not replace durable job handling.

The host retains action tasks and their cancellation sources when work should outlive a dialog.
Dropping a settings object discards its edit; nothing is committed without `SaveAsync`. A settings
object saves once. After a successful or uncertain save, open the entry again for a later edit; an
uncertain outcome requires readback first. The
[generic editor example](../Common/examples/Darkspyre.Inference.Setup.Examples/README.md) compiles the
same renderer against both Connect and Tensor. No UI framework is shipped in these packages.

These contracts use the synchronized source package version 11.15.1-rc.1. The old LLM setup-input/action description
types were replaced by Common setup contracts in the 11.3.0-rc.1 breaking-contract release.
Settings objects carry provider-owned unsaved-change state, the current authentication interaction and
explicit choice capabilities for the shared Settings editor.

### Azure Resource OpenAI v1

`AddLlmConnectAzure()` registers the optional Azure assembly after `AddLlmConnect()`.
`AzureOpenAiResponses` and `AzureOpenAiChat` select distinct wire protocols. Set `Endpoint` to the
HTTPS resource authority, such as `https://resource.openai.azure.com` or
`https://resource.services.ai.azure.com`. Paths, credentials, queries and fragments are rejected.
The adapter constructs `/openai/v1/responses` or `/openai/v1/chat/completions`.

`LlmConnection.Azure` contains the explicit `V1` API mode, `ApiKey` authentication mode,
`DeploymentId` and optional `ModelProfileId`. The request's `ModelId` must match `DeploymentId`.
The underlying profile selects existing model reasoning and sampling validation. It never replaces
the wire deployment or proves Azure availability. Manual deployment entry requires no discovery or
management-plane privileges. Deployment discovery and queried account usage are not implemented.
This adapter does not discover context/output limits or deployment availability.

The shared SDK also exposes the closed `Versioned` and `Entra` selections, explicit cloud/audience
and five identity kinds. `ConnectLlmJson` exports their exact type-name metadata, including managed
identity selectors and protected `LlmAzureSecret` variants. Unknown selections and fields refuse.
Client secrets and PKCS12 bytes/passwords belong only in `LlmCredential.Azure`; their diagnostic
strings are redacted. Metadata never opens a workload file or loads a certificate. These declarations
do not enable Entra or versioned routes: the current Azure adapters reject them before dispatch.
The API-key setter refuses Entra mode. Settings expose an incompatible saved API key only for an
explicit protected edit; it does not become an Entra credential.

Unknown profiles advertise no media, structured-output, reasoning-level or sampling support.
Implemented paths can be selected through supported explicit connection/model overrides; model
overrides take precedence. Tool schema strictness remains Unknown without evidence. The Azure
settings expose the deployment, optional profile and supported explicit capability corrections.
Their local choices also expose API mode/version, authentication, cloud/audience, the five explicit
identity families and all three user-managed identity selectors. Incomplete identity values stay
in a private editor draft and cannot become an effective connection. Selecting V1 removes the
draft API version; selecting API key removes the draft Entra configuration. These mode edits never
delete protected credentials. Workload paths are explicit data; editing does not read a token file.
Client-secret and PKCS12/password fields use independent Keep, Replace and Clear intentions.
They compose into one protected credential transaction. Clearing a certificate removes its
dependent password; password Clear means null and explicit empty Replace preserves an empty
password. Incompatible protected payloads require an explicit edit, not an auth fallback.
The generic settings editor enables empty Replace only for nonrequired secret fields.
Entra and versioned drafts remain invalid for Save and unavailable for checks and inference tests.
Choice enumeration performs no token acquisition, certificate loading, discovery or provider call.
The settings keep credentials separate from configuration and save only through the host's
version-conditional commit target. Checking key presence does not verify deployment invocation.

API-key mode sends only the protected resource key in `api-key`. Other credential schemes and
`Authorization`/`api-key` entries in additional or secret header bags are rejected case-insensitively.
The adapter does not acquire Entra tokens or send legacy versioned routes. Responses and Chat
retain their existing shared codecs; opaque state is bound to authority, protocol, deployment,
configuration and protected-key principal. State from public OpenAI or another scope is rejected.

Azure Chat defaults to `max_completion_tokens`. An explicit Chat output-limit field wins.
Conversation cache keys are omitted by default on Azure Chat and require explicit opt-in.
Prompt/tool binary and schema conversions use the shared matching protocol builders.

The registered Azure HTTP clients have no generation timeout and disable automatic redirects.
The adapter refuses 3xx responses without retry. Replacing the named Azure clients' handlers
transfers redirect-policy responsibility to the caller; response checks cannot undo credential
forwarding by a handler that already followed a redirect. The Azure clients have no public
constructor accepting an arbitrary borrowed `HttpClient`.

## Realtime Voice Contracts

`AddRealtimeConnect()` registers `RealtimeProviderService` and `XaiRealtimeProvider`.
Its setup catalog contains registered `IRealtimeProvider` definitions. Registration performs no
OAuth or socket request and does not prove provider entitlement or session readiness.
An unregistered transport refuses.

The facade exposes `GetCapabilitiesAsync(connection, modelId?, cancellationToken)` and
`OpenSessionAsync(connection, config, limits, xai?, resources?, cancellationToken)`.
`RealtimeConnection` carries the endpoint, a five-minute library request timeout, copied headers,
data-only xAI selection and optional borrowed authorization. Runtime bindings and resource access
remain explicit operation arguments. `InferenceStreamLimits` contains three positive caller-local
budgets with no implicit defaults. A returned `IRealtimeVoiceSession` owns its socket and tasks;
the caller disposes it. The facade retires a session returned after cancellation before handoff.

The facade implements `IDisposable` and `IAsyncDisposable`. Retirement fences new work, cancels
entered provider callbacks and waits for admitted synchronous and asynchronous calls to finish.
It detaches borrowed providers after drain, including when cancellation callbacks fail. Concurrent
disposal callers observe the same completion and failure. Providers remain caller-owned. A late,
unhanded session is disposed before its open operation exits; cancellation and cleanup failures are
both preserved. A session already returned to its caller is not disposed by facade retirement.
Retained foreign exceptions can still retain their own type and stack metadata.

`RealtimeSessionConfig` retains the complete typed instructions, audio, tools, turn detection,
transcription, speed and explicit scoped resumption settings. Tool schemas and hosted-tool data
are snapshotted before provider entry. `XaiRealtimeOptions.Replace` distinguishes omitted and
explicit empty maps. `XaiRealtimeForceMessageItem` retains text and optional interruptibility;
an inherited client ID refuses because that variant has no declared ID mapping.
`XaiRealtimeOptions.TranscriptionModel` preserves an explicit input-transcription selector;
null omits it. It does not identify the session model or observed model provenance. Setup saves
and edits it through the same session provider-options value. Session snapshots preserve the
actual conversation receipt object; copying configuration does not issue a new native receipt.

Text/audio content and function-call items can represent unknown observed payloads as null.
Required JSON member presence remains distinct from its nullable value. Audio content preserves
unknown versus empty bytes and an actual transcript; text also carries an optional actual
transcript. Audio deltas still require non-null bytes. These observations do not authorize sending
incomplete history: an implemented adapter must reject missing outgoing text/audio or function
name/JSON-object arguments before effects. Transcript text does not substitute for missing audio bytes.
Session events preserve optional observed modalities, and item events preserve raw provider status.
`RealtimeHostedObservationEvent` carries partial hosted observations without fabricating function
call names, arguments, client execution or completion. Its argument-final flag is independent of
tool completion; final snapshots are not appended to earlier deltas.

`LlmWebSearchTool.Location` carries optional country, city, region and timezone without local
normalization. Null differs from a supplied object with omitted members. The existing hosted-tool
snapshot copies the location value. Current built-in chat and Tensor routes reject unsupported
hosted tools before effects; these fields do not enable a realtime or hosted-tool implementation.

`ConnectRealtimeJson` exposes exact data-type getters for connections, settings changes,
configuration, content, conversation items and concrete events. Its complete graphs admit the
fixed xAI options, force-message item and X search tool. Common's metadata does not admit these
Connect-only extensions. Non-null runtime collaborators refuse portable JSON without invoking
them. Audio-content observations use Common's targeted nullable byte policy, while audio deltas
retain the separate required-byte policy. The new hosted-observation event has exact Common and
ConnectRealtimeJson getters, and Common owns the exact WebSearch location getter and nested graph.

Response and error events carry nullable ordered `AudioArtifacts` and raw `ProviderStatus`.
Each `RealtimeAudioArtifact` preserves the actual output identity, committed descriptor,
optional observed audio configuration and actual selection version. Null differs from an
observed empty list. The data contracts alone do not stage audio;
`RealtimeAudioDoneEvent` remains a separate native observation without a resource receipt.

Common exposes `RealtimeOperationException` and `RealtimeOperationCanceledException` for copied
error observations with actual causes. The xAI session uses one cached completion for Events and
every disposer. It settles resource handles, socket cleanup and lifetime cancellation callbacks
before reporting the final failure. A failure with pending committed audio carries its ordered
actual artifacts and terminal provider status. A lone operation cancellation preserves its token;
independent cleanup or publication failures remain faults. Provider execution remains unqualified.

`XaiRealtimeProvider` implements the xAI WebSocket command and observation route registered by
`AddRealtimeConnect()`. It also accepts explicitly borrowed HTTP and the selected same-access binding.
Opening acquires one immutable OAuth connection snapshot and validates controls, headers,
resumption evidence and optional workspace scope before opening the socket. OAuth WebSocket
acceptance and actual provider/model readiness remain unqualified. A returned session owns its
socket, receive task and ordered command sends. Provider retirement does not dispose returned
sessions or borrowed collaborators.

Each command enters the session lifetime and freezes caller bytes and structured data before
waiting for the send gate. Resource and authorization callbacks run in the serialized command
stage. Command completion means local send completion, not provider acknowledgment. Commands
do not wait for Events. The single Events consumer reads a bounded event/payload feed; retirement
interrupts backpressure and drains admitted sends, receive work and resource callbacks.
Dispatched send failure ends unsafe framing without reconnecting or replaying commands.
Explicit disposal prevents late Events delivery and does not wait for a paused reader. Natural
remote completion permits queued observations to drain, including when reading begins afterward.
Caller-canceled enumeration exits without waiting for session cleanup; disposal still reports the
settled session outcome.

Explicit `session.update` model selectors follow the published WebSocket schema. An attempted
routing change invalidates the original opening receipt proof before acknowledgment, including
receipts issued afterward. A bounded set of attempted selectors classifies subsequent actual
model observations as caller-requested. It does not establish acknowledgment order or command
success. Requested models never substitute for actual model observations. Original same-issuer
receipt objects can reopen under matching binding/version/endpoint/routing proof with unknown
model observation. Imported or cloned receipts refuse before effects when this owner lacks
actual compatible model/account evidence.

Named tool documents use the shared scoped reader and once-only borrowed-content handling.
Audio outputs use the shared speech writer; terminal publication carries actual committed
descriptors and selection versions. Truncate and delete do not mutate immutable workspace bytes.
The writer retains a validated actual commit receipt before a late scope fence. Session completion
recovers pending artifacts when terminal publication fails, including a late post-commit refusal.
Successful fenced publication releases that recovery snapshot; no session-wide artifact history is
retained. Owned cleanup failures retain their actual refused owners and the same retirement outcome.
Healthy borrowed references detach after drain.

`CreateSettingsManager(target)` borrows the conditional commit target. Setup preserves every typed
session default, separates protected header edits and removes live authorization from saved data.
Capability inspection does not prove authentication or session readiness. Manager retirement
fences new operations, drains admitted work and detaches borrowed collaborators. An admitted Save
retains its actual committed or uncertain outcome. Capability inspection and source compilation
do not qualify OAuth, remote resumption, model mutation or provider execution.

## Account usage

Subscription adapters that can read account-level quotas report `SupportsAccountUsage = true` and
implement `ILlmAccountUsageClient`. Hosts normally call the provider-neutral facade:

```csharp
LlmAccountUsageSnapshot usage = await llms.GetAccountUsageAsync(connectionId);
```

The snapshot separates proportional or absolute quota windows, credit balances, and aggregate usage
metrics. Reset times are UTC `DateTimeOffset` values; `IsApproximate` identifies estimates. A CLI may
also supply a sanitized `Summary` for display only—hosts must not parse that text as a stable contract.
Credentials, account email, and raw protocol responses are never included. Codex, Claude subscription,
Antigravity and compatible connections whose endpoint host is exactly `openrouter.ai` expose queried
usage. OpenRouter reads the authenticated key's `/key` report, not the management-only account balance.
Other compatible endpoints reject account-usage overrides that claim this operation. A missing
`IsUnlimited` value is null, distinct from an explicit false.

Facade account-usage operations check caller cancellation and facade retirement after capability
callbacks, before queried dispatch and before publishing completed results. Passive polling checks
the same owner fence after connection resolution and after reading the observation store,
before returning the snapshot. Retiring the facade drains admitted operations
without disposing borrowed clients or the borrowed observation store.

`GetObservedAccountUsageAsync(connectionId)` reads passive provider evidence without a provider call.
It returns null until that connection identity reports a quota. OpenAI Responses and compatible Chat
Completions read rate-limit headers, Anthropic Messages reads its rate-limit headers, Codex reads quota
headers, and both Claude CLI lanes record `rate_limit_event`. HTTP observations precede status handling,
including failed responses, and cover streaming and complete response paths. Vertex Claude does not
inherit Anthropic account observations. These measurements are separate from generation-token usage
and are not public stream events.

The store merges each reported bucket by its own observation time. Each observed quota or credit
retains `Observation.ObservedAt` and `Source`; reset and expiry stay unknown unless reported. Raw Claude
utilization uses a fraction and its reset uses Unix seconds. Codex raw utilization uses percent.
Anthropic token remaining counts are approximate. Codex observed IDs match queried meter/window IDs.
Connection version, provider or account-scope replacement clears prior observations and fences late
results from the old binding. A generation-owned reservation is taken before each chat, polling or
native-session host resolution: an older asynchronous completion cannot replace a newer identity,
while late resolutions of the same identity retain valid in-flight bindings. Host versions remain
opaque; the store does not compare their numeric or lexical order. After an OAuth credential save,
chat confirms the persisted provider/account scope and exact refreshed credential before attaching
response evidence to its persisted version. Concurrent replacement or deletion drops that response's
observation without changing generation behavior or clearing a newer bucket. A failed confirmation
read also drops evidence; cancellation still stops the operation. Confirmation and polling
participate in facade cancellation and retirement. The store does not invent fresh balances when a
reset passes.

Standalone composition passes the same `LlmAccountUsageObservations` to the `LlmProviderService` and
`LlmAgentProviderService` constructors. Their injected store remains borrowed; its generation owner
disposes it after the facades retire. A facade without an injected store owns a private store and
disposes it on retirement; that store is not shared with a separately constructed native-agent facade.
Store disposal clears measurements and ignores later emissions. `ConnectLlmJson.AccountUsageSnapshot`
supplies generation-local JSON metadata for the closed result and nullable provenance graph.

## Direct ChatGPT subscription transport and managed agents

`OpenAiCodex` is a direct subscription Responses adapter, separate from the OpenAI API-key
transport. Configure HTTPS and sign in through the existing device-OAuth flow:

```csharp
new LlmConnection
{
    Version = version,
    Transport = LlmTransport.OpenAiCodex,
    Endpoint = "https://chatgpt.com/backend-api/codex",
    Credential = protectedOAuthCredentialJson
};

// Uses the host prompt and protected ILlmConnectionSource, including compare-and-save.
await llms.SignInAsync(connectionId, authPrompt, cancellationToken: cancellationToken);
```

The host owns this OAuth credential. An existing Codex CLI login is separate and is never read,
copied, or used as a fallback. Former executable endpoints (`codex`, `local://codex-cli`, executable
paths) now fail with migration guidance. Direct Codex settings have no endpoint field: they always
use the official endpoint, so saving an edit replaces a former value. Save requires a completed
ChatGPT sign-in, or a saved credential that the edit keeps. Model
discovery uses the direct authenticated catalog. A model whose catalog entry lists
`supported_reasoning_levels` offers exactly those levels; other models offer `Minimal` to `ExtraHigh`.

The adapter targets Codex CLI `0.159.3`, independently of the Inference package version.
Its `User-Agent`, `version` header and catalog `client_version` use that same version for discovery,
generation and account usage. Additional `version` and `User-Agent` headers are reserved on this
transport; other custom headers retain their existing policy. The reviewed upstream baseline is
[`rust-v0.159.3`](https://github.com/openai/codex/releases/tag/rust-v0.159.3), source commit
`01fc69f4026735edfdf6789820549727a4867b11`. Catalog limits use `context_window`, falling back to
`max_context_window` only when the former is absent or null. Positive reported limits are physical
context limits, not an invented usable-context or compaction budget. Unknown limits remain null.
Matching the version does not establish full CLI tooling or endpoint parity.

Upstream defines the release number in `codex-rs/Cargo.toml` under `[workspace.package]`.
The [main-branch source](https://github.com/openai/codex/blob/2635431edbd43279a4da9c1199456758fbbc8ad3/codex-rs/Cargo.toml#L161-L162)
contains the placeholder `0.0.0`; the
[selected release source](https://github.com/openai/codex/blob/01fc69f4026735edfdf6789820549727a4867b11/codex-rs/Cargo.toml#L160-L161)
contains `0.159.3`. The
[release workflow](https://github.com/openai/codex/blob/2635431edbd43279a4da9c1199456758fbbc8ad3/.github/workflows/rust-release.yml#L11-L55)
runs on `rust-v*.*.*` tags and blocks the build unless the tag without `rust-v` exactly matches
the Cargo package version. Cargo supplies that package metadata to `env!("CARGO_PKG_VERSION")`,
which upstream uses for its
[User-Agent](https://github.com/openai/codex/blob/01fc69f4026735edfdf6789820549727a4867b11/codex-rs/login/src/auth/default_client.rs#L156-L165)
and [version header](https://github.com/openai/codex/blob/01fc69f4026735edfdf6789820549727a4867b11/codex-rs/model-provider-info/src/lib.rs#L532-L535).
There is no separate release-version build override in this path. Connect's compatibility pin
matches the selected release metadata, not main's placeholder or the Inference package version.

Chat and managed agents share SSE request mapping, attachments, tool definitions, reasoning level,
JSON Schema, and ordered opaque response-state replay. Requests use `store: false`; the library
does not retrieve server history or send `previous_response_id`. `ConversationKey` provides cache
affinity only; managed sessions use `ThreadId`. Temperature, JSON-object mode, and
`MaxOutputTokens` are unsupported; the managed output reserve budgets context without sending
that unsupported parameter.

Subscription requests explicitly opt into `reasoning.summary: "auto"`, retaining the selected
effort or leaving effort to the provider when unspecified. Summary deltas flow through
`LlmReasoningDeltaEvent` and managed `LlmAgentReasoningDeltaEvent` with `IsSummary = true`, so hosts
can display progress before the reply. Summaries are provider output and may be absent for a turn;
the library does not manufacture reasoning text. See [OpenAI reasoning summaries](https://developers.openai.com/api/docs/guides/reasoning#reasoning-summaries).

Both `SupportsToolUse` and `SupportsAgentToolUse` are true. With chat, the caller runs the tool
loop. With `LlmAgentProviderService`, Darkspyre.Inference.Connect.LLM runs it and invokes the host's handlers; do not
run a second Agent DS loop on top. Start through that facade so every model iteration re-resolves
and refreshes the host connection. Account/endpoint changes reject an existing session.

Account usage reads the same-origin `/backend-api/wham/usage` endpoint: reported quota windows,
balances and available reset counts are mapped without exposing identity. Missing measurements
remain unknown; activity aggregates and reset-credit expiry are unavailable. This is read-only
and never consumes credits. The upstream route is evidenced in
[OpenAI's backend client](https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/client/rate_limit_resets.rs).

| Former Codex runtime behavior | Managed replacement |
| --- | --- |
| App Server dynamic tools | Host-defined tools and the Darkspyre.Inference.Connect.LLM loop |
| Provider resume/fork | Capture complete context; start with saved/new thread identity |
| Native compaction | Host compaction or a Codex provider-created proposal, committed through idle replacement or same-run pause/continue |
| Immediate steering | Queued safe-boundary steering |
| CLI commands, file edits, web/MCP/subagents | Only tools the host explicitly registers and authorizes |
| Native approval/sandbox enforcement | Host handler policy; no built-in command executor or host sandbox |

The old Codex process/App Server implementation is removed, not retained as a second lane.
Claude and AgentCore still use their native session adapters.

Tools are sent non-strict unless their definition sets `Strict`: `LlmToolDefinition` and
`LlmAgentToolDefinition` both default to false, because a host schema describes what the tool accepts.
Structured output keeps `Strict = true`, because its schema describes the reply the host requires. A strict
tool's schema must meet the provider's strict rules; for OpenAI, every object closes
`additionalProperties` and lists every property as required, and any other schema fails the whole
request.

OpenAI Responses and Codex send the flag as given. Anthropic Messages, OpenAI-compatible Chat Completions and Bedrock Converse send `strict` only
for a strict tool, because some models and compatible servers reject the field even when it is false.

```csharp
LlmAgentProviderService agents = services.GetRequiredService<LlmAgentProviderService>();
var agentWeatherTool = new LlmAgentToolDefinition
{
    Name = "get_weather",
    Description = "Get the current weather for a city.",
    InputSchema = weatherTool.InputSchema
};
await using ILlmAgentSession session = await agents.StartSessionAsync(
    connectionId,
    new LlmAgentSessionOptions
    {
        ModelId = "gpt-5.6-terra",
        Tools = [agentWeatherTool],
        ToolHandler = ExecuteToolAsync
    },
    cancellationToken);

await foreach (LlmAgentEvent agentEvent in session.RunTurnAsync(
                   new LlmAgentTurnRequest
                   {
                       Input = [new LlmAgentTextContent("Inspect this repository and summarize the report.")],
                       Attachments =
                       [
                           new LlmAttachment
                           {
                               Kind = LlmBinaryKind.Document,
                               FileName = "report.pdf",
                               Data = reportBytes
                           }
                       ]
                   },
                   cancellationToken))
{
    Render(agentEvent);
}

// The attachment is not resent. This turn continues on the same headless agent session.
await foreach (LlmAgentEvent agentEvent in session.RunTurnAsync(
                   new LlmAgentTurnRequest
                   {
                       Input =
                       [
                           new LlmAgentTextContent(
                               "What was the second recommendation in the report?")
                       ]
                   },
                   cancellationToken))
{
    Render(agentEvent);
}
```

`LlmAgentSessionOptions.Runtime` (`LlmAgentNativeRuntimeOptions`) controls a provider-owned runtime:
its working directory, `Ephemeral` and `Policy`. Native runtimes require it; managed sessions reject it.
`Policy` is one of two variants:

- `LlmAgentHostRuntimePolicy` (the default): host-selected writable roots, network access, sandbox,
  approval policy and `AllowRuntimeBackgroundWork`. The runtime enforces these limits.
- `LlmAgentProviderRuntimePolicy`: explicit adoption of the policy configured in the provider's own
  settings, including its background behavior. Connect does not read, edit or strengthen that policy and
  makes no claim about the filesystem, network or background limits it applies.

`LlmAgentCapabilities.SupportedRuntimePolicies` lists the variants a runtime accepts. The Claude CLI and
AgentCore accept `HostControlled`; the Antigravity CLI accepts only `ProviderConfigured`; managed runtimes
report an empty set. A runtime rejects any other
variant before effects. A resume without `Runtime` uses the current directory as working directory and a
host-controlled policy with that directory as its only writable root, `WorkspaceWrite`, network allowed,
`OnRequest` and no runtime background work. A runtime that accepts only `ProviderConfigured` therefore
rejects a resume without `Runtime`. A supplied `Runtime` is used whole. Managed Codex has no built-in shell, file editor,
network tool, or approval executor. Agent DS must enforce authorization, filesystem roots, and network
policy inside each tool handler. Managed sessions are always live/in-memory; persistence is explicit
host capture.

Claude uses restricted mode and `--add-dir` for read-only/workspace sessions. A workspace session
with `NetworkAllowed = false` is rejected because the CLI cannot independently constrain Bash
network access. AgentCore permits remote Harness-managed tools only with `DangerFullAccess`;
local workspace restrictions cannot constrain remote tools.
Claude start, resume and fork retire an unsuccessfully initialized session before returning.
Successful cleanup preserves the original opening error. Failed cleanup returns an aggregate
containing the original opening error followed by the cleanup error, without retrying cleanup.

### Headless attachment lifetime

Codex keeps bytes and ordered adapter state in the live managed context and projects them again
on subsequent requests. Claude keeps its CLI conversation open and sends attachment blocks inline.
Neither behavior promises a durable LLM-side file cache.

| Situation | Direct Codex | Claude CLI |
| --- | --- | --- |
| Later turn, same open session | Live context re-sends retained bytes/state | Existing CLI conversation |
| New independent chat request | Send the required history and bytes | Attach again |
| Process restart | Reopen a host snapshot captured with bytes, or reattach | Native resume does not guarantee exact binary access |
| Fork | Start a complete context prefix with new thread identity | Native current-position fork; retain original bytes |
| Session disposal | Live context is gone; host owns any saved snapshot | CLI process closes |
| Compaction | Host replacement decides retained exact bytes | Provider may replace context with summaries |

Default capture omits bytes and adds notices. Use `IncludeAttachmentData = true` when exact
attachment access must survive reopen or live compaction. Keep the original files in host storage;
cache affinity is not file storage.

Managed sessions expose `ThreadId` and `SessionId`, explicit interrupt, safe-boundary steering,
and host context operations. Resume and fork are optional: a client that implements
`ILlmAgentResumeClient` or `ILlmAgentForkClient` supports them, and `LlmAgentProviderService` throws
`NotSupportedException` for one that does not. Native `ResumeSessionAsync` and `ForkSessionAsync` are
unsupported for managed Codex and managed Bedrock Converse; AgentCore resumes but does not fork. Claude retains native resume/current-position fork and interrupt; its steering method
is unsupported. Tools return
text plus provider-neutral image/document attachments. Managed tools reject URL-based image/audio
content; use `Attachments`. Claude materializes MCP document resources through its read-only
`Read` tool. Every tool-capable session must receive a host tool handler.

## AWS Bedrock AgentCore Harness transport

Converse sends a tool's `strict` flag only when `LlmToolDefinition.Strict` is true. Models without
strict schemas, such as Amazon Nova, reject the field even when it is false; a strict tool on such a
model fails with Bedrock's own error.

Converse sends a reasoning level through `additionalModelRequestFields` for Claude and Nova 2 models,
classified from the model or inference-profile id:

| Family | Levels | Fields |
| --- | --- | --- |
| Claude (`anthropic.claude-*`) | The Anthropic Messages set for the same model | `thinking` and `output_config.effort`, or a thinking budget added to `inferenceConfig.maxTokens` (8,192 when the request sets none) |
| Nova 2 (`amazon.nova-2-*`) | `None`, `Low`, `Medium`, `High` | `reasoningConfig` with `type: "disabled"`, or `type: "enabled"` and `maxReasoningEffort` |

gpt-oss, DeepSeek-R1, other models and ARNs offer no level. A capability override can narrow a family's
set but cannot widen it. A Claude request with a level above `None` rejects temperature and forced tool
choice, and a Nova 2 request at `High` rejects temperature and `MaxOutputTokens`, before the call.

Converse streams through ConverseStream. A connection with `SupportsStreaming = false` sends Converse
instead. Its events then arrive after generation ends, in the order reasoning, text, tool calls, then the
completion. Reasoning text arrives as `LlmReasoningDeltaEvent` with `IsSummary = false`. A Claude model
under its default `omitted` reasoning display streams no reasoning text. Usage arrives in the stream's
final metadata event and reaches the host on the completion.

Tool calls return only with `RequiresAction`. The stop reasons `max_tokens` and
`model_context_window_exceeded` report `Incomplete`, `content_filtered` and `guardrail_intervened` report
`Refused`, and `malformed_model_output` and `malformed_tool_use` report `Failed` with no tool calls and no
continuation state. An unknown or missing stop reason reports `Unknown`.

A `tool_use` reply with null, scalar or array tool input throws `LlmProviderException` in both
Converse modes. Non-streaming mapping rejects it before publishing response events. Empty object
input remains valid, and provider-reported malformed stop reasons keep their `Failed` result.

Managed Converse compaction rejects a change to the provider-visible message prefix through the last
signed assistant before resource callbacks or context replacement. It compares the shared Converse
projection, not context entry IDs or count metadata. Unsigned and counts-only histories retain their
existing compaction behavior. Tool-result reductions after that signed prefix remain permitted.
An explicit idle `ReplaceContextAsync` can start a fresh context without the old provider state.

Converse model listing returns the models the account can call, each under an identifier it can
invoke. It omits a foundation model whose only inference type is `PROVISIONED`, such as
`amazon.nova-lite-v1:0:300k`. Such a model runs only through purchased provisioned throughput, so a
connection cannot invoke it by model ID.

Inference-profile discovery follows every returned pagination token. Model listing and
foundation-model profile resolution use the complete catalog from the same helper.

For every other foundation model, listing calls `GetFoundationModelAvailability`, at most eight at a
time. A model is callable when its agreement status is `AVAILABLE` and its authorization status is
`AUTHORIZED`. A model that is not callable is omitted, and so is a system inference profile whose
models are all not callable. A callable model that supports `ON_DEMAND` is listed by its model ID. A
callable model that supports only `INFERENCE_PROFILE` is not listed by its model ID; it appears
through the region's system inference profiles that include it, such as `us.` and `global.` profiles.
The connection's credentials need `bedrock:GetFoundationModelAvailability`. When any availability
request fails, for example with `AccessDenied`, listing stops admitting more checks and waits for
the checks already in flight. Each failed request logs one warning. A single failure stays a single
provider exception; multiple failures remain together in an aggregate. A failed logging sink does
not skip another request's failure or diagnostic attempt.

Bedrock's catalog requests report no model sizes. Each listed model carries the context window and
maximum output of its base model from `BedrockModelLimits`, a table of the limits Anthropic publishes for
Claude on Bedrock. An inference profile such as `us.anthropic.claude-sonnet-4-5-20250929-v1:0` takes its
base model's entry. A model that is not in the table, including every non-Claude model and an ARN,
reports null limits, which mean unknown.

Converse streams use the factory's existing bounded producer. Factory retirement cancels and drains
the producer, closes owned streams and releases clients without waiting for paused callers to resume.
Actual cleanup failures remain visible to retirement callers.

Converse also runs on the host-owned managed agent runtime. `AddLlmConnectBedrock()` registers an
`ILlmAgentClient` for `AwsBedrockConverse`; each model step is one Converse request, and the host runs
every tool. Managed Converse sessions take the same options, context and tool output as managed Codex,
with tool output carried on the Converse route.

`AwsBedrockAgentCore` is a separate transport from `AwsBedrockConverse`. Converse is a stateless chat
API with a host-managed tool loop; AgentCore invokes a deployed
[Harness](https://docs.aws.amazon.com/bedrock-agentcore/latest/devguide/harness.html) and owns the
remote agent session. Both are registered by `AddLlmConnectBedrock()`.

Configure AgentCore with the Harness ARN as the endpoint and the same protected AWS profile/region
shape used by Bedrock Converse. The AWS SDK resolves the actual credentials from its standard chain:

```csharp
new LlmConnection
{
    Version = version,
    Transport = LlmTransport.AwsBedrockAgentCore,
    Endpoint = "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/my-harness",
    Credential = new LlmCredential
    {
        Aws = new LlmAwsCredentials
        {
            Profile = "application-profile",
            Region = "us-east-1"
        }
    }.Serialize()
};
```

Use `LlmAgentProviderService` for the full Harness contract. Model ID `default` preserves the model
deployed with the Harness; another ID supplies a Bedrock model override. New sessions generate an AWS
runtime session ID, and `ResumeSessionAsync` continues an existing ID. AgentCore does not expose the
portable fork, steer, remote interrupt, compaction, or non-persistent session operations, so those
methods fail explicitly. Canceling `RunTurnAsync` cancels the local streaming request.

The factory-owned AgentCore invocation uses the same bounded producer as Converse. Factory retirement
cancels and drains the SDK invocation and closes its owned response independently of a paused chat
reader. Resuming that reader after retirement reports cancellation.
An invocation failure survives a later iterator or response cleanup failure. Cleanup failures carry
the typed cleanup phase and remain observable through factory retirement without duplicating the
same actual failure.

Direct AgentCore session disposal fences new operational calls and cancels its active SDK request
and host tool callback. Concurrent disposal callers share the same retirement result and failures.
Retirement closes the observation feed and waits for active operations before releasing the turn gate.
An ignoring SDK reader or callback keeps retirement pending until it returns. A bounded turn producer
drains independently of a paused public iterator. Closed resource observations hold no operation
lease across yields. Late provider and tool results are not delivered. The injected invoker remains
borrowed. Local retirement does not claim a remote Harness interrupt or shutdown.

AgentCore chat and native sessions replace usage observations within each actual Harness invocation.
Only `end_turn` and `stop_sequence` report normal completion. Unknown terminal stop reasons,
including a final `tool_result` without a subsequent completed message, remain `Unknown`.
Malformed, interrupted, partial and hook-stopped chat outcomes report `Failed`. Native sessions
preserve their interruption/refusal/failure reasons; a hook-stopped invocation reports provider
failure. Missing message-stop events retain their explicit protocol-failure path.
Native unknown-completion diagnostics retain the actual stop reason.
Chat emits changed known counts before its completion; its final usage can be null. Its running event
cannot represent wholly unavailable usage. A later known observation after unavailable metadata is
emitted again. Native observations share one request ID per invocation and can report unknown usage.
Each native invocation emits authoritative settled usage even when interim observations are disabled.
Turn totals sum distinct completed invocations, including host-tool follow-ups, without adding repeated
metadata. Absent and overflowing counts remain unknown. Input, output and total counts retain the
provider values independently. Reasoning counts are unavailable. Cache-read/write normalization is
unqualified, so the normalized cache subsets remain unavailable; no cache counts are added to input
or total usage. Usage observations do not establish billing or remote-session cumulative totals.

Host tools become AgentCore inline functions. When the Harness stops with `tool_use`, Darkspyre.Inference.Connect.LLM
captures the streamed arguments, invokes `LlmAgentToolHandler`, and performs the required follow-up
with both the assistant `toolUse` and user `toolResult` messages on the same runtime session. Tool
results are text-only. See `ProviderToolExamples.BedrockAgentCoreAsync` for a complete callback.

The generic `WorkingDirectory` value is not sent to AWS; Harness files and commands run in the remote
AgentCore environment, never in the caller's local directory. `ReadOnly` sandbox mode, or any
approval policy other than `Never`, restricts `allowedTools` to the explicitly registered host tools
because AgentCore cannot route approval requests for its remote built-in tools to the host. With a
writable sandbox and `Never`, the deployed Harness tool policy is allowed to operate remotely.

The current Harness wire contract has no image or document message block and no binary tool-result
block. This transport therefore reports all four attachment capabilities as `false`; callers must not
splice files into text or base64 themselves. It also has no portable model-list endpoint, so hosts
normally expose `default` plus a manually entered Bedrock override.

## Anthropic API-key and Claude CLI transports

The two Claude integrations are separate providers. `AnthropicMessages` is the direct
[Messages API](https://platform.claude.com/docs/en/api/messages) transport and accepts only an API
key or protected `x-api-key` header. It supports chat-style host tools and never launches the Claude
CLI. `AnthropicClaudeCli` is the Claude subscription transport; it launches the official CLI, reports
agent-tool support, and rejects API-key credentials. OAuth is never used to choose between them.
The CLI process starts and ends as [Wrapped CLI processes](#wrapped-cli-processes) describes.

Configure an API-key connection with the API endpoint and a host-owned credential:

```csharp
new LlmConnection
{
    Version = version,
    Transport = LlmTransport.AnthropicMessages,
    Endpoint = "https://api.anthropic.com/v1",
    Credential = protectedApiKeyCredentialJson
};
```

Configure a subscription connection with the CLI executable and no credential:

```csharp
new LlmConnection
{
    Version = version,
    Transport = LlmTransport.AnthropicClaudeCli,
    Endpoint = "claude",
    Credential = null
};
```

For connections created by 0.9.0, migrate Claude subscriptions by changing `Transport` from
`AnthropicMessages` to `AnthropicClaudeCli`, changing `Endpoint` from the Anthropic API URL to
`claude` (or an absolute CLI path), and clearing `Credential` unless the host intentionally retains
the optional model-catalog OAuth. Leaving the transport as `AnthropicMessages` now means direct
API-key billing and will never launch the CLI.

Install the official Claude CLI, ensure `claude` is on `PATH`, and complete its interactive sign-in once:

```bash
claude
```

Subscription prompts run in an empty temporary workspace with safe mode, tools, slash commands,
permission prompts, and session persistence disabled. Darkspyre.Inference.Connect.LLM sends the complete prompt as BOM-free
UTF-8 stream JSON over standard input rather than exposing it in process arguments. It consumes output
line by line and treats the terminal `result` event as authoritative, including when the CLI retries
an intermediate refusal on a fallback model. `IsAuthenticatedAsync` checks only `claude auth status
--json` for a signed-in first-party account; it does not inspect or require a stored credential.

Claude subscription account usage runs the CLI-owned `/usage` command separately and normalizes its
quota windows plus approximate local request/session activity. It only needs the CLI login, not the
separate model-catalog OAuth credential. API-key connections do not advertise account usage because
Anthropic does not expose the subscription report through the Messages API.

Native chat-style tools are enabled for API-key connections. Claude subscription chat requests remain
isolated one-shot generations, report `SupportsToolUse = false`, and launch the CLI with tools disabled.
Tool history in those requests is carried as text blocks (`ToolHistory = Text`).
For host tools, use `LlmAgentProviderService`: it registers the supplied schemas as an SDK-hosted MCP
server over Claude's streaming stdin/stdout control protocol. MCP `tools/call` requests are converted
to `LlmAgentToolCall`, executed only by the host's `ToolHandler`, and returned to Claude as MCP results.
Each call runs apart from the adapter's stream reader, so the session keeps routing output while a host
tool works. Interrupting a turn makes the CLI cancel its call with `notifications/cancelled`; the adapter
cancels the handler's token and answers the call with an error result. The CLI exiting, or the session
being disposed, cancels calls in flight too. A result that arrives after its turn ended reaches no later
turn.
The adapter launches in restricted mode, disables built-in tools and settings-based MCP servers, and
allows only the explicitly registered Darkspyre.Inference.Connect.LLM tools. Claude subscription connections therefore
report `SupportsAgentSessions = true` and `SupportsAgentToolUse = true`.

### Native background resources

A provider-owned runtime can run background work inside its own process. `ILlmAgentSession` reports it:

- `ObserveAsync` is a session-lifetime feed, separate from each turn's events. It carries
  `LlmAgentResourceEvent` (Added, Updated, Ended), `LlmAgentResourceOutputEvent` hints, and turns the
  runtime starts itself: `LlmAgentProviderTurnStartedEvent`, the ordinary message and tool events, then
  `LlmAgentTurnCompletedEvent`. It has one reader at a time and ends when the session is disposed.
- `GetResourcesAsync` returns the current set. `ReadResourceOutputAsync` reads output nondestructively by
  cursor; a null cursor starts at the earliest retained output, and a cursor before it returns Gap.
  `StopResourceAsync` returns Requested; the resource reports Stopped when the runtime confirms.
- `LlmAgentCapabilities.ResourceObservation` is Supported for the Claude CLI, Unknown for AgentCore
  (not measured) and Unsupported for managed Codex, whose tools run in the host.
- `LlmAgentHostRuntimePolicy.AllowRuntimeBackgroundWork` (off by default) lets the Claude CLI start
  subagents and monitors. A background Bash command is part of the Bash tool and is observed either
  way. `ResourceOutputRetentionBytes` (1 MiB by default) caps the subagent output Connect retains.

The Claude CLI reports each task's start, status and end. Command output is read from the CLI's output
file, and the exit code from the trailer it appends. A subagent's output is the text of its own messages,
which stay out of the main turn. Terminal attach is unsupported.

The Claude CLI starts a turn of its own when background work reports. Connect launches it with
`--replay-user-messages` and reads it for the session's lifetime: a host turn starts at the CLI's echo of
its message, and output with no echo before it is a provider-started turn on `ObserveAsync`. A host turn
sent during a provider turn is queued by the CLI and starts after it.

### Sampling

The neutral request carries `TopP`, `TopK`, `Seed`, `PresencePenalty`, `FrequencyPenalty` and
`StopSequences`. Null controls leave provider defaults intact. An explicit unsupported control fails
before dispatch. `SupportsTopP`, `SupportsTopK`, `SupportsSeed`, `SupportsPresencePenalty` and
`SupportsFrequencyPenalty` default to false; `MaxStopSequences` defaults to zero.

Raw provider options cannot carry `temperature`, `top_p`, `top_k`, `seed`, `presence_penalty`,
`frequency_penalty`, `stop`, `stop_sequences`, or Gemini's `topP`, `topK`, `presencePenalty`,
`frequencyPenalty` and `stopSequences`. The guard applies to connection and request options even
when the typed control is absent. The facade checks the resolved connection before credential
refresh, connection preparation or model discovery. Its shared guard throws `InvalidOperationException`,
including for xAI sampling options. Direct Anthropic and Vertex Claude validation retains its
`NotSupportedException` for raw temperature. Other xAI protocol options retain their adapter errors.
Nonstandard extension options retain request-over-connection precedence.

| Route | Sampling mapping |
|---|---|
| OpenAI Responses | `top_p` on sampling-capable profiles; reasoning models require `ReasoningLevel.None` |
| OpenAI-compatible Chat | `top_p`, `seed`, penalties in [-2, 2], up to four `stop` strings; `top_k` requires a known endpoint override |
| Anthropic and Vertex Claude | Profile-owned temperature/top-p/top-k, `stop_sequences` without an invented Connect limit; thinking permits top-p from 0.95 to 1 and no top-k |
| Bedrock Converse | `inferenceConfig.topP` and `stopSequences`; Claude `top_k`, Nova 2 `inferenceConfig.topK` up to 128, merged with reasoning fields; stop limit 2500, or 10 for DeepSeek R1 |
| AgentCore | `BedrockModelConfig.TopP` on an explicit Bedrock model ID; the harness-default route cannot carry it |
| Gemini API and Vertex Gemini | `generationConfig` sampling fields and penalties, up to five stops; Gemini API top-k requires a catalog default, Vertex uses its shared model profile |
| Apple Foundation Models | Top-p or top-k random mode with optional seed; seed alone selects top-p 1; no stops or penalties |
| Claude CLI, Antigravity CLI and Codex | All six controls rejected; capability overrides cannot enable an unmapped control |
| SelfHost | All six controls and support flags pass to the selected Common engine; unsupported engine controls fail before its stream starts |

Claude Opus 4.7 and newer non-sampling profiles reject temperature/top-p/top-k even with an override.
Earlier sampling-capable Claude profiles enable these controls by default; overrides can disable them.
Gemini Flash 3.6 and later ignore temperature/top-p/top-k and reject penalties, so Connect rejects
explicit values. Vertex Gemini penalties use [-2, 2); Gemini API requires finite penalties.
Compatible endpoint settings expose typed sampling overrides and a nullable maximum-stop count;
zero disables stops, and blank uses detection. Raw provider options cannot inject these typed fields.

### Reasoning

Direct API-key requests map a reasoning level per model. Current models take
`thinking: {type: "adaptive"}` with `output_config.effort` (`low`, `medium`, `high`, `xhigh`, `max`),
and `None` sends `thinking: {type: "disabled"}` where the model accepts it. Sonnet 5.5, Opus 5.5 and
Fable reject disabled thinking and offer no `None`. Opus 4.6 and Sonnet 4.6
offer no `ExtraHigh`, and Opus 4.5 offers effort only up to `High`. Models without effort, such as
Haiku 4.5, take a thinking budget of 1,024, 4,096, 16,384, 32,768 or 64,000 tokens for `Low` through
`Maximum`, added to `max_tokens`.

The levels of an API-key connection come from `GET /v1/models`. A model that reports adaptive thinking
offers the effort levels its entry marks supported; a model that reports only budget thinking offers
`Low` to `Maximum`. `None` is added where the model family accepts disabled or omitted thinking. A model
the catalog lists but the family table does not know therefore gains its reported effort levels without
a library change. Without a catalog entry, the family table alone applies. A model with neither accepts
no level unless a capability override supplies the set.

### Maximum output tokens

For direct API-key requests, omitting `MaxOutputTokens` sends `max_tokens` of 8192. Adaptive thinking is on by default on current Claude models, and thinking tokens count against `max_tokens`, so a lower ceiling can truncate the response before visible text and return `LlmCompletionStatus.Incomplete`. The official CLI does not expose a maximum-output-token option, so subscription requests reject `MaxOutputTokens` rather than silently ignoring it.

### Refusals

A direct Messages API classifier refusal is an HTTP 200 response with `stop_reason: "refusal"`, an empty content list, and a `stop_details` object. Darkspyre.Inference.Connect.LLM emits the explanation as an `LlmRefusalDeltaEvent`, then completes with `LlmCompletionStatus.Refused`, `Refusal` set to `stop_details.explanation`, and `RefusalCategory` set to `stop_details.category`. Both `stop_details` fields are null when the refusal maps to no named category, and `RefusalCategory` is then null.

A refusal can also arrive after partial output. `Text` keeps the streamed partial so the host can log it. That partial is not an answer; hosts branch on `Status`. For subscription prompts, only the CLI's terminal result determines whether the completed request was refused; intermediate fallback attempts do not produce a premature refusal result.

Claude CLI chat and native sessions require a terminal `success` subtype for normal completion.
A successful result with no model stop reason remains completed without inventing that reason.
An explicit unknown stop reason, or a missing/unknown result subtype without another classified
termination, reports `Unknown`. Refusal and token/context-window truncation keep their real terminal
classification. Native unknown diagnostics retain the observed subtype and stop reason. Existing
process errors and interruptions retain their precedence; intermediate fallback attempts do not
classify the completed operation.

### Structured output

Direct API-key connections use the Models API's `structured_outputs` evidence for native schema and
strict tool support. A missing entry does not advertise native output enforcement; a host can
explicitly enable schema support when the catalog is unknown. A catalog rejection cannot be
overridden. JSON-object-only enforcement is not advertised. Supported output schemas use
`output_config.format` with `type: json_schema`; reasoning effort remains in the same output config.

Anthropic Messages and Vertex Claude preflight the same schema subset. Objects must close
`additionalProperties`; unsupported constraints, recursive/external references, complex enums,
unsupported formats and unsupported pattern constructs do not reach native output enforcement.
Schemas are not rewritten. A request must explicitly permit `LocalValidation` to use a schema
outside that subset or a model without native support. That path omits the native format and
validates the response against the original schema, including its omitted constraints.

Strict tools use the same subset check and a maximum of 20 strict tools per request. Ordinary tools
do not count toward that limit and retain their original schemas. Unsupported strict schemas are
rejected before dispatch, not simplified or changed to non-strict. Unknown model strictness still
permits an explicit strict attempt; a known unsupported profile rejects it. Provider options cannot
replace the typed output config or supply the retired `output_format` field.

The combined typed strict-tool and native-output budget permits at most 24 optional parameters and
16 union parameters. A property's omission from its object's `required` list makes it optional;
defaults do not make it required. `anyOf` or a type array consumes a union slot. The iterative
analysis counts nested schemas and each use of an internal reference. Unused definitions are
validated but do not spend the budget. Saturated costs prevent reference graphs from expanding
or overflowing the counters. Non-strict tools and locally validated output schemas spend none of
the native budget. An explicitly permitted `LocalValidation` fallback can omit the output format
when it exceeds the combined budget; it cannot weaken over-budget strict tools.

Regex preflight validates syntax and known unsupported features. It invents no numeric cutoff
for unpublished range or compiled-grammar limits. Passing local checks does not guarantee provider
compilation. Provider compilation failures propagate without retries, schema rewriting or an
automatic switch to local validation.

Subscription connections use the Claude CLI's native `--json-schema` contract and accept the `Low`,
`Medium`, `High`, `ExtraHigh` and `Maximum` reasoning levels. Darkspyre.Inference.Connect.LLM uses
the terminal event's canonical `structured_output` value and validates it locally before returning
completion.

### Model discovery

`AnthropicMessages.ListModelsAsync` uses the API key against `GET /v1/models`. Each listed model carries
`max_input_tokens` and `max_tokens` as its limits, and the reasoning levels its entry reports.
`AnthropicClaudeCli` advertises model listing through a no-prompt initialize control exchange with
the signed-in CLI. Selectable IDs and display names come from its actual `models` response;
returned aliases stay aliases, and an empty list stays empty. The owned temporary process is
retired before options are published. Manual model selection remains available. A host can
explicitly attach the optional browser-authorized catalog OAuth credential to use the existing
paged HTTP catalog instead. Prompts, CLI readiness, usage and agent tools ignore that credential.
Installed CLI initialization compatibility remains a separate qualification requirement.

## Antigravity CLI subscription transport

The [DSA-74 technical design](docs/antigravity-agent-tech-design.md) holds the evidence and mapping for
native agent sessions. The published 11.15.0-rc.1 adapter includes chat and native agent sessions;
installed CLI and target-platform execution remain separate qualification requirements.

`GoogleAntigravityCli` wraps the official [Antigravity CLI headless mode](https://antigravity.google/docs/cli/headless/). Darkspyre.Inference.Connect.LLM sends prompts over the documented stdin NDJSON protocol, consumes stdout events line by line, and emits model text and activity while the CLI is still running. It does not read, copy, or store Antigravity OAuth credentials. The CLI process starts and ends as [Wrapped CLI processes](#wrapped-cli-processes) describes.

Install the CLI and complete its interactive Google sign-in once:

```bash
curl -fsSL https://antigravity.google/cli/install.sh | bash
agy
```

Configure the host-owned connection without a credential:

```csharp
new LlmConnection
{
    Version = version,
    Transport = LlmTransport.GoogleAntigravityCli,
    Endpoint = "agy"
};
```

`Endpoint` may be `agy`, `local://antigravity-cli`, or an absolute executable path. Desktop apps launched outside a login shell should prefer the absolute path because their `PATH` can differ from Terminal or PowerShell. Model discovery calls `agy models`; model `auto` omits the command-line model override and uses the CLI default.

Each request runs in an empty temporary workspace with terminal sandboxing and slash-command expansion disabled. A streamed request sends its prompt as BOM-free UTF-8 through standard input. A request with `SupportsStreaming = false` is the exception: the CLI's only non-streaming form, `agy -p "<prompt>" --output-format json`, takes the prompt as a process argument, because stream-json input requires stream-json output. That prompt is visible in the local process list. It must fit in one argument: at most 131,071 UTF-8 bytes on macOS and Linux, and at most 30,000 characters on Windows, where a `.cmd` or `.bat` launcher cannot carry it at all because the prompt contains line breaks. A prompt that does not fit fails with `NotSupportedException` before the CLI starts. Native JSON Schema and the `Low`, `Medium` and `High` reasoning levels are supported. Only `agent_response` step deltas stream as answer text. A schema-backed request streams no provisional text: it returns the terminal event's canonical `structured_output` value, validated locally. The free-text `response` remains a compatibility fallback for older CLI versions. Temperature, maximum output tokens, headers, and provider options are rejected rather than silently ignored.

Antigravity may emit its own internal tool activity, but the CLI owns that execution. Because
Darkspyre.Inference.Connect.LLM cannot surface the call for host authorization and result submission, this adapter reports
`SupportsToolUse = false`. Its documented headless input protocol accepts only `text` blocks and
terminates on any other content-block type, so `SupportsPromptImages` and
`SupportsPromptDocuments` are also false. Darkspyre.Inference.Connect.LLM does not rely on undocumented `@path` behavior.

Antigravity chat reports `Unknown` for an unrecognized or missing terminal status and retains the
actual response text, usage, conversation ID and observed status. It does not validate that result
as successful structured output. Recognized success and error/interruption handling remain distinct.
Native Unknown diagnostics retain the observed status.

### Antigravity native agent sessions

`LlmAgentProviderService` starts and resumes native AGY conversations. AGY owns the conversation, its
Google sign-in, its native tools and its configured permission policy. Connect owns one local process
per open session, with an open standard input and one output reader.

- `StartSessionAsync` requires `Runtime` with a working directory and an explicit
  `LlmAgentProviderRuntimePolicy`. The runtime rejects `LlmAgentHostRuntimePolicy`, because the CLI cannot
  enforce host roots, network, approval or background limits. The working directory is the process
  working directory; it is not a filesystem boundary. `Ephemeral = true` is rejected: AGY persists every
  conversation.
- Start waits up to 15 seconds for the CLI handshake and returns its conversation ID as both
  `ThreadId` and `SessionId`. No hidden prompt is sent.
- `ResumeSessionAsync` passes that exact ID with `--conversation` and fails if the handshake reports
  another. Resume without `Runtime` fails, so the host must supply its stored working directory and
  provider policy. `--continue` is never used.
- Host tools, a tool handler, host context, host identities, session instructions, per-turn instructions,
  images and attachments are rejected before any process starts or any prompt is written.
- One turn runs at a time. Model, effort (`Low`, `Medium`, `High`) and output schema apply to one turn.
  When they differ from the running process, the session closes that process while idle and reopens the
  same conversation with the new flags before sending the prompt.
- A turn emits `LlmAgentActivityEvent` per step, `LlmAgentTextDeltaEvent` for `agent_response` deltas,
  `LlmAgentToolEvent` for native tools with their reported output, running usage observations and
  `LlmAgentMessageCompletedEvent`. A schema turn streams no text and publishes one
  `LlmAgentStructuredOutputSnapshotEvent` holding the validated canonical result.
- `SUCCESS` completes the turn. `ERROR` and `INVALID` are provider failures. `CANCELED` and `INTERRUPTED`
  are interruptions. Every other status is Unknown. End of output without a result is a provider failure.
- Usage covers the current turn only. It is the difference between cumulative result counters within
  one process, or the sum of the turn's completed step usage after a start or reopen. Decreasing
  counters make the turn total unavailable. Cache-read and reasoning subsets stay null until their
  inclusion in the input and output totals is established.
  Running and final `LlmAgentUsageEvent` observations share one request ID. The final observation
  replaces the estimate, including when the reconciled total is unknown. The running-usage override
  controls interim observations; the final usage event is always emitted.
- Cancelling or abandoning a turn kills the owned process and faults the session object. Output that
  arrives while no turn is running also faults it. Resume opens a fresh session. Connect does not stop
  work AGY may keep running on its side.
- Disposal closes observation immediately, cancels admitted turns and reopens, and waits for their
  writes and producers to stop using the session before releasing the process. A bounded turn producer
  drains independently of a paused public iterator. Closed resource observations hold no operation
  lease across yields. Late output cannot publish events or update
  usage. All disposal callers share one completion and its cleanup failure. Cancelled turns retain
  cancellation; a failed local termination also faults the session with its PID and remains visible
  to disposal. Failed opening preserves both the opening and cleanup errors when both occur.
- `SupportsAgentToolUse` is false, `ToolExecutionOwnership` is Provider, `ResourceObservation` is
  Unsupported and resource operations throw. Interrupt, steering, fork and context capture, status,
  replacement and compaction handoff are unsupported.

The ConsoleTester `agy-probe` command runs bounded live qualification cases; see
[Subscription and AWS smoke tests](#subscription-and-aws-smoke-tests).

`IsAuthenticatedAsync` performs the non-consuming `agy models` check, so an installed but signed-out executable is not reported as authenticated. Run `agy` interactively to sign in or refresh the CLI session. Antigravity owns its credential and session storage in the operating system's native keyring; Darkspyre.Inference.Connect.LLM does not inspect it.

Account usage runs `/usage` as a standalone headless CLI command. Normalized model quota fields are
returned when present; the sanitized CLI report is also retained in `Summary` because Antigravity
documents the slash-command response as human-readable text rather than a versioned JSON schema.

## Apple Foundation Models on device

`AddLlmConnect` registers `apple-foundation-models` with self-described capabilities and setup.
Use `LlmTransport.AppleFoundationModels`, endpoint `darkspyre-apple-fm` (or an absolute helper
path), model `apple-system-default` and no credential/headers. The host supplies the connection
through `ILlmConnectionSource` as usual. `Bind` uses the same adapter and semantic tool history.

Setup exposes the helper path, a local readiness check, local model discovery and an explicit
short generation test. It has no API-key, OAuth or CLI-login step and no potential API billing.
Apple Silicon, macOS 26+ and a ready Apple Intelligence system model are required. Provider
metadata remains discoverable on other platforms without loading native code. The runtime has
been exercised on macOS 26.7; oldest-OS and signed clean-machine certification remain release gates.
See the [helper build/test instructions and process limits](tools/AppleFoundationModelsHost/README.md).

Supported: text/instructions, user/assistant semantic history, streaming or one complete
`respond(to:)` response when `SupportsStreaming` is false, temperature in [0,1],
provider-enforced output caps, host-executed chat tools and existing managed agent sessions.
Each model operation owns one helper process, which starts and ends as
[Wrapped CLI processes](#wrapped-cli-processes) describes. Tools and persistence remain with the host.
System/developer messages inside history, prompt images/documents, cloud/PCC, custom weights,
account usage, reasoning controls and native resume/fork are unsupported. Put instructions in
`Instructions`. Full input occupancy and token usage remain unknown.

Tool choice supports Auto and None. Required/Specific and `AllowParallelToolCalls = false` fail
before generation; prompt wording cannot enforce them. None disables active tools while retaining
historical calls/results. Tool results use the existing text projection and omission reports.
Missing, duplicate or mismatched results fail before another generation. No hidden provider state
is needed. Model selection of tools is nondeterministic; multiple successive batches are handled
by the same managed loop and its host execution policy.

Structured output supports closed objects, required/optional nested fields, strings/booleans,
Int64/finite-double values and inclusive bounds, string enum/const, homogeneous arrays/count
bounds, and acyclic local `$defs` references (depth at most 64, at most 16,384 expanded nodes). Unsupported constraints fail explicitly. Explicit
`LocalValidation` fallback allows unconstrained text plus final validation; arbitrary JsonObject
fallback is unavailable. Original schemas always validate final JSON and returned tool arguments.

Structured streaming emits replacement `LlmStructuredOutputSnapshotEvent` objects, not JSON text
deltas. The agent equivalent carries thread/turn/item identity. Final `Text` contains the full JSON.
Apple does not report natural-stop versus truncation, so normal results use terminal
`LlmCompletionStatus.Unknown`; managed agents retain the response and end with reason/status
Unknown. Do not infer completion from valid JSON or automatically continue/retry an Unknown result.
Known tool handoff remains RequiresAction; refusal, cancellation and failures remain distinct.
`Diagnostics.HttpStatusCode = 200` is the existing process-adapter success sentinel, not an HTTP call.

The [design and verification record](docs/apple-foundation-models-design.md) lists runtime evidence
and remaining release qualifications. No helper binary, Apple SDK or upstream C-bridge source is
included in the Connect NuGet.

## SelfHost transport: in-process engines

`LlmTransport.SelfHost` runs a model that the host loads in its own process, such as a Tensor Qwen
engine. The host implements `ILlmSelfHost` and registers it with `AddLlmConnectSelfHost()`:

```csharp
services.AddLlmConnect();
services.AddLlmConnectSelfHost();
services.AddSingleton<ILlmSelfHost, MyEngineHost>(); // the host's engine map
```

A SelfHost connection's `Endpoint` is an engine ID from `ILlmSelfHost.ListEnginesAsync`, and it has no
credential. Several connections can name one engine. Streaming, model listing and agent sessions go to
the engine returned by `GetInferenceAsync`, which is Common's `ILlmInference`; an engine that is unknown
or not loaded fails with `InferenceException` and `ModelUnavailable`. `GetCapabilities` projects the
loaded engine's capabilities for operations the adapter accepts, and does no I/O, because Connect reads
a transport's capabilities synchronously. Streaming, final streamed usage and running usage flags come
from the engine. Native file and caller-owned resource references remain unsupported; SelfHost reports
`SupportsFileReferences = false` even when the engine declares support. Binary prompt attachments
retain the engine's image/document capability gates. An unloaded engine declares no usage or tool support.

Connect keeps the connection's catalog in its shared model cache. A provider listing refreshes the
selected engine's catalog, so explicit Connect refresh also replaces any list the engine keeps.

Generation and request accounting reject nonempty connection or request `ProviderOptions`, connection
`AdditionalHeaders`, and connection or model capability overrides. SelfHost passes only typed Common
request controls to the engine. An engine owns its capabilities; connection overrides do not change
them. Settings cannot save these unsupported options. Model and capability discovery remain available
so an invalid saved connection can be inspected and repaired.

Agent sessions run on the managed agent lane, which executes host tools; context estimates count the
request text with Connect's local tokenizer. Connect references Common only, never the engine library.
The settings offer an **Engine** choice listed from the host's engines and a **Model** choice listed
from that engine; both are required to save. Checking and testing are local verification.

## Console smoke tester

`tools/Darkspyre.Inference.Connect.ConsoleTester` exercises the public Darkspyre.Inference.Connect.LLM facade directly without a shim or
consuming application. `providers` lists the registered adapters; `setup --provider <id>` displays
their own setup steps, fields, requirements and action effects without running those actions.
Canonical provider IDs and the short aliases `codex|claude|antigravity|bedrock|agentcore|apple`
are accepted. Adapter metadata owns names, endpoints and model defaults. Existing aliases retain
their credential-store identities. Credential entry/sign-in commands retain the subscription/AWS
flows described below; listing another adapter does not provision its credentials.
Codex uses host-owned device OAuth stored in `Darkspyre.SecureStore`; Claude and Antigravity keep
CLI-owned credentials. The tester also stores host-owned AWS profile/region configuration.

### Apple console harness

From `Connect/`, build the separate development helper and inspect its provider-owned setup:

```bash
xcrun swift build --package-path tools/AppleFoundationModelsHost
APPLE_HELPER="$PWD/tools/AppleFoundationModelsHost/.build/debug/darkspyre-apple-fm"
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- providers
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- setup --provider apple
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- status --provider apple --helper "$APPLE_HELPER"
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- models --provider apple --helper "$APPLE_HELPER"
```

Apple requires no login or vault credential. `--helper` accepts an executable name on `PATH` or an
absolute path, and is valid only with Apple. Without it, the adapter's default executable is used.
`status` runs the settings manager's prerequisite check, with actionable helper/model readiness
results. `prompt` and `agent` also check readiness. Apple's model defaults to the adapter's
`apple-system-default`; an explicit `--model` is passed through normally.

```bash
echo 'Reply with hello.' | dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  prompt --provider apple --helper "$APPLE_HELPER"
echo 'Call darkspyre_inference_connect_echo with text hello and report the result.' | \
  dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- agent --provider apple --helper "$APPLE_HELPER"
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  apple-probe --provider apple --helper "$APPLE_HELPER" --repeat 2
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  apple-probe --provider apple --helper "$APPLE_HELPER" --case agent --repeat 5
```

`apple-probe` performs explicit synthetic on-device generation. Select `--case
all|text|structured|tools|agent|cancel` (default `all`), `--repeat 1..20` (default 1), and
`--timeout-seconds 1..600` per case (default 180). Cases execute sequentially; each repetition
starts fresh requests/sessions. There is no retry of a failed case. The managed agent is limited
to four model iterations and exposes only an in-memory synthetic lookup tool.

| Case | Checks |
| --- | --- |
| `text` | Complete and streamed text, terminal `Unknown`, no invented usage/finish reason |
| `structured` | Replacement snapshots without text deltas; final original-schema validation |
| `tools` | Two identical-argument calls with distinct IDs, reversed host results, replay with tools disabled |
| `agent` | One host lookup, settled result retention, no duplicate call-ID execution, final retained response |
| `cancel` | Cancel after a text chunk, await stream cleanup, then complete a new request |

Each case reports `PASS`, `FAIL` or `SKIP`, iteration and elapsed time, followed by totals.
Exit codes are 0 for all passed, 1 for any failure, 3 for unavailable prerequisites, and 130
for caller cancellation. A failed prerequisite operation returns failure rather than a skip.
Unknown termination is expected for Apple; it does not certify a natural stop. Missing snapshots,
unexpected tool selections and native continuation failures remain failures, even if later runs
pass. Reports omit prompts, model output, raw provider errors and credentials; agent failures
include the typed terminal reason and settled tool counts when available. Ctrl+C cancels the
active case and stops subsequent runs. Interactive `prompt` and `agent` commands return failure
on a failed or missing terminal event and accept Apple's normal `Unknown` terminal.

These live checks are separate from deterministic tests. In particular, successful runs do not
resolve the intermittent native continuation issue tracked in DSA-59. The helper's signing and
clean-machine/oldest-OS distribution qualifications remain open.

### Subscription and AWS smoke tests

Sign in to the direct ChatGPT adapter (CLI login alone is insufficient):

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  login --provider codex
```

Check the saved credential or load the provider's models:

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- status --provider codex
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- models --provider codex
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- usage --provider codex
```

Stream a prompt through Darkspyre.Inference.Connect.LLM. Interactive use reads one line; redirected input can contain the complete prompt:

```bash
echo 'Hello' | dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  prompt --provider codex --model gpt-5.6-terra

echo 'Summarize the attached report.' | dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  prompt --provider claude --model sonnet --attach-document ./report.pdf
```

Smoke-test managed Codex. The tester exposes a harmless `darkspyre_inference_connect_echo` tool; host tool security remains the host's responsibility:

```bash
echo 'Call darkspyre_inference_connect_echo with text Hello, then reply with its result.' | dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  agent --provider codex --model gpt-5.6-terra
```

Run the bounded live scenarios (these consume model usage):

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- codex-probe --provider codex --model gpt-5.5
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- managed-probe --provider codex --model gpt-5.5
```

The first checks direct tools, attachments and state replay. The second checks the managed loop,
JSON capture/reopen, fork and low-cap host compaction. Use a model returned by `models`.
[Recorded results](docs/managed-agent-verification.md) distinguish live proof from deterministic tests.

Pass `--attach-image path`, `--attach-document path`, `--effort <level>` (a reasoning level name such as `high` or `ExtraHigh`), or
`--schema path/to/schema.json` to exercise those request features. Attachment flags may be repeated.
Prompt text is sent to the tester over standard input and dispatched through the selected provider;
it is not placed in process arguments. Antigravity rejects attachment options because its headless
protocol is text-only.

Claude uses the same commands with `--provider claude`:

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- login --provider claude
```

Claude subscription prompts require the official Claude CLI to be installed and signed in. The
tester creates an `AnthropicClaudeCli` connection with `Credential = null`; Darkspyre.Inference.Connect.LLM does not read
or store the CLI credential. Enter `sonnet`, `opus`, another alias, or an exact model ID manually
because the tester does not configure the optional HTTP catalog credential.

Antigravity uses `--provider antigravity` (or `agy`). Run `agy` interactively once to sign in; the
tester does not request or store a Google authorization code. `agy-probe` runs explicit live cases
through the native session: `text`, `structured`, `native-tools`, `session`, `resume`, `controls` and
`cancel`. It works in a temporary workspace with one fixture, reports the CLI version and the number
of turns sent, and never retries. The `usage` command is available for all three subscription providers:

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- usage --provider claude
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- usage --provider antigravity
```

Configure Bedrock with a named AWS profile and region. Only those identifiers are stored in SecureStore; AWS credentials remain in the standard AWS credential chain:

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  login --provider bedrock --profile brandon-cli --region us-east-1
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- models --provider bedrock
echo 'Hello' | dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  prompt --provider bedrock --model amazon.nova-micro-v1:0
```

AgentCore uses the same profile/region storage. Pass the deployed Harness ARN on every console
invocation because this small tester deliberately does not own a connection database:

```bash
dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  login --provider agentcore --harness-arn "$HARNESS_ARN" \
  --profile brandon-cli --region us-east-1

echo 'Call darkspyre_inference_connect_echo with text Hello, then reply with its result.' | \
  dotnet run --project tools/Darkspyre.Inference.Connect.ConsoleTester -- \
  agent --provider agentcore --harness-arn "$HARNESS_ARN" --model default
```

The AgentCore tester exposes only `darkspyre_inference_connect_echo`: Harness prompt and tool-result attachments are
unsupported by the current AWS wire contract.

## Verification

```bash
dotnet restore Darkspyre.Inference.Connect.slnx
dotnet build Darkspyre.Inference.Connect.slnx --no-restore
dotnet test Darkspyre.Inference.Connect.slnx --no-build --no-restore
dotnet format Darkspyre.Inference.Connect.slnx --verify-no-changes --no-restore
dotnet pack Darkspyre.Inference.Connect.slnx -c Release --no-restore
dotnet list Darkspyre.Inference.Connect.slnx package --vulnerable --include-transitive
```

Releases are immutable tags such as `v11.3.1-rc.1`. `Common`, `Connect`, `Tensor`, and `UI.Settings` are built and
published in tandem from the approved local release environment to the private GitHub Packages feed;
GitHub Actions is not the release pipeline.
