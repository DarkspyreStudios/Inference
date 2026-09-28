using Darkspyre.Inference.Common.LLM;
namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// Removes continuation state the target cannot replay, when the caller opted in with
/// <see cref="LlmUnboundContinuationState.Omit"/>. Works on a copy; the caller's request is unchanged.
/// </summary>
internal static class ContinuationStateProjection
{
    public static (LlmChatRequest Request, int Omitted) Project(LlmChatRequest request, LlmConnection connection)
    {
        if (!Enum.IsDefined(request.UnboundContinuationState))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Unknown unbound continuation state handling.");
        }
        // SelfHost delegates ownership/binding decisions to the selected engine.
        if (connection.Transport == LlmTransport.SelfHost || request.UnboundContinuationState == LlmUnboundContinuationState.Reject)
        {
            return (request, 0);
        }

        int omitted = 0;
        LlmChatMessage[] messages = new LlmChatMessage[request.Messages.Count];
        for (int index = 0; index < messages.Length; index++)
        {
            LlmChatMessage message = request.Messages[index];
            if (message.ContinuationState is { } state && !ProviderContinuationStateCodec.IsBound(state, connection, request.ModelId))
            {
                message = message with { ContinuationState = null };
                omitted++;
            }
            messages[index] = message;
        }
        return omitted == 0 ? (request, 0) : (request with { Messages = messages }, omitted);
    }
}
