<img src=".github/banner.svg" alt="Darkspyre Inference">

# Darkspyre Inference

`Darkspyre.ML.Tokenizers` is the explicit NuGet exception. It remains a package in source mode; changes to that stable fork are consumed through a NuGet version update.

`DarkspyreDependencyMode` defaults to `Source`. Builds resolve TensorSharp and host-tool storage
references from sibling TensorSharp, SecureStore and PersistenceStore worktrees automatically.
Release commands select `-p:DarkspyreDependencyMode=Packages` explicitly. `eng/deploy.sh` selects
that mode for its nested processes. A missing source target fails without a package fallback.
Shipping capability libraries remain independent of application credential storage and persistence.

Typed .NET inference libraries for applications that need external AI providers, resident local
models, or both. Darkspyre Inference separates **what an application asks a model to do** from
**how a particular provider or engine performs it**.

The NuGet libraries supply inference libraries. A Desktop application can call Tensor
directly without an HTTP bridge. A web service can host the same libraries and translate its API
into their typed contracts. In either case, the application owns startup, configuration, credentials,
persistence, user consent, and shutdown.

Current source and published package version: **11.15.1-rc.1**, targeting **.NET 11**.
The library family is distributed through the private
[DarkspyreStudios GitHub Packages feed](https://github.com/orgs/DarkspyreStudios/packages).
The restore source is `https://nuget.pkg.github.com/DarkspyreStudios/index.json`.
Unimplemented provider capabilities remain unavailable; publishing their contracts does not enable them.
GGML engine activation additionally requires a compatible per-RID native package and catalog.
Tensor uses the published TensorSharp **2.8.6.9** managed packages. A macOS ARM64 engine explicitly selects
`Darkspyre.TensorSharp.Backends.GGML.Native.osx-arm64` **2.8.6.9**; the native 2.8.6.8 catalog is
not compatible with its managed wrapper. The corrected Mac package is available on the same private
feed. Fresh package-only consumer checks verify CPU, Metal and Auto initialization and clean shutdown
in Debug and Release. These checks do not qualify model execution. The managed GGML wrapper is not
a native driver and does not select the native package automatically.

The repository also hosts the [GPU arbiter](App/README.md), an independent .NET 10 Windows
x64 application under `App/` that shares a GPU between llama.cpp and ComfyUI. It owns its
solution, version and deployment outside the coordinated library release.

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
| `Darkspyre.Inference.Connect.Common` | Universal conversation and embedding contracts, shared services and automatic provider discovery |
| `Darkspyre.Inference.Connect.Codex`, `.Claude`, `.OpenAiApi`, `.SelfHost` | Independently selected providers through the universal contract |
| `Darkspyre.Inference.Connect` | Existing Image, TTS and Audio capability assemblies |
| `Darkspyre.Inference.Tensor.Common` | Tensor capability contracts, model lifecycle and verified model packs |
| `Darkspyre.Inference.Tensor.*` model libraries | Independently consumed in-process loaders and their runtime dependencies |
| `Darkspyre.Inference.UI.Settings` | Optional host-placed Avalonia editor for the shared setup contracts |

Connect, Tensor, and UI.Settings reference the **exact same-version Common NuGet**; they do not
embed its DLL. UI.Settings does not depend on Connect or Tensor. Connect provider and Tensor model libraries are independent packages at the synchronized family version.
The media Connect package retains its Image, TTS and Audio assemblies.
Third-party runtimes are declared package dependencies rather than copied into these archives.

Shared contracts do not imply identical provider capabilities. Query what the selected model
supports. Selecting a backend is explicit; the library does not silently switch providers or fall
back from Connect to Tensor.

## Features and implementations

### Connect: out-of-process providers

- **Text and agents:** native Codex, the official Claude CLI, OpenAI Responses and caller-owned
  SelfHost engines. Applications select these providers through library references; see
  [Connect's phase-1 build](Connect/README.md#phase-1-build).
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
- Model-pack storage in a local pack directory, by default
  `LocalApplicationData/Darkspyre Studios/Inference/Tensor`, or one the host supplies.
- User-selected files, folders, direct URLs, and optional repository-source adapters, subject to each
  engine's compatibility policy.
- Bounded metadata inspection followed by explicit installation/import, integrity checks, and
  registered pack identities. Inspecting a source is not permission to download full model weights.
- Host-controlled cancellation and teardown. Normal teardown protects active requests; cancellation
  asks for prompt termination but waits for native work to stop before freeing resources.
- Engine discovery: `AddTensorEngines` registers every shipped engine and a `TensorEngineRegistry`
  that lists each engine's setup definition and model store key and resolves its factories by
  implementation ID.

`StartAsync` may be a no-op. `UnloadAsync` frees the model so another can be loaded; `StopAsync`
ends the instance's lifetime. Native dependencies, hardware, memory, and backend availability still
matter. Laya uses ONNX Runtime with CPU or Core ML; its Core ML configuration uses CPU/GPU execution.
See the [Tensor guide](Tensor/README.md) for exact backend and artifact requirements.

### Shared setup and optional UI

Providers expose typed fields, validation, authentication interactions and unsaved-change state
through an `InferenceSettings` object. Its `IInferenceSettingsManager` runs actions, lists choices and
saves. Hosts can render those contracts themselves or use UI.Settings. The editor includes invalid-input Save protection, dependent choice loading,
typed numeric inputs, and optional host-owned file/folder selection.

The host supplies commit targets, protected credentials, consent, and interaction handling. The
editor does not own the connection list, dialogs, routing, or any engine behind the settings.

## Install

Use an authorized account with access to the private GitHub Packages feed. The repository's
[NuGet.Config](NuGet.Config) declares the public/private sources and package-source mapping.
Provide credentials through your approved local credential mechanism; never commit tokens.
The projects currently use the .NET 11 RC SDK pinned in each product's `global.json`.

Install only the products your application needs, keeping their versions synchronized:
Workspace builds consume sibling source projects. Package installation uses the synchronized family version.

```bash
dotnet add package Darkspyre.Inference.Connect.OpenAiApi --version 11.15.1-rc.1
```

```bash
dotnet add package Darkspyre.Inference.Tensor.Image.Supra --version 11.15.1-rc.1
```

Optional, for an Avalonia host:

```bash
dotnet add package Darkspyre.Inference.UI.Settings --version 11.15.1-rc.1
```

Common is brought in transitively. An application that only consumes the shared contracts can
reference `Darkspyre.Inference.Common` directly. Model weights are **not** included in the packages;
hosts must arrange explicit acquisition and comply with each model's license.

## Simple examples

These examples are host integration methods, not complete applications. They accept configured
services or storage from the caller so they do not invent a database or credential implementation.

### Ask a configured text provider

Reference the chosen Connect provider libraries. Register `AddLlmConnect()` and a host implementation of `ILlmConnectionSource` with dependency
injection, then resolve `LlmAgentProviderService`. The connection source supplies versioned connection metadata and protected
credentials; credential replacement must be atomic. See [host integration](Connect/README.md#host-integration).

```csharp
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;

namespace MyApp.Examples;

public static class TextExample
{
    public static async Task<string> AskAsync(
        LlmAgentProviderService agents, string connectionId, string modelId,
        string prompt, CancellationToken cancellationToken = default)
    {
        await using ILlmAgentSession session = await agents.StartSessionAsync(
            connectionId, new LlmAgentSessionOptions { ModelId = modelId }, cancellationToken);
        var text = new System.Text.StringBuilder();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(
            new LlmAgentTurnRequest { Input = [new LlmAgentTextContent(prompt)] }, cancellationToken))
        {
            if (item is LlmAgentTextDeltaEvent delta) { text.Append(delta.Delta); }
        }

        return text.ToString();
    }
}
```

Connect's agent session is the one conversation surface for its providers. The `ILlmInference`
request/result surface is implemented by Tensor's Qwen engine. The host still needs to install and load
that engine's model before issuing a request.

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
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Image.Supra;

namespace MyApp.Examples;

public static class LocalImageExample
{
    public static async Task<ImageGenerationResult> GenerateAsync(
        string packDirectory, string prompt,
        CancellationToken cancellationToken = default)
    {
        TensorModelPackDefinition model = TensorImageModels.Supra2Image;
        using var packs = new TensorModelPackManager(
            new TensorModelCatalog([model]), packDirectory);

        await packs.InstallAsync(model.Key, cancellationToken: cancellationToken);
        await using var engine = new SupraImageInference(packs);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.EngineStatusChanged += (_, status) =>
        {
            if (status.State == TensorEngineState.Loaded) { loaded.TrySetResult(); }
            else if (status.State == TensorEngineState.Failed) { loaded.TrySetException(status.Error!); }
        };
        await engine.StartAsync(cancellationToken);
        try
        {
            // LoadAsync returns once the engine accepts the load; the outcome arrives through the event.
            await engine.LoadAsync(model.Key, cancellationToken);
            await loaded.Task.WaitAsync(cancellationToken);
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

### Place the shared editor in an Avalonia host

Given provider settings, their manager, a host interaction handler, and a commit handler:

```csharp
using Darkspyre.Inference.UI.Settings;

InferenceSettingsEditor editor = new(settings, manager, interactionHost);
editor.CommitCompleted += (_, result) => OnCommitCompleted(result);
dialog.Content = editor;
```

The host supplies a typed commit target and owns the settings' lifetime. See the [UI.Settings guide](UI/README.md) and
[compiled host example](UI/examples/Darkspyre.Inference.UI.Settings.Example/HostPlacedSettingsExample.cs).

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

All library versions come from [Directory.Build.props](Directory.Build.props):
`<net>.<release>.<minor>` means .NET generation, breaking-contract release, and compatible tweak/fix.
The current `-rc.1` suffix reflects .NET 11 RC dependencies. Releases are coordinated and immutable.

### Release-candidate mode and source mode

Without source properties a build is in release-candidate mode: TensorSharp comes from the published packages
pinned in `Directory.Packages.props`. No `NuGet.Config` names a local feed.

Interim builds use source mode. `-p:TensorSharpSourceRoot=/absolute/TensorSharp` names a TensorSharp checkout,
normally a detached worktree of its `origin/dev`. `Directory.Build.targets` then imports that checkout's
`eng/SourceReferences.targets`, which turns each `Darkspyre.TensorSharp.*` PackageReference into a ProjectReference
to the checkout.

Consumers of these packages use [eng/SourceReferences.targets](eng/SourceReferences.targets) the same way. A consumer
that sets `InferenceSourceRoot` imports it, and each `Darkspyre.Inference.*` PackageReference becomes ProjectReferences
to the projects that package carries: Common and UI.Settings map to their one project, and Connect and Tensor to every
Connect library project except its packaging project. A Tensor reference selects its named library project.
ExcludeAssets and PrivateAssets carry over. Connect.Common references `Microsoft.Bcl.Memory` directly, because the central
transitive pin that patches it does not reach a project-reference consumer.

To deploy from a clean `master` checkout:

```bash
./deploy-major.sh --dry-run  # Preview the next breaking-contract version.
./deploy-major.sh           # 11.5.x -> 11.6.0; retain the prerelease suffix.
# Or, for a compatible tweak:
./deploy-minor.sh           # 11.5.1 -> 11.5.2; retain the prerelease suffix.
```

Each deployment commits the version change, runs the release checks, publishes all library packages
to the private feed, verifies fresh downloads, and pushes the release source and tag. See
[deployment prerequisites and failure handling](eng/README.md) before running either command.

## License

<!-- ownership-notice:start -->
> **Proprietary software.** Copyright © 2026 Brandon Mathis. All rights reserved.
> No permission is granted to use, copy, modify, or distribute this software
> without prior written authorization.
> Any contribution, including code, reviews, patches, issues and designs, is
> assigned to Brandon Mathis on submission. See [LICENSE.md](LICENSE.md) and
> [CONTRIBUTING.md](CONTRIBUTING.md).
<!-- ownership-notice:end -->
