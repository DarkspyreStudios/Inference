# Darkspyre.Inference.Common

Shared, implementation-neutral contracts for in-process Tensor inference and out-of-process Connect
providers. The package contains semantic request, response, capability, progress, provenance, and
failure types. It does not select an implementation or provide automatic fallback between local and
external inference. The current synchronized package family targets .NET 11.

Applications choose an implementation explicitly and work with typed contracts:

```csharp
ILlmInference inference = connectProviders.Bind(connectionId);
LlmInferenceResult result = await inference.CompleteAsync(new LlmInferenceRequest
{
    ModelId = modelId,
    Messages = [new LlmMessage("user", prompt)]
}, cancellationToken);
```

The same `ILlmInference` and `IImageInference` contracts are implemented by the corresponding Tensor
packages. `IDecisionInference` adds a typed decision lane with distinct choice, ordinal-score, and
boolean question and answer records; its polymorphic JSON discriminators can be projected by a host
service without falling back to untyped dictionaries. An implementation that can also measure a
request without deciding it implements `IDecisionInputMeasurement`: `MeasureAsync` returns the
tokens the request's state encodes to and, per question, the most state tokens that question's input
keeps. A question truncates the state when the first exceeds the second. Rich provider-specific
operations remain on their owning packages; Common is the portable inference lane, not a
least-common-denominator replacement for implementation management.

## JSON metadata

`LlmExplicitCacheReference` carries a native cache resource name, model, opaque scope and optional
observed expiry. `InferenceJson.LlmExplicitCacheReference` exports its data-only metadata. The
reference grants no authority and is not yet a generation-request selection.

`LlmClientToolDefinition`, `LlmClientToolCall` and `LlmClientToolResult` are closed
function/shell data unions. Existing function record names and constructors remain intact.
Shell data preserves ordered commands and outputs, nullable raw 64-bit timeout milliseconds,
nullable raw 32-bit output length, optional stdout/stderr and explicit exit/timeout outcomes.
Lists copy their inputs at construction and `with` assignment. Skills contain caller paths;
constructing or serializing them does not read files or execute commands.

The existing request, message and result collections still accept only function records.
The standalone shell metadata is an inert contract foundation, not an execution capability.
`InferenceJson` exposes every fixed shell record and each mixed union with its exact type-name
getter. Union discriminators are `function` and `shell`; outcomes use `exit` and `timeout`.

`InferenceJson` supplies Common-owned source-generated metadata for closed LLM, decision and image
request/result contracts, capabilities, tool output, continuation state, concrete stream events and
data-only setup observations, choices and outcomes. Pass its typed metadata to `JsonSerializer`.
The metadata preserves the existing property names, numeric enum values, binary encodings,
required members, unknown-member rules and decision/tool-output discriminators. It installs no
reflection resolver. Caller-defined types require caller-owned metadata.

The stream-event base has no JSON discriminator. Serialize each concrete event with its matching
metadata; this package does not invent a stream envelope. Callback-backed setup fields and abstract
setup interactions are not transport contracts. `InferenceImplementationDescriptor` and the
callback-backed setup interactions have no portable JSON representation in this metadata surface.
`ImageCapabilities` uses the named `ImageSize` contract, not value tuples. Its exact typed metadata,
`GeneratedImage`, and `ImageGenerationStage` are exported alongside the unchanged Image request,
result and progress getters. Generated metadata does not establish native runtime retirement.

`LlmUsage` preserves provider-reported decimal USD costs and independent hosted-tool/source counts.
Its named hosted counts snapshot their input; negative hosted counts become unknown at assignment
and owned JSON import. `InferenceJson.LlmUsage` and `InferenceJson.LlmHostedToolKind` expose the exact
typed metadata, also composed into existing result and usage-event graphs. These facts do not enable
provider features or infer totals from partial observations.

