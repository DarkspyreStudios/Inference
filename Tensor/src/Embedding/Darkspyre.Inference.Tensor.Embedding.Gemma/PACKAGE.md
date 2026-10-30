# Darkspyre.Inference.Tensor.Embedding.Gemma

Text and image embeddings with EmbeddingGemma 2 inside the application's process, through ONNX Runtime on WebGPU or
CPU. The engine implements Common's `IEmbeddingInference` and returns 768-dimensional unit-length vectors.

## Install

```bash
dotnet add package Darkspyre.Inference.Tensor.Embedding.Gemma
```

## Register

Construct `GemmaEmbeddingInference` over a `TensorModelPackManager`, or call `AddTensorEngines()` from
`Darkspyre.Inference.Tensor.Registry`, which registers `GemmaEmbeddingEngineProvider`.
`GemmaEmbeddingModels.EmbeddingGemma2` is the built-in pack definition.

## Example

```csharp
TensorModelPackDefinition gemma = GemmaEmbeddingModels.EmbeddingGemma2;
using var packs = new TensorModelPackManager(new TensorModelCatalog([gemma]), packDirectory);
await using var embeddings = new GemmaEmbeddingInference(packs);
await embeddings.StartAsync();
await embeddings.LoadAsync(gemma.Key); // Loaded or Failed arrives through EngineStatusChanged.

EmbeddingResult result = await embeddings.EmbedAsync(new EmbeddingRequest
{
    ModelId = gemma.Key.ModelId,
    Inputs = [new TextEmbeddingInput("The meeting moved to Thursday.", Title: "Calendar")]
});
```

A `MediaEmbeddingInput` carries PNG or JPEG bytes the caller owns until the call completes.

## Settings

`GemmaEmbeddingSetup.CreateSettingsManager` opens the engine's settings: the model with its install state, "Runs on",
and the download, check, load and backend-check actions.

## Limits

- Automatic backend selection uses WebGPU. CPU is an explicit choice; a missing GPU is reported as
  `BackendUnavailable`, with no silent CPU fallback.
- The input limit is 8,192 tokens, formatting included. Truncation is explicit and reports the inputs it cut.
- A query request runs before waiting document requests.
- Vectors with different `ModelFingerprint` values are not comparable.
