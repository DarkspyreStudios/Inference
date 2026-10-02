namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class ExplicitCaches
{
    internal static void ValidateProviderTokenCount(LlmConnection connection, LlmChatRequest request)
    {
        if (connection.Transport == LlmTransport.GoogleVertexAi && request.ExplicitCache is not null)
            throw new NotSupportedException("Vertex does not implement exact counting for an explicitly cached request.");
    }

    internal static void Validate(LlmChatRequest request, bool supportsExplicitCache)
    {
        if (request.ExplicitCache is not null && !supportsExplicitCache)
            throw new NotSupportedException("The selected route does not implement explicit cache selection.");
    }
}
