using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal static class VertexSchemas
{
    public static LlmStructuredOutputEnforcement? Enforcement(LlmStructuredOutputRequest? request, bool claude) =>
        request is not null && claude ? LlmStructuredOutputEnforcement.ProviderJsonSchema : GeminiSchemas.Enforcement(request, "Vertex");
}
