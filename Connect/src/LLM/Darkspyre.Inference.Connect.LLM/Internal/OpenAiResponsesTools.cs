using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class OpenAiResponsesTools
{
    public static JsonArray BuildInput(
        LlmChatRequest request,
        LlmConnection connection,
        Func<LlmChatMessage, JsonObject> mapTextMessage)
    {
        var input = new JsonArray();

        foreach (LlmChatMessage message in request.Messages)
        {
            if (message.ResponseState is not null)
            {
                AppendClones(input, ProviderResponseStateCodec.DecodeMessage(message, connection, request.ModelId));
                continue;
            }

            if (message.ToolResults.Count > 0)
            {
                foreach (LlmToolResult result in message.ToolResults)
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = result.ToolCallId,
                        ["output"] = BuildToolOutput(result)
                    });
                }

                if (message.Content.Length > 0 || message.Attachments.Count > 0)
                {
                    input.Add(mapTextMessage(message));
                }

                continue;
            }

            if (message.Content.Length > 0 || message.ToolCalls.Count == 0)
            {
                input.Add(mapTextMessage(message));
            }

            foreach (LlmToolCall call in message.ToolCalls)
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = call.Id,
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson
                });
            }
        }

        return input;
    }

    public static JsonObject BuildMessage(LlmChatMessage message)
    {
        if (message.Attachments.Count == 0)
        {
            return new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = message.Content
            };
        }

        var content = new JsonArray();
        if (message.Content.Length > 0)
        {
            content.Add(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = message.Content
            });
        }

        foreach (LlmAttachment attachment in message.Attachments)
        {
            content.Add(BuildAttachment(attachment));
        }

        return new JsonObject
        {
            ["role"] = message.Role,
            ["content"] = content
        };
    }

    private static JsonNode BuildToolOutput(LlmToolResult result)
    {
        if (result.Attachments.Count == 0)
        {
            return JsonValue.Create(result.Content)!;
        }

        var output = new JsonArray();
        if (result.Content.Length > 0)
        {
            output.Add(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = result.Content
            });
        }

        foreach (LlmAttachment attachment in result.Attachments)
        {
            output.Add(BuildAttachment(attachment));
        }

        return output;
    }

    private static JsonObject BuildAttachment(LlmAttachment attachment) => attachment.Kind switch
    {
        LlmAttachmentKind.Image => new JsonObject
        {
            ["type"] = "input_image",
            ["image_url"] = LlmAttachments.ToDataUrl(attachment)
        },
        LlmAttachmentKind.Document => new JsonObject
        {
            ["type"] = "input_file",
            ["filename"] = attachment.FileName,
            ["file_data"] = LlmAttachments.ToDataUrl(attachment)
        },
        _ => throw new ArgumentOutOfRangeException(nameof(attachment))
    };

    public static JsonArray BuildTools(IReadOnlyList<LlmToolDefinition> definitions)
    {
        var tools = new JsonArray();
        foreach (LlmToolDefinition definition in definitions)
        {
            var tool = new JsonObject
            {
                ["type"] = "function",
                ["name"] = definition.Name,
                ["parameters"] = JsonNode.Parse(definition.InputSchema.GetRawText()),
                ["strict"] = definition.Strict
            };
            if (!string.IsNullOrWhiteSpace(definition.Description))
            {
                tool["description"] = definition.Description;
            }

            tools.Add(tool);
        }

        return tools;
    }

    public static JsonNode BuildToolChoice(LlmToolChoice choice) => choice.Mode switch
    {
        LlmToolChoiceMode.Auto => JsonValue.Create("auto"),
        LlmToolChoiceMode.None => JsonValue.Create("none"),
        LlmToolChoiceMode.Required => JsonValue.Create("required"),
        LlmToolChoiceMode.Specific => new JsonObject
        {
            ["type"] = "function",
            ["name"] = choice.Name
        },
        _ => throw new ArgumentOutOfRangeException(nameof(choice))
    };

    public static LlmToolCall[] ReadToolCalls(JsonElement responseRoot)
    {
        if (!responseRoot.TryGetProperty("output", out JsonElement output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var calls = new List<LlmToolCall>();
        foreach (JsonElement item in output.EnumerateArray())
        {
            if (TryReadToolCall(item, out LlmToolCall? call) && call is not null)
            {
                calls.Add(call);
            }
        }

        return [.. calls];
    }

    public static bool TryReadToolCall(JsonElement item, out LlmToolCall? call)
    {
        call = null;
        if (LlmJson.GetString(item, "type") != "function_call")
        {
            return false;
        }

        string? id = LlmJson.GetString(item, "call_id") ?? LlmJson.GetString(item, "id");
        string? name = LlmJson.GetString(item, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        call = new LlmToolCall(
            id,
            name,
            LlmJson.GetString(item, "arguments") ?? "{}");
        return true;
    }

    public static LlmProviderResponseState? EncodeResponseState(
        JsonElement responseRoot,
        LlmConnection connection,
        string modelId)
    {
        if (!responseRoot.TryGetProperty("output", out JsonElement output)
            || output.ValueKind != JsonValueKind.Array
            || output.GetArrayLength() == 0)
        {
            return null;
        }

        return ProviderResponseStateCodec.Encode(
            connection, modelId, (JsonArray)JsonNode.Parse(output.GetRawText())!);
    }

    private static void AppendClones(JsonArray target, JsonArray source)
    {
        foreach (JsonNode? item in source)
        {
            target.Add(item?.DeepClone());
        }
    }
}

internal sealed class OpenAiToolCallStream
{
    private readonly Dictionary<int, LlmToolCall> _callsByIndex = [];
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);

    public IReadOnlyList<LlmStreamEvent> ObserveAdded(JsonElement root)
    {
        if (!root.TryGetProperty("item", out JsonElement item)
            || !OpenAiResponsesTools.TryReadToolCall(item, out LlmToolCall? call)
            || call is null)
        {
            return [];
        }

        int index = LlmJson.GetInt(root, "output_index") ?? _callsByIndex.Count;
        _callsByIndex[index] = call;
        if (_started.Add(call.Id))
        {
            return [new LlmToolCallStartedEvent(call.Id, call.Name, index)];
        }

        return [];
    }

    public IReadOnlyList<LlmStreamEvent> ObserveArgumentsDelta(JsonElement root)
    {
        int? index = LlmJson.GetInt(root, "output_index");
        string delta = LlmJson.GetString(root, "delta") ?? string.Empty;
        if (index is null || delta.Length == 0 || !_callsByIndex.TryGetValue(index.Value, out LlmToolCall? call))
        {
            return [];
        }

        return [new LlmToolCallArgumentsDeltaEvent(call.Id, delta)];
    }

    public IReadOnlyList<LlmStreamEvent> ObserveDone(JsonElement root)
    {
        if (!root.TryGetProperty("item", out JsonElement item)
            || !OpenAiResponsesTools.TryReadToolCall(item, out LlmToolCall? call)
            || call is null)
        {
            return [];
        }

        int index = LlmJson.GetInt(root, "output_index") ?? _callsByIndex.Count;
        var events = new List<LlmStreamEvent>(2);
        if (_started.Add(call.Id))
        {
            events.Add(new LlmToolCallStartedEvent(call.Id, call.Name, index));
        }

        if (_completed.Add(call.Id))
        {
            events.Add(new LlmToolCallCompletedEvent(call));
        }

        return events;
    }

    public IReadOnlyList<LlmStreamEvent> CompleteMissing(IReadOnlyList<LlmToolCall> calls)
    {
        var events = new List<LlmStreamEvent>();
        for (int index = 0; index < calls.Count; index++)
        {
            LlmToolCall call = calls[index];
            if (_started.Add(call.Id))
            {
                events.Add(new LlmToolCallStartedEvent(call.Id, call.Name, index));
                if (call.ArgumentsJson.Length > 0)
                {
                    events.Add(new LlmToolCallArgumentsDeltaEvent(call.Id, call.ArgumentsJson));
                }
            }

            if (_completed.Add(call.Id))
            {
                events.Add(new LlmToolCallCompletedEvent(call));
            }
        }

        return events;
    }
}
