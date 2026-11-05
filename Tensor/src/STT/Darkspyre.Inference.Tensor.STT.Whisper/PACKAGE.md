# Darkspyre.Inference.Tensor.STT.Whisper

Speech transcription with Whisper models inside the application's process, through the public Whisper.net runtime.
The model stays loaded across requests on CPU or CUDA. The application owns model storage and the engine's lifetime.

## Install

```bash
dotnet add package Darkspyre.Inference.Tensor.STT.Whisper --prerelease
```

Whisper.net, its CPU and CUDA 12 Linux runtimes and NAudio.Core arrive as NuGet dependencies.

## Register

Construct `WhisperSpeechToTextInference` over a `TensorModelPackManager`, or call `AddTensorEngines()` from
`Darkspyre.Inference.Tensor.Registry`, which registers `WhisperEngineProvider`. `TensorSpeechModels.Whisper` lists the
twelve built-in packs: tiny, base, small and medium with their English-only variants, large-v1, large-v2, large-v3
and large-v3-turbo.

## Example

Install the selected pack through `TensorModelPackManager.InstallAsync` when the person chooses to acquire it. The
following load assumes `modelKey` names an installed entry from `TensorSpeechModels.Whisper`.

```csharp
using var packs = new TensorModelPackManager(new TensorModelCatalog(TensorSpeechModels.Whisper), packDirectory);
await using var whisper = new WhisperSpeechToTextInference(packs);
await whisper.StartAsync();
await whisper.LoadAsync(modelKey); // Accepts loading, not completion.
```

Subscribe to `EngineStatusChanged` before loading. After it reports Loaded, transcribe; handle Failed instead of sending
a request after a failed load. Awaiting `LoadAsync` alone does not wait for Loaded.

```csharp
SpeechTranscriptionResult result = await whisper.TranscribeAsync(new SpeechTranscriptionRequest(audio));
```

`audio` is a `SpeechAudio` of interleaved float PCM with its sample rate and channel count. `TranscribeStreamAsync`
transcribes a sequence of audio windows and returns one result per window.

## Settings

`WhisperSetup.CreateSettingsManager` opens the engine's settings: the twelve built-in models with their install state,
"Runs on", and the download, check, load and backend-check actions.

## Limits

- Audio from 8 to 192 kHz with 1 to 8 channels is converted to mono 16 kHz. An empty clip gives an empty result.
- A language code and translation to English are request options. Translation is refused on English-only and turbo
  models.
- Automatic backend selection prefers CUDA 12 and falls back to CPU. `TensorBackendKind.Cpu` turns the GPU off; the
  constructor's `gpuDevice` picks the GPU.
- Standard F32 and F16 Whisper GGML files load. Quantized variants are not offered.
- Window transcription does not merge overlapping windows, separate speakers or emit partial results.
- Native processing runs in the application's process, and teardown has no fixed deadline.
