using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

/// <summary>The Vertex Gemini route sends the shared Gemini generateContent body unchanged.</summary>
internal static class VertexGeminiRequest
{
    public static JsonObject Build(LlmConnection connection, LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement, Func<LlmToolOutput, ProjectedToolOutput> project) =>
        GeminiRequest.Build(connection, request, enforcement, project);
}