Hosted tools use a closed `type` union, separate from caller-executed functions. Requests carry
ordered `HostedTools` and optional positive `MaxHostedTurns`; capability sets are frozen and empty
by default. Hosted calls retain actual status, optional input/output and ordered source/log/error
facts. Citation offsets are nullable UTF-16 positions in emitted text, with an exclusive end.
Unknown positions and optional output remain absent, not inferred. Concrete hosted/citation events
have exact-name `InferenceJson` getters; their event base has no discriminator. Live remote-MCP
authorization is borrowed and refuses non-null owned JSON read/write without invoking its callback.
Null authorization roundtrips; explicitly serialized header data remains secret data, while
remote-MCP diagnostics are redacted. Common's union rejects caller-defined variants; Connect owns
the fixed xAI search extension and its extension-aware portable metadata.
Owned hosted metadata rejects missing observation identities, kind/phase facts and image bytes.
Explicit null image bytes are not an observed empty image; explicit empty bytes remain valid.
The new phase/action enum roots reject undefined values without changing other enum policies.
Caller-generated closed envelopes can compose the owned resolver and its fixed converters; the
image-byte refusal applies only to the hosted image `Data` property, not every byte-memory field.

## Session and workspace resources

`Darkspyre.Inference.Common.Resources` defines immutable content references, observed descriptors,
ordered selection snapshots and primitive operation-failure facts. A reference identifies workspace
content but grants no access. A selection version belongs to one logical workspace/session, not a
provider thread. Selection snapshots copy their ordered unique membership; empty means no selected
resources. Nullable byte counts distinguish unknown from zero. Constructors reject missing IDs,
negative counts and malformed selection membership; access implementations also validate inputs,
authorization and current scope at each operation boundary.
Positional-record `with` assignments can bypass scalar constructor checks, including changing a
selection's scope without changing its copied membership. Data objects are not authorization or
intrinsic-validity proofs: operation boundaries must revalidate every caller-supplied value, including
`with` copies. Owned JSON import checks constructor inputs and required facts; serialization alone
does not validate authorization or the current store version.

`IInferenceResourceAccess` is borrowed caller-owned authority. Its single-result methods return
`Task<T>`. Opened read/write handles are operation-owned `IAsyncDisposable` values and own their
streams. Dispose the handle, not its stream separately. The caller implements storage, retention and
authorization; this package supplies no store or provider materialization.

`SelectAsync` atomically appends authorized committed references. `ReplaceSelectionAsync` is an
explicit revision-checked replacement; removing membership does not delete content. A write handle's
single commit publishes complete immutable content. With `SelectForSession = true`, content and
additive selection must publish atomically in the same caller store transaction; separate commit then
select is not equivalent. Null/default options do not select content. These are implementation
obligations of the caller-owned authority, not an implementation supplied by Common.

Cancellation or a lost acknowledgment does not prove rollback. Primitive content and selection
outcomes preserve unknown effects independently. Reconcile the known reference and current selection
after the attempt is terminal, without automatic retry or inferred historical outcomes. Runtime
operation exceptions preserve the original inner exception; cancellation remains an
`OperationCanceledException`. Exceptions, authority and handles are not portable data.

`InferenceJson` exports typed metadata named after each resource data contract, including the scoped
selection version, commit result and failure facts. Required scope, revision and membership fields
cannot disappear into defaults during JSON import. Its metadata options also contain fixed internal
refusal converters for the three resource interfaces: null is allowed, but non-null values refuse
before any collaborator callback. A closed caller-owned JSON context can compose those exact
converters from `InferenceJson.InferenceResourceReference.Options.Converters` by the known interface
type's `CanConvert` match before construction. This is not a global serializer policy or support for
arbitrary caller CLR graphs.

LLM messages use one ordered `LlmMessageAttachment` union: `LlmAttachment` carries binary data,
`LlmFileReference` carries a provider-native file ID, and `LlmResourceAttachment` carries a workspace
reference. Their JSON discriminators are `binary`, `file` and `workspace-resource`.
`LlmToolOutputSessionResource` adds a workspace reference to tool output without changing the
existing `resource` URI/body variant. `InferenceJson` exports each concrete attachment and the base
union under its exact type name. No reference contains invented bytes or grants access.

`LlmInferenceRequest.Resources` borrows caller authority. Non-null authority refuses owned JSON
serialization and deserialization; null remains portable. Request diagnostics do not traverse it.
`SupportsFileReferences` defaults to false and describes native file input, not automatic workspace
materialization. Current built-in LLM routes reject unimplemented reference inputs before provider
effects. These contracts do not yet establish automatic provider switching.

