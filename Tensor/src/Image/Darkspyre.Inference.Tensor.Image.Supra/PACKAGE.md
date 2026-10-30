# Darkspyre.Inference.Tensor.Image.Supra

Text-to-image generation with the Supra2-IMG model inside the application's process. The Supra DiT, the Flan-T5-Base
text encoder and the Stable Diffusion VAE decoder run through TensorSharp on CPU or CUDA, and the result is PNG bytes
in the shared `ImageGenerationResult`.

## Install

```bash
dotnet add package Darkspyre.Inference.Tensor.Image.Supra
```

## Register

Construct `SupraImageInference` over a `TensorModelPackManager`, or call `AddTensorEngines()` from
`Darkspyre.Inference.Tensor.Registry`, which registers `SupraEngineProvider`. `TensorImageModels.Supra2Image` is the
built-in pack definition.

## Example

```csharp
TensorModelPackDefinition supra = TensorImageModels.Supra2Image;
using var packs = new TensorModelPackManager(new TensorModelCatalog([supra]), packDirectory);
await using ITensorImageInference image = new SupraImageInference(packs);

await image.StartAsync();
await image.LoadAsync(supra.Key); // Loaded or Failed arrives through EngineStatusChanged.
ImageGenerationResult result = await image.GenerateAsync(new ImageGenerationRequest
{
    ModelId = supra.Key.ModelId,
    Prompt = "a small glowing jellyfish"
});
await image.StopAsync();
```

Wait for `EngineStatusChanged` to report Loaded before generating. The pack must already be installed through
`TensorModelPackManager.InstallAsync`.

## Settings

`SupraSetup.CreateSettingsManager` opens the engine's settings: the model with its install state, "Runs on", and the
download, check, load and backend-check actions.

## Limits

- Output is 256x256. Seeds are deterministic; step count and classifier-free guidance are adjustable. Negative prompts
  are not supported.
- Reference images are refused before the engine runs.
- Automatic backend selection can fall back to managed CPU.
- The DiT checkpoint is read as data through a checkpoint reader that accepts only the tensor forms the pinned file
  uses; no Python or PyTorch runs.
- The package carries `THIRD-PARTY-NOTICES.md`, the BSD-3 notice for code ported from TensorSharp.
