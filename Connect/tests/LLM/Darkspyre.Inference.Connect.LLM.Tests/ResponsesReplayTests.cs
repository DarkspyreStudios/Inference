using System.Net;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>Each Responses provider replays continuation state through its own codec, in the format saved conversations
/// already hold.</summary>
public sealed class ResponsesReplayTests
{
    private const string Answer = """{"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"answer"}]}""";

    /// <summary>A state in today's saved format, with each provider's own hosted or compaction item, still replays
    /// through the codec its client returns.</summary>
    [Theory]
    [InlineData(nameof(LlmTransport.OpenAiResponses),
        """[{"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"search","query":"weather"}},""" + Answer + "]")]
    public void SavedStateReplaysThroughTheClientsCodec(string transportName, string payload)
    {
        var transport = Enum.Parse<LlmTransport>(transportName);
        using var http = new HttpClient();
        ResponsesLlmClientBase client = transport switch
        {
            LlmTransport.OpenAiResponses => new OpenAiResponsesLlmClient(http),
            LlmTransport.OpenAiCodex => new OpenAiCodexLlmClient(http),
            _ => new GrokResponsesLlmClient(http, TimeProvider.System)
        };
        LlmConnection connection = new() { Version = "1", Transport = transport, Endpoint = client.ProviderDefinition.DefaultEndpoint };
        LlmChatMessage message = new("assistant", "answer") { ContinuationState = SavedState(connection, "model", payload) };

        JsonArray replayed = ProviderContinuationStateCodec.DecodeMessage(message, connection, "model", client.ContinuationCodec);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload), replayed));
    }

    /// <summary>A continuation state as saved conversations hold it: the transport's implementation id, format version 2,
    /// the model and the account scope.</summary>
    private static LlmContinuationState SavedState(LlmConnection connection, string modelId, string payload) => new()
    {
        ImplementationId = "darkspyre.connect.llm." + connection.Transport,
        ModelId = modelId,
        ScopeId = ProviderContinuationStateCodec.Scope(connection),
        Payload = payload
    };
}
