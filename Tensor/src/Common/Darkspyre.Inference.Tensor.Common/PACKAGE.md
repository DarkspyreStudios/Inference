# Darkspyre.Inference.Tensor.Common

The shared base of the Tensor engines, which run models inside the application's own process: model packs and their
verified local store, the host-controlled model lifecycle, execution coordination and the Tensor capability
interfaces (`ITensorLlmInference`, `ITensorImageInference`, `ITensorDecisionInference`,
`ITensorSpeechToTextInference` and `ITensorEmbeddingInference`). Reference an engine package to run a model.

## Install

```bash
dotnet add package Darkspyre.Inference.Tensor.Common
```

## Register

The package needs no registration. The application builds a `TensorModelCatalog` from the pack definitions of the
engines it ships and one `TensorModelPackManager` over its pack directory. `Darkspyre.Inference.Tensor.Registry`
registers the shipped engines when the application wants them as services.

## Example

```csharp
// definition is a pack definition an engine package publishes.
var packs = new TensorModelPackManager(new TensorModelCatalog([definition]), packDirectory);

// Downloads, checks and stores the pack. Run it only when the person chooses to download the model.
await packs.InstallAsync(definition.Key, progress, cancellationToken);
```

An engine then follows the same lifecycle: `StartAsync`, `LoadAsync`, the capability's inference calls,
`UnloadAsync` and `StopAsync`. Each call returns once the engine accepts it; `EngineStatusChanged` reports Loading,
Loaded, Unloaded, Stopped or Failed with its error.

## Settings

Each engine's `CreateSettingsManager` returns a `TensorSettingsManager` for the shared setup editor. Its settings
choose the model and where it runs ("Runs on"), show the model's install state, and offer **Download**, **Delete
download**, **Check files**, **Load now** and **Check backend prerequisites**. Selecting and saving never downloads or
loads a model.

## Limits

- Running a request never downloads weights or loads a model. The application installs packs and loads the model
  explicitly.
- The pack directory defaults to `LocalApplicationData/Darkspyre Studios/Inference/Tensor`; the application can supply
  another. Downloads are staged inside the pack directory, checked by length and SHA-256, and renamed into place.
- One engine instance holds one model at a time. `StopAsync` is final for an instance: create a new one to run again.
- Unload and stop wait for running requests; cancelling their token cancels those requests. Native work finishes
  before model memory is released.