## Media source data

`Darkspyre.Inference.Common.Media` distinguishes binary input, provider-fetch URLs, provider-native
file IDs and portable workspace resource references. Binary input memory is borrowed, not cloned;
callers must not mutate it during an entered request. The DTOs and their JSON codecs never fetch
URLs, read resources, refresh authority or upload bytes. File and URL variants contain no fabricated
empty binary data. Native file IDs grant no access or ownership; `InferenceResourceScope` describes
the actual endpoint/route/account authority of native receipts, not portable content ownership.
`InferenceResourceFailure` composes the existing resource operation facts without live exceptions.

`InferenceJson` exports exact type-name metadata for all nine media data roots. The source union uses
the closed `type` discriminators `binary`, `url`, `file` and `resource`; unknown variants refuse rather
than invoking reflection or an extension registry. JSON decoding creates its own byte memory, while
constructing a binary source preserves caller memory. These are standalone, data-only foundations,
not provider bindings or automatic resource resolution. Image requests retain ordered source data;
Connect ComfyUI supports zero/one binary reference or a logical resource resolved through borrowed
access. Supra refuses supplied references before backend entry. Other ComfyUI source variants and
multiple references refuse before source reads or upload.
Operation boundaries validate caller-authored IDs, URLs, scope and source support before effects.

Image capability sizes use `ImageSize` with actual reference-kind support and nullable known reference
limits. A generated image retains bytes plus optional native file identity, committed resource
descriptor and observed dimensions. Its required `ContentType` fact may be null when MIME is unknown;
unknown MIME is not inferred from a workflow default or file extension.

## Provider authorization

`IProviderAuthorization.GetAsync` acquires authorization for one operation. The returned
`ProviderAuthorizationHeader` carries its scheme and secret value without exposing credential storage.
Its diagnostic string redacts both fields. Header values are not log, diagnostic or persistent data.
The authorization implementation owns credential acquisition; a consumer borrows it.

## Sampling controls

`LlmInferenceRequest` carries nullable `TopP`, `TopK`, `Seed`, `PresencePenalty` and
`FrequencyPenalty`, plus `StopSequences` (empty by default). Null leaves the provider default intact.
An explicit unsupported control fails before generation. Top-p is finite and in (0, 1]; top-k is
positive; seeds are non-negative. Penalties are finite, with ranges owned by the selected adapter.
Stop strings are non-empty. A seed does not promise deterministic output.

The corresponding capability flags default to false. `MaxStopSequences` is zero when unsupported
and `int.MaxValue` when the adapter imposes no limit. Connect bindings and SelfHost preserve all
controls and capabilities across the portable boundary. Qwen requires a positive temperature for
top-p, top-k and seed; stops and penalties also work during greedy decoding.

## Prompt caching

`LlmInferenceRequest.PromptCache` carries an advisory `LlmPromptCacheHint`. `Prefix` asks to cache
tools and instructions. `Conversation` also includes the conversation prefix through its last message
and is the default scope. `Default` uses the provider's standard lifetime. `Extended` asks for its
longer lifetime, which can increase cache-write charges.

`LlmCapabilities.PromptCache` declares automatic caching, hint acceptance and supported retentions.
Unsupported implementations ignore the hint and report `AcceptsHints = false`. An unsupported
retention falls back to `Default`. A hint does not promise a cache hit or create a billed cache resource.
Connect bindings and SelfHost preserve the hint and capability declaration.

## Streaming and terminal results

`LlmStructuredOutputSnapshotEvent.Content` replaces the previous JSON snapshot; it is not
an append-only text fragment or a final schema validation. Use the terminal result's `Text`
and `StructuredOutput` for the final representation and validation. Ordinary text uses
`LlmTextDeltaEvent`.

`LlmCapabilities.SupportsReasoningText` describes separate public `LlmReasoningDeltaEvent` output.
It is independent of `ReasoningLevels`, which describe control over reasoning effort. The flag
defaults to false. Engines set it only when they implement public reasoning output. SelfHost and
the portable Connect binding preserve the selected adapter's flag.

