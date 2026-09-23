# Whisper host example (source only)

This non-packable ASP.NET example owns startup, one resident model and shutdown. It is not a
hosted product or a runtime inside the Tensor library. It binds only `127.0.0.1:5097`, has no
authentication and is not intended for external deployment. Production transport, access control,
request limits and shutdown policy belong to the real host. This is not an OpenAI-compatible API.

First, explicitly install a chosen definition from `TensorSpeechModels.Whisper` with the library's
`ITensorModelPackManager.InstallAsync` using your application's persistence store (or the explicit
Install action on `WhisperSetup`). The example never downloads or repairs weights. Then run:

```sh
dotnet run --project Tensor/examples/Darkspyre.Inference.Tensor.STT.Bridge -- --WhisperModel whisper/tiny.en
```

Optionally supply `--TensorStore /absolute/application/store` to use the same direct-file store as
the installing host. Missing, corrupt or uninstalled weights fail startup. The library also supports
other `IPersistenceStore` implementations; replace the example composition, not the library API.

POST JSON to `/transcribe`:

```json
{"samples":[0.0,0.0],"sampleRate":16000,"channels":1,"language":"en","translateToEnglish":false}
```

Samples are interleaved float PCM, not encoded WAV/MP3. The adapter validates finite samples,
complete frames, 8–192 kHz and 1–8 channels, then normalizes to mono 16 kHz. Empty clips return an
empty result. `language: null` selects automatic detection for multilingual models, or English for
English-only models. Translation is rejected for English-only and turbo models.

The response contains text, clip-relative segments, nullable detected language and model/runtime
provenance. The example maps transport DTOs to library contracts; it exposes no administrative
model operations. Streaming library calls accept host-supplied identified PCM windows, not microphone
capture, VAD, deduplication or partial tokens. Hosts must dispose abandoned stream enumerators.

Stop is terminal. Normal Stop waits for requests; passing a cancelled token requests the earliest
safe cancellation and still waits for native processing before disposing the model and its store
lease. No hard shutdown deadline or crash isolation is promised for in-process native code.
