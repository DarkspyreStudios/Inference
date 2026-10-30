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
    [InlineData(nameof(LlmTransport.OpenAiCodex),
        """[{"type":"compaction","id":"cmp_1","encrypted_content":"opaque-summary"},""" + Answer + "]")]
    [InlineData(nameof(LlmTransport.XaiResponses),
        """[{"type":"x_search_call","id":"xs_1","status":"completed"},""" + Answer + "]")]
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

    /// <summary>Under Reject, a Codex request whose assistant message carries state bound to another model is refused
    /// before anything is sent.</summary>
    [Fact]
    public async Task RejectRefusesCodexStateBoundToAnotherModel()
    {
        using var handler = new DirectCodexTests.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new OpenAiCodexLlmClient(new HttpClient(handler));
        LlmConnection connection = DirectCodexTests.Connection();
        LlmContinuationState other = CodexReplay.Codec.Encode(connection, "other-model", (JsonArray)JsonNode.Parse("[" + Answer + "]")!);
        var request = new LlmChatRequest
        {
            ModelId = "model",
            UnboundContinuationState = LlmUnboundContinuationState.Reject,
            Messages = [new("user", "hello"), new("assistant", "answer") { ContinuationState = other }, new("user", "again")]
        };

        ArgumentException refusal = await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
        });

        Assert.Equal("Response state does not match this version, transport, model, or account scope.", refusal.Message);
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>A state whose projected message item is not the assistant's is refused.</summary>
    [Fact]
    public void AProjectedUserMessageIsRefused()
    {
        LlmConnection connection = DirectCodexTests.OpenAiConnection("https://api.openai.com/v1");
        const string payload = """[{"type":"message","id":"msg_1","role":"user","status":"completed","content":[{"type":"output_text","text":"answer"}]}]""";
        LlmChatMessage message = new("assistant", "answer") { ContinuationState = SavedState(connection, "model", payload) };

        ArgumentException refusal = Assert.Throws<ArgumentException>(() => OpenAiApiReplay.Codec.DecodeMessage(message, connection, "model"));

        Assert.Equal("Response state cannot contain non-assistant messages.", refusal.Message);
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
