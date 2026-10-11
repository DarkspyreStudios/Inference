using Darkspyre.Inference.Common;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Shared Responses projection, streaming and retained-output mapping.</summary>
internal abstract class ResponsesLlmClientBase(HttpClient http) : LlmClientBase(http), ILlmContextEstimator
{
    public string? EstimateDiagnostic => null;

    public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request,
        IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> resources)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        ValidateModelSampling(connection, request);
        var enforcement = ResolveStructuredOutputEnforcement(connection, request, capabilities);
        // Attachments, binary tool parts and caller-owned resources are counted by AttachmentTokens and the resolved
        // descriptors; the tokenized projection leaves them out. Accounting never reads or uploads resource content.
        IReadOnlyList<LlmChatMessage> sent = request.Messages;
        request = request with { Resources = null, Messages = [.. sent.Select(WithoutMedia)] };
        JsonObject payload = BuildPayload(connection, request, enforcement, (capabilities.SupportsStreaming != InferenceSupport.Unsupported));
        RequestJson.ApplyProviderOptions(payload, connection, request);

        // The size is the request actually being sent, tokenized locally. A count the provider reported for an earlier
        // request describes that request, not this one, so none is used (Brandon, 2026-10-11).
        long media = checked(sent.Sum(MediaTokens) + ResourceSizes.Tokens(sent, sent, resources));
        // Tokenize the actual textual projection, including schemas/framing. This is an estimate:
        // server framing/current-model tokenization can differ from the bundled o200k encoding.
        var input = new JsonObject
        {
            ["input"] = payload["input"]?.DeepClone(),
            ["instructions"] = payload["instructions"]?.DeepClone(),
            ["tools"] = payload["tools"]?.DeepClone(),
            ["text"] = payload["text"]?.DeepClone()
        };
        long cipherTokens = 0;
        if (Transport == LlmTransport.OpenAiCodex)
        {
            foreach (JsonObject item in ((JsonArray)input["input"]!).OfType<JsonObject>())
            {
                if (item["type"]?.GetValue<string>() is not ("reasoning" or "compaction" or "compaction_summary")
                    || item["encrypted_content"] is not { } cipher) continue;
                // Codex 0.159.3 estimates decoded bytes minus fixed framing, then four bytes per token.
                // A CLR string's int length keeps both multiplications below long overflow.
                long bytes = Math.Max(0, cipher.GetValue<string>().Length * 3L / 4 - 650);
                cipherTokens = checked(cipherTokens + (bytes + 3) / 4);
                item.Remove("encrypted_content");
            }
        }
        return new ContextEstimate(checked(LocalTokenCount.Count(input.ToJsonString()) + cipherTokens + media));
    }

    /// <summary>The message as the tokenizer counts it: without attachments, binary tool parts or resource parts.</summary>
    private static LlmChatMessage WithoutMedia(LlmChatMessage message) => message with
    {
        Attachments = [],
        ToolResults = [.. message.ToolResults.Select(result => result is LlmToolResult function
            ? function with { Output = function.Output with { Parts = [.. function.Output.Parts.Where(part => !IsMedia(part))] } }
            : result)]
    };

    private static bool IsMedia(LlmToolOutputPart part) =>
        part is LlmToolOutputBinary or LlmToolOutputResource { Data: not null } or LlmToolOutputSessionResource;

    /// <summary>The AttachmentTokens cost of a message's inline attachments and binary tool parts.</summary>
    private static long MediaTokens(LlmChatMessage message)
    {
        long tokens = 0;
        foreach (LlmAttachment attachment in message.Attachments.OfType<LlmAttachment>())
            tokens = checked(tokens + AttachmentTokens.Estimate(attachment.Kind, attachment.MediaType, attachment.Data.Span));
        foreach (LlmToolOutputPart part in message.ToolResults.OfType<LlmToolResult>().SelectMany(result => result.Output.Parts))
            tokens = checked(tokens + part switch
            {
                LlmToolOutputBinary binary => AttachmentTokens.Estimate(binary.Kind, binary.MediaType, binary.Data.Span),
                LlmToolOutputResource { Data: { } data } resource =>
                    AttachmentTokens.Estimate(resource.BodyKind!.Value, resource.MediaType, data.Span),
                _ => 0
            });
        return tokens;
    }

    public override IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
        CancellationToken cancellationToken = default) => StreamCoreAsync(connection, request, cancellationToken);

    private async IAsyncEnumerable<LlmStreamEvent> StreamCoreAsync(LlmConnection connection, LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken);
        cancellationToken = consumption.Token;
        void Fence() { cancellationToken.ThrowIfCancellationRequested(); consumption.Fence(); cancellationToken.ThrowIfCancellationRequested(); }
        Fence();
        request = ClientTools.Snapshot(request);
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        ValidateModelSampling(connection, request);
        LlmStructuredOutputEnforcement? enforcement = ResolveStructuredOutputEnforcement(connection, request, capabilities);
        bool resourceAdmitted = request.Resources is not null;

        LlmResourcePreparation? resources = null;
        IAsyncEnumerator<LlmStreamEvent>? iterator = null;
        Exception? failure = null;
        LlmChatCompletionEvent? terminal = null;
        try
        {
            try
            {
                if (request.Resources is not null)
                {
                    resources = await LlmResourcePreparation.PrepareInlineAsync(request, Fence, cancellationToken, includeSelection: false).ConfigureAwait(false);
                    ValidateRequest(connection, resources.Request, capabilities);
                    Fence();
                    request = resources.Request;
                }
                JsonObject payload = BuildPayload(connection, request, enforcement, (capabilities.SupportsStreaming != InferenceSupport.Unsupported), resources);
                RequestJson.ApplyProviderOptions(payload, connection, request);
                Fence();
                iterator = StreamResponseAsync(connection, request, payload, capabilities, enforcement, consumption, resources is not null, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error)
            {
                failure = resources is null ? error : LlmResourcePreparation.AsResourceFailure(error, cancellationToken);
                ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }
            while (true)
            {
                bool moved;
                try { moved = await iterator.MoveNextAsync().ConfigureAwait(false); Fence(); }
                catch (Exception error)
                {
                    failure = resources is null ? error : LlmResourcePreparation.AsResourceFailure(error, cancellationToken);
                    ExceptionDispatchInfo.Capture(failure).Throw();
                    throw;
                }
                if (!moved) break;
                if (resources is not null && iterator.Current is LlmChatCompletionEvent completion) terminal = completion;
                else yield return iterator.Current;
            }
        }
        finally
        {
            var cleanup = new List<Exception>();
            try { if (iterator is not null) await iterator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanup.Add(resources is null ? error : LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: iterator)); }
            try { if (resources is not null) await resources.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanup.Add(LlmResourcePreparation.AsResourceFailure(error, cancellationToken)); }
            if (cleanup.Count > 0)
            {
                if (failure is not null) cleanup.Insert(0, failure);
                if (cleanup.Count == 1) ExceptionDispatchInfo.Capture(cleanup[0]).Throw();
                ExceptionDispatchInfo.Capture(new AggregateException(cleanup)).Throw();
            }
        }
        try { Fence(); }
        catch (Exception error) when (resourceAdmitted)
        { ExceptionDispatchInfo.Capture(LlmResourcePreparation.AsResourceFailure(error, cancellationToken)).Throw(); throw; }
        if (terminal is not null) yield return terminal;
    }

    private async IAsyncEnumerable<LlmStreamEvent> StreamResponseAsync(LlmConnection connection, LlmChatRequest request,
        JsonObject payload, LlmProviderCapabilities capabilities, LlmStructuredOutputEnforcement? enforcement,
        SdkHttpConsumption.Operation consumption, bool resourceAdmitted,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpRequestMessage? httpRequest = null;
        HttpResponseMessage? response = null;
        Stream? stream = null;
        IAsyncEnumerator<LlmStreamEvent>? body = null;
        Exception? failure = null;
        try
        {
            LlmCredential? credential;
            string[] secrets;
            LlmStreamEvent? observation;
            try
            {
                httpRequest = new(HttpMethod.Post, BuildRequestUri(connection, request)) { Content = RequestJson.Content(payload) };
                ApplyRequestHeaders(httpRequest, connection, request);
                credential = LlmCredential.Parse(connection.Credential);
                response = await Http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                secrets = httpRequest.Headers.SelectMany(header => header.Value).ToArray();
                observation = ObserveAccountUsage(response) is { } observed
                    ? consumption.Publish<LlmStreamEvent>(new AccountUsageObservedEvent(observed)) : null;
            }
            catch (Exception error) { failure = error; throw; }
            if (observation is not null) yield return observation;
            try
            {
                await ProviderHttp.ThrowIfUnsuccessfulAsync(response, Transport, credential, "OpenAI Responses request",
                    secrets, cancellationToken).ConfigureAwait(false);
                if ((capabilities.SupportsStreaming != InferenceSupport.Unsupported))
                    stream = await SdkHttpConsumption.ReadAsStreamAsync(response.Content, cancellationToken).ConfigureAwait(false);
                body = ReadResponseBodyAsync(connection, request, response, stream, capabilities, enforcement, credential, consumption, secrets, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error) { failure = error; throw; }
            while (true)
            {
                bool moved;
                try { moved = await body.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error) { failure = error; throw; }
                if (!moved) break;
                yield return body.Current;
            }
        }
        finally
        {
            var cleanup = new List<Exception>();
            void Record(Exception error, object? refusedOwner = null) => cleanup.Add(resourceAdmitted
                ? LlmResourcePreparation.AsCleanupFailure(error, refusedOwner: refusedOwner) : error);
            bool bodyRefused = false;
            try { if (body is not null) await body.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { bodyRefused = true; Record(error, (body, stream, response)); }
            bool streamRefused = false;
            try { if (!bodyRefused && stream is not null) await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { streamRefused = true; Record(error, (stream, response)); }
            // Response content owns the same stream; a refused release cannot be retried through its owner.
            if (!bodyRefused && !streamRefused)
            {
                try { response?.Dispose(); }
                catch (Exception error) { Record(error, response); }
            }
            try { httpRequest?.Dispose(); }
            catch (Exception error) { Record(error, httpRequest); }
            if (cleanup.Count > 0)
            {
                if (failure is not null) cleanup.Insert(0, failure);
                if (cleanup.Count == 1) ExceptionDispatchInfo.Capture(cleanup[0]).Throw();
                ExceptionDispatchInfo.Capture(new AggregateException(cleanup)).Throw();
            }
        }
    }

    private async IAsyncEnumerable<string> ReadResponseDataAsync(Stream stream, HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var data = Sse.ReadDataPayloadsAsync(stream, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool moved;
            try { moved = await data.MoveNextAsync().ConfigureAwait(false); }
            catch (IOException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LlmProviderException("The model response stream was interrupted.", Transport,
                    statusCode: response.StatusCode, innerException: error, isResponseStreamInterrupted: true);
            }
            if (!moved) yield break;
            yield return data.Current;
        }
    }

    private async IAsyncEnumerable<LlmStreamEvent> ReadResponseBodyAsync(LlmConnection connection, LlmChatRequest request,
        HttpResponseMessage response, Stream? stream, LlmProviderCapabilities capabilities, LlmStructuredOutputEnforcement? enforcement,
        LlmCredential? credential, SdkHttpConsumption.Operation consumption,
        IReadOnlyList<string> secrets, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if ((capabilities.SupportsStreaming == InferenceSupport.Unsupported))
        {
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
            foreach (LlmStreamEvent completeEvent in await ReadCompleteResponseAsync(
                         body, response, request, enforcement, credential, connection, (capabilities.SupportsReasoningText != InferenceSupport.Unsupported),
                         secrets, cancellationToken).ConfigureAwait(false))
            {
                yield return consumption.Publish<LlmStreamEvent>(completeEvent);
            }

            yield break;
        }

        var text = new StringBuilder();
        var refusal = new StringBuilder();
        var toolCallStream = new OpenAiToolCallStream();
        ResponsesHostedToolObserver? hosted = CreateHostedToolObserver(() =>
            ProtocolException($"{ResponseName} returned a malformed hosted-tool observation.", response, string.Empty, credential,
                extraSecrets: secrets));
        var completedOutput = new SortedDictionary<int, JsonElement>();
        bool completed = false;
        string? observedModel = null;

        await foreach (string data in ReadResponseDataAsync(stream!, response, cancellationToken))
        {
            if (data == "[DONE]")
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(data);
            }
            catch (JsonException exception)
            {
                throw ProtocolException(
                    $"{ResponseName} returned a malformed stream event.",
                    response,
                    data,
                    credential,
                    exception, secrets);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                string? type = LlmJson.GetString(root, "type");
                if (LlmJson.GetObject(root, "response") is { } modelResponse)
                    observedModel = ObservedModel.Read(modelResponse, "model", Transport, connection,
                        credential, response.RequestMessage, observedModel, secrets);
                if (hosted is not null)
                    foreach (LlmStreamEvent observed in hosted.Observe(root)) yield return consumption.Publish(observed);
                switch (type)
                {
                    case "response.text.delta":
                    case "response.output_text.delta":
                        {
                            string delta = LlmJson.GetString(root, "delta") ?? string.Empty;
                            if (delta.Length > 0)
                            {
                                text.Append(delta);
                                yield return consumption.Publish<LlmStreamEvent>(new LlmTextDeltaEvent(delta));
                            }

                            break;
                        }

                    case "response.reasoning_summary_text.delta":
                    case "response.reasoning_text.delta":
                        if ((capabilities.SupportsReasoningText != InferenceSupport.Unsupported) && LlmJson.GetString(root, "delta") is { Length: > 0 } reasoning)
                        {
                            yield return consumption.Publish<LlmStreamEvent>(new LlmReasoningDeltaEvent(reasoning,
                                type == "response.reasoning_summary_text.delta"));
                        }
                        break;
                    case "response.refusal.delta":
                        {
                            string delta = LlmJson.GetString(root, "delta") ?? string.Empty;
                            if (delta.Length > 0)
                            {
                                refusal.Append(delta);
                                yield return consumption.Publish<LlmStreamEvent>(new LlmRefusalDeltaEvent(delta));
                            }

                            break;
                        }

                    case "response.output_item.added":
                        foreach (LlmStreamEvent toolEvent in toolCallStream.ObserveAdded(root))
                        {
                            yield return consumption.Publish<LlmStreamEvent>(toolEvent);
                        }

                        break;

                    case "response.function_call_arguments.delta":
                        foreach (LlmStreamEvent toolEvent in toolCallStream.ObserveArgumentsDelta(root))
                        {
                            yield return consumption.Publish<LlmStreamEvent>(toolEvent);
                        }

                        break;

                    case "response.output_item.done":
                        if (root.TryGetProperty("item", out var outputItem))
                        {
                            int index = LlmJson.GetInt(root, "output_index") ?? completedOutput.Count;
                            completedOutput[index] = outputItem.Clone();
                        }
                        foreach (LlmStreamEvent toolEvent in toolCallStream.ObserveDone(root))
                        {
                            yield return consumption.Publish<LlmStreamEvent>(toolEvent);
                        }

                        break;

                    case "response.completed":
                    case "response.incomplete":
                    case "response.failed":
                    case "response.cancelled":
                        {
                            JsonElement terminalOutput = CompleteOutput(root, completedOutput, Transport);
                            LlmChatResult result = BuildResult(
                                terminalOutput,
                                response,
                                data,
                                text.ToString(),
                                refusal.ToString(),
                                request.StructuredOutput,
                                enforcement,
                                credential,
                                connection,
                                request.ModelId,
                                observedModel,
                                secrets);
                            foreach (LlmStreamEvent toolEvent in toolCallStream.CompleteMissing(result.ToolCalls,
                                         emitMissingArguments: false))
                            {
                                yield return consumption.Publish<LlmStreamEvent>(toolEvent);
                            }

                            if (hosted is not null)
                            {
                                foreach (LlmStreamEvent toolEvent in await hosted.CompleteAsync(root, cancellationToken).ConfigureAwait(false))
                                    yield return consumption.Publish(toolEvent);
                                result = hosted.Decorate(result);
                            }

                            completed = true;
                            yield return consumption.Publish<LlmStreamEvent>(new LlmChatCompletionEvent(result));
                            break;
                        }

                    case "error":
                        throw StreamError(root, response, data, credential, secrets);
                }
            }

            if (completed)
            {
                yield break;
            }
        }

        throw new LlmProviderException("The model response stream was interrupted.", Transport,
            statusCode: response.StatusCode, isResponseStreamInterrupted: true);
    }

    protected virtual LlmAccountUsageSnapshot? ObserveAccountUsage(HttpResponseMessage response) =>
        OpenAiRateLimitHeaders.Parse(response.Headers, DateTimeOffset.UtcNow);

    protected virtual ResponsesHostedToolObserver? CreateHostedToolObserver(Func<Exception> malformed) => null;

    private const string ResponseName = "OpenAI Responses";

    /// <summary>Applies OpenAI Responses model sampling rules only to the OpenAI API transport.</summary>
    protected virtual void ValidateModelSampling(LlmConnection connection, LlmChatRequest request)
    {
        if (Transport == LlmTransport.OpenAiResponses)
        {
            OpenAiModelProfiles.ValidateSampling(request);
        }
    }

    protected virtual Uri BuildRequestUri(LlmConnection connection, LlmChatRequest request) =>
        ProviderHttp.BuildUri(connection, "responses");

    /// <summary>
    /// Maps a response that arrived as one Response object. Reasoning comes from the reasoning items in output order.
    /// </summary>
    private async Task<IReadOnlyList<LlmStreamEvent>> ReadCompleteResponseAsync(
        string body,
        HttpResponseMessage response,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        LlmCredential? credential,
        LlmConnection connection,
        bool reasoningText, IReadOnlyList<string> secrets, CancellationToken token)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw ProtocolException(
                $"{ResponseName} returned a malformed response.",
                response,
                body,
                credential,
                exception, secrets);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolException($"{ResponseName} returned a malformed response.", response,
                    body, credential, extraSecrets: secrets);
            }

            LlmChatResult result = BuildResult(
                root,
                response,
                body,
                string.Empty,
                string.Empty,
                request.StructuredOutput,
                enforcement,
                credential,
                connection,
                request.ModelId, extraSecrets: secrets);
            ResponsesHostedToolObserver? hosted = CreateHostedToolObserver(() =>
                ProtocolException($"{ResponseName} returned a malformed hosted-tool observation.", response, string.Empty, credential,
                    extraSecrets: secrets));
            IReadOnlyList<LlmStreamEvent> hostedEvents = hosted is null ? []
                : await hosted.CompleteAsync(root, token).ConfigureAwait(false);
            if (hosted is not null) result = hosted.Decorate(result);
            var observed = CompleteResponseEvents.From(result, reasoningText ? ReadReasoning(root) : []).ToList();
            observed.InsertRange(observed.Count - 1, hostedEvents);
            return observed;
        }
    }

    private static List<LlmReasoningText> ReadReasoning(JsonElement root)
    {
        var reasoning = new List<LlmReasoningText>();
        if (!root.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array)
        {
            return reasoning;
        }

        foreach (JsonElement item in output.EnumerateArray())
        {
            if (LlmJson.GetString(item, "type") != "reasoning")
            {
                continue;
            }

            AddReasoning(item, "summary", "summary_text", isSummary: true);
            AddReasoning(item, "content", "reasoning_text", isSummary: false);
        }

        return reasoning;

        void AddReasoning(JsonElement item, string property, string partType, bool isSummary)
        {
            if (!item.TryGetProperty(property, out JsonElement parts) || parts.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (LlmJson.GetString(part, "type") == partType && LlmJson.GetString(part, "text") is { } text)
                {
                    reasoning.Add(new LlmReasoningText(text, isSummary));
                }
            }
        }
    }

    protected virtual JsonObject BuildPayload(
        LlmConnection connection,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        bool stream,
        LlmResourcePreparation? resources = null)
    {
        JsonArray input = OpenAiResponsesTools.BuildInput(
            request,
            connection,
            OpenAiResponsesTools.BuildMessage,
            output => ProjectToolOutput(connection, request.ModelId, output));

        var payload = new JsonObject
        {
            ["model"] = request.ModelId,
            ["input"] = input,
            ["stream"] = stream,
            ["store"] = false
        };

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            payload["instructions"] = request.Instructions;
        }

        if (request.Temperature is double temperature)
        {
            payload["temperature"] = temperature;
        }
        if (request.TopP is { } topP) { payload["top_p"] = topP; }

        if (request.MaxOutputTokens is int maxOutputTokens)
        {
            payload["max_output_tokens"] = maxOutputTokens;
        }

        if (request.ReasoningLevel is { } level)
        {
            payload["reasoning"] = new JsonObject { ["effort"] = LlmReasoningEffort.OpenAiWord(level) };
        }
        if ((GetCapabilities(connection, request.ModelId).SupportsReasoningText != InferenceSupport.Unsupported)
            && request.ReasoningLevel != LlmReasoningLevel.None
            && (request.ReasoningLevel is not null || ReasonsByDefault(connection, request.ModelId)))
        {
            payload["reasoning"] ??= new JsonObject();
            payload["reasoning"]!["summary"] = "auto";
        }

        // Stateless ordinary responses need their reasoning state just as tool responses do.
        payload["include"] = new JsonArray("reasoning.encrypted_content");
        if (request.Tools.Count > 0)
        {
            payload["tools"] = OpenAiResponsesTools.BuildTools(request.Tools);
        }

        if (request.ToolChoice is { } toolChoice)
        {
            payload["tool_choice"] = OpenAiResponsesTools.BuildToolChoice(toolChoice);
        }

        if (request.AllowParallelToolCalls is bool allowParallelToolCalls)
        {
            payload["parallel_tool_calls"] = allowParallelToolCalls;
        }

        if (request.StructuredOutput is { } structured
            && enforcement != LlmStructuredOutputEnforcement.LocalValidationOnly)
        {
            payload["text"] = BuildStructuredText(structured, enforcement!.Value);
        }

        if (request.ConversationKey is not null)
        {
            payload["prompt_cache_key"] = request.ConversationKey;
        }
        if (Transport == LlmTransport.OpenAiResponses && request.PromptCache is { } hint
            && (OpenAiModelProfiles.Get(request.ModelId).SupportsPromptCacheOptions != InferenceSupport.Unsupported))
        {
            PromptCaching.RejectRawOption(connection, request, "prompt_cache_options");
            if (hint.Retention == LlmPromptCacheRetention.Extended)
                payload["prompt_cache_options"] = new JsonObject { ["ttl"] = "30m" };
        }
        return payload;
    }

    protected virtual LlmStructuredOutputEnforcement? ResolveStructuredOutputEnforcement(
        LlmConnection connection, LlmChatRequest request, LlmProviderCapabilities capabilities) =>
        request.StructuredOutput is { } output ? StructuredOutput.ResolveEnforcement(output, capabilities) : null;

    /// <summary>Only a verified API-key profile requests a default summary; fallback effort levels are not evidence.</summary>
    protected virtual bool ReasonsByDefault(LlmConnection connection, string modelId) =>
        OpenAiModelProfiles.Get(modelId) is { Verified: true, IsReasoning: true };

    private static JsonElement CompleteOutput(JsonElement root, SortedDictionary<int, JsonElement> completedOutput,
        LlmTransport transport)
    {
        JsonElement response = root.TryGetProperty("response", out var nested) ? nested : root;
        if (completedOutput.Count == 0 || (response.TryGetProperty("output", out var output)
                && output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0))
        {
            return root;
        }
        // Codex SSE may provide complete items only in output_item.done and an empty terminal
        // output array. Preserve those items in source order for tool completion and future replay.
        var node = JsonNode.Parse(root.GetRawText())!.AsObject();
        var target = node["response"] as JsonObject ?? node;
        target["output"] = new JsonArray([.. completedOutput.Values.Select(item => JsonNode.Parse(item.GetRawText()))]);
        return JsonElement.Parse(node.ToJsonString());
    }

    protected virtual void ApplyRequestHeaders(
        HttpRequestMessage message, LlmConnection connection, LlmChatRequest request) =>
        ProviderHttp.ApplyHeaders(message, connection);

    private static JsonObject BuildStructuredText(
        LlmStructuredOutputRequest structured,
        LlmStructuredOutputEnforcement enforcement)
    {
        JsonObject format = enforcement switch
        {
            LlmStructuredOutputEnforcement.ProviderJsonSchema => new JsonObject
            {
                ["type"] = "json_schema",
                ["name"] = structured.Name,
                ["strict"] = false,
                ["schema"] = JsonNode.Parse(structured.Schema.GetRawText())
            },
            LlmStructuredOutputEnforcement.ProviderJsonObject => new JsonObject
            {
                ["type"] = "json_object"
            },
            LlmStructuredOutputEnforcement.LocalValidationOnly =>
                throw new ArgumentException("Local validation does not add a provider response format.", nameof(enforcement)),
            _ => throw new ArgumentOutOfRangeException(nameof(enforcement))
        };

        if (!string.IsNullOrWhiteSpace(structured.Description)
            && enforcement == LlmStructuredOutputEnforcement.ProviderJsonSchema)
        {
            format["description"] = structured.Description;
        }

        return new JsonObject { ["format"] = format };
    }

    private LlmChatResult BuildResult(
        JsonElement eventRoot,
        HttpResponseMessage response,
        string rawEvent,
        string streamedText,
        string streamedRefusal,
        LlmStructuredOutputRequest? structuredRequest,
        LlmStructuredOutputEnforcement? enforcement,
        LlmCredential? credential,
        LlmConnection connection,
        string modelId,
        string? observedModel = null,
        IEnumerable<string>? extraSecrets = null)
    {
        JsonElement terminal = LlmJson.GetObject(eventRoot, "response") ?? eventRoot;
        if (LlmContextOverflow.Responses(terminal))
            throw StreamError(terminal, response, rawEvent, credential, extraSecrets);
        return BuildResultCore(eventRoot, response, rawEvent, streamedText, streamedRefusal,
            structuredRequest, enforcement, credential, connection, modelId, observedModel, extraSecrets);
    }

    private LlmChatResult BuildResultCore(
        JsonElement eventRoot,
        HttpResponseMessage response,
        string rawEvent,
        string streamedText,
        string streamedRefusal,
        LlmStructuredOutputRequest? structuredRequest,
        LlmStructuredOutputEnforcement? enforcement,
        LlmCredential? credential,
        LlmConnection connection,
        string modelId,
        string? observedModel,
        IEnumerable<string>? extraSecrets)
    {
        JsonElement responseRoot = eventRoot.TryGetProperty("response", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.Object
                ? nested
                : eventRoot;

        (string finalText, string finalRefusal) = ReadOutput(responseRoot, connection.Transport);
        finalText = finalText.Length > 0 ? finalText : streamedText;
        finalRefusal = finalRefusal.Length > 0 ? finalRefusal : streamedRefusal;

        LlmClientToolCall[] toolCalls = OpenAiResponsesTools.ReadToolCalls(responseRoot, connection.Transport);

        string? providerStatus = LlmJson.GetString(responseRoot, "status");
        if (providerStatus is null && connection.Transport == LlmTransport.OpenAiCodex)
        {
            // Codex determines terminal state from the SSE type without requiring response.status.
            providerStatus = LlmJson.GetString(eventRoot, "type") switch
            {
                "response.completed" => "completed",
                "response.incomplete" => "incomplete",
                "response.failed" => "failed",
                "response.cancelled" => "cancelled",
                _ => null
            };
        }
        string? incompleteReason = LlmJson.GetObject(responseRoot, "incomplete_details") is { } details
            ? LlmJson.GetString(details, "reason")
            : null;
        LlmCompletionStatus status = finalRefusal.Length > 0
            ? LlmCompletionStatus.Refused
            : providerStatus switch
            {
                "incomplete" or "cancelled" => LlmCompletionStatus.Incomplete,
                "failed" => LlmCompletionStatus.Failed,
                "completed" => toolCalls.Length > 0 ? LlmCompletionStatus.RequiresAction : LlmCompletionStatus.Completed,
                _ => LlmCompletionStatus.Unknown
            };

        LlmStructuredOutputValidation? validation = null;
        if (structuredRequest is not null
            && enforcement is not null
            && status != LlmCompletionStatus.RequiresAction)
        {
            validation = finalRefusal.Length > 0
                ? new LlmStructuredOutputValidation(
                    enforcement.Value,
                    false,
                    false,
                    ["Structured output was not evaluated because the provider refused the request."])
                : StructuredOutput.Validate(finalText, structuredRequest, enforcement.Value);
        }

        LlmUsage? usage = ReadUsage(responseRoot);
        return new LlmChatResult
        {
            Text = finalText,
            ModelId = ObservedModel.Read(responseRoot, "model", Transport, connection, credential,
                response.RequestMessage, observedModel, extraSecrets),
            Refusal = finalRefusal.Length == 0 ? null : finalRefusal,
            Status = status,
            FinishReason = status == LlmCompletionStatus.RequiresAction ? "tool_calls" : providerStatus,
            IncompleteReason = incompleteReason,
            Usage = usage,
            ProviderResponseId = LlmJson.GetString(responseRoot, "id"),
            ToolCalls = toolCalls,
            // The reported counts travel with the state, so later accounting can use them for this history.
            ContinuationState = status is LlmCompletionStatus.Failed or LlmCompletionStatus.Incomplete or LlmCompletionStatus.Unknown
                ? null
                : OpenAiResponsesTools.EncodeContinuationState(responseRoot, connection, modelId) is { } state
                    ? state with { InputTokens = usage?.InputTokens, OutputTokens = usage?.OutputTokens }
                    : null,
            StructuredOutput = validation,
            Diagnostics = new LlmProviderDiagnostics(
                (int)response.StatusCode,
                ProviderHttp.ReadRequestId(response, credential, extraSecrets),
                SecretRedactor.Sanitize(rawEvent, credential, extraSecrets ?? []))
        };
    }

    private static (string Text, string Refusal) ReadOutput(JsonElement root, LlmTransport transport)
    {
        var text = new StringBuilder();
        var refusal = new StringBuilder();
        if (!root.TryGetProperty("output", out JsonElement output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, string.Empty);
        }

        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement part in content.EnumerateArray())
            {
                switch (LlmJson.GetString(part, "type"))
                {
                    case "output_text":
                        text.Append(LlmJson.GetString(part, "text"));
                        break;
                    case "refusal":
                        refusal.Append(LlmJson.GetString(part, "refusal"));
                        break;
                }
            }
        }

        return (text.ToString(), refusal.ToString());
    }

    protected virtual LlmUsage? ReadUsage(JsonElement root)
    {
        if (LlmJson.GetObject(root, "usage") is not { } usage)
        {
            return null;
        }

        return new LlmUsage(
            LlmJson.GetInt(usage, "input_tokens"),
            LlmJson.GetInt(usage, "output_tokens"),
            LlmJson.GetInt(usage, "total_tokens"))
        {
            CachedInputTokens = LlmJson.GetObject(usage, "input_tokens_details") is { } inputDetails
                ? LlmJson.GetInt(inputDetails, "cached_tokens") : null,
            ReasoningOutputTokens = LlmJson.GetObject(usage, "output_tokens_details") is { } outputDetails
                ? LlmJson.GetInt(outputDetails, "reasoning_tokens") : null
        };
    }

    private LlmProviderException StreamError(
        JsonElement root,
        HttpResponseMessage response,
        string rawEvent,
        LlmCredential? credential, IEnumerable<string>? extraSecrets = null)
    {
        string safe = SecretRedactor.Sanitize(rawEvent, credential, extraSecrets);
        (string? code, string? message, _) = ProviderHttp.ReadProviderError(
            SecretRedactor.Sanitize(root.GetRawText(), credential, extraSecrets));
        return new LlmProviderException(
            message ?? "The provider returned a stream error.",
            Transport,
            response.StatusCode,
            code,
            ProviderHttp.ReadRequestId(response, credential, extraSecrets),
            sanitizedResponseBody: safe,
            isContextOverflow: LlmContextOverflow.Responses(root))
        {
            Usage = ReadUsage(root)
        };
    }
}
