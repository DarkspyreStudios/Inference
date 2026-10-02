using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Builds the Gemini generateContent body shared by the Gemini API and Vertex AI routes.</summary>
internal static class GeminiRequest
{
    internal const string DummyThoughtSignature = "skip_thought_signature_validator";

    /// <summary>Converts an admitted frozen cache prefix through the generation codec, without issuing resource scope.</summary>
    internal static JsonObject BuildCachePrefix(LlmConnection connection, LlmContextCacheCreate request,
        Func<LlmToolOutput, ProjectedToolOutput> project, GeminiModelProfile reasoning,
        Func<LlmMessageAttachment, JsonObject>? attachment = null, Func<string, JsonObject>? nativeFile = null)
    {
        var prefix = new LlmChatRequest
        {
            ModelId = request.ModelId,
            Instructions = request.Instructions,
            Messages = request.Messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Resources = request.Resources
        };
        (JsonArray contents, JsonArray system) = BuildContents(connection, prefix, project, reasoning, attachment, nativeFile);
        ValidateToolOptions(connection, prefix);
        var payload = new JsonObject { ["contents"] = contents };
        AppendPrefixConfiguration(payload, system, prefix, reasoning, streamArguments: false);
        return payload;
    }

    private static (JsonArray Contents, JsonArray System) BuildContents(LlmConnection connection,
        LlmChatRequest request, Func<LlmToolOutput, ProjectedToolOutput> project, GeminiModelProfile reasoning,
        Func<LlmMessageAttachment, JsonObject>? attachmentProjection = null, Func<string, JsonObject>? nativeFile = null)
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
                parts = GeminiContinuation.WireParts(retained);
            }
            else
            {
                parts = new JsonArray();
                if (message.Content.Length > 0) { parts.Add(Text(message.Content)); }
                foreach (LlmMessageAttachment source in message.Attachments)
                {
                    if (attachmentProjection is not null) { parts.Add(attachmentProjection(source)); continue; }
                    LlmAttachment attachment = LlmAttachments.RequireBinary(source);
                    string mime = LlmAttachments.ResolveMediaType(attachment);
                    parts.Add(new JsonObject
                    {
                        ["inlineData"] = new JsonObject
                        { ["mimeType"] = mime, ["data"] = LlmAttachments.ToBase64(attachment) }
                    });
                }
                bool first = true;
                foreach (LlmToolCall call in ClientTools.Functions(message.ToolCalls))
                {
                    if (request.UnboundContinuationState != LlmUnboundContinuationState.Omit)
                    { throw new ArgumentException("Gemini tool-call history requires its original continuation state."); }
                    var part = new JsonObject
                    {
                        ["functionCall"] = new JsonObject { ["name"] = call.Name, ["args"] = JsonNode.Parse(call.ArgumentsJson) }
                    };
                    if (first && reasoning.RequiresThoughtSignatures) { part["thoughtSignature"] = DummyThoughtSignature; }
                    first = false;
                    AddCall(call.Id, call.Name, null);
                    parts.Add(part);
                }
            }
            // Without native call IDs, Gemini associates repeated names by their original order.
            // Restore that order from Connect IDs rather than trusting host result arrival order.
            string[] callOrder = calls.Keys.ToArray();
            foreach (LlmToolResult result in ClientTools.Functions(message.ToolResults).OrderBy(result => Array.IndexOf(callOrder, result.ToolCallId)))
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
                var nested = new JsonArray();
                var references = new JsonArray();
                foreach (SentToolPart part in output.Parts)
                {
                    if (part is SentNativeFile fileReference && nativeFile is not null)
                    {
                        JsonObject filePart = nativeFile(fileReference.FileId);
                        string fileName = DisplayName(nested.Count + 1, null);
                        filePart["fileData"]!["displayName"] = fileName;
                        nested.Add(filePart);
                        references.Add(new JsonObject { ["$ref"] = fileName });
                        continue;
                    }
                    (string? mime, ReadOnlyMemory<byte> data, string? name) = part switch
                    {
                        SentImage image => (image.MediaType, image.Data, image.FileName),
                        SentFile { IsPdf: true } file => (file.MediaType, file.Data, file.FileName),
                        _ => (null, default, null)
                    };
                    if (mime is null) { continue; }
                    string displayName = DisplayName(nested.Count + 1, name);
                    nested.Add(new JsonObject
                    {
                        ["inlineData"] = new JsonObject
                        { ["displayName"] = displayName, ["mimeType"] = mime, ["data"] = Convert.ToBase64String(data.Span) }
                    });
                    references.Add(new JsonObject { ["$ref"] = displayName });
                }
                if (nested.Count > 0)
                {
                    function["parts"] = nested;
                    function["response"]!["attachments"] = references;
                }
                if (call.WireId is not null) { function["id"] = call.WireId; }
                parts.Add(new JsonObject { ["functionResponse"] = function });
            }
            if (parts.Count > 0)
            {
                contents.Add(new JsonObject { ["role"] = message.Role == "assistant" ? "model" : "user", ["parts"] = parts });
            }
        }
        if (pending.Count != 0) { throw new ArgumentException("Every Gemini tool call requires a result before continuing generation."); }
        return (contents, system);

        void AddCall(string id, string name, string? wireId)
        {
            if (!calls.TryAdd(id, (name, wireId))) { throw new ArgumentException("Duplicate Gemini tool-call identity."); }
            pending.Add(id);
        }
    }

    public static JsonObject Build(LlmConnection connection, LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement, Func<LlmToolOutput, ProjectedToolOutput> project,
        GeminiModelProfile reasoning, bool reasoningText, bool streamArguments = false)
    {
        (JsonArray contents, JsonArray system) = BuildContents(connection, request, project, reasoning);
        var generation = new JsonObject { ["candidateCount"] = 1 };
        if (request.MaxOutputTokens is int maximum) { generation["maxOutputTokens"] = maximum; }
        if (request.Temperature is double temperature) { generation["temperature"] = temperature; }
        if (request.TopP is double topP) { generation["topP"] = topP; }
        if (request.TopK is int topK) { generation["topK"] = topK; }
        if (request.Seed is int seed) { generation["seed"] = seed; }
        if (request.PresencePenalty is double presence) { generation["presencePenalty"] = presence; }
        if (request.FrequencyPenalty is double frequency) { generation["frequencyPenalty"] = frequency; }
        if (request.StopSequences.Count > 0) { generation["stopSequences"] = System.Text.Json.JsonSerializer.SerializeToNode(request.StopSequences); }
        if (GeminiModelProfiles.ThinkingConfig(reasoning, request.ReasoningLevel, reasoningText) is { } thinking)
        {
            generation["thinkingConfig"] = thinking;
        }
        if (request.StructuredOutput is { } schema && enforcement == LlmStructuredOutputEnforcement.ProviderJsonSchema)
        {
            var format = new JsonObject
            {
                ["text"] = new JsonObject { ["mimeType"] = "APPLICATION_JSON", ["schema"] = JsonNode.Parse(schema.Schema.GetRawText()) }
            };
            generation["responseFormat"] = connection.Transport == LlmTransport.GoogleVertexAi ? new JsonArray(format) : format;
        }
        ValidateToolOptions(connection, request);
        RequestJson.ApplyProviderOptions(generation, connection, request);
        var payload = new JsonObject { ["contents"] = contents, ["generationConfig"] = generation };
        if (request.ExplicitCache is { } cache) { payload["cachedContent"] = cache.ResourceName; }
        else { AppendPrefixConfiguration(payload, system, request, reasoning, streamArguments); }
        return payload;
    }

    private static void ValidateToolOptions(LlmConnection connection, LlmChatRequest request)
    {
        if (connection.ProviderOptions.ContainsKey(AnthropicTools.OptionName) || request.ProviderOptions.ContainsKey(AnthropicTools.OptionName))
        { throw new NotSupportedException("Gemini does not accept Anthropic tool configuration."); }
    }

    private static void AppendPrefixConfiguration(JsonObject payload, JsonArray system, LlmChatRequest request,
        GeminiModelProfile reasoning, bool streamArguments)
    {
        if (system.Count > 0) { payload["systemInstruction"] = new JsonObject { ["parts"] = system }; }
        if (request.Tools.Count > 0)
        {
            var declarations = new JsonArray();
            foreach (LlmToolDefinition tool in ClientTools.Functions(request.Tools))
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
        if (streamArguments && reasoning.StreamedFunctionArguments && request.Tools.Count > 0)
        {
            payload["toolConfig"] ??= new JsonObject { ["functionCallingConfig"] = new JsonObject() };
            payload["toolConfig"]!["functionCallingConfig"]!["streamFunctionCallArguments"] = true;
        }
    }

    private static JsonObject Text(string text) => new() { ["text"] = text };

    private static string DisplayName(int ordinal, string? name)
    {
        string value = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + (name ?? "attachment");
        return new string(value.Take(128).Select(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-' ? character : '_').ToArray());
    }
}
