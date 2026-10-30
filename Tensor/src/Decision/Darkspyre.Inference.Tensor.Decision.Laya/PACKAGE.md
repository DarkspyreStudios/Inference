# Darkspyre.Inference.Tensor.Decision.Laya

Typed decisions with Laya-family models inside the application's process. The ModernBERT encoder and the typed
decision heads run through ONNX Runtime: Core ML on macOS and the CPU provider elsewhere. Tokenization, sequence
framing and calibration run in C#. The engine implements Common's `IDecisionInference` and
`IDecisionInputMeasurement`.

## Install

```bash
dotnet add package Darkspyre.Inference.Tensor.Decision.Laya
```

## Register

Construct `LayaDecisionInference` over a `TensorModelPackManager`, or call `AddTensorEngines()` from
`Darkspyre.Inference.Tensor.Registry`, which registers `LayaEngineProvider`. The package has no built-in pack: the
application registers the Laya packs it offers, built with `TensorDecisionModels.LayaRuntimeArchitecture`.

## Example

```csharp
var catalog = new TensorModelCatalog([customPack]);
using var packs = new TensorModelPackManager(catalog, packDirectory);
await packs.InstallAsync(customPack.Key, cancellationToken: cancellationToken);

await using var laya = new LayaDecisionInference(packs);
await laya.StartAsync(cancellationToken);
await laya.LoadAsync(customPack.Key, cancellationToken); // Loaded or Failed arrives through EngineStatusChanged.
DecisionInferenceResult result = await laya.DecideAsync(
    request with { ModelId = customPack.Key.ModelId }, cancellationToken);
```

`MeasureAsync` reports how many tokens a request's state encodes to and how many each question keeps, without running
the model, so the application can tell when a question truncates the state.

## Settings

`LayaSetup.CreateSettingsManager` opens the engine's settings over the application's model list: the model with its
install state, "Runs on", the fields that import a model from a file, folder or URL, and the download, check, load and
backend-check actions.

## Limits

- A pack must provide the `model` ONNX graph, `tokenizer` and `agent-config` artifacts, and keep the Laya architecture,
  typed heads and tokenizer conventions. A pack that declares CUDA is refused.
- On Core ML the graph compiles on every load, so loading a large checkpoint takes tens of seconds. Load in the
  background and keep the instance loaded.
- A request with more options than the graph's option slots fails with `InvalidRequest`.
- A state longer than a question's room is truncated without an error; measure first to detect it.
