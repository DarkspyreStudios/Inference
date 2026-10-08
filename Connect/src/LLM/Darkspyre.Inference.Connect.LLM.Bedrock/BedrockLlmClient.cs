using System.Collections.Frozen;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.Documents;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockLlmClient(
    BedrockClientFactory factory,
    BedrockLoggingGuard loggingGuard,
    IBedrockConverseInvoker invoker,
    ILogger<BedrockLlmClient> logger) : ILlmClient, ILlmContextEstimator, ILlmCompactionValidator, ILlmResourcePreparationClient
{
    internal const string RequestIdMetadataKey = "request-id";
    /// <summary>Converse materializes logical resources through the shared inline owner.</summary>
    public bool CanPrepareResources => true;

    /// <summary>
    /// The output allowance a Claude thinking budget is added to when the request sets none. It matches the
    /// Anthropic Messages adapter's default.
    /// </summary>
    private const int ClaudeDefaultMaxOutputTokens = 8192;

    private static readonly IReadOnlySet<LlmReasoningLevel> Nova2Levels = new[]
    {
        LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High
    }.ToFrozenSet();

    private static readonly IReadOnlySet<LlmReasoningLevel> GptOssLevels = new[]
    {
        LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High
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
        // The India Haiku 4.5 profile does not support structured outputs, unlike its other profiles.
        bool indiaHaiku = modelId is "in.anthropic.claude-haiku-4-5-20251001-v1:0";
        LlmRichContentSupported richContent = ModelRichContent(modelId);
        bool claudeSampling = family != BedrockModelFamily.AnthropicClaude
            || AnthropicModelProfiles.Resolve(BedrockModelFamilies.AnthropicModelId(baseModelId), null).AllowsSampling;
        LlmProviderCapabilities capabilities = LlmCapabilityOverrideLookup.Apply(
            Defaults with
            {
                ReasoningLevels = offered,
                SupportsJsonSchema = !indiaHaiku && baseModelId is "openai.gpt-oss-20b-1:0" or "openai.gpt-oss-120b-1:0"
                    or "anthropic.claude-sonnet-4-6" or "anthropic.claude-opus-4-6-v1"
                    or "anthropic.claude-sonnet-4-5-20250929-v1:0"
                    or "anthropic.claude-haiku-4-5-20251001-v1:0"
                    or "anthropic.claude-opus-4-5-20251101-v1:0",
                SupportsPromptImages = richContent.Images == LlmRichContentCarriage.Native,
                SupportsPromptDocuments = richContent.PdfDocuments == LlmRichContentCarriage.Native,
                RichContentSupported = richContent,
                Agent = Defaults.Agent with { RichContentSupported = richContent },
                PromptCache = BedrockPromptCache.Capabilities(modelId ?? string.Empty),
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
        if (capabilities.SupportsJsonObject)
        {
            throw new NotSupportedException("Bedrock Converse does not expose JSON-object-only output enforcement.");
        }
        if (indiaHaiku && capabilities.SupportsJsonSchema)
        {
            throw new NotSupportedException(
                "Bedrock capability overrides cannot enable JSON Schema output for the India Haiku 4.5 inference profile.");
        }
        if ((richContent.Images == LlmRichContentCarriage.Unsupported && capabilities.SupportsPromptImages)
            || (richContent.PdfDocuments == LlmRichContentCarriage.Unsupported && capabilities.SupportsPromptDocuments))
        {
            throw new NotSupportedException("Bedrock capability overrides cannot enable image or document input for this text-only model.");
        }
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

    private static LlmRichContentSupported ModelRichContent(string? modelId) =>
        BedrockModelFamilies.Classify(modelId ?? string.Empty).BaseModelId is
            "amazon.nova-micro-v1:0" or "openai.gpt-oss-20b-1:0" or "openai.gpt-oss-120b-1:0" or "deepseek.r1-v1:0"
            ? RichContentRoutes.BedrockConverse with
            {
                Images = LlmRichContentCarriage.Unsupported,
                ImageMediaTypes = FrozenSet<string>.Empty,
                PdfDocuments = LlmRichContentCarriage.Unsupported,
                TextDocuments = LlmRichContentCarriage.Text
            }
            : RichContentRoutes.BedrockConverse;

    /// <summary>
    /// The levels a model takes through additional model request fields. Only the qualified gpt-oss identities
    /// offer effort controls; DeepSeek-R1 and unknown models take none.
    /// </summary>
    internal static IReadOnlySet<LlmReasoningLevel> ReasoningLevels(string modelId)
    {
        (BedrockModelFamily family, string baseModelId) = BedrockModelFamilies.Classify(modelId);
        return family switch
        {
            BedrockModelFamily.AnthropicClaude => AnthropicModelProfiles.Resolve(
                BedrockModelFamilies.AnthropicModelId(baseModelId), catalogEntry: null).ReasoningLevels,
            BedrockModelFamily.AmazonNova2 => Nova2Levels,
            BedrockModelFamily.OpenAiGptOss when baseModelId is "openai.gpt-oss-20b-1:0" or "openai.gpt-oss-120b-1:0" => GptOssLevels,
            _ => FrozenSet<LlmReasoningLevel>.Empty
        };
    }

    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
        CancellationToken cancellationToken = default) =>
        factory.StreamAsync(token => request.Resources is null
            ? StreamOwnedAsync(connection, request, token)
            : LlmResourcePreparation.StreamInlineAsync(request, input => { _ = ValidateRequest(connection, input); },
                (input, innerToken) => StreamOwnedAsync(connection, input, innerToken), token), cancellationToken);

    [SuppressMessage("Design", "CA1031", Justification = "The adapter preserves the operation and each independent owned cleanup failure.")]
    [SuppressMessage("Usage", "CA2219", Justification = "Iterator retirement executes finally; recorded operation and cleanup failures must also surface on early reader disposal.")]
    private async IAsyncEnumerable<LlmStreamEvent> StreamOwnedAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var owned = new ConverseOperation();
        var failures = new List<Exception>();
        IAsyncEnumerator<LlmStreamEvent> events = StreamCoreAsync(connection, request, owned, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try { moved = await events.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    failures.Add(ClassifyConverseFailure(error, connection));
                    break;
                }
                if (!moved) break;
                yield return events.Current;
            }
        }
        finally
        {
            try { await events.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(CleanupFailure(error, events)); }
            if (owned.Events is { } reader)
                try { await reader.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { failures.Add(CleanupFailure(error, reader)); }
            if (owned.Stream is { } stream)
                try { await stream.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { failures.Add(CleanupFailure(error, stream)); }
            if (owned.Control is { } control)
                try { control.Dispose(); }
                catch (Exception error) { failures.Add(CleanupFailure(error, control)); }
            if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
            if (failures.Count > 1) throw new AggregateException("Bedrock Converse and owned cleanup failed.", failures);
        }
    }

    private async IAsyncEnumerable<LlmStreamEvent> StreamCoreAsync(
        LlmConnection connection, LlmChatRequest request, ConverseOperation owned,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LlmProviderCapabilities capabilities = ValidateRequest(connection, request);
        request = ClientTools.Snapshot(request);
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is null
            ? null
            : StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);
        LlmAwsCredentials aws = RequireAws(connection);
        BedrockClientFactory.Lease<Amazon.Bedrock.AmazonBedrockClient> control = factory.Control(aws, cancellationToken);
        owned.Control = control;
        cancellationToken = control.Token;
        string modelId = await BedrockModelResolver.ResolveAsync(
            control,
            aws.Region!,
            request.ModelId,
            logger,
            LlmCredential.Parse(connection.Credential)!,
            cancellationToken);
        await loggingGuard.EnsureLoggingDisabledAsync(
            control,
            aws,
            cancellationToken);
        control.ThrowIfCanceled();
        ConverseRequest built = BuildRequest(connection, request, modelId, enforcement);
        if (!capabilities.SupportsStreaming)
        {
            ConverseResponse response = await invoker.ConverseAsync(aws, built, cancellationToken);
            control.ThrowIfCanceled();
            foreach (LlmStreamEvent item in MapResponse(connection, request, enforcement, response))
            {
                control.ThrowIfCanceled();
                yield return item;
            }
            yield break;
        }

        IBedrockEventStream stream;
        try { stream = await invoker.ConverseStreamAsync(aws, ToStreamRequest(built), cancellationToken).ConfigureAwait(false); }
        catch (AggregateException error) { throw ClassifyConverseOperationAndCleanup(error, connection); }
        owned.Stream = stream;
        IAsyncEnumerator<IEventStreamEvent> events = stream.ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        owned.Events = events;
        var accumulator = new BedrockStreamAccumulator();
        while (true)
        {
            bool moved;
            try { moved = await events.MoveNextAsync().ConfigureAwait(false); }
            catch (AggregateException error) { throw ClassifyConverseOperationAndCleanup(error, connection); }
            if (!moved) break;
            control.ThrowIfCanceled();
            foreach (LlmStreamEvent item in accumulator.Observe(events.Current))
            {
                if (item is LlmReasoningDeltaEvent && !capabilities.SupportsReasoningText) { continue; }
                control.ThrowIfCanceled();
                yield return item;
            }
        }

        // The completion follows the end of the stream, because the metadata event with usage follows message-stop.
        control.ThrowIfCanceled();
        BedrockReply reply = accumulator.Complete(stream.RequestId);
        control.ThrowIfCanceled();
        yield return new LlmChatCompletionEvent(BuildResult(connection, request, enforcement, reply));
    }

    private static Exception ClassifyConverseFailure(Exception error, LlmConnection connection)
    {
        if (error is not AmazonBedrockRuntimeException
            && error is not LlmProviderException { Transport: LlmTransport.AwsBedrockConverse }) return error;
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        string Safe(string value) => SecretRedactor.Sanitize(value, credential, connection.AdditionalHeaders.Values);
        if (error is LlmProviderException classified)
            return new LlmProviderException(Safe(classified.Message), classified.Transport, classified.StatusCode,
                classified.ProviderCode is { } classifiedCode ? Safe(classifiedCode) : null,
                classified.RequestId is { } classifiedId ? Safe(classifiedId) : null,
                classified.RetryAfter,
                classified.SanitizedResponseBody is { } classifiedBody ? Safe(classifiedBody) : null,
                new ConverseFailureEvidence(classified), classified.IsContextOverflow);
        var aws = (AmazonBedrockRuntimeException)error;
        string message = Safe(aws.Message);
        if (aws is ModelStreamErrorException stream)
        {
            if (stream.OriginalMessage is { Length: > 0 } originalMessage)
                message += $" Original model error: {Safe(originalMessage)}";
            if (stream.OriginalStatusCode is { } originalStatus)
                message += $" Original model status: {originalStatus.ToString(CultureInfo.InvariantCulture)}.";
        }
        return new LlmProviderException($"Bedrock {Safe(aws.GetType().Name)}: {message}",
            LlmTransport.AwsBedrockConverse, (int)aws.StatusCode == 0 ? null : aws.StatusCode,
            aws.ErrorCode is { } code ? Safe(code) : null, aws.RequestId is { } id ? Safe(id) : null,
            innerException: new ConverseFailureEvidence(aws));
    }

    private static AggregateException ClassifyConverseOperationAndCleanup(AggregateException error, LlmConnection connection)
    {
        if (error.InnerExceptions.Count < 2) return error;
        return new AggregateException("Bedrock Converse and owned cleanup failed.",
            new[] { ClassifyConverseFailure(error.InnerExceptions[0], connection) }
                .Concat(error.InnerExceptions.Skip(1).Select(cleanup => CleanupFailure(cleanup, error))));
    }

    internal static Exception CleanupFailure(Exception error, object owner)
    {
        if (LlmResourcePreparation.IsCleanupFailure(error))
            return LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: owner);
        Exception safe = error is OperationCanceledException canceled
            ? new OperationCanceledException("Bedrock owned cleanup was interrupted.", new ConverseFailureEvidence(error), canceled.CancellationToken)
            : new ConverseFailureEvidence(error);
        return LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: owner);
    }

    private sealed class ConverseFailureEvidence(Exception original) : Exception("Bedrock reported an operation or cleanup failure.")
    {
        internal Exception Original { get; } = original;
    }

    private sealed class ConverseOperation
    {
        internal BedrockClientFactory.Lease<Amazon.Bedrock.AmazonBedrockClient>? Control { get; set; }
        internal IBedrockEventStream? Stream { get; set; }
        internal IAsyncEnumerator<IEventStreamEvent>? Events { get; set; }
    }

    /// <summary>
    /// Estimates the Converse request's input without I/O. The history up to the last reply that recorded
    /// Bedrock's reported token counts is counted from them; only what follows is tokenized.
    /// </summary>
    public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
    {
        _ = ValidateRequest(connection, request);
        if (request.Resources is not null) return null;
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

    /// <summary>Admits a Converse request without acquiring an SDK client or preparing resources.</summary>
    internal LlmProviderCapabilities ValidateRequest(LlmConnection connection, LlmChatRequest request)
    {
        EnsureTransport(connection);
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            throw new ArgumentException("A model id is required.", nameof(request));
        }
        if (connection.ProviderOptions.Count > 0 || request.ProviderOptions.Count > 0)
        {
            throw new NotSupportedException(
                "Bedrock provider request options are not yet mapped to Converse fields.");
        }

        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        StructuredOutput.ValidateRequest(request);
        if (request.StructuredOutput is { } structured)
        {
            _ = StructuredOutput.ResolveEnforcement(structured, capabilities);
        }
        HostedTools.Validate(request, capabilities);
        ExplicitCaches.Validate(request, capabilities.SupportsExplicitCache);
        ProviderControlPolicy.Validate(connection, request);
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
        LlmClientBase.ValidatePromptAttachments(request, capabilities, CanPrepareResources);
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

        var reply = new BedrockReply(blocks, response.StopReason?.Value, response.Usage, response.ResponseMetadata?.RequestId,
            response.Trace?.PromptRouter?.InvokedModelId);
        LlmChatResult result = BuildResult(connection, request, enforcement, reply);
        LlmReasoningText[] reasoningText = [.. blocks.OfType<BedrockReasoningBlock>()
            .Where(block => block.Text.Length > 0)
            .Select(block => new LlmReasoningText(block.Text, IsSummary: false))];
        return CompleteResponseEvents.From(result,
            LlmCapabilityOverrideLookup.Apply(Defaults, connection, request.ModelId).SupportsReasoningText ? reasoningText : []);
    }

    /// <summary>
    /// Builds the result of a complete Converse reply, streamed or not. Tool calls return only with
    /// <see cref="LlmCompletionStatus.RequiresAction"/> or <see cref="LlmCompletionStatus.Unknown"/>.
    /// An unknown stop preserves observed calls without continuation or structured-answer evaluation.
    /// A continuation-eligible reply with reasoning keeps the ordered reply as continuation state for signed replay.
    /// Other continuation-eligible replies keep their reported token counts.
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
        IReadOnlyList<LlmToolCall> toolCalls = status is LlmCompletionStatus.RequiresAction or LlmCompletionStatus.Unknown ? validCalls : [];
        LlmStructuredOutputValidation? validation = request.StructuredOutput is { } structured
            && enforcement is not null
            && status is not (LlmCompletionStatus.RequiresAction or LlmCompletionStatus.Unknown)
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
        LlmUsage? usage = null;
        if (reply.Usage is { } reported)
        {
            usage = AnthropicUsage.Create(reported.InputTokens, reported.OutputTokens,
                reported.CacheReadInputTokens, reported.CacheWriteInputTokens);
            if (reported.TotalTokens is { } total)
                usage = (usage ?? new LlmUsage(null, null, null)) with { TotalTokens = total };
        }
        return new LlmChatResult
        {
            Text = text,
            ModelId = ObservedModel.Read(reply.ModelId, LlmTransport.AwsBedrockConverse, connection,
                LlmCredential.Parse(connection.Credential), null),
            Refusal = status == LlmCompletionStatus.Refused ? text : null,
            Status = status,
            FinishReason = MapFinishReason(stopReason),
            IncompleteReason = status == LlmCompletionStatus.Incomplete ? stopReason : null,
            Usage = usage,
            ContinuationState = status is LlmCompletionStatus.Failed or LlmCompletionStatus.Unknown
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
                    output.Add(item: new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;
                case BedrockReasoningBlock reasoning:
                    var item = new JsonObject { ["type"] = "reasoning", ["text"] = reasoning.Text };
                    if (reasoning.Signature is not null)
                    {
                        item["signature"] = reasoning.Signature;
                    }
                    output.Add(item: item);
                    break;
                case BedrockRedactedReasoningBlock redacted:
                    output.Add(item: new JsonObject { ["type"] = "redacted_reasoning", ["data"] = Convert.ToBase64String(redacted.Data) });
                    break;
                case BedrockToolUseBlock tool when toolCalls.Any(call => call.Id == tool.Id):
                    output.Add(item: new JsonObject
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
        || ClientTools.Functions(message.ToolResults).Any(result => result.Output.Parts.Any(part => part is not LlmToolOutputText));

    /// <summary>The textual projection a Converse request carries, for local token counting.</summary>
    private static string Projection(LlmChatRequest request, IEnumerable<LlmChatMessage> messages, bool framing)
    {
        var projection = new System.Text.Json.Nodes.JsonObject
        {
            ["messages"] = new System.Text.Json.Nodes.JsonArray([.. messages.Select(message => new System.Text.Json.Nodes.JsonObject
            {
                ["role"] = message.Role,
                ["content"] = message.Content,
                ["tool_calls"] = new System.Text.Json.Nodes.JsonArray([.. ClientTools.Functions(message.ToolCalls).Select(call =>
                    (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["name"] = call.Name, ["input"] = call.ArgumentsJson })]),
                ["tool_results"] = new System.Text.Json.Nodes.JsonArray([.. ClientTools.Functions(message.ToolResults).SelectMany(result => result.Output.Parts)
                    .OfType<LlmToolOutputText>().Select(part => (System.Text.Json.Nodes.JsonNode)part.Text)])
            })])
        };
        if (framing)
        {
            projection["system"] = request.Instructions;
            projection["tools"] = new System.Text.Json.Nodes.JsonArray([.. ClientTools.Functions(request.Tools).Select(tool =>
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
            control,
            aws.Region!,
            logger,
            LlmCredential.Parse(connection.Credential)!,
            control.Token);
        control.ThrowIfCanceled();
        return models;
    }

    public void ValidateCompaction(LlmConnection connection, LlmChatRequest before, LlmChatRequest after)
    {
        int lastSigned = -1;
        for (int index = 0; index < before.Messages.Count; index++)
        {
            LlmChatMessage message = before.Messages[index];
            if (message.ContinuationState is not null
                && ProviderContinuationStateCodec.DecodeMessage(message, connection, before.ModelId)
                    .OfType<JsonObject>().Any(item => item["type"]?.GetValue<string>() == "reasoning"
                        && item["signature"] is JsonValue signature && signature.TryGetValue<string>(out _)))
            {
                lastSigned = index;
            }
        }
        if (lastSigned < 0) return;

        var streams = new List<MemoryStream>();
        try
        {
            LlmChatRequest prefix = before with { Messages = before.Messages.Take(lastSigned + 1).ToArray() };
            List<SystemContentBlock> originalSystem = BuildSystem(before);
            List<SystemContentBlock> replacementSystem = BuildSystem(after);
            List<Message> original = BuildMessages(connection, prefix, before.ModelId, streams);
            List<Message> replacement = BuildMessages(connection, after, after.ModelId, streams, original.Count);
            if (originalSystem.Count != replacementSystem.Count
                || originalSystem.Where((block, index) => block.Text != replacementSystem[index].Text).Any()
                || original.Count != replacement.Count
                || original.Where((message, index) => !SameMessage(message, replacement[index])).Any())
            {
                throw new NotSupportedException("Compaction cannot change the prior messages of signed Bedrock history. Start a fresh context explicitly instead.");
            }
        }
        finally
        {
            foreach (MemoryStream stream in streams) stream.Dispose();
        }
    }

    private static bool SameMessage(Message left, Message right) =>
        left.Role?.Value == right.Role?.Value && left.Content.Count == right.Content.Count
        && left.Content.Where((part, index) => !SameContent(part, right.Content[index])).Any() == false;

    private static bool SameContent(ContentBlock left, ContentBlock right) =>
        left.Text == right.Text
        && SameReasoning(left.ReasoningContent, right.ReasoningContent)
        && SameToolUse(left.ToolUse, right.ToolUse)
        && SameToolResult(left.ToolResult, right.ToolResult)
        && SameImage(left.Image, right.Image)
        && SameDocumentBlock(left.Document, right.Document);

    private static bool SameReasoning(ReasoningContentBlock? left, ReasoningContentBlock? right) =>
        left is null ? right is null : right is not null
        && (left.ReasoningText is null ? right.ReasoningText is null : right.ReasoningText is not null
            && left.ReasoningText.Text == right.ReasoningText.Text
            && left.ReasoningText.Signature == right.ReasoningText.Signature)
        && SameBytes(left.RedactedContent, right.RedactedContent);

    private static bool SameToolUse(ToolUseBlock? left, ToolUseBlock? right) =>
        left is null ? right is null : right is not null && left.ToolUseId == right.ToolUseId
        && left.Name == right.Name && SameDocument(left.Input, right.Input);

    private static bool SameToolResult(ToolResultBlock? left, ToolResultBlock? right) =>
        left is null ? right is null : right is not null && left.ToolUseId == right.ToolUseId
        && left.Status?.Value == right.Status?.Value && left.Content.Count == right.Content.Count
        && left.Content.Where((part, index) => !SameToolResultContent(part, right.Content[index])).Any() == false;

    private static bool SameToolResultContent(ToolResultContentBlock left, ToolResultContentBlock right) =>
        left.Text == right.Text && SameImage(left.Image, right.Image)
        && SameDocumentBlock(left.Document, right.Document) && SameDocument(left.Json, right.Json);

    private static bool SameImage(ImageBlock? left, ImageBlock? right) =>
        left is null ? right is null : right is not null && left.Format?.Value == right.Format?.Value
        && SameBytes(left.Source?.Bytes, right.Source?.Bytes);

    private static bool SameDocumentBlock(DocumentBlock? left, DocumentBlock? right) =>
        left is null ? right is null : right is not null && left.Format?.Value == right.Format?.Value
        && left.Name == right.Name && SameBytes(left.Source?.Bytes, right.Source?.Bytes);

    private static bool SameDocument(Document? left, Document? right) =>
        left.HasValue == right.HasValue && (!left.HasValue
            || string.Equals(SerializeDocument(left), SerializeDocument(right), StringComparison.Ordinal));

    private static bool SameBytes(MemoryStream? left, MemoryStream? right) =>
        left is null ? right is null : right is not null && left.ToArray().AsSpan().SequenceEqual(right.ToArray());

    private static MemoryStream ContentStream(byte[] bytes, List<MemoryStream>? temporaryStreams)
    {
        var stream = new MemoryStream(bytes, writable: false);
        temporaryStreams?.Add(stream);
        return stream;
    }

    private static List<SystemContentBlock> BuildSystem(LlmChatRequest request)
    {
        var system = new List<SystemContentBlock>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            system.Add(new SystemContentBlock { Text = request.Instructions });
        }
        foreach (LlmChatMessage message in request.Messages)
        {
            if (message.Role is "system" or "developer"
                && (!string.IsNullOrEmpty(message.Content) || message.ToolCalls.Count > 0
                    || message.ToolResults.Count > 0 || message.Attachments.Count > 0))
            {
                system.Add(new SystemContentBlock { Text = message.Content });
            }
        }
        return system;
    }

    private static List<Message> BuildMessages(LlmConnection connection, LlmChatRequest request, string modelId,
        List<MemoryStream>? temporaryStreams = null, int? messageLimit = null)
    {
        var messages = new List<Message>();
        int documents = 0;
        foreach (LlmChatMessage message in request.Messages)
        {
            if (messageLimit is int limit && messages.Count >= limit) break;
            if (message.ContinuationState is not null
                && ProviderContinuationStateCodec.DecodeMessage(message, connection, request.ModelId) is { Count: > 0 } retained)
            {
                // The decoded state already matches the visible text and tool calls, so it replaces them.
                messages.Add(new Message { Role = ConversationRole.Assistant, Content = ReplayContent(retained, temporaryStreams) });
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
                continue;
            }
            else
            {
                var content = new List<ContentBlock>();
                foreach (LlmToolResult toolResult in ClientTools.Functions(message.ToolResults))
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
                                ModelRichContent(modelId).For(connection, modelId)), ref documents, temporaryStreams)
                        }
                    });
                }

                if (!string.IsNullOrEmpty(message.Content))
                {
                    content.Add(new ContentBlock { Text = message.Content });
                }

                foreach (LlmAttachment attachment in message.Attachments.Select(LlmAttachments.RequireBinary))
                {
                    content.Add(BuildPromptAttachment(attachment, ref documents, temporaryStreams));
                }

                foreach (LlmToolCall call in ClientTools.Functions(message.ToolCalls))
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
        return messages;
    }

    internal static ConverseRequest BuildRequest(
        LlmConnection connection,
        LlmChatRequest request,
        string modelId,
        LlmStructuredOutputEnforcement? enforcement = null)
    {
        List<SystemContentBlock> system = BuildSystem(request);
        List<Message> messages = BuildMessages(connection, request, modelId);
        if (messages.Count == 0)
        {
            throw new InvalidOperationException(
                "Bedrock requires at least one user or assistant message.");
        }

        var result = new ConverseRequest { ModelId = modelId, Messages = messages };
        if (enforcement == LlmStructuredOutputEnforcement.ProviderJsonSchema
            && request.StructuredOutput is { } structured)
        {
            result.OutputConfig = new OutputConfig
            {
                TextFormat = new OutputFormat
                {
                    Type = OutputFormatType.Json_schema,
                    Structure = new OutputFormatStructure
                    {
                        JsonSchema = new JsonSchemaDefinition
                        {
                            Name = structured.Name,
                            Description = structured.Description,
                            Schema = structured.Schema.GetRawText()
                        }
                    }
                }
            };
        }
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
                Tools = ClientTools.Functions(request.Tools).Select(definition => new Tool
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

        // Bedrock records this free-form map. Keep it deliberately generic and never derive it from a
        // user, prompt, application, tenant, or connection name.
        result.RequestMetadata = new Dictionary<string, string>
        {
            [RequestIdMetadataKey] = $"request-{Interlocked.Increment(ref _requestCounter)}"
        };
        BedrockPromptCache.Apply(result, request.PromptCache, modelId);
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
            case BedrockModelFamily.OpenAiGptOss:
                fields["reasoning_effort"] = LlmReasoningEffort.Word(level);
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
        OutputConfig = request.OutputConfig,
        RequestMetadata = request.RequestMetadata,
        AdditionalModelRequestFields = request.AdditionalModelRequestFields
    };

    /// <summary>The assistant content blocks of a retained Converse reply, in reply order.</summary>
    private static List<ContentBlock> ReplayContent(JsonArray retained, List<MemoryStream>? temporaryStreams = null)
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
                        RedactedContent = ContentStream(Convert.FromBase64String(item["data"]!.GetValue<string>()), temporaryStreams)
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

    private static ContentBlock BuildPromptAttachment(LlmAttachment attachment, ref int documents,
        List<MemoryStream>? temporaryStreams = null)
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
                        Bytes = ContentStream(attachment.Data.ToArray(), temporaryStreams)
                    }
                }
            },
            LlmBinaryKind.Document => new ContentBlock
            {
                Document = new DocumentBlock
                {
                    Format = BedrockDocumentFormat(mediaType),
                    Name = $"prompt-document-{++documents}",
                    Source = new DocumentSource
                    {
                        Bytes = ContentStream(attachment.Data.ToArray(), temporaryStreams)
                    }
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(attachment))
        };
    }

    /// <summary>The ordered Converse blocks; an empty result still sends one empty text block.</summary>
    private static List<ToolResultContentBlock> BuildToolResultContent(ProjectedToolOutput projected, ref int documents,
        List<MemoryStream>? temporaryStreams = null)
    {
        var content = new List<ToolResultContentBlock>();
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
                        Source = new ImageSource { Bytes = ContentStream(image.Data.ToArray(), temporaryStreams) }
                    }
                },
                // Converse document names must be unique within a request and use a restricted character set.
                SentFile file => new ToolResultContentBlock
                {
                    Document = new DocumentBlock
                    {
                        Format = ToolDocumentFormat(file.MediaType),
                        Name = $"tool-result-document-{++documents}",
                        Source = new DocumentSource { Bytes = ContentStream(file.Data.ToArray(), temporaryStreams) }
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
            _ when LlmAttachments.IsTextMediaType(mediaType) => DocumentFormat.Txt,
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
        BedrockDocumentJson.Read(json);

    private static string SerializeDocument(Document? document) => BedrockDocumentJson.Write(document);

    private void EnsureTransport(LlmConnection connection)
    {
        RuntimeInputAdmission.Validate(connection);
        if (connection.Transport != Transport)
        {
            throw new ArgumentException(
                $"A {connection.Transport} connection cannot use the Bedrock client.",
                nameof(connection));
        }
        if (connection.AdditionalHeaders.Count > 0)
        {
            throw new NotSupportedException(
                "Bedrock Converse does not support custom HTTP headers. Remove AdditionalHeaders from the connection.");
        }
    }
}
