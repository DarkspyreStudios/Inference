# Darkspyre.Inference.Common

The typed contracts every Darkspyre.Inference package shares: requests, results, streaming events, capabilities,
failures and provenance for text, image, decision, embedding and speech inference, plus the provider setup model the
setup editors render. A Connect provider and a Tensor engine implement the same contracts, so application code that
calls a contract does not depend on where the model runs.

## Install

```bash
dotnet add package Darkspyre.Inference.Common
```

## Register

The package needs no registration. It holds contracts and data only: it selects no implementation and never falls
back from one implementation to another. The application references the Connect or Tensor package that implements a contract and calls it
through the contract.

## Example

```csharp
ILlmInference inference = implementation;
LlmInferenceResult result = await inference.CompleteAsync(new LlmInferenceRequest
{
    ModelId = modelId,
    Messages = [new LlmMessage("user", prompt)]
}, cancellationToken);
```

`IImageInference`, `IDecisionInference` and `IEmbeddingInference` follow the same pattern for images, typed decisions
and embeddings.

## Settings

`Darkspyre.Inference.Common.Setup` holds the UI-neutral setup model. A provider manager's `OpenAsync` returns an
`InferenceSettings` object with typed fields, steps and actions. `IInferenceSettingsManager` runs an action against the
current values (`RunAsync`), lists a choice field's choices (`QueryChoicesAsync`) and saves through the host's typed
commit target (`SaveAsync`). A field edit changes only the settings object. `CanSave` is true when every visible
required field has a value and any required sign-in has succeeded.

## Limits

- Capability support is `InferenceSupport`: `Unknown`, `Supported` or `Unsupported`. `Unknown` is the default and does
  not mean the feature is missing; an explicit request proceeds unless the feature is `Unsupported`.
- `InferenceJson` supplies source-generated JSON metadata for the data contracts. It installs no reflection resolver,
  and types the caller defines need the caller's own metadata. Live services, such as resource access and provider
  authorization, refuse serialization.
- `ProviderAuthorizationHeader` redacts its scheme and value in its diagnostic text. Header values are never log or
  persistent data.
