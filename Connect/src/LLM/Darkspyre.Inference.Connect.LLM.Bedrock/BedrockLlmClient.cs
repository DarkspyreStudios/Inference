using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.Documents;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockLlmClient(
    BedrockClientFactory factory,
    BedrockLoggingGuard loggingGuard) : ILlmClient
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
        SupportsPromptImages = true,
        SupportsPromptDocuments = true,
        SupportsToolResultImages = true,
        SupportsToolResultDocuments = true
    };

    public LlmTransport Transport => LlmTransport.AwsBedrockConverse;

    public LlmProviderDefinition ProviderDefinition => BedrockProviderDefinitions.AwsProfile;

    public LlmProviderCapabilities GetCapabilities(
        LlmConnection connection,
        string? modelId = null)
    {
        EnsureTransport(connection);
        LlmProviderCapabilities capabilities = Defaults.Apply(connection.CapabilityOverrides);
        return modelId is not null
            && connection.ModelCapabilityOverrides.TryGetValue(
                modelId,
                out LlmCapabilityOverrides? modelOverrides)
                    ? capabilities.Apply(modelOverrides)
                    : capabilities;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

        LlmClientBase.ValidatePromptAttachments(request, capabilities);
        LlmClientBase.ValidateToolUse(request, capabilities);
        if (request.Messages.Any(message => message.ResponseState is not null))
        {
            throw new NotSupportedException("Bedrock Converse cannot import adapter response state.");
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
        yield return new LlmCompletionEvent(new LlmChatResult
        {
            Text = finalText,
            Refusal = refused ? finalText : null,
            Status = completionStatus,
            FinishReason = MapFinishReason(stopReason),
            IncompleteReason = incomplete ? stopReason : null,
            Usage = response.Usage is null
                ? null
                : new LlmChatUsage(
                    response.Usage.InputTokens,
                    response.Usage.OutputTokens,
                    response.Usage.TotalTokens),
            ToolCalls = toolCalls,
            StructuredOutput = validation,
            Diagnostics = new LlmProviderDiagnostics(
                200,
                response.ResponseMetadata?.RequestId,
                RawFinalResponseJson: null)
        });
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
                            Status = toolResult.IsError
                                ? ToolResultStatus.Error
                                : ToolResultStatus.Success,
                            Content = BuildToolResultContent(toolResult)
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
                        Strict = definition.Strict
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
            LlmAttachmentKind.Image => new ContentBlock
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
            LlmAttachmentKind.Document => new ContentBlock
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

    private static List<ToolResultContentBlock> BuildToolResultContent(LlmToolResult result)
    {
        LlmAttachments.Validate(
            result.Attachments,
            supportsImages: true,
            supportsDocuments: true,
            parameterName: nameof(result));
        var content = new List<ToolResultContentBlock>();
        if (result.Content.Length > 0 || result.Attachments.Count == 0)
        {
            content.Add(new ToolResultContentBlock { Text = result.Content });
        }

        foreach (LlmAttachment attachment in result.Attachments)
        {
            string mediaType = LlmAttachments.ResolveMediaType(attachment);
            content.Add(attachment.Kind switch
            {
                LlmAttachmentKind.Image => new ToolResultContentBlock
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
                LlmAttachmentKind.Document => new ToolResultContentBlock
                {
                    Document = new DocumentBlock
                    {
                        Format = BedrockDocumentFormat(mediaType),
                        Name = "tool-result-document",
                        Source = new DocumentSource
                        {
                            Bytes = new MemoryStream(attachment.Data.ToArray(), writable: false)
                        }
                    }
                },
                _ => throw new ArgumentOutOfRangeException(nameof(result))
            });
        }

        return content;
    }

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
