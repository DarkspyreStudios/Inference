## The Darkspyre.Inference packages

This package is one library of the Darkspyre.Inference family. The family runs AI inference from .NET applications
through typed, provider-neutral contracts.

- `Darkspyre.Inference.Common` holds the contracts every other package shares: requests, results, capabilities,
  failures and the provider setup model.
- `Darkspyre.Inference.Connect.Common` holds the conversation and embedding services. Each Connect provider package
  adds one provider, and `AddLlmConnect()` discovers every provider package the application references.
- The Tensor packages load and run models inside the application's own process.
- The UI.Settings packages provide the setup editors a host places in its own window or page.

Every package of the family carries the same version. Reference all of them at the same version: each package depends
on the exact version of the family packages it uses.

The packages are licensed under the MIT license. `LICENSE.md` in each package holds the license text.

| Package | Holds |
| --- | --- |
| [Darkspyre.Inference.Common](https://www.nuget.org/packages/Darkspyre.Inference.Common) | Shared contracts for Connect and Tensor |
| [Darkspyre.Inference.Connect](https://www.nuget.org/packages/Darkspyre.Inference.Connect) | Image, speech and audio connectors |
| [Darkspyre.Inference.Connect.Common](https://www.nuget.org/packages/Darkspyre.Inference.Connect.Common) | Conversation and embedding services and provider discovery |
| [Darkspyre.Inference.Connect.Codex](https://www.nuget.org/packages/Darkspyre.Inference.Connect.Codex) | Conversations on a ChatGPT subscription |
| [Darkspyre.Inference.Connect.Claude](https://www.nuget.org/packages/Darkspyre.Inference.Connect.Claude) | Conversations on a Claude subscription through the Claude CLI |
| [Darkspyre.Inference.Connect.HostTools](https://www.nuget.org/packages/Darkspyre.Inference.Connect.HostTools) | The host-tools local service and its client |
| [Darkspyre.Inference.Connect.Antigravity](https://www.nuget.org/packages/Darkspyre.Inference.Connect.Antigravity) | Conversations on a Google subscription through the Antigravity CLI |
| [Darkspyre.Inference.Connect.Grok](https://www.nuget.org/packages/Darkspyre.Inference.Connect.Grok) | Conversations on a Grok subscription |
| [Darkspyre.Inference.Connect.OpenAiApi](https://www.nuget.org/packages/Darkspyre.Inference.Connect.OpenAiApi) | Conversations and embeddings through the OpenAI API |
| [Darkspyre.Inference.Connect.SelfHost](https://www.nuget.org/packages/Darkspyre.Inference.Connect.SelfHost) | Conversations and embeddings on engines the application runs |
| [Darkspyre.Inference.Tensor.Common](https://www.nuget.org/packages/Darkspyre.Inference.Tensor.Common) | Model packs, the model lifecycle and the Tensor capability interfaces |
| [Darkspyre.Inference.Tensor.Decision.Laya](https://www.nuget.org/packages/Darkspyre.Inference.Tensor.Decision.Laya) | Laya typed decisions through ONNX Runtime |
| [Darkspyre.Inference.Tensor.Embedding](https://www.nuget.org/packages/Darkspyre.Inference.Tensor.Embedding) | Text embeddings from GGUF sentence encoders |
| [Darkspyre.Inference.Tensor.Embedding.Gemma](https://www.nuget.org/packages/Darkspyre.Inference.Tensor.Embedding.Gemma) | EmbeddingGemma text and image embeddings |
| [Darkspyre.Inference.Tensor.Ggml](https://www.nuget.org/packages/Darkspyre.Inference.Tensor.Ggml) | The process's GGML and MLX native runtime |
| [Darkspyre.Inference.Tensor.Image.Supra](https://www.nuget.org/packages/Darkspyre.Inference.Tensor.Image.Supra) | Supra2-IMG image generation |
| [Darkspyre.Inference.Tensor.LLM.Qwen](https://www.nuget.org/packages/Darkspyre.Inference.Tensor.LLM.Qwen) | Qwen GGUF text generation |
| [Darkspyre.Inference.Tensor.Registry](https://www.nuget.org/packages/Darkspyre.Inference.Tensor.Registry) | Registration and discovery of the Tensor engines |
| [Darkspyre.Inference.Tensor.STT.Whisper](https://www.nuget.org/packages/Darkspyre.Inference.Tensor.STT.Whisper) | Whisper speech transcription |
| [Darkspyre.Inference.UI.Settings.Core](https://www.nuget.org/packages/Darkspyre.Inference.UI.Settings.Core) | Editor state and host contracts both setup editors share |
| [Darkspyre.Inference.UI.Settings.Avalonia](https://www.nuget.org/packages/Darkspyre.Inference.UI.Settings.Avalonia) | The Avalonia setup editor |
| [Darkspyre.Inference.UI.Settings.Blazor](https://www.nuget.org/packages/Darkspyre.Inference.UI.Settings.Blazor) | The Blazor setup editor |
