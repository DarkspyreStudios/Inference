using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Owned by a protocol adapter. The session runtime never interprets native payloads.</summary>
internal interface ILlmContinuationCodec
{
    LlmContinuationState Encode(LlmConnection connection, string modelId, JsonArray content);
    bool IsBound(LlmContinuationState state, LlmConnection connection, string modelId);
    JsonArray DecodeMessage(LlmChatMessage message, LlmConnection connection, string modelId);
}

/// <summary>Resolves short-lived adapter identity before replay validation. Does not persist tokens.</summary>
internal interface ILlmConnectionPreparation
{
    Task<LlmConnection> PrepareAsync(LlmConnection connection, CancellationToken cancellationToken);
}
