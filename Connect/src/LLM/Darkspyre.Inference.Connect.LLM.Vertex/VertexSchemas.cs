using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal static class VertexSchemas
{
    public static LlmStructuredOutputEnforcement? Enforcement(LlmChatRequest request,
        LlmProviderCapabilities capabilities, bool claude) =>
        claude ? AnthropicSchemas.Enforcement(request, capabilities)
            : request.StructuredOutput is { } structured ? StructuredOutput.ResolveEnforcement(structured, capabilities) : null;
}
