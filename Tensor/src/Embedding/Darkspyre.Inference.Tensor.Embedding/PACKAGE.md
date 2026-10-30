# Darkspyre.Inference.Tensor.Embedding

Text embeddings from GGUF sentence encoders inside the application's process, through TensorSharp. The engine
implements Common's `IEmbeddingInference` and returns unit-length vectors.

## Install

```bash
dotnet add package Darkspyre.Inference.Tensor.Embedding
```

## Register

Construct `TensorEmbeddingInference` over a `TensorModelPackManager`, or call `AddTensorEngines()` from
`Darkspyre.Inference.Tensor.Registry`, which registers `EmbeddingEngineProvider`. Models arrive by import: the package
has no built-in pack.

## Example

```csharp
await using var embeddings = new TensorEmbeddingInference(packs);
await embeddings.StartAsync();
await embeddings.LoadAsync(modelKey); // Loaded or Failed arrives through EngineStatusChanged.

EmbeddingResult result = await embeddings.EmbedAsync(new EmbeddingRequest
{
    ModelId = modelKey.ModelId,
    Kind = EmbeddingInputKind.Query,
    Inputs = [new TextEmbeddingInput("Where did the meeting move?")]
});
```

The engine formats queries itself; documents are embedded as given. A query request runs before waiting document
requests.

## Settings

`EmbeddingSetup.CreateSettingsManager` opens the engine's settings: the model with its install state, "Runs on", the
fields that import a model, and the download, check, load and backend-check actions.

## Limits

- A model must be a BERT embedding GGUF (mean, CLS or last-token pooling) with 1,024 dimensions and at least an
  8,192-token context. A Qwen3-Embedding pack is reported as not supported.
- The engine runs on managed CPU and GGML CPU, and shares the process's GGML runtime through
  `Darkspyre.Inference.Tensor.Ggml`.
- Text only: a request with a media input fails with `UnsupportedFeature` before the model runs.
- An input over the limit fails the request with `EmbeddingInputTooLongException`, unless the request sets `Truncate`.
- A smaller requested size (256) keeps the vector's leading values and scales them back to unit length.
- Vectors with different `ModelFingerprint` values are not comparable.
