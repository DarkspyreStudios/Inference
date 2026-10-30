# Darkspyre.Inference.Connect.SelfHost

Conversations, agent sessions and embeddings on engines the application loads in its own process, such as a Tensor
Qwen or embedding engine. Connect borrows each engine through the shared Common contracts; the application owns its
loading and retirement. Its provider appears as "In-process model".

## Install

```bash
dotnet add package Darkspyre.Inference.Connect.SelfHost
```

## Register

```csharp
services.AddLlmConnect();
services.AddSingleton<ILlmSelfHost, MyEngineHost>();
```

`AddLlmConnect()` from `Darkspyre.Inference.Connect.Common` discovers this package. The application supplies
`ILlmSelfHost`, which this package defines with `LlmSelfHostEngine`: it lists the engines a connection can name and
returns the loaded engine for a request.

## Example

```csharp
public sealed class MyEngineHost(ILlmInference qwen) : ILlmSelfHost
{
    public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LlmSelfHostEngine>>([new("qwen", "Qwen on this computer")]);

    public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
        engineId == "qwen" ? Task.FromResult(qwen)
            : throw new InferenceException(InferenceFailureCode.ModelUnavailable, $"Engine {engineId} is not loaded.");

    public Task<IEmbeddingInference> GetEmbeddingInferenceAsync(string engineId,
        CancellationToken cancellationToken = default) =>
        throw new InferenceException(InferenceFailureCode.ModelUnavailable, $"Engine {engineId} does not embed.");

    public LlmCapabilities? GetCapabilities(string engineId, string? modelId) => null;
}
```

A SelfHost connection's `Endpoint` is an engine id from `ListEnginesAsync`, and it has no credential. Several
connections can name one engine.

## Settings

- **Engine** lists the engines from `ILlmSelfHost.ListEnginesAsync`.
- **Model** lists the chosen engine's models. Both are required to save.
- **Check the engine's models** and the model test run on the local engine.

## Limits

- Connect never loads, unloads or installs an engine. A request to an engine that is unknown or not loaded fails with
  `InferenceException` and `ModelUnavailable`.
- `GetCapabilities` must answer from the host's own records without I/O. The engine's declared capabilities decide
  what a connection offers; connection capability overrides, custom headers and provider options are refused.
- Native file references and caller-owned resource references are not supported.
