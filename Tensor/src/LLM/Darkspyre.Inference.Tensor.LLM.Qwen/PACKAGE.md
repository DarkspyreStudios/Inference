# Darkspyre.Inference.Tensor.LLM.Qwen

Text generation with Qwen-compatible GGUF models inside the application's process, through TensorSharp. The engine
implements Common's `ILlmInference`, so `Darkspyre.Inference.Connect.SelfHost` can serve it as a conversation
provider.

## Install

```bash
dotnet add package Darkspyre.Inference.Tensor.LLM.Qwen
```

## Register

Construct `QwenLlmInference`, or call `AddTensorEngines()` from `Darkspyre.Inference.Tensor.Registry`, which registers
`QwenEngineProvider`. Qwen models arrive by import: the package has no built-in pack.

## Example

```csharp
await using var qwen = new QwenLlmInference(packs, TensorBackendKind.Auto, scratchDirectory);
await qwen.StartAsync();
await qwen.LoadAsync(modelKey); // Loaded or Failed arrives through EngineStatusChanged.

LlmInferenceResult result = await qwen.CompleteAsync(new LlmInferenceRequest
{
    ModelId = modelKey.ModelId,
    Messages = [new LlmMessage("user", "Name three uses for a paperclip.")]
}, cancellationToken);
```

`scratchDirectory` is an absolute folder the application owns and empties before the engine runs; the engine writes
request images beneath it.

## Settings

`QwenSetup.CreateSettingsManager` opens the engine's settings: the model with its install state, "Runs on", the fields
that import a model from a file, folder, URL or repository, and the download, check, load and backend-check actions.

## Limits

- Automatic backend selection uses Metal, then GGML CPU, on macOS; CUDA, Vulkan, then GGML CPU elsewhere. CUDA is not
  offered on Windows ARM64. Apple MLX is a separate backend choice.
- Reasoning levels are `None` and `High`.
- Top-p, top-k and seed need a positive temperature. Stop sequences and penalties also work with greedy decoding.
- A `qwen3` GGUF is reported as not supported.
- The engine shares the process's one GGML runtime through `Darkspyre.Inference.Tensor.Ggml`.
- An application that stops reading a stream disposes its enumerator, so unloading can finish.
