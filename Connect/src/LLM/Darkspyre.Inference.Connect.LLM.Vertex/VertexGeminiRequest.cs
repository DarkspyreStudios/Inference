using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal static class VertexGeminiRequest
{
    public static JsonObject Build(LlmConnection connection, LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement, Func<LlmToolOutput, ProjectedToolOutput> project)
    {
        var contents = new JsonArray();
        var calls = new Dictionary<string, (string Name, string? WireId)>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var system = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.Instructions)) { system.Add(Text(request.Instructions)); }
        foreach (LlmChatMessage message in request.Messages)
        {
            if (message.Role == "system")
            {
                if (message.ToolCalls.Count != 0 || message.ToolResults.Count != 0 || message.ContinuationState is not null)
                { throw new ArgumentException("A system message cannot carry tool or continuation content."); }
                system.Add(Text(message.Content));
                continue;
            }
            if (message.Role is not ("user" or "assistant" or "tool"))
            { throw new ArgumentException("Unsupported Gemini message role."); }
            if (pending.Count > 0 && message.ToolResults.Count == 0)
            { throw new ArgumentException("Gemini tool results must immediately follow the assistant call group."); }
            JsonArray parts;
            if (message.ContinuationState is not null)
            {
                JsonArray retained = ProviderContinuationStateCodec.DecodeMessage(message, connection, request.ModelId);
                foreach (JsonObject part in retained.OfType<JsonObject>().Where(part => part["functionCall"] is not null))
                {
                    JsonNode call = part["functionCall"]!;
                    AddCall((string)part["connectCallId"]!, (string)call["name"]!, (string?)call["id"]);
                }
                parts = VertexContinuationCodec.WireParts(retained);
            }
            else
            {
                parts = new JsonArray();
                if (message.Content.Length > 0) { parts.Add(Text(message.Content)); }
                foreach (LlmAttachment attachment in message.Attachments)
                {
                    string mime = LlmAttachments.ResolveMediaType(attachment);
                    parts.Add(new JsonObject
                    {
                        ["inlineData"] = new JsonObject
                        { ["mimeType"] = mime, ["data"] = LlmAttachments.ToBase64(attachment) }
                    });
                }
                foreach (LlmToolCall call in message.ToolCalls)
                {
                    // Gemini 3 signed tool turns cannot be reconstructed from the visible call alone.
                    _ = call;
                    throw new ArgumentException("Gemini tool-call history requires its original continuation state.");
                }
            }
            // Without native call IDs, Gemini associates repeated names by their original order.
            // Restore that order from Connect IDs rather than trusting host result arrival order.
            string[] callOrder = calls.Keys.ToArray();
            foreach (LlmToolResult result in message.ToolResults.OrderBy(result => Array.IndexOf(callOrder, result.ToolCallId)))
            {
                if (!calls.TryGetValue(result.ToolCallId, out var call) || !pending.Remove(result.ToolCallId))
                { throw new ArgumentException("A Gemini tool result must match one unresolved call in the retained history."); }
                if (call.WireId is null && callOrder.TakeWhile(id => id != result.ToolCallId)
                    .Any(id => pending.Contains(id) && calls[id].Name == call.Name))
                { throw new ArgumentException("A partial same-name tool result group is ambiguous without native call IDs."); }
                ProjectedToolOutput output = project(result.Output);
                var function = new JsonObject
                {
                    ["name"] = call.Name,
                    ["response"] = new JsonObject { [output.IsError ? "error" : "output"] = output.Text }
                };
                if (call.WireId is not null) { function["id"] = call.WireId; }
                parts.Add(new JsonObject { ["functionResponse"] = function });
            }
            if (parts.Count > 0)
            {
                contents.Add(new JsonObject { ["role"] = message.Role == "assistant" ? "model" : "user", ["parts"] = parts });
            }
        }
        if (pending.Count != 0) { throw new ArgumentException("Every Gemini tool call requires a result before continuing generation."); }
        var generation = new JsonObject { ["candidateCount"] = 1 };
        if (request.MaxOutputTokens is int maximum) { generation["maxOutputTokens"] = maximum; }
        if (request.Temperature is double temperature) { generation["temperature"] = temperature; }
        if (request.ReasoningLevel is { } level)
        {
            generation["thinkingConfig"] = new JsonObject
            { ["thinkingLevel"] = level.ToString().ToUpperInvariant(), ["includeThoughts"] = true };
        }
        if (request.StructuredOutput is { } schema && enforcement == LlmStructuredOutputEnforcement.ProviderJsonSchema)
        {
            generation["responseMimeType"] = "application/json";
            generation["responseJsonSchema"] = JsonNode.Parse(schema.Schema.GetRawText());
        }
        var payload = new JsonObject { ["contents"] = contents, ["generationConfig"] = generation };
        if (system.Count > 0) { payload["systemInstruction"] = new JsonObject { ["parts"] = system }; }
        if (request.Tools.Count > 0)
        {
            var declarations = new JsonArray();
            foreach (LlmToolDefinition tool in request.Tools)
            {
                var definition = new JsonObject
                { ["name"] = tool.Name, ["parametersJsonSchema"] = JsonNode.Parse(tool.InputSchema.GetRawText()) };
                if (tool.Description is not null) { definition["description"] = tool.Description; }
                declarations.Add(definition);
            }
            payload["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        }
        if (request.ToolChoice is { } choice)
        {
            var config = new JsonObject
            {
                ["mode"] = choice.Mode switch
                {
                    LlmToolChoiceMode.Auto => "AUTO",
                    LlmToolChoiceMode.None => "NONE",
                    LlmToolChoiceMode.Required or LlmToolChoiceMode.Specific => "ANY",
                    _ => throw new ArgumentException("Unsupported tool choice.")
                }
            };
            if (choice.Mode == LlmToolChoiceMode.Specific) { config["allowedFunctionNames"] = new JsonArray(choice.Name); }
            payload["toolConfig"] = new JsonObject { ["functionCallingConfig"] = config };
        }
        return payload;

        void AddCall(string id, string name, string? wireId)
        {
            if (!calls.TryAdd(id, (name, wireId))) { throw new ArgumentException("Duplicate Gemini tool-call identity."); }
            pending.Add(id);
        }
    }

    private static JsonObject Text(string text) => new() { ["text"] = text };
}
