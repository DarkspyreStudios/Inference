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
service without falling back to untyped dictionaries. Rich provider-specific operations remain on
their owning packages; Common is the portable inference lane, not a least-common-denominator
replacement for implementation management.

## Provider setup

`Darkspyre.Inference.Common.Setup` supplies UI-neutral definitions, typed fields, callable actions,
validation, safe status observations and a version-checked host commit boundary. It does not store
settings/secrets, execute an application loop, or wrap inference requests in a generic payload.
Text, Image, Audio and Decision grouping comes from the implementation's declared `InferenceTask`
values; the three Audio tasks remain distinct.

The [compile-checked generic editor example](examples/Darkspyre.Inference.Setup.Examples/README.md)
uses one field/action driver for real Connect and Tensor sessions, including speech/audio. Hosts retain
operation lifetime, user consent, storage and rendering. Field edits are revision-checked; cancelled
edits never commit. Unknown observations are nullable, separate from false, stale and not applicable.
These contracts are included in the synchronized **11.4.1-rc.1** package family. Connect, Tensor and
UI.Settings declare exact Common NuGet dependencies; none embeds the Common DLL. This release
targets .NET 11 RC; the shared version retains a prerelease suffix while the UI's ASP.NET dependency
is a release candidate.

### Settings corrections in 11.3.1-rc.1

`SetupSnapshot.IsDirty` reports provider-owned draft changes against the session's starting values,
including explicit secret changes. Checks, progress and revision changes alone do not make a draft
dirty; reverting edits restores clean state. `CommitOutcomeUncertain` separately tells the host to
read back persisted state before attempting another save. Neither signal authorizes an automatic retry.

`Snapshot.Interaction` is the authoritative current authorization/prerequisite interaction, including
fresh revision-bound controls. Browser callback input is one-way and identified by authorization
attempt; its value is not echoed in snapshots. Choice fields declare search, paging and refresh through
`SetupChoiceCapabilities`, independently of manual-entry permission. Decimal fields declare their
step as well as limits. Common remains UI-framework-neutral; renderers must also gate Save on any
locally invalid or unapplied input which has not reached the typed provider draft.
