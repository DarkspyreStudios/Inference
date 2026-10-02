namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class ExplicitCaches
{
    internal static void Validate(LlmChatRequest request, bool supportsExplicitCache)
    {
        if (request.ExplicitCache is not null && !supportsExplicitCache)
            throw new NotSupportedException("The selected route does not implement explicit cache selection.");
    }
}
