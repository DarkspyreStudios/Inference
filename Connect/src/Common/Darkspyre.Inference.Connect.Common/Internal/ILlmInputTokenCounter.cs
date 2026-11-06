namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>A client whose provider counts a whole projected request's input tokens before it is sent.</summary>
internal interface ILlmInputTokenCounter
{
    /// <summary>The provider's input-token count for <paramref name="request"/>, or null when the provider cannot
    /// count this request (for example caller resources it would have to upload). Never a local estimate.</summary>
    ValueTask<long?> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request,
        CancellationToken cancellationToken);
}
