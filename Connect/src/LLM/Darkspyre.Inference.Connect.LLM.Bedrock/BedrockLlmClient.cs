using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.Documents;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockLlmClient(
    BedrockClientFactory factory,
    BedrockLoggingGuard loggingGuard,
    IBedrockConverseInvoker invoker) : ILlmClient, ILlmContextEstimator
{
    internal const string RequestIdMetadataKey = "request-id";

    /// <summary>
    /// The output allowance a Claude thinking budget is added to when the request sets none. It matches the
    /// Anthropic Messages adapter's default.
    /// </summary>
    private const int ClaudeDefaultMaxOutputTokens = 8192;

    private static readonly IReadOnlySet<LlmReasoningLevel> Nova2Levels = new[]
    {
        LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High
    }.ToFrozenSet();

    private static long _requestCounter;

    private static readonly LlmProviderCapabilities Defaults = new()
    {
        OutputLimitEnforcement = Common.LLM.LlmOutputLimitEnforcement.ProviderEnforced,
        SupportsStreaming = true,
        SupportsReasoningText = true,
        // ConverseStream reports usage in its final metadata event.
        SupportsStreamingUsage = true,
        SupportsModelListing = true,
        SupportsJsonSchema = false,
        SupportsJsonObject = false,
        SupportsTemperature = true,
        SupportsTopP = true,
        MaxStopSequences = 2500,
        SupportsToolUse = true,
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.Required, LlmToolChoiceMode.Specific }.ToFrozenSet(),
            SupportsParallelCalls = true,
            SupportsArgumentStreaming = true
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
        IReadOnlySet<LlmReasoningLevel> offered = modelId is null ? FrozenSet<LlmReasoningLevel>.Empty : ReasoningLevels(modelId);
        (BedrockModelFamily family, string baseModelId) = BedrockModelFamilies.Classify(modelId ?? string.Empty);
        bool claudeSampling = family != BedrockModelFamily.AnthropicClaude
            || AnthropicModelProfiles.Resolve(BedrockModelFamilies.AnthropicModelId(baseModelId), null).AllowsSampling;
        LlmProviderCapabilities capabilities = LlmCapabilityOverrideLookup.Apply(
            Defaults with
            {
                ReasoningLevels = offered,
                SupportsTemperature = claudeSampling,
                SupportsTopP = claudeSampling,
                SupportsTopK = family == BedrockModelFamily.AmazonNova2
                    || family == BedrockModelFamily.AnthropicClaude && claudeSampling,
                MaxStopSequences = family == BedrockModelFamily.DeepSeekR1 ? 10 : 2500
            }, connection, modelId) with
        {
            SupportsSeed = false,
            SupportsPresencePenalty = false,
            SupportsFrequencyPenalty = false
        };
        // An override can narrow the model family's levels but never widen them: Converse maps no other level.
        if (capabilities.ReasoningLevels.Any(level => !offered.Contains(level)))
        {
            throw new NotSupportedException(
                "Bedrock capability overrides cannot offer a reasoning level the model family does not accept.");
        }
        if ((!claudeSampling && (capabilities.SupportsTemperature || capabilities.SupportsTopP))
            || capabilities.SupportsTopK && !(family == BedrockModelFamily.AmazonNova2
                || family == BedrockModelFamily.AnthropicClaude && claudeSampling)
            || capabilities.MaxStopSequences > (family == BedrockModelFamily.DeepSeekR1 ? 10 : 2500))
        {
            throw new NotSupportedException("Bedrock capability overrides cannot enable unsupported sampling controls.");
        }
        return capabilities;
    }

    /// <summary>
    /// The levels a model family takes through additional model request fields. gpt-oss, DeepSeek-R1 and unknown
    /// models take none.
    /// </summary>
    internal static IReadOnlySet<LlmReasoningLevel> ReasoningLevels(string modelId)
    {
        (BedrockModelFamily family, string baseModelId) = BedrockModelFamilies.Classify(modelId);
        return family switch
        {
            BedrockModelFamily.AnthropicClaude => AnthropicModelProfiles.Resolve(
                BedrockModelFamilies.AnthropicModelId(baseModelId), catalogEntry: null).ReasoningLevels,
            BedrockModelFamily.AmazonNova2 => Nova2Levels,
            _ => FrozenSet<LlmReasoningLevel>.Empty
        };
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
        using BedrockClientFactory.Lease<Amazon.Bedrock.AmazonBedrockClient> control = factory.Control(aws, cancellationToken);
        cancellationToken = control.Token;
        string modelId = await BedrockModelResolver.ResolveAsync(
            control.Client,
            aws.Region!,
            request.ModelId,
            cancellationToken);
        await loggingGuard.EnsureLoggingDisabledAsync(
            control.Client,
            aws,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ConverseRequest built = BuildRequest(connection, request, modelId);
        if (!capabilities.SupportsStreaming)
        {
            ConverseResponse response = await invoker.ConverseAsync(aws, built, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (LlmStreamEvent item in MapResponse(connection, request, enforcement, response))
            {
                yield return item;
            }
            yield break;
        }

        await using IBedrockEventStream stream = await invoker.ConverseStreamAsync(
            aws,
            ToStreamRequest(built),
            cancellationToken);
        var accumulator = new BedrockStreamAccumulator();
        await foreach (IEventStreamEvent streamEvent in stream.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (LlmStreamEvent item in accumulator.Observe(streamEvent))
            {
                if (item is LlmReasoningDeltaEvent && !capabilities.SupportsReasoningText) { continue; }
                yield return item;
            }
        }

        // The completion follows the end of the stream, because the metadata event with usage follows message-stop.
        cancellationToken.ThrowIfCancellationRequested();
        BedrockReply reply = accumulator.Complete(stream.RequestId);
        yield return new LlmChatCompletionEvent(BuildResult(connection, request, enforcement, reply));
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
        LlmClientBase.ValidateSampling(request, capabilities);
        if (request.Temperature is { } temperature && (!double.IsFinite(temperature) || temperature is < 0 or > 1))
        { throw new ArgumentOutOfRangeException(nameof(request), "Converse temperature must be finite and between 0 and 1."); }
        BedrockModelFamily family = BedrockModelFamilies.Classify(request.ModelId).Family;
        if (family == BedrockModelFamily.AnthropicClaude)
        {
            AnthropicModelProfiles.ValidateSampling(request, AnthropicModelProfiles.Resolve(
            BedrockModelFamilies.AnthropicModelId(BedrockModelFamilies.Classify(request.ModelId).BaseModelId), null));
        }
        if (family == BedrockModelFamily.AmazonNova2 && request.TopK > 128)
        { throw new ArgumentOutOfRangeException(nameof(request), "Nova 2 top-k cannot exceed 128."); }
        if (family == BedrockModelFamily.AmazonNova2
            && request.ReasoningLevel == LlmReasoningLevel.High
            && (request.Temperature is not null || request.MaxOutputTokens is not null || request.TopP is not null || request.TopK is not null))
        {
            throw new NotSupportedException(
                "Nova 2 high reasoning requires temperature, top-p, top-k and maximum output tokens to be unset.");
        }
        LlmClientBase.ValidatePromptAttachments(request, capabilities);
        LlmClientBase.ValidateToolUse(request, capabilities);
        foreach (LlmChatMessage message in request.Messages.Where(message => message.ContinuationState is not null))
        {
            // Only this connection's own state is accepted, and it must match the visible message.
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
    /// Maps a Converse reply to the events of a complete response: reasoning, text, tool calls, then the completion.
    /// </summary>
    internal static IEnumerable<LlmStreamEvent> MapResponse(
        LlmConnection connection,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        ConverseResponse response)
    {
        var blocks = new List<BedrockBlock>();
        foreach (ContentBlock block in response.Output?.Message?.Content ?? [])
        {
            if (block.Text is { } text)
            {
                blocks.Add(new BedrockTextBlock(text));
            }
            else if (block.ReasoningContent?.ReasoningText is { } reasoning)
            {
                blocks.Add(new BedrockReasoningBlock(reasoning.Text ?? string.Empty, reasoning.Signature));
            }
            else if (block.ReasoningContent?.RedactedContent is { } redacted)
            {
                blocks.Add(new BedrockRedactedReasoningBlock(redacted.ToArray()));
            }
            else if (block.ToolUse is { } toolUse)
            {
                blocks.Add(new BedrockToolUseBlock(
                    toolUse.ToolUseId ?? string.Empty,
                    toolUse.Name ?? string.Empty,
                    SerializeDocument(toolUse.Input),
                    ValidArguments: toolUse.Input.IsDictionary()));
            }
        }

        var reply = new BedrockReply(blocks, response.StopReason?.Value, response.Usage, response.ResponseMetadata?.RequestId);
        LlmChatResult result = BuildResult(connection, request, enforcement, reply);
        LlmReasoningText[] reasoningText = [.. blocks.OfType<BedrockReasoningBlock>()
            .Where(block => block.Text.Length > 0)
            .Select(block => new LlmReasoningText(block.Text, IsSummary: false))];
        return CompleteResponseEvents.From(result,
            LlmCapabilityOverrideLookup.Apply(Defaults, connection, request.ModelId).SupportsReasoningText ? reasoningText : []);
    }

    /// <summary>
    /// Builds the result of a complete Converse reply, streamed or not. Tool calls return only with
    /// <see cref="LlmCompletionStatus.RequiresAction"/>. A reply with reasoning keeps the ordered reply as continuation
    /// state, so its signed reasoning is replayed on the next request; any other reply keeps its reported token counts.
    /// </summary>
    internal static LlmChatResult BuildResult(
        LlmConnection connection,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        BedrockReply reply)
    {
        string text = string.Concat(reply.Blocks.OfType<BedrockTextBlock>().Select(block => block.Text));
        BedrockToolUseBlock[] tools = [.. reply.Blocks.OfType<BedrockToolUseBlock>()];
        LlmToolCall[] validCalls = [.. tools.Where(tool => tool.ValidArguments)
            .Select(tool => new LlmToolCall(tool.Id, tool.Name, tool.ArgumentsJson))];
        string? stopReason = reply.StopReason;
        if (stopReason == "tool_use" && tools.Any(tool => !tool.ValidArguments))
        {
            throw new LlmProviderException(
                "Bedrock returned tool input that is not a JSON object.",
                LlmTransport.AwsBedrockConverse,
                requestId: reply.RequestId);
        }

        LlmCompletionStatus status = stopReason switch
        {
            "end_turn" or "stop_sequence" => validCalls.Length > 0
                ? LlmCompletionStatus.RequiresAction
                : LlmCompletionStatus.Completed,
            "tool_use" => LlmCompletionStatus.RequiresAction,
            "max_tokens" or "model_context_window_exceeded" => LlmCompletionStatus.Incomplete,
            "content_filtered" or "guardrail_intervened" => LlmCompletionStatus.Refused,
            "malformed_model_output" or "malformed_tool_use" => LlmCompletionStatus.Failed,
            _ => LlmCompletionStatus.Unknown
        };
        IReadOnlyList<LlmToolCall> toolCalls = status == LlmCompletionStatus.RequiresAction ? validCalls : [];
        LlmStructuredOutputValidation? validation = request.StructuredOutput is { } structured
            && enforcement is not null
            && status != LlmCompletionStatus.RequiresAction
                ? status switch
                {
                    LlmCompletionStatus.Refused => new LlmStructuredOutputValidation(
                        enforcement.Value,
                        false,
                        false,
                        ["Structured output was not evaluated because the provider refused the request."]),
                    LlmCompletionStatus.Failed => new LlmStructuredOutputValidation(
                        enforcement.Value,
                        false,
                        false,
                        ["Structured output was not evaluated because the provider reported malformed output."]),
                    _ => StructuredOutput.Validate(text, structured, enforcement.Value)
                }
                : null;
        LlmUsage? usage = reply.Usage is { } reported
            ? AnthropicUsage.Create(reported.InputTokens, reported.OutputTokens, reported.CacheReadInputTokens, reported.CacheWriteInputTokens)
            : null;
        return new LlmChatResult
        {
            Text = text,
            Refusal = status == LlmCompletionStatus.Refused ? text : null,
            Status = status,
            FinishReason = MapFinishReason(stopReason),
            IncompleteReason = status == LlmCompletionStatus.Incomplete ? stopReason : null,
            Usage = usage,
            ContinuationState = status == LlmCompletionStatus.Failed
                ? null
                : EncodeState(connection, request.ModelId, reply, toolCalls, usage),
            ToolCalls = toolCalls,
            StructuredOutput = validation,
            Diagnostics = new LlmProviderDiagnostics(200, reply.RequestId, RawFinalResponseJson: null)
        };
    }

    /// <summary>
    /// The reply as ordered, replayable state when it contains reasoning, otherwise its reported counts. A tool block
    /// is retained only when the result returns its call, so the state always matches the visible message.
    /// </summary>
    private static LlmContinuationState? EncodeState(
        LlmConnection connection,
        string modelId,
        BedrockReply reply,
        IReadOnlyList<LlmToolCall> toolCalls,
        LlmUsage? usage)
    {
        if (!reply.Blocks.Any(block => block is BedrockReasoningBlock or BedrockRedactedReasoningBlock))
        {
            return usage is { InputTokens: { } counted, OutputTokens: { } generated }
                ? ProviderContinuationStateCodec.EncodeCounts(connection, modelId, counted, generated)
                : null;
        }

        var output = new JsonArray();
        foreach (BedrockBlock block in reply.Blocks)
        {
            switch (block)
            {
                case BedrockTextBlock text:
                    output.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;
                case BedrockReasoningBlock reasoning:
                    var item = new JsonObject { ["type"] = "reasoning", ["text"] = reasoning.Text };
                    if (reasoning.Signature is not null)
                    {
                        item["signature"] = reasoning.Signature;
                    }
                    output.Add(item);
                    break;
                case BedrockRedactedReasoningBlock redacted:
                    output.Add(new JsonObject { ["type"] = "redacted_reasoning", ["data"] = Convert.ToBase64String(redacted.Data) });
                    break;
                case BedrockToolUseBlock tool when toolCalls.Any(call => call.Id == tool.Id):
                    output.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = tool.Id,
                        ["name"] = tool.Name,
                        ["input"] = JsonNode.Parse(tool.ArgumentsJson)
                    });
                    break;
            }
        }

        return ProviderContinuationStateCodec.Encode(connection, modelId, output) with
        {
            InputTokens = usage?.InputTokens,
            OutputTokens = usage?.OutputTokens
        };
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
        using BedrockClientFactory.Lease<Amazon.Bedrock.AmazonBedrockClient> control = factory.Control(aws, cancellationToken);
        IReadOnlyList<LlmModel> models = await BedrockModelResolver.ListAsync(
            control.Client,
            aws.Region!,
            control.Token);
        control.Token.ThrowIfCancellationRequested();
        return models;
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
            if (message.ContinuationState is not null
                && ProviderContinuationStateCodec.DecodeMessage(message, connection, request.ModelId) is { Count: > 0 } retained)
            {
                // The decoded state already matches the visible text and tool calls, so it replaces them.
                messages.Add(new Message { Role = ConversationRole.Assistant, Content = ReplayContent(retained) });
                continue;
            }

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
        if (request.TopP is { } topP) { inference.TopP = (float)topP; hasInference = true; }
        if (request.StopSequences.Count > 0) { inference.StopSequences = [.. request.StopSequences]; hasInference = true; }

        JsonObject additional = new();
        if (request.ReasoningLevel is { } level
            && ReasoningFields(request.ModelId, level, request.MaxOutputTokens) is var (fields, raisedMaxTokens))
        {
            if (fields.Count > 0)
            {
                additional = fields;
            }
            if (raisedMaxTokens is int raised)
            {
                inference.MaxTokens = raised;
                hasInference = true;
            }
        }
        if (request.TopK is { } topK)
        {
            if (BedrockModelFamilies.Classify(request.ModelId).Family == BedrockModelFamily.AnthropicClaude)
            { additional["top_k"] = topK; }
            else
            {
                JsonObject config = additional["inferenceConfig"] as JsonObject ?? new();
                config["topK"] = topK;
                if (additional["inferenceConfig"] is null) { additional["inferenceConfig"] = config; }
            }
        }
        if (additional.Count > 0) { result.AdditionalModelRequestFields = DeserializeDocument(additional.ToJsonString()); }

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

    /// <summary>
    /// The additional model request fields a reasoning level adds, and the raised maximum output tokens a Claude
    /// thinking budget needs. Validation has already confirmed the model family offers the level.
    /// </summary>
    private static (JsonObject Fields, int? MaxTokens) ReasoningFields(string modelId, LlmReasoningLevel level, int? maxOutputTokens)
    {
        (BedrockModelFamily family, string baseModelId) = BedrockModelFamilies.Classify(modelId);
        var fields = new JsonObject();
        switch (family)
        {
            case BedrockModelFamily.AnthropicClaude:
                AnthropicReasoningFields claude = AnthropicModelProfiles.Fields(
                    AnthropicModelProfiles.Resolve(BedrockModelFamilies.AnthropicModelId(baseModelId), catalogEntry: null),
                    level,
                    maxOutputTokens ?? ClaudeDefaultMaxOutputTokens,
                    requestReasoningText: false);
                if (claude.Thinking is { } thinking)
                {
                    fields["thinking"] = thinking;
                }
                if (claude.OutputConfig is { } outputConfig)
                {
                    fields["output_config"] = outputConfig;
                }
                return (fields, claude.MaxTokens);
            case BedrockModelFamily.AmazonNova2:
                fields["reasoningConfig"] = level == LlmReasoningLevel.None
                    ? new JsonObject { ["type"] = "disabled" }
                    : new JsonObject { ["type"] = "enabled", ["maxReasoningEffort"] = LlmReasoningEffort.Word(level) };
                return (fields, null);
            default:
                return (fields, null);
        }
    }

    /// <summary>The ConverseStream form of a built request. It carries every property <see cref="BuildRequest"/> sets.</summary>
    internal static ConverseStreamRequest ToStreamRequest(ConverseRequest request) => new()
    {
        ModelId = request.ModelId,
        Messages = request.Messages,
        System = request.System,
        InferenceConfig = request.InferenceConfig,
        ToolConfig = request.ToolConfig,
        RequestMetadata = request.RequestMetadata,
        AdditionalModelRequestFields = request.AdditionalModelRequestFields
    };

    /// <summary>The assistant content blocks of a retained Converse reply, in reply order.</summary>
    private static List<ContentBlock> ReplayContent(JsonArray retained)
    {
        var content = new List<ContentBlock>();
        foreach (JsonObject item in retained.OfType<JsonObject>())
        {
            content.Add(item["type"]!.GetValue<string>() switch
            {
                "text" => new ContentBlock { Text = item["text"]!.GetValue<string>() },
                "reasoning" => new ContentBlock
                {
                    ReasoningContent = new ReasoningContentBlock
                    {
                        ReasoningText = new ReasoningTextBlock
                        {
                            Text = item["text"]!.GetValue<string>(),
                            Signature = item["signature"]?.GetValue<string>()
                        }
                    }
                },
                "redacted_reasoning" => new ContentBlock
                {
                    ReasoningContent = new ReasoningContentBlock
                    {
                        RedactedContent = new MemoryStream(
                            Convert.FromBase64String(item["data"]!.GetValue<string>()),
                            writable: false)
                    }
                },
                "tool_use" => new ContentBlock
                {
                    ToolUse = new ToolUseBlock
                    {
                        ToolUseId = item["id"]!.GetValue<string>(),
                        Name = item["name"]!.GetValue<string>(),
                        Input = DeserializeDocument(item["input"]!.ToJsonString())
                    }
                },
                _ => throw new NotSupportedException("The adapter cannot replay this assistant output type.")
            });
        }
        return content;
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

    internal static string? MapFinishReason(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" => "stop",
        "max_tokens" => "length",
        "content_filtered" or "guardrail_intervened" => "content_filter",
        "tool_use" => "tool_calls",
        _ => reason
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
