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

## Provider setup (unreleased development)

`Darkspyre.Inference.Common.Setup` supplies UI-neutral definitions, typed fields, callable actions,
validation, safe status observations and a version-checked host commit boundary. It does not store
settings/secrets, execute an application loop, or wrap inference requests in a generic payload.
Text, Image, Audio and Decision grouping comes from the implementation's declared `InferenceTask`
values; the three Audio tasks remain distinct.

The [compile-checked generic editor example](examples/Darkspyre.Inference.Setup.Examples/README.md)
uses one field/action driver for real Connect LLM/Image and Tensor Qwen/Supra sessions. Hosts retain
operation lifetime, user consent, storage and rendering. Field edits are revision-checked; cancelled
edits never commit. Unknown observations are nullable, separate from false, stale and not applicable.
The version remains 11.2.0 during development; these additions are not in the published 11.2.0 package.
