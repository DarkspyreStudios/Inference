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
        Func<LlmToolOutput, ProjectedToolOutput> projectToolOutput,
        int? messageCount = null)
    {
        var input = new JsonArray();
        IReadOnlyDictionary<string, LlmShellCall> shellCalls = ClientTools.Validate(request, false);

        // Accounting may project a prefix, but admission always validates the complete history.
        foreach (LlmChatMessage message in request.Messages.Take(messageCount ?? request.Messages.Count))
        {
            if (message.ContinuationState is not null)
            {
                AppendClones(input, ProviderContinuationStateCodec.DecodeMessage(message, connection, request.ModelId));
                continue;
            }

            if (message.ToolResults.Count > 0)
            {
                foreach (LlmClientToolResult result in message.ToolResults)
                {
                    if (result is LlmShellCallResult shell)
                    {
                        var output = new JsonArray();
                        foreach (LlmShellCommandResult command in shell.Output)
                        {
                            var item = new JsonObject
                            {
                                ["outcome"] = command.Outcome switch
                                {
                                    LlmShellExitedOutcome exit => new JsonObject { ["type"] = "exit", ["exit_code"] = exit.ExitCode },
                                    LlmShellTimedOutOutcome => new JsonObject { ["type"] = "timeout" },
                                    _ => throw new NotSupportedException("Unknown shell outcome.")
                                }
                            };
                            if (command.Stdout is not null) item["stdout"] = command.Stdout;
                            if (command.Stderr is not null) item["stderr"] = command.Stderr;
                            output.Add(item: item);
                        }
                        var itemOutput = new JsonObject
                        {
                            ["type"] = "shell_call_output",
                            ["call_id"] = shell.ToolCallId,
                            ["output"] = output
                        };
                        if (shellCalls[shell.ToolCallId].MaxOutputLength is { } limit)
                            itemOutput["max_output_length"] = limit;
                        input.Add(item: itemOutput);
                    }
                    else if (result is LlmToolResult function)
                        input.Add(item: new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = function.ToolCallId,
                            ["output"] = BuildToolOutput(projectToolOutput(function.Output))
                        });
                    else throw new NotSupportedException("Unknown client tool result.");
                }

                if (message.Content.Length > 0 || message.Attachments.Count > 0)
                {
                    input.Add(item: mapTextMessage(message));
                }

                continue;
            }

            if (message.Content.Length > 0 || message.ToolCalls.Count == 0)
            {
                input.Add(item: mapTextMessage(message));
            }

            foreach (LlmClientToolCall call in message.ToolCalls)
            {
                if (call is LlmShellCall shell)
                {
                    var action = new JsonObject
                    {
                        ["commands"] = new JsonArray(shell.Commands.Select(command => (JsonNode?)JsonValue.Create(command)).ToArray())
                    };
                    if (shell.TimeoutMs is { } timeout) action["timeout_ms"] = timeout;
                    if (shell.MaxOutputLength is { } limit) action["max_output_length"] = limit;
                    input.Add(item: new JsonObject { ["type"] = "shell_call", ["call_id"] = shell.Id, ["action"] = action });
                }
                else if (call is LlmToolCall function)
                    input.Add(item: new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = function.Id,
                        ["name"] = function.Name,
                        ["arguments"] = function.ArgumentsJson
                    });
                else throw new NotSupportedException("Unknown client tool call.");
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
            content.Add(item: new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = message.Content
            });
        }

        foreach (LlmMessageAttachment attachment in message.Attachments)
        {
            content.Add(item: attachment is LlmFileReference file
                ? new JsonObject { ["type"] = "input_file", ["file_id"] = file.File.FileId }
                : BuildAttachment(LlmAttachments.RequireBinary(attachment)));
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
            output.Add(item: part switch
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

    public static JsonArray BuildTools(IReadOnlyList<LlmClientToolDefinition> definitions)
    {
        var tools = new JsonArray();
        foreach (LlmClientToolDefinition definition in definitions)
        {
            if (definition is LlmShellTool shell)
            {
                tools.Add(item: new JsonObject
                {
                    ["type"] = "shell",
                    ["environment"] = new JsonObject
                    {
                        ["type"] = "local",
                        ["skills"] = new JsonArray(shell.Skills.Select(skill => (JsonNode)new JsonObject
                        {
                            ["name"] = skill.Name,
                            ["description"] = skill.Description,
                            ["path"] = skill.Path
                        }).ToArray())
                    }
                });
                continue;
            }
            if (definition is not LlmToolDefinition function)
                throw new NotSupportedException("Unknown client tool definition.");
            var tool = new JsonObject
            {
                ["type"] = "function",
                ["name"] = function.Name,
                ["parameters"] = JsonNode.Parse(function.InputSchema.GetRawText()),
                ["strict"] = function.Strict
            };
            if (!string.IsNullOrWhiteSpace(function.Description))
            {
                tool["description"] = function.Description;
            }

            tools.Add(item: tool);
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

    public static LlmClientToolCall[] ReadToolCalls(JsonElement responseRoot, LlmTransport transport)
    {
        if (!responseRoot.TryGetProperty("output", out JsonElement output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var calls = new List<LlmClientToolCall>();
        foreach (JsonElement item in output.EnumerateArray())
        {
            if (TryReadToolCall(item, false, out LlmClientToolCall? call) && call is not null)
            {
                calls.Add(call);
            }
        }

        return [.. calls];
    }

    public static bool TryReadToolCall(JsonElement item, bool supportsShell, out LlmClientToolCall? call)
    {
        call = null;
        if (LlmJson.GetString(item, "type") == "shell_call")
        {
            if (!supportsShell) throw new NotSupportedException("This route does not implement shell calls.");
            if (!item.TryGetProperty("call_id", out JsonElement identity)
                || !item.TryGetProperty("action", out JsonElement action)
                || action.ValueKind != JsonValueKind.Object || !action.TryGetProperty("commands", out JsonElement commands))
                throw new ArgumentException("Shell calls require identity, action and commands.");
            string shellId = identity.GetString()!;
            ArgumentException.ThrowIfNullOrWhiteSpace(shellId);
            if (action.TryGetProperty("type", out JsonElement actionType)
                && actionType.ValueKind != JsonValueKind.Null && actionType.GetString() != "exec")
                throw new ArgumentException("Unknown shell action.");
            if (commands.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Shell commands must be an ordered array.");
            string[] commandText = commands.EnumerateArray().Select(command => command.GetString()
                ?? throw new ArgumentException("Shell commands must be strings.")).ToArray();
            long? timeout = null;
            if (action.TryGetProperty("timeout_ms", out JsonElement timeoutValue) && timeoutValue.ValueKind != JsonValueKind.Null)
                timeout = timeoutValue.TryGetInt64(out long actualTimeout) ? actualTimeout : throw new ArgumentException("Shell timeout must be INT64.");
            int? limit = null;
            if (action.TryGetProperty("max_output_length", out JsonElement limitValue) && limitValue.ValueKind != JsonValueKind.Null)
                limit = limitValue.TryGetInt32(out int actualLimit) ? actualLimit : throw new ArgumentException("Shell output limit must be INT32.");
            if (item.TryGetProperty("environment", out JsonElement environment)
                && environment.ValueKind != JsonValueKind.Null
                && (!environment.TryGetProperty("type", out JsonElement environmentType) || environmentType.GetString() != "local"))
                throw new ArgumentException("Unknown shell environment.");
            call = new LlmShellCall(shellId, commandText, timeout, limit);
            return true;
        }
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
    private readonly HashSet<string> _argumentsObserved = new(StringComparer.Ordinal);

    public IReadOnlyList<LlmStreamEvent> ObserveAdded(JsonElement root)
    {
        if (!root.TryGetProperty("item", out JsonElement item)
            || LlmJson.GetString(item, "type") != "function_call"
            || !OpenAiResponsesTools.TryReadToolCall(item, false, out LlmClientToolCall? observed)
            || observed is not LlmToolCall call)
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

        _argumentsObserved.Add(call.Id);
        return [new LlmToolCallArgumentsDeltaEvent(call.Id, delta)];
    }

    public IReadOnlyList<LlmStreamEvent> ObserveDone(JsonElement root)
    {
        if (!root.TryGetProperty("item", out JsonElement item)
            || LlmJson.GetString(item, "type") != "function_call"
            || !OpenAiResponsesTools.TryReadToolCall(item, false, out LlmClientToolCall? observed)
            || observed is not LlmToolCall call)
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

    public IReadOnlyList<LlmStreamEvent> CompleteMissing(IReadOnlyList<LlmClientToolCall> calls,
        bool emitMissingArguments = false)
    {
        var events = new List<LlmStreamEvent>();
        for (int index = 0; index < calls.Count; index++)
        {
            if (calls[index] is LlmShellCall shell)
            {
                if (_completed.Add(shell.Id)) events.Add(new LlmShellCallEvent((LlmShellCall)ClientTools.Snapshot(shell)));
                continue;
            }
            LlmToolCall call = calls[index] as LlmToolCall ?? throw new NotSupportedException("Unknown client tool call.");
            if (_started.Add(call.Id))
            {
                events.Add(new LlmToolCallStartedEvent(call.Id, call.Name, index));
                if (call.ArgumentsJson.Length > 0 && _argumentsObserved.Add(call.Id))
                    events.Add(new LlmToolCallArgumentsDeltaEvent(call.Id, call.ArgumentsJson));
            }

            if (emitMissingArguments && call.ArgumentsJson.Length > 0 && _argumentsObserved.Add(call.Id))
                events.Add(new LlmToolCallArgumentsDeltaEvent(call.Id, call.ArgumentsJson));

            if (_completed.Add(call.Id))
            {
                events.Add(new LlmToolCallCompletedEvent(call));
            }
        }

        return events;
    }
}
