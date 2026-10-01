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

`InferenceJson` supplies Common-owned source-generated metadata for closed LLM, decision and image
request/result contracts, capabilities, tool output, continuation state, concrete stream events and
data-only setup observations, choices and outcomes. Pass its typed metadata to `JsonSerializer`.
The metadata preserves the existing property names, numeric enum values, binary encodings,
required members, unknown-member rules and decision/tool-output discriminators. It installs no
reflection resolver. Caller-defined types require caller-owned metadata.

The stream-event base has no JSON discriminator. Serialize each concrete event with its matching
metadata; this package does not invent a stream envelope. Callback-backed setup fields and abstract
setup interactions are not transport contracts. `InferenceImplementationDescriptor` and the
value-tuple sizes in `ImageCapabilities` have no round-trip JSON representation in this metadata
surface. Generated metadata does not change those contracts or establish native runtime retirement.

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