`LlmCapabilities.SupportsStreamingUsage` describes final usage on a streamed completion. It defaults
to false and is independent of running usage observations. Connect and SelfHost preserve the selected
adapter or engine's declaration.

`LlmUsageDeltaEvent.Cumulative` reports running usage for the entire call. Each observation replaces
the previous observation; never add them together. Null fields are unreported, not zero. Completion
usage is authoritative and can differ from the last observation. An adapter emits changed observations
only while streaming, when `LlmCapabilities.SupportsUsageDeltas` is true, and never after completion.
Non-streaming responses report only final usage. Tensor engines report this capability as false
unless the selected engine implements running usage.

`LlmCompletionStatus.Unknown` is terminal: the implementation returned output but cannot
classify a natural stop versus truncation. It does not mean failure or authorize a retry.
Check validation separately and leave unreported finish reasons and usage unknown.

## Provider setup

`Darkspyre.Inference.Common.Setup` supplies UI-neutral definitions, a settings object with typed fields,
a settings manager that runs actions, lists choices and saves, validation, safe status observations and a
version-checked host commit boundary. It does not store
settings/secrets, execute an application loop, or wrap inference requests in a generic payload.
Text, Image, Audio and Decision grouping comes from the implementation's declared `InferenceTask`
values; the three Audio tasks remain distinct.

The [compile-checked generic editor example](examples/Darkspyre.Inference.Setup.Examples/README.md)
uses one field/action driver for real Connect and Tensor settings, including speech/audio. Hosts retain
operation lifetime, user consent, storage and rendering. A field edit changes only the settings object.
Nothing reaches the host until `SaveAsync`. Unknown observations are nullable, separate from false,
stale and not applicable.
These contracts are included in the synchronized **11.14.0-rc.1** package family. Connect, Tensor and
UI.Settings declare exact Common NuGet dependencies; none embeds the Common DLL. This release
targets .NET 11 RC; the shared version retains a prerelease suffix while the UI's ASP.NET dependency
is a release candidate.

### Settings objects and managers

A provider manager's `OpenAsync` returns an `InferenceSettings` object for a provider type, blank or
from a saved entry. The object is plain data. A field edit changes only that object and never calls a
provider or writes storage. `Steps` and `Actions` are rebuilt from the current values on each read, so
show, hide and enable rules run locally.

`IInferenceSettingsManager` is the service that acts on the object. `RunAsync` runs one enabled action
against the current values. `QueryChoicesAsync` lists choices for one choice field. `SaveAsync`
validates the object and calls the host's typed commit target once. One object saves once; a later
edit opens the entry again at its new saved version.

The shared `InferenceSettingsManager` operations are virtual. A derived lifetime owner can admit and
drain calls around the existing base workflow, including calls through `IInferenceSettingsManager`
or the base class. After fencing and draining all admitted operations, it can call the protected
`DetachCommitTarget` to release its borrowed commit target. Detachment does not dispose the target
or implement retirement for the derived manager. A subsequent base save rejects before creating
changes or reporting an uncertain commit. Ordinary action, choice, cancellation and save-result
policies remain in the base workflow.

`HasUnsavedChanges` reports changes against the opened values, including explicit secret changes.
Checks and progress do not make the object dirty; reverting edits restores clean state. `CanSave` is
true when every visible required field has a value and any required sign-in has succeeded.
`SaveOutcomeUncertain` tells the host to read back persisted state before opening another edit.
Neither signal authorizes an automatic retry.

`Interaction` is the current authorization or prerequisite interaction. Its Continue, Cancel and
Recheck actions run through `RunAsync` by ID. Browser callback input is one-way; its value is not
echoed in field values. Choice fields declare search, paging and refresh through
`SetupChoiceCapabilities`, independently of manual-entry permission. Decimal fields declare their step
as well as limits. A manager whose settings follow a running engine implements
`IInferenceSettingsStatusSource`: `StatusChanged` reports a change, and `RefreshStatusAsync` reads it
into the object. Common remains UI-framework-neutral; renderers must also gate Save on any locally
invalid or unapplied input which has not reached the settings object.
