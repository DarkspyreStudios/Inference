using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal sealed class VertexContinuationCodec : ILlmContinuationCodec
{
    private const string Owner = "darkspyre.connect.llm.google-vertex-ai";

    public bool IsBound(LlmContinuationState state, LlmConnection connection, string modelId) =>
        state.FormatVersion == 2 && state.ImplementationId == Owner && state.ModelId == modelId
        && connection.ResolvedContinuationScope is { } scope && state.ScopeId == scope;

    public LlmContinuationState Encode(LlmConnection connection, string modelId, JsonArray content)
    {
        JsonArray projected = VertexEndpoints.ParseModel(modelId).Publisher == "anthropic"
            ? JsonNode.Parse(ProviderContinuationStateCodec.Encode(AnthropicConnection(connection), modelId, content).Payload)!.AsArray()
            : GeminiContinuation.Validate(content);
        return new()
        {
            ImplementationId = Owner,
            ModelId = modelId,
            ScopeId = connection.ResolvedContinuationScope ?? throw new InvalidOperationException("Vertex identity is unresolved."),
            Payload = projected.ToJsonString()
        };
    }

    public JsonArray DecodeMessage(LlmChatMessage message, LlmConnection connection, string modelId)
    {
        var state = message.ContinuationState ?? throw new ArgumentException("Missing Vertex continuation state.");
        if (!IsBound(state, connection, modelId) || state.Payload is null || state.Payload.Length > GeminiContinuation.MaxPayloadLength)
        {
            throw new ArgumentException("Vertex continuation state has an incompatible binding or size.");
        }
        try
        {
            if (VertexEndpoints.ParseModel(modelId).Publisher == "anthropic")
            {
                LlmConnection native = AnthropicConnection(connection);
                var nativeState = state with
                {
                    ImplementationId = ProviderContinuationStateCodec.ImplementationId(LlmTransport.AnthropicMessages),
                    ScopeId = ProviderContinuationStateCodec.Scope(native)
                };
                return ProviderContinuationStateCodec.DecodeMessage(message with { ContinuationState = nativeState }, native, modelId);
            }

            return GeminiContinuation.DecodeVisible(state.Payload, message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ArgumentException("Invalid Vertex assistant continuation content.");
        }
    }

    private static LlmConnection AnthropicConnection(LlmConnection connection) => connection with
    {
        Transport = LlmTransport.AnthropicMessages,
        ContinuationCodec = null,
        ResolvedContinuationScope = null
    };
}
