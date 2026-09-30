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

## Streaming and terminal results

`LlmStructuredOutputSnapshotEvent.Content` replaces the previous JSON snapshot; it is not
an append-only text fragment or a final schema validation. Use the terminal result's `Text`
and `StructuredOutput` for the final representation and validation. Ordinary text uses
`LlmTextDeltaEvent`.

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
