using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Recognizes only the route-specific provider facts that identify input context overflow.</summary>
internal static class LlmContextOverflow
{
    internal static bool Responses(string? code) => code == "context_length_exceeded";

    internal static bool Responses(JsonElement root) =>
        Responses(LlmJson.GetString(LlmJson.GetObject(root, "error") ?? root, "code"));

    internal static bool Chat(string? code, string? message) => Responses(code)
        || Contains(message, "maximum context length") || Contains(message, "context length");

    internal static bool Anthropic(string? type, string? message) => type == "invalid_request_error"
        && Contains(message, "prompt is too long");

    internal static bool ClaudeCli(string? detail) => Contains(detail, "prompt is too long");

    internal static bool Google(string? status, string? message) => status == "INVALID_ARGUMENT"
        && Contains(message, "exceeds the maximum number of tokens");

    internal static bool BedrockValidation(string? message) => Contains(message, "Input is too long")
        || Contains(message, "too many input tokens");

    internal static bool Http(LlmTransport transport, string? code, string? type, string? message) => transport switch
    {
        LlmTransport.OpenAiResponses or LlmTransport.OpenAiCodex or LlmTransport.AzureOpenAiResponses => Responses(code),
        LlmTransport.OpenAiCompatibleChat or LlmTransport.AzureOpenAiChat => Chat(code, message),
        // Vertex Claude reaches this boundary through the same Anthropic Messages adapter.
        LlmTransport.AnthropicMessages or LlmTransport.GoogleVertexAi => Anthropic(type, message),
        _ => false
    };

    internal static bool Google(JsonElement root)
    {
        JsonElement error = LlmJson.GetObject(root, "error") ?? root;
        return Google(LlmJson.GetString(error, "status"), LlmJson.GetString(error, "message"));
    }

    private static bool Contains(string? message, string phrase) =>
        message?.Contains(phrase, StringComparison.OrdinalIgnoreCase) == true;
}
