using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Recognizes only the route-specific provider facts that identify input context overflow.</summary>
internal static class LlmContextOverflow
{
    internal static bool Responses(string? code) => code == "context_length_exceeded";

    internal static bool Chat(string? code, string? message) => Responses(code)
        || Contains(message, "maximum context length") || Contains(message, "context length");

    // Anthropic Messages uses this; Anthropic Messages is out of the build (Providers/AnthropicMessagesLlmClient.cs and
    // ManagedAnthropicAgentClient.cs, under Compile Remove). It moves into that provider's own library when the provider
    // returns to the build.
    internal static bool Anthropic(string? type, string? message) => type == "invalid_request_error"
        && Contains(message, "prompt is too long");

    // The Gemini API and Google Vertex AI use this; they are out of the build (Providers/GeminiApiLlmClient*.cs and
    // Internal/Gemini/, under Compile Remove; Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Vertex, outside the
    // solution). It moves into their own libraries when they return to the build.
    internal static bool Google(string? status, string? message) => status == "INVALID_ARGUMENT"
        && Contains(message, "exceeds the maximum number of tokens");

    // AWS Bedrock uses this; AWS Bedrock is out of the build (Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Bedrock,
    // outside the solution). It moves into that provider's own library when the provider returns to the build.
    internal static bool BedrockValidation(string? message) => Contains(message, "Input is too long")
        || Contains(message, "too many input tokens");

    // Azure OpenAI, OpenAI-compatible Chat, Anthropic Messages and Google Vertex AI use this; they are out of the build
    // (Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Azure and .LLM.Vertex, outside the solution;
    // Providers/ChatCompletionsLlmClientBase.cs and AnthropicMessagesLlmClient.cs, under Compile Remove). It moves into
    // their own libraries when they return to the build.
    internal static bool Http(LlmTransport transport, string? code, string? type, string? message) => transport switch
    {
        LlmTransport.AzureOpenAiResponses => Responses(code),
        LlmTransport.OpenAiCompatibleChat or LlmTransport.AzureOpenAiChat => Chat(code, message),
        // Vertex Claude reaches this boundary through the same Anthropic Messages adapter.
        LlmTransport.AnthropicMessages or LlmTransport.GoogleVertexAi => Anthropic(type, message),
        _ => false
    };

    // The Gemini API and Google Vertex AI use this; they are out of the build (Providers/GeminiApiLlmClient*.cs and
    // Internal/Gemini/, under Compile Remove; Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Vertex, outside the
    // solution). It moves into their own libraries when they return to the build.
    internal static bool Google(JsonElement root)
    {
        JsonElement error = LlmJson.GetObject(root, "error") ?? root;
        return Google(LlmJson.GetString(error, "status"), LlmJson.GetString(error, "message"));
    }

    private static bool Contains(string? message, string phrase) =>
        message?.Contains(phrase, StringComparison.OrdinalIgnoreCase) == true;
}
