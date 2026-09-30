using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

/// <summary>Shared Messages protocol with Vertex-owned routing and authentication.</summary>
internal sealed class VertexClaudeProtocol(HttpClient http, string token) : AnthropicMessagesLlmClient(http)
{
    public override LlmTransport Transport => LlmTransport.GoogleVertexAi;
    public override LlmProviderDefinition ProviderDefinition => VertexProviderDefinition.Value;
    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) =>
        VertexLlmClient.Capabilities(connection, modelId);
    protected override string ReasoningModelId(string modelId) => VertexEndpoints.ParseModel(modelId).Model;

    protected override HttpRequestMessage CreateHttpRequest(LlmConnection connection, LlmChatRequest request, JsonObject payload, bool stream)
    {
        var result = new HttpRequestMessage(HttpMethod.Post, VertexEndpoints.Generation(connection, request.ModelId, stream))
        { Content = JsonContent.Create(payload) };
        result.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return result;
    }

    protected override JsonObject BuildPayload(LlmConnection connection, LlmChatRequest request,
        Func<LlmToolOutput, ProjectedToolOutput> projectToolOutput, bool stream)
    {
        if (request.ReasoningLevel is > LlmReasoningLevel.None
            && (request.Temperature is not null || request.ToolChoice?.Mode is LlmToolChoiceMode.Required or LlmToolChoiceMode.Specific))
        { throw new NotSupportedException("Claude thinking cannot be combined with sampling temperature or forced tool choice."); }
        if (request.Messages.Count > 0 && request.Messages[^1].Role == "assistant")
        { throw new NotSupportedException("This Claude model does not accept assistant prefilling."); }
        JsonObject payload = base.BuildPayload(connection, request, projectToolOutput, stream);
        payload.Remove("model");
        payload["anthropic_version"] = "vertex-2023-10-16";
        if (request.StructuredOutput is { } structured && GetCapabilities(connection, request.ModelId).SupportsJsonSchema)
        {
            JsonObject output = payload["output_config"]?.AsObject() ?? new JsonObject();
            output["format"] = new JsonObject
            { ["type"] = "json_schema", ["schema"] = JsonNode.Parse(structured.Schema.GetRawText()) };
            if (payload["output_config"] is null) { payload["output_config"] = output; }
        }
        return payload;
    }
}
