# Darkspyre.Inference.Connect

Image, speech and audio connectors for .NET applications: image generation through a ComfyUI server, speech through a
PocketTTS server and sound generation through a TangoFlux server. Each connector calls a server the application
names; it never starts the server, installs models or plays audio.

## Install

```bash
dotnet add package Darkspyre.Inference.Connect
```

## Register

```csharp
services.AddImageConnect();
services.AddTtsConnect();
services.AddAudioConnect();
```

Each call registers one capability's provider service: `ImageProviderService`, `TtsProviderService` and
`AudioProviderService`. Register only the ones the application uses.

## Example

```csharp
TtsProviderService ttsProviders = serviceProvider.GetRequiredService<TtsProviderService>();
await using ITtsClient speech = ttsProviders.Bind(new TtsConnection
{
    Endpoint = new Uri("http://127.0.0.1:8020"),
    PocketTts = new PocketTtsEndpointOptions { Protocol = PocketTtsProtocol.Multipart }
});

IReadOnlyList<TtsVoice> voices = await speech.ListVoicesAsync(cancellationToken);
TtsResult clip = await speech.SpeakAsync(new TtsRequest
{
    Text = "The next room is quiet.",
    VoiceId = voices[0].Id,
    Format = TtsAudioFormat.Wav
}, cancellationToken);
```

`AudioProviderService.Bind(new AudioConnection { Endpoint = ... })` returns an `IAudioGenerationClient` the same way.
`ImageProviderService.GetRequiredTransport(ImageTransport.ComfyUi)` returns the ComfyUI transport, which uploads
reference images, prepares a workflow request, submits it and reads the job.

## Settings

Each provider service exposes `SetupDefinitions` and `CreateSettingsManager`, which open the connection's typed
settings for the shared setup editor:

- PocketTTS: endpoint, timeout, protocol, paths, protected headers, voice and speech defaults.
- TangoFlux: endpoint, timeout, generation path, protected headers, duration, steps, guidance, seed and the
  provider-cache choice.

Save makes no network request. Voice checks, test synthesis and test generation are separate actions.

## Limits

- PocketTTS returns WAV only. TangoFlux accepts durations of 1 to 30 seconds, 10 to 100 steps and guidance from 0.1
  to 20; a value outside these ranges fails before any request.
- TangoFlux has no model list or health check: its test sends a real generation request.
- Speech and audio requests are never retried automatically, and their errors carry no prompts, protected headers or
  raw server responses.
- The speech and audio registrations turn off automatic redirects, so protected headers never follow a redirect to
  another origin.
- The application owns connection storage, credentials, workflow storage, job scheduling and media storage.
