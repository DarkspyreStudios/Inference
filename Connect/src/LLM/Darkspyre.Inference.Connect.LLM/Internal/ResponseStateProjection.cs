namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// Removes response state the target cannot replay, when the caller opted in with
/// <see cref="LlmUnboundResponseState.Omit"/>. Works on a copy; the caller's request is unchanged.
/// </summary>
internal static class ResponseStateProjection
{
    public static (LlmChatRequest Request, int Omitted) Project(LlmChatRequest request, LlmConnection connection)
    {
        if (!Enum.IsDefined(request.UnboundResponseState))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Unknown unbound response state handling.");
        }
        if (request.UnboundResponseState == LlmUnboundResponseState.Reject)
        {
            return (request, 0);
        }

        int omitted = 0;
        LlmChatMessage[] messages = new LlmChatMessage[request.Messages.Count];
        for (int index = 0; index < messages.Length; index++)
        {
            LlmChatMessage message = request.Messages[index];
            if (message.ResponseState is { } state && !ProviderResponseStateCodec.IsBound(state, connection, request.ModelId))
            {
                message = message with { ResponseState = null };
                omitted++;
            }
            messages[index] = message;
        }
        return omitted == 0 ? (request, 0) : (request with { Messages = messages }, omitted);
    }
}
