using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>How the OpenAI API and the services that serve its API read their HTTP error objects.</summary>
internal static class OpenAiApiErrors
{
    /// <summary>OpenAI's code, the llama.cpp server's type, or a "context length" message; refused settings on a 400.</summary>
    public static ProviderErrorReading Reading { get; } = new(error => LlmContextOverflow.Responses(error.Code)
        || error.Type == "exceed_context_size_error" || LlmContextOverflow.Chat(null, error.Message))
    { RefusedSettings = error => LlmUnsupportedSetting.Read(error.Code, error.Param, error.Message) };
}
