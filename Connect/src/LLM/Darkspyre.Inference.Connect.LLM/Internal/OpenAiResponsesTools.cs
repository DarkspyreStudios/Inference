using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class OpenAiResponsesTools
{
    /// <summary>Builds the input items; each tool output goes through the route's projection.</summary>
    public static JsonArray BuildInput(
        LlmChatRequest request,
        LlmConnection connection,
        Func<LlmChatMessage, JsonObject> mapTextMessage,
        Func<LlmToolOutput, ProjectedToolOutput> projectToolOutput)
    {
        var input = new JsonArray();

        foreach (LlmChatMessage message in request.Messages)
        {
            if (message.ContinuationState is not null)
            {
                AppendClones(input, ProviderContinuationStateCodec.DecodeMessage(message, connection, request.ModelId));
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
                        ["output"] = BuildToolOutput(projectToolOutput(result.Output))
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

    /// <summary>No parts or a single text part stays a string; anything richer is the ordered output-item list.</summary>
    private static JsonNode BuildToolOutput(ProjectedToolOutput projected)
    {
        if (projected.Parts is [])
        {
            return JsonValue.Create(string.Empty);
        }
        if (projected.Parts is [SentText only])
        {
            return JsonValue.Create(only.Text);
        }

        var output = new JsonArray();
        foreach (SentToolPart part in projected.Parts)
        {
            output.Add(part switch
            {
                SentText text => new JsonObject { ["type"] = "input_text", ["text"] = text.Text },
                SentImage image => new JsonObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = DataUrl(image.MediaType, image.Data)
                },
                SentFile file => new JsonObject
                {
                    ["type"] = "input_file",
                    ["filename"] = file.FileName,
                    ["file_data"] = DataUrl(file.MediaType, file.Data)
                },
                _ => throw new InvalidOperationException("Responses tool output does not carry this part natively.")
            });
        }
        return output;
    }

    private static string DataUrl(string mediaType, ReadOnlyMemory<byte> data) =>
        $"data:{mediaType};base64,{Convert.ToBase64String(data.Span)}";

    private static JsonObject BuildAttachment(LlmAttachment attachment) => attachment.Kind switch
    {
        LlmBinaryKind.Image => new JsonObject
        {
            ["type"] = "input_image",
            ["image_url"] = LlmAttachments.ToDataUrl(attachment)
        },
        LlmBinaryKind.Document => new JsonObject
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

    public static LlmContinuationState? EncodeContinuationState(
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

        return ProviderContinuationStateCodec.Encode(
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
