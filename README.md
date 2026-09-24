<img src=".github/banner.svg" alt="Darkspyre Inference">

# Darkspyre Inference

Typed .NET inference libraries for applications that need external AI providers, resident local
models, or both. Darkspyre Inference separates **what an application asks a model to do** from
**how a particular provider or engine performs it**.

The project ships libraries, not a standalone server. A Desktop application can call Tensor
directly without an HTTP bridge. A web service can host the same libraries and translate its API
into their typed contracts. In either case, the application owns startup, configuration, credentials,
persistence, user consent, and shutdown.

Current release: **11.5.3-rc.1**, targeting **.NET 11**, distributed through the private
[DarkspyreStudios GitHub Packages feed](https://github.com/orgs/DarkspyreStudios/packages).

## Goals

- **Keep application code provider-neutral.** Reuse typed requests, results, capabilities, streaming
  events, and failures wherever implementations share the same semantics.
- **Support external and in-process inference without blurring their boundaries.** A locally hosted
  HTTP endpoint is still Connect; a model loaded into the application's process is Tensor.
- **Keep the host in control.** The libraries do not own an application's database, secret vault,
  API server, model-selection policy, playback, or UI navigation.
- **Keep models resident across requests.** Loading is an explicit operation, not a hidden part of
  running each prompt. Downloading, importing, loading, and executing are separate actions.
- **Describe capabilities and configuration as data.** Hosts can discover registered providers,
  offer supported settings, and select implementations at runtime without duplicating provider forms.
- **Provide a C# integration surface.** Applications call .NET interfaces rather than managing a
  Python/PyTorch server. Tensor implementations may use native runtime dependencies; this is not a
  promise that every backend is pure managed code or available on every platform.

## Packages and boundaries

| NuGet package | Responsibility |
|---|---|
| `Darkspyre.Inference.Common` | Genuinely shared typed inference and setup contracts, including LLM, image, and decision models |
| `Darkspyre.Inference.Connect` | Connections to external inference providers, HTTP services, and supported official CLIs |
| `Darkspyre.Inference.Tensor` | In-process engines, explicit model lifecycle, compatible model-pack discovery, installation, and import |
| `Darkspyre.Inference.UI.Settings` | Optional host-placed Blazor/Lumeo editor for the shared setup contracts |

Connect, Tensor, and UI.Settings reference the **exact same-version Common NuGet**; they do not
embed its DLL. UI.Settings does not depend on Connect or Tensor. Capability-specific assemblies
ship inside their owning product package, not as additional independently versioned NuGets.
Third-party runtimes are declared package dependencies rather than copied into these archives.

Shared contracts do not imply identical provider capabilities. Query what the selected model
supports. Selecting a backend is explicit; the library does not silently switch providers or fall
back from Connect to Tensor.

## Features and implementations

### Connect: out-of-process providers

- **Text and agents:** OpenAI Responses, OpenAI-compatible chat, Anthropic Messages, direct ChatGPT
  subscription access, official Claude and Antigravity CLIs, AWS Bedrock Converse, and AgentCore.
- **Capability-dependent LLM features:** streaming, model discovery, structured output, attachments,
  embeddings, host-executed tools, managed-agent sessions, and account-usage reporting.
- **Typed reasoning levels:** `None`, `Minimal`, `Low`, `Medium`, `High`, `ExtraHigh`, and `Maximum`.
  Models advertise their accepted subset; null leaves provider defaults unchanged. Unsupported
  levels are rejected, not silently rounded to another value.
- **Image generation:** ComfyUI HTTP/WebSocket integration with host-selected workflows.
- **Speech synthesis:** PocketTTS, returning typed WAV results.
- **Audio generation:** TangoFlux, through its external service contract.

Features vary by transport and model. Connect does not start these external services or take
ownership of an official CLI's login. See the [Connect guide](Connect/README.md) for configuration,
authentication, tools, and provider-specific requirements.

### Tensor: resident in-process models

| Implementation | Supported scope |
|---|---|
| Qwen | The implemented Qwen3 GGUF text-model schema, including validated split packs—not arbitrary GGUF architectures |
| Supra | Supra2-IMG and compatible DiT variants within its supported text-encoder/VAE pipeline—not arbitrary diffusion models |
| Whisper | Whisper speech transcription using Whisper.net; twelve recognized weight variants |
| Laya | Laya-family ONNX typed-decision models, with model, tokenizer, and agent-config artifacts |

Tensor provides:

- Explicit `StartAsync`, `LoadAsync`, inference, `UnloadAsync`, and `StopAsync` operations.
- Repeated inference against a loaded model; one loaded model per engine instance.
- Model-pack storage through the host's `IPersistenceStore`, with an optional direct-file default.
- User-selected files, folders, direct URLs, and optional repository-source adapters, subject to each
  engine's compatibility policy.
- Bounded metadata inspection followed by explicit installation/import, integrity checks, and
  registered pack identities. Inspecting a source is not permission to download full model weights.
- Host-controlled cancellation and teardown. Normal teardown protects active requests; cancellation
  asks for prompt termination but waits for native work to stop before freeing resources.

`StartAsync` may be a no-op. `UnloadAsync` frees the model so another can be loaded; `StopAsync`
ends the instance's lifetime. Native dependencies, hardware, memory, and backend availability still
matter. Laya uses ONNX Runtime with CPU or Core ML; its Core ML configuration uses CPU/GPU execution.
See the [Tensor guide](Tensor/README.md) for exact backend and artifact requirements.

### Shared setup and optional UI

Providers expose typed fields, choices, validation, authentication interactions, actions, progress,
and draft-dirty state through `IInferenceSetupSession`. Hosts can render those contracts themselves
or use UI.Settings. The editor includes invalid-input Save protection, dependent choice loading,
typed numeric inputs, and optional host-owned file/folder selection.

The host supplies commit targets, protected credentials, consent, and interaction handling. The
editor does not own the connection list, dialogs, routing, or the lifetime of a setup session.

## Install

Use an authorized account with access to the private GitHub Packages feed. The repository's
[NuGet.Config](NuGet.Config) declares the public/private sources and package-source mapping.
Provide credentials through your approved local credential mechanism; never commit tokens.
The projects currently use the .NET 11 RC SDK pinned in each product's `global.json`.

Install only the products your application needs, keeping their versions synchronized:

```bash
dotnet add package Darkspyre.Inference.Connect --version 11.5.3-rc.1
dotnet add package Darkspyre.Inference.Tensor --version 11.5.3-rc.1
# Optional, for a Blazor host:
dotnet add package Darkspyre.Inference.UI.Settings --version 11.5.3-rc.1
```

Common is brought in transitively. An application that only consumes the shared contracts can
reference `Darkspyre.Inference.Common` directly. Model weights are **not** included in the packages;
hosts must arrange explicit acquisition and comply with each model's license.

## Simple examples

These examples are host integration methods, not complete applications. They accept configured
services or storage from the caller so they do not invent a database or credential implementation.

### Ask a configured text provider

Register `AddLlmConnect()` and a host implementation of `ILlmConnectionSource` with dependency
injection, then resolve `LlmProviderService`. Register `AddLlmConnectBedrock()` as well when using
AWS transports. The connection source supplies versioned connection metadata and protected
credentials; credential replacement must be atomic. See [host integration](Connect/README.md#host-integration).

```csharp
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;

namespace MyApp.Examples;

public static class TextExample
{
    public static async Task<string> AskAsync(
        LlmProviderService providers, string connectionId, string modelId,
        string prompt, CancellationToken cancellationToken = default)
    {
        ILlmInference llm = providers.Bind(connectionId);
        LlmInferenceResult result = await llm.CompleteAsync(new LlmInferenceRequest
        {
            ModelId = modelId,
            Messages = [new LlmMessage("user", prompt)]
        }, cancellationToken);

        return result.Text;
    }
}
```

The `ILlmInference` request/result surface is also implemented by Tensor's Qwen engine. The host
still needs to install and load that engine's model before issuing a request.

### Stream text through the same contract

```csharp
using Darkspyre.Inference.Common.LLM;

namespace MyApp.Examples;

public static class StreamingExample
{
    public static async Task PrintAsync(
        ILlmInference llm, string modelId, string prompt,
        CancellationToken cancellationToken = default)
    {
        var request = new LlmInferenceRequest
        {
            ModelId = modelId,
            Messages = [new LlmMessage("user", prompt)]
        };

        await foreach (LlmStreamEvent item in llm.StreamAsync(request, cancellationToken))
        {
            if (item is LlmTextDeltaEvent text) { Console.Write(text.Delta); }
        }
    }
}
```

This intentionally prints only text deltas. Real hosts can also handle completion, tool-call,
reasoning, refusal, and activity events. Check `GetCapabilitiesAsync(modelId)` for streaming and
supported reasoning levels; not every implementation emits incremental text.

### Install a model explicitly, then generate an image

This example **downloads the Supra pack if needed**. Invoke it only after user consent and with
sufficient storage, memory, and a supported runtime. For an already installed pack, skip installation.

```csharp
using Darkspyre.Persistence;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Image.Supra;

namespace MyApp.Examples;

public static class LocalImageExample
{
    public static async Task<ImageGenerationResult> GenerateAsync(
        IPersistenceStore applicationStore, string prompt,
        CancellationToken cancellationToken = default)
    {
        TensorModelPackDefinition model = TensorImageModels.Supra2Image;
        using var packs = new TensorModelPackManager(
            new TensorModelCatalog([model]), applicationStore);

        await packs.InstallAsync(model.Key, cancellationToken: cancellationToken);
        await using var engine = new SupraImageInference(packs);
        await engine.StartAsync(cancellationToken);
        try
        {
            await engine.LoadAsync(model.Key, cancellationToken);
            return await engine.GenerateAsync(new ImageGenerationRequest
            {
                ModelId = model.Key.ModelId,
                Prompt = prompt
            }, cancellationToken: cancellationToken);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None); // Normal shutdown also unloads.
        }
    }
}
```

For interactive use, retain `packs` and `engine` in the host: Start → Load → Generate → Generate →
Unload → Load another compatible model, or Stop. The short helper above shuts down after one image
only to make resource ownership explicit. Keep the store alive until engine teardown completes.

### Synthesize speech through an external PocketTTS service

The host registers `AddTtsConnect()` and resolves `TtsProviderService`. The endpoint must already
be running; select a voice returned by `ListVoicesAsync` for that endpoint.

```csharp
using Darkspyre.Inference.Connect.TTS;

namespace MyApp.Examples;

public static class SpeechExample
{
    public static async Task<TtsResult> SpeakAsync(
        TtsProviderService providers, Uri endpoint, string voiceId, string text,
        CancellationToken cancellationToken = default)
    {
        ITtsClient speech = providers.Bind(new TtsConnection
        {
            Endpoint = endpoint,
            PocketTts = new PocketTtsEndpointOptions { Protocol = PocketTtsProtocol.Multipart }
        });
        return await speech.SpeakAsync(new TtsRequest
        {
            Text = text,
            VoiceId = voiceId,
            Format = TtsAudioFormat.Wav
        }, cancellationToken);
    }
}
```

The returned audio belongs to the host to store or play. For speech **recognition**, use Tensor's
[Whisper implementation](Tensor/README.md#speech-transcription--whisper).

### Place the shared editor in a Blazor host

Given a provider-created setup session, a host interaction handler, and a commit callback:

```razor
@using Darkspyre.Inference.UI.Settings

<InferenceSettingsEditor Session="@selectedSession"
                         Host="@interactionHost"
                         CommitCompleted="OnCommitCompletedAsync" />
```

The host registers Lumeo, includes its theme and Razor Class Library assets, supplies a typed commit
target, and owns session disposal. See the [UI.Settings guide](UI/README.md) and
[compiled host example](UI/examples/Darkspyre.Inference.UI.Settings.Example/HostPlacedSettingsExample.razor).

## Repository and further examples

| Location | Contents |
|---|---|
| [Common](Common/README.md) | Shared contracts and [provider-neutral setup examples](Common/examples/Darkspyre.Inference.Setup.Examples/README.md) |
| [Connect](Connect/README.md) | External adapters, [tool/agent examples](Connect/examples/Darkspyre.Inference.Connect.LLM.Examples/README.md), tests, and a console tester |
| [Tensor](Tensor/README.md) | Resident engines, model packs, tests, and a [source-only Whisper bridge example](Tensor/examples/Darkspyre.Inference.Tensor.STT.Bridge/README.md) |
| [UI](UI/README.md) | Shared settings editor, component tests, and a host-composition example |
| [docs](docs) | Technical designs, contract changes, and implementation records |
| [eng](eng) | Package-boundary validation and package-only consumer smoke tests |

The Whisper bridge is example host source, not a shipped service product. Tensor is not a general
ComfyUI workflow executor, and support for one model family does not imply support for every model
sharing its file format.

## Build and versioning

Build from a product directory so its `global.json` selects the pinned SDK. For example, after
configuring private-feed authentication:

```bash
cd Connect
dotnet restore Darkspyre.Inference.Connect.slnx
dotnet build Darkspyre.Inference.Connect.slnx --no-restore
dotnet test Darkspyre.Inference.Connect.slnx --no-build --no-restore
dotnet format Darkspyre.Inference.Connect.slnx --verify-no-changes --no-restore
```

Tensor and UI have equivalent focused solutions in their own directories. Normal automated tests
use controlled fixtures and doubles; paid provider calls and full model downloads are not test gates.
Release checks also cover Debug/Release builds, vulnerability audits, package contents, synchronized
dependencies, and a fresh package-only consumer.

All four versions come from [Directory.Build.props](Directory.Build.props):
`<net>.<release>.<minor>` means .NET generation, breaking-contract release, and compatible tweak/fix.
The current `-rc.1` suffix reflects .NET 11 RC dependencies. Releases are coordinated and immutable.

To deploy from a clean `master` checkout:

```bash
./deploy-major.sh --dry-run  # Preview the next breaking-contract version.
./deploy-major.sh           # 11.5.x -> 11.6.0; retain the prerelease suffix.
# Or, for a compatible tweak:
./deploy-minor.sh           # 11.5.1 -> 11.5.2; retain the prerelease suffix.
```

Each deployment commits the version change, runs the release checks, publishes all four packages
to the private feed, verifies fresh downloads, and pushes the release source and tag. See
[deployment prerequisites and failure handling](eng/README.md) before running either command.

## License

> **Proprietary software.** Copyright © 2026 Brandon Mathis. All rights reserved.
> No permission is granted to use, copy, modify, or distribute this software
> without prior written authorization. See [LICENSE.md](LICENSE.md).
