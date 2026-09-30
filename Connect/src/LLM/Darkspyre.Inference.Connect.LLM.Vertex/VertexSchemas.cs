using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal static class VertexSchemas
{
    public static LlmStructuredOutputEnforcement? Enforcement(LlmStructuredOutputRequest? request,
        LlmProviderCapabilities capabilities, bool claude) =>
        claude ? AnthropicSchemas.Enforcement(request, capabilities) : GeminiSchemas.Enforcement(request, "Vertex");
}
