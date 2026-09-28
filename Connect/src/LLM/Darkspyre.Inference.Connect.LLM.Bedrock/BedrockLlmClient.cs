using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.Documents;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockLlmClient(
    BedrockClientFactory factory,
    BedrockLoggingGuard loggingGuard) : ILlmClient, ILlmContextEstimator
{
    internal const string RequestIdMetadataKey = "request-id";

    private static long _requestCounter;

    private static readonly LlmProviderCapabilities Defaults = new()
    {
        OutputLimitEnforcement = Common.LLM.LlmOutputLimitEnforcement.ProviderEnforced,
        SupportsStreaming = true,
        SupportsStreamingUsage = false,
        SupportsModelListing = true,
        SupportsJsonSchema = false,
        SupportsJsonObject = false,
        SupportsTemperature = true,
        SupportsToolUse = true,
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.Required, LlmToolChoiceMode.Specific }.ToFrozenSet(),
            SupportsParallelCalls = true
        },
        SupportsPromptImages = true,
        SupportsPromptDocuments = true,
        RichContentSupported = RichContentRoutes.BedrockConverse,
        SupportsAgentSessions = true,
        SupportsAgentToolUse = true,
        SupportsAgentTurnInstructions = true,
        // The managed engine runs Converse turns; tool output travels the Converse route.
        Agent = ManagedAgentPolicy.Capabilities with { RichContentSupported = RichContentRoutes.BedrockConverse }
    };

    public LlmTransport Transport => LlmTransport.AwsBedrockConverse;

    public LlmProviderDefinition ProviderDefinition => BedrockProviderDefinitions.AwsProfile;

    public LlmProviderCapabilities GetCapabilities(
        LlmConnection connection,
        string? modelId = null)
    {
        EnsureTransport(connection);
        LlmProviderCapabilities capabilities = Defaults.Apply(connection.CapabilityOverrides);
        capabilities = modelId is not null
            && connection.ModelCapabilityOverrides.TryGetValue(
                modelId,
                out LlmCapabilityOverrides? modelOverrides)
                    ? capabilities.Apply(modelOverrides)
                    : capabilities;
        // Converse sends no reasoning setting, so no override can offer a level.
        return capabilities with { ReasoningLevels = FrozenSet<LlmReasoningLevel>.Empty };
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LlmProviderCapabilities capabilities = ValidateRequest(connection, request);
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is null
            ? null
            : StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);
        LlmAwsCredentials aws = RequireAws(connection);
        string modelId = await BedrockModelResolver.ResolveAsync(
            factory.Control(aws),
            aws.Region!,
            request.ModelId,
            cancellationToken);
        await loggingGuard.EnsureLoggingDisabledAsync(
            factory.Control(aws),
            aws,
            cancellationToken);
        ConverseResponse response = await factory.Runtime(aws).ConverseAsync(
            BuildRequest(connection, request, modelId),
            cancellationToken);
        foreach (LlmStreamEvent item in MapResponse(connection, request, enforcement, response))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Estimates the Converse request's input without I/O. The history up to the last reply that recorded
    /// Bedrock's reported token counts is counted from them; only what follows is tokenized.
    /// </summary>
    public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
    {
        _ = ValidateRequest(connection, request);
        int reported = -1;
        for (int index = request.Messages.Count - 1; index >= 0 && reported < 0; index--)
        {
            if (request.Messages[index].ContinuationState is { InputTokens: not null, OutputTokens: not null })
            {
                reported = index;
            }
        }
        LlmChatMessage[] tail = [.. request.Messages.Skip(reported + 1)];
        if (tail.Any(Uncountable))
        {
            return null;
        }
        if (reported < 0)
        {
            return new ContextEstimate(LocalTokenCount.Count(Projection(request, tail, framing: true)));
        }
        // The reported input already includes that request's instructions and tools.
        LlmContinuationState state = request.Messages[reported].ContinuationState!;
        return new ContextEstimate(state.InputTokens!.Value + state.OutputTokens!.Value
            + (tail.Length == 0 ? 0 : LocalTokenCount.Count(Projection(request, tail, framing: false))),
            IncludesProviderReport: true);
    }

    public string? EstimateDiagnostic =>
        "Estimated with a general-purpose tokenizer; each Bedrock model counts tokens its own way.";

    private LlmProviderCapabilities ValidateRequest(LlmConnection connection, LlmChatRequest request)
    {
        EnsureTransport(connection);
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            throw new ArgumentException("A model id is required.", nameof(request));
        }

        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        if (request.Temperature is not null && !capabilities.SupportsTemperature)
        {
            throw new NotSupportedException("This Bedrock connection does not support temperature.");
        }

        LlmReasoningEffort.Validate(request.ReasoningLevel, capabilities);
        LlmClientBase.ValidatePromptAttachments(request, capabilities);
        LlmClientBase.ValidateToolUse(request, capabilities);
        foreach (LlmChatMessage message in request.Messages.Where(message => message.ContinuationState is not null))
        {
            // Only this connection's own counts-only state is accepted; it adds nothing to the request.
            _ = ProviderContinuationStateCodec.DecodeMessage(message, connection, request.ModelId);
        }

        if (request.AllowParallelToolCalls is not null)
        {
            throw new NotSupportedException(
                "Bedrock Converse does not expose a portable parallel-tool-call control.");
        }

        if (request.Tools.Count > 0
            && request.ToolChoice is { Mode: LlmToolChoiceMode.None })
        {
            throw new NotSupportedException(
                "Bedrock Converse does not expose a tool-choice 'none' value.");
        }

        return capabilities;
    }

    /// <summary>
    /// Maps a Converse reply to stream events. A reply that reports both token counts carries them as
    /// counts-only continuation state, so accounting can count the history it closes.
    /// </summary>
    internal static IEnumerable<LlmStreamEvent> MapResponse(
        LlmConnection connection,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        ConverseResponse response)
    {
        var text = new StringBuilder();
        var toolCalls = new List<LlmToolCall>();
        foreach (ContentBlock block in response.Output?.Message?.Content ?? [])
        {
            if (!string.IsNullOrEmpty(block.Text))
            {
                text.Append(block.Text);
            }

            if (block.ToolUse is { } toolUse
                && !string.IsNullOrWhiteSpace(toolUse.ToolUseId)
                && !string.IsNullOrWhiteSpace(toolUse.Name))
            {
                string arguments = SerializeDocument(toolUse.Input);
                var call = new LlmToolCall(toolUse.ToolUseId, toolUse.Name, arguments);
                int index = toolCalls.Count;
                toolCalls.Add(call);
                yield return new LlmToolCallStartedEvent(call.Id, call.Name, index);
                yield return new LlmToolCallArgumentsDeltaEvent(call.Id, call.ArgumentsJson);
                yield return new LlmToolCallCompletedEvent(call);
            }
        }

        string finalText = text.ToString();
        if (finalText.Length > 0)
        {
            yield return new LlmTextDeltaEvent(finalText);
        }

        string? stopReason = response.StopReason?.Value;
        bool refused = stopReason is "content_filtered" or "guardrail_intervened";
        bool incomplete = stopReason == "max_tokens";
        bool requiresAction = toolCalls.Count > 0 || stopReason == "tool_use";
        LlmCompletionStatus completionStatus = refused
            ? LlmCompletionStatus.Refused
            : incomplete
                ? LlmCompletionStatus.Incomplete
                : requiresAction
                    ? LlmCompletionStatus.RequiresAction
                    : LlmCompletionStatus.Completed;
        LlmStructuredOutputValidation? validation = request.StructuredOutput is { } structured
            && enforcement is not null
            && !requiresAction
                ? refused
                    ? new LlmStructuredOutputValidation(
                        enforcement.Value,
                        false,
                        false,
                        ["Structured output was not evaluated because the provider refused the request."])
                    : StructuredOutput.Validate(finalText, structured, enforcement.Value)
                : null;
        LlmUsage? normalizedUsage = response.Usage is { } usage
            ? AnthropicUsage.Create(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens, usage.CacheWriteInputTokens)
            : null;
        yield return new LlmChatCompletionEvent(new LlmChatResult
        {
            Text = finalText,
            Refusal = refused ? finalText : null,
            Status = completionStatus,
            FinishReason = MapFinishReason(stopReason),
            IncompleteReason = incomplete ? stopReason : null,
            Usage = normalizedUsage,
            ContinuationState = normalizedUsage is { InputTokens: { } input, OutputTokens: { } output }
                ? ProviderContinuationStateCodec.EncodeCounts(connection, request.ModelId, input, output)
                : null,
            ToolCalls = toolCalls,
            StructuredOutput = validation,
            Diagnostics = new LlmProviderDiagnostics(
                200,
                response.ResponseMetadata?.RequestId,
                RawFinalResponseJson: null)
        });
    }

    /// <summary>Content the local tokenizer cannot count: attachments and binary tool output.</summary>
    private static bool Uncountable(LlmChatMessage message) => message.Attachments.Count > 0
        || message.ToolResults.Any(result => result.Output.Parts.Any(part => part is not LlmToolOutputText));

    /// <summary>The textual projection a Converse request carries, for local token counting.</summary>
    private static string Projection(LlmChatRequest request, IEnumerable<LlmChatMessage> messages, bool framing)
    {
        var projection = new System.Text.Json.Nodes.JsonObject
        {
            ["messages"] = new System.Text.Json.Nodes.JsonArray([.. messages.Select(message => new System.Text.Json.Nodes.JsonObject
            {
                ["role"] = message.Role,
                ["content"] = message.Content,
                ["tool_calls"] = new System.Text.Json.Nodes.JsonArray([.. message.ToolCalls.Select(call =>
                    (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["name"] = call.Name, ["input"] = call.ArgumentsJson })]),
                ["tool_results"] = new System.Text.Json.Nodes.JsonArray([.. message.ToolResults.SelectMany(result => result.Output.Parts)
                    .OfType<LlmToolOutputText>().Select(part => (System.Text.Json.Nodes.JsonNode)part.Text)])
            })])
        };
        if (framing)
        {
            projection["system"] = request.Instructions;
            projection["tools"] = new System.Text.Json.Nodes.JsonArray([.. request.Tools.Select(tool =>
                (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["schema"] = System.Text.Json.Nodes.JsonNode.Parse(tool.InputSchema.GetRawText())
                })]);
            projection["output_schema"] = request.StructuredOutput is { } structured
                ? System.Text.Json.Nodes.JsonNode.Parse(structured.Schema.GetRawText())
                : null;
        }
        return projection.ToJsonString();
    }

    public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        EnsureTransport(connection);
        LlmAwsCredentials aws = RequireAws(connection);
        return await BedrockModelResolver.ListAsync(
            factory.Control(aws),
            aws.Region!,
            cancellationToken);
    }

    internal static ConverseRequest BuildRequest(
        LlmConnection connection,
        LlmChatRequest request,
        string modelId)
    {
        var system = new List<SystemContentBlock>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            system.Add(new SystemContentBlock { Text = request.Instructions });
        }

        var messages = new List<Message>();
        foreach (LlmChatMessage message in request.Messages)
        {
            if (string.IsNullOrEmpty(message.Content)
                && message.ToolCalls.Count == 0
                && message.ToolResults.Count == 0
                && message.Attachments.Count == 0)
            {
                continue;
            }

            if (message.Role is "system" or "developer")
            {
                system.Add(new SystemContentBlock { Text = message.Content });
            }
            else
            {
                var content = new List<ContentBlock>();
                foreach (LlmToolResult toolResult in message.ToolResults)
                {
                    content.Add(new ContentBlock
                    {
                        ToolResult = new ToolResultBlock
                        {
                            ToolUseId = toolResult.ToolCallId,
                            Status = toolResult.Output.IsError
                                ? ToolResultStatus.Error
                                : ToolResultStatus.Success,
                            Content = BuildToolResultContent(ToolOutputProjector.Project(
                                toolResult.Output,
                                RichContentRoutes.BedrockConverse.For(connection, null),
                                RichContentRoutes.BedrockConverse.For(connection, modelId)))
                        }
                    });
                }

                if (!string.IsNullOrEmpty(message.Content))
                {
                    content.Add(new ContentBlock { Text = message.Content });
                }

                foreach (LlmAttachment attachment in message.Attachments)
                {
                    content.Add(BuildPromptAttachment(attachment));
                }

                foreach (LlmToolCall call in message.ToolCalls)
                {
                    content.Add(new ContentBlock
                    {
                        ToolUse = new ToolUseBlock
                        {
                            ToolUseId = call.Id,
                            Name = call.Name,
                            Input = DeserializeDocument(call.ArgumentsJson)
                        }
                    });
                }

                messages.Add(new Message
                {
                    Role = message.ToolResults.Count == 0 && message.Role == "assistant"
                        ? ConversationRole.Assistant
                        : ConversationRole.User,
                    Content = content
                });
            }
        }

        if (messages.Count == 0)
        {
            throw new InvalidOperationException(
                "Bedrock requires at least one user or assistant message.");
        }

        var result = new ConverseRequest { ModelId = modelId, Messages = messages };
        if (system.Count > 0)
        {
            result.System = system;
        }

        var inference = new InferenceConfiguration();
        bool hasInference = false;
        if (request.MaxOutputTokens is int maxTokens)
        {
            inference.MaxTokens = maxTokens;
            hasInference = true;
        }

        if (request.Temperature is double temperature)
        {
            inference.Temperature = (float)temperature;
            hasInference = true;
        }

        if (hasInference)
        {
            result.InferenceConfig = inference;
        }

        if (request.Tools.Count > 0)
        {
            var toolConfiguration = new ToolConfiguration
            {
                Tools = request.Tools.Select(definition => new Tool
                {
                    ToolSpec = new ToolSpecification
                    {
                        Name = definition.Name,
                        Description = definition.Description,
                        InputSchema = new ToolInputSchema
                        {
                            Json = DeserializeDocument(definition.InputSchema.GetRawText())
                        },
                        // Models without strict schemas, such as Amazon Nova, reject the field even when false.
                        Strict = definition.Strict ? true : null
                    }
                }).ToList()
            };
            if (request.ToolChoice is { } choice)
            {
                toolConfiguration.ToolChoice = choice.Mode switch
                {
                    LlmToolChoiceMode.Auto => new ToolChoice { Auto = new AutoToolChoice() },
                    LlmToolChoiceMode.Required => new ToolChoice { Any = new AnyToolChoice() },
                    LlmToolChoiceMode.Specific => new ToolChoice
                    {
                        Tool = new SpecificToolChoice { Name = choice.Name }
                    },
                    LlmToolChoiceMode.None => throw new NotSupportedException(
                        "Bedrock Converse does not expose a tool-choice 'none' value."),
                    _ => throw new ArgumentOutOfRangeException(nameof(request))
                };
            }

            result.ToolConfig = toolConfiguration;
        }

        if (connection.ProviderOptions.Count > 0 || request.ProviderOptions.Count > 0)
        {
            throw new NotSupportedException(
                "Bedrock provider request options are not yet mapped to Converse fields.");
        }

        // Bedrock records this free-form map. Keep it deliberately generic and never derive it from a
        // user, prompt, application, tenant, or connection name.
        result.RequestMetadata = new Dictionary<string, string>
        {
            [RequestIdMetadataKey] = $"request-{Interlocked.Increment(ref _requestCounter)}"
        };

        return result;
    }

    private static ContentBlock BuildPromptAttachment(LlmAttachment attachment)
    {
        string mediaType = LlmAttachments.ResolveMediaType(attachment);
        return attachment.Kind switch
        {
            LlmBinaryKind.Image => new ContentBlock
            {
                Image = new ImageBlock
                {
                    Format = BedrockImageFormat(mediaType),
                    Source = new ImageSource
                    {
                        Bytes = new MemoryStream(attachment.Data.ToArray(), writable: false)
                    }
                }
            },
            LlmBinaryKind.Document => new ContentBlock
            {
                Document = new DocumentBlock
                {
                    Format = BedrockDocumentFormat(mediaType),
                    Name = "prompt-document",
                    Source = new DocumentSource
                    {
                        Bytes = new MemoryStream(attachment.Data.ToArray(), writable: false)
                    }
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(attachment))
        };
    }

    /// <summary>The ordered Converse blocks; an empty result still sends one empty text block.</summary>
    private static List<ToolResultContentBlock> BuildToolResultContent(ProjectedToolOutput projected)
    {
        var content = new List<ToolResultContentBlock>();
        int documents = 0;
        foreach (SentToolPart part in projected.Parts)
        {
            content.Add(part switch
            {
                SentText text => new ToolResultContentBlock { Text = text.Text },
                SentImage image => new ToolResultContentBlock
                {
                    Image = new ImageBlock
                    {
                        Format = BedrockImageFormat(image.MediaType),
                        Source = new ImageSource { Bytes = new MemoryStream(image.Data.ToArray(), writable: false) }
                    }
                },
                // Converse document names must be unique within a request and use a restricted character set.
                SentFile file => new ToolResultContentBlock
                {
                    Document = new DocumentBlock
                    {
                        Format = ToolDocumentFormat(file.MediaType),
                        Name = $"tool-result-document-{++documents}",
                        Source = new DocumentSource { Bytes = new MemoryStream(file.Data.ToArray(), writable: false) }
                    }
                },
                SentStructured structured => new ToolResultContentBlock { Json = DeserializeDocument(structured.Value.GetRawText()) },
                _ => throw new InvalidOperationException("Converse tool results do not carry this part natively.")
            });
        }
        if (content.Count == 0)
        {
            content.Add(new ToolResultContentBlock { Text = string.Empty });
        }
        return content;
    }

    /// <summary>A projected document is a PDF or UTF-8 text; text without a named Converse format goes as txt.</summary>
    private static DocumentFormat ToolDocumentFormat(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "application/pdf" => DocumentFormat.Pdf,
        "text/csv" => DocumentFormat.Csv,
        "text/html" => DocumentFormat.Html,
        "text/markdown" => DocumentFormat.Md,
        _ => DocumentFormat.Txt
    };

    private static ImageFormat BedrockImageFormat(string mediaType) =>
        mediaType.ToLowerInvariant() switch
        {
            "image/gif" => ImageFormat.Gif,
            "image/jpeg" => ImageFormat.Jpeg,
            "image/png" => ImageFormat.Png,
            "image/webp" => ImageFormat.Webp,
            _ => throw new NotSupportedException(
                $"Bedrock Converse does not support '{mediaType}' image attachments.")
        };

    private static DocumentFormat BedrockDocumentFormat(string mediaType) =>
        mediaType.ToLowerInvariant() switch
        {
            "text/csv" => DocumentFormat.Csv,
            "application/msword" => DocumentFormat.Doc,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" =>
                DocumentFormat.Docx,
            "text/html" => DocumentFormat.Html,
            "text/markdown" => DocumentFormat.Md,
            "application/pdf" => DocumentFormat.Pdf,
            "text/plain" => DocumentFormat.Txt,
            "application/vnd.ms-excel" => DocumentFormat.Xls,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" =>
                DocumentFormat.Xlsx,
            _ => throw new NotSupportedException(
                $"Bedrock Converse does not support '{mediaType}' document attachments.")
        };

    private static LlmAwsCredentials RequireAws(LlmConnection connection) =>
        LlmCredential.Parse(connection.Credential)?.Aws
        ?? throw new InvalidOperationException("Bedrock requires AWS connection settings.");

    internal static string MapFinishReason(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" => "stop",
        "max_tokens" => "length",
        "content_filtered" or "guardrail_intervened" => "content_filter",
        "tool_use" => "tool_calls",
        _ => reason ?? "stop"
    };

    private static Document DeserializeDocument(string json) =>
        JsonSerializer.Deserialize<Document>(json);

    private static string SerializeDocument(Document? document) => document is null
        ? "{}"
        : JsonSerializer.Serialize(document.Value);

    private void EnsureTransport(LlmConnection connection)
    {
        if (connection.Transport != Transport)
        {
            throw new ArgumentException(
                $"A {connection.Transport} connection cannot use the Bedrock client.",
                nameof(connection));
        }
    }
}
