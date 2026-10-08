using System.Collections.Frozen;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>OAuth-only Grok Responses adapter. The facade owns its published chat catalog.</summary>
internal sealed class XaiResponsesLlmClient(HttpClient http, XaiMediaDownloadClient? mediaDownloads = null) : ResponsesLlmClientBase(http), ILlmCatalogCapabilities,
    ILlmTextTokenizerClient, ILlmCredentialAdmissionPolicy, ILlmModelCatalogClient
{
    void ILlmCredentialAdmissionPolicy.ValidateCredential(LlmConnection connection) => _ = XaiRequestHeaders.Require(connection);

    private const int MaxImageBytes = 20 * 1024 * 1024;
    private static readonly FrozenSet<string> ReservedOptions = new[]
    {
        "model", "input", "stream", "store", "include", "previous_response_id", "service_tier", "instructions",
        "temperature", "top_p", "top_k", "seed", "presence_penalty", "frequency_penalty", "stop", "stop_sequences",
        "max_output_tokens", "reasoning", "reasoning_effort", "tools", "tool_choice", "parallel_tool_calls", "text",
        "prompt_cache_key", "background", "deferred", "agent_count", "max_turns", "no_inline_citations"
    }.ToFrozenSet(StringComparer.Ordinal);

    public override LlmTransport Transport => LlmTransport.XaiResponses;
    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.GrokSubscription;
    protected override bool OffersNonStreamingRequests => true;
    protected override bool OffersUsageDeltas => false;
    protected override bool SupportsResourcePreparation => true;
    protected override ResponsesHostedToolObserver CreateHostedToolObserver(Func<Exception> malformed,
        XaiHostedToolPreparation? preparation) => new XaiHostedToolObserver(malformed,
            preparation ?? throw new InvalidOperationException("Grok hosted observations require the admitted request frame."));

    // xAI quota names and units require its own qualified evidence; OpenAI provenance is not interchangeable.
    protected override LlmAccountUsageSnapshot? ObserveAccountUsage(HttpResponseMessage response) => null;

    protected override LlmUsage? ReadUsage(JsonElement root) => XaiUsage.Enrich(base.ReadUsage(root), root);

    internal XaiFileClient CreateFileClient() => new(Http);
    internal HttpClient SharedHttp => Http;
    internal XaiMediaDownloadClient MediaDownloads => mediaDownloads
        ?? throw new NotSupportedException("The selected xAI core has no unsigned media download route.");

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        OutputLimitEnforcement = LlmOutputLimitEnforcement.ProviderEnforced,
        SupportsStreaming = true,
        SupportsStreamingUsage = true,
        SupportsReasoningText = true,
        SupportsModelListing = true,
        SupportsJsonSchema = true,
        SupportsJsonObject = true,
        SupportsToolUse = true,
        SupportsAgentSessions = true,
        SupportsAgentToolUse = true,
        SupportsAgentTurnInstructions = true,
        SupportsShellTools = true,
        Agent = ManagedAgentPolicy.Capabilities with
        {
            SupportsCompactionHandoff = false,
            CompactionOwnership = LlmAgentOwnership.Provider,
            SupportsShellTools = true,
            SupportedCallerServices = new[] { LlmAgentCallerServiceKind.ShellExecution }.ToFrozenSet(),
            RichContentSupported = RichContentRoutes.Responses
        },
        HostedTools = new[] { LlmHostedToolKind.DocumentSearch, LlmHostedToolKind.WebSearch, LlmHostedToolKind.SocialSearch,
            LlmHostedToolKind.CodeExecution, LlmHostedToolKind.ImageGeneration, LlmHostedToolKind.RemoteMcp }.ToFrozenSet(),
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.None, LlmToolChoiceMode.Required, LlmToolChoiceMode.Specific }.ToFrozenSet(),
            StrictInputSchema = LlmStrictInputSchemaSupport.Supported,
            SupportsParallelCalls = true,
            SupportsParallelCallControl = true,
            SupportsArgumentStreaming = true
        },
        SupportsPromptDocuments = true,
        SupportsFileReferences = true,
        Files = new() { Upload = true, List = true, Get = true, Download = true, Delete = true, TextDownload = true, PublicUrls = true },
        RichContentSupported = RichContentRoutes.Responses
    };

    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        XaiRequestHeaders.ValidateEndpoint(connection);
        XaiModelProfile profile = XaiModelProfiles.Get(connection, modelId);
        LlmProviderCapabilities defaults = DefaultCapabilities with
        {
            ReasoningLevels = profile.ReasoningLevels,
            SupportsTemperature = profile.AllowsSampling,
            SupportsTopP = profile.AllowsSampling,
            SupportsPromptImages = profile.AcceptsImages,
            RichContentSupported = DefaultCapabilities.RichContentSupported with
            {
                Images = modelId is null || profile.AcceptsImages ? LlmRichContentCarriage.Native : LlmRichContentCarriage.Unsupported
            }
        };
        LlmProviderCapabilities capabilities = LlmCapabilityOverrideLookup.Apply(defaults, connection, modelId, OffersUsageDeltas);
        return capabilities with
        {
            ReasoningLevels = profile.MultiAgent is null ? capabilities.ReasoningLevels.Where(level => level is
                LlmReasoningLevel.Low or LlmReasoningLevel.Medium or LlmReasoningLevel.High or LlmReasoningLevel.ExtraHigh).ToFrozenSet()
                : LlmReasoningEffort.Levels(),
            SupportsPromptImages = capabilities.SupportsPromptImages && profile.AcceptsImages,
            SupportsTopK = false,
            SupportsSeed = false,
            SupportsPresencePenalty = false,
            SupportsFrequencyPenalty = false,
            // The Responses request schema publishes no string-stop field.
            MaxStopSequences = 0,
            HostedTools = capabilities.HostedTools.Intersect(DefaultCapabilities.HostedTools).ToFrozenSet()
        };
    }

    internal void PreflightResourceRequest(LlmConnection connection, LlmChatRequest request)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        ValidateModelSampling(connection, request);
        _ = ResolveStructuredOutputEnforcement(connection, request, capabilities);
    }

    internal LlmChatRequest ProjectBatchRequest(LlmConnection connection, LlmChatRequest request)
    {
        PreflightResourceRequest(connection, request);
        (LlmChatRequest projected, _) = ContinuationStateProjection.Project(request, connection);
        foreach (LlmChatMessage message in projected.Messages)
            if (message.ContinuationState is not null)
                _ = ProviderContinuationStateCodec.DecodeMessage(message, connection, projected.ModelId);
        return projected;
    }

    internal JsonObject BuildBatchPayload(LlmConnection connection, LlmChatRequest request,
        LlmResourcePreparation? resources)
    {
        request = resources?.Request ?? request;
        LlmStructuredOutputEnforcement? enforcement = ResolveStructuredOutputEnforcement(connection, request,
            GetCapabilities(connection, request.ModelId));
        JsonObject payload = BuildPayload(connection, request, enforcement, stream: false, resources);
        RequestJson.ApplyProviderOptions(payload, connection, request);
        return payload;
    }

    internal void PreflightManagedOpeningRequest(LlmConnection connection, LlmChatRequest request)
    {
        ProviderHttp.ValidateAdditionalHeaders(connection.AdditionalHeaders);
        ProviderControlPolicy.Validate(connection, request);
        RequestJson.ValidateSamplingOptions(connection, request);
        LlmAttachments.ValidateReferences(request, () => GetCapabilities(connection, request.ModelId).SupportsFileReferences, supportsResources: true);
        ValidateAdditionalRequest(connection, request);
    }

    internal void PreflightCompactionRequest(LlmConnection connection, LlmChatRequest request)
    {
        if (connection.Xai?.ServiceTier == XaiServiceTier.Priority)
            throw new NotSupportedException("Grok Priority context compaction has not been qualified.");
        PreflightManagedOpeningRequest(connection, request);
    }

    [SuppressMessage("Design", "CA1031", Justification = "Attempt all owned cleanup and retain independent safe failures and actual refused owners.")]
    internal async Task<ProviderCompactionResult> CompactWithResourcesAsync(LlmConnection connection, LlmChatRequest request,
        XaiFileClient files, Action outerFence, CancellationToken cancellationToken)
    {
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(outerFence);
        void Fence() { consumption.Token.ThrowIfCancellationRequested(); consumption.Fence(); outerFence(); }
        Fence();
        connection = EmbeddingRequestData.Snapshot(connection);
        request = ClientTools.Snapshot(request) with { HostedTools = HostedTools.Snapshot(request.HostedTools) };
        Fence();
        PreflightCompactionRequest(connection, request);
        PreflightResourceRequest(connection, request);
        Fence();
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        LlmStructuredOutputEnforcement? enforcement = ResolveStructuredOutputEnforcement(connection, request, capabilities);
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        LlmResourcePreparation? resources = null;
        HttpRequestMessage? message = null;
        HttpResponseMessage? response = null;
        string[] secrets = [];
        var failures = new List<Exception>();
        ProviderCompactionResult? result = null;
        Exception? classifiedFailure = null;
        try
        {
            if (request.Resources is not null)
            {
                resources = await LlmResourcePreparation.PrepareAsync(connection, request, files, Fence, consumption.Token).ConfigureAwait(false);
                Fence();
                request = resources.Request;
            }
            JsonObject canonical = BuildPayload(connection, request, enforcement, stream: false, resources);
            var input = (JsonArray)canonical["input"]!.DeepClone();
            if (request.Instructions is { } instructions)
                input.Insert(0, new JsonObject { ["role"] = "system", ["content"] = instructions });
            var payload = new JsonObject { ["model"] = request.ModelId, ["input"] = input };
            Fence();
            message = new(HttpMethod.Post, ProviderHttp.BuildUri(connection, "responses/compact"));
            message.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            XaiRequestHeaders.Apply(message, connection, stream: false);
            Fence();
            response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, consumption.Token).ConfigureAwait(false);
            Fence();
            secrets = connection.AdditionalHeaders.Values.Concat(message.Headers.SelectMany(header => header.Value)).ToArray();
            try
            {
                await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, credential, secrets, consumption.Token).ConfigureAwait(false);
            }
            catch (LlmProviderException error) { classifiedFailure = error; throw; }
            catch (InferenceException error) when (error.InnerException is LlmProviderException) { classifiedFailure = error; throw; }
            Fence();
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, consumption.Token).ConfigureAwait(false);
            Fence();
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("object", out JsonElement kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "response.compaction"
                || !root.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
                || !root.TryGetProperty("model", out JsonElement model) || model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString())
                || !root.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array || output.GetArrayLength() != 1)
                throw new InvalidOperationException("Invalid compact response.");
            JsonElement item = output[0];
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String || type.GetString() != "compaction"
                || !item.TryGetProperty("encrypted_content", out JsonElement encrypted) || encrypted.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(encrypted.GetString()))
                throw new InvalidOperationException("Invalid compact output.");
            string requestId = id.GetString()!;
            if (SecretRedactor.Sanitize(requestId, credential, secrets) != requestId
                || SecretRedactor.Sanitize(model.GetString()!, credential, secrets) != model.GetString())
                throw new InvalidOperationException("Compact response metadata cannot be published safely.");
            int? dropped = null;
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind != JsonValueKind.Null)
            {
                if (usage.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid compact usage.");
                if (usage.TryGetProperty("dropped_message_count", out JsonElement count))
                {
                    if (!count.TryGetInt32(out int value) || value < 0) throw new InvalidOperationException("Invalid dropped-message count.");
                    dropped = value;
                }
            }
            var state = ProviderContinuationStateCodec.Encode(connection, request.ModelId, (JsonArray)JsonNode.Parse(output.GetRawText())!);
            result = new(state, requestId, ReadUsage(root), dropped);
            Fence();
        }
        catch (Exception error)
        {
            failures.Add(LlmResourcePreparation.IsResourceFailure(error) ? error
                : ReferenceEquals(error, classifiedFailure) ? ClassifiedCompactionFailure(error, credential, secrets)
                : CompactionFailure(error, response, credential, secrets));
        }
        finally
        {
            if (response is not null)
                try { response.Dispose(); }
                catch (Exception error) { failures.Add(CompactionCleanupFailure(error, response)); }
            if (message is not null)
                try { message.Dispose(); }
                catch (Exception error) { failures.Add(CompactionCleanupFailure(error, message)); }
            if (resources is not null)
                try { await resources.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Grok context compaction and owned cleanup failed.", failures);
        Fence();
        return consumption.Publish(result!);
    }

    private static Exception CompactionFailure(Exception original, HttpResponseMessage? response,
        LlmCredential? credential, string[] secrets) => original is OperationCanceledException canceled
        ? new OperationCanceledException("Grok context compaction was interrupted.", new CompactionEvidence(original), canceled.CancellationToken)
        : new LlmProviderException("Grok context compaction failed.", LlmTransport.XaiResponses, response?.StatusCode,
            requestId: response is null ? null : ProviderHttp.ReadRequestId(response) is { } id ? SecretRedactor.Sanitize(id, credential, secrets) : null,
            innerException: new CompactionEvidence(original));

    private static Exception ClassifiedCompactionFailure(Exception original, LlmCredential? credential, string[] secrets)
    {
        LlmProviderException provider = original is InferenceException inference
            ? (LlmProviderException)inference.InnerException! : (LlmProviderException)original;
        var safe = new LlmProviderException("Grok context compaction request failed.", LlmTransport.XaiResponses,
            provider.StatusCode, provider.ProviderCode is { } code ? SecretRedactor.Sanitize(code, credential, secrets) : null,
            provider.RequestId is { } id ? SecretRedactor.Sanitize(id, credential, secrets) : null, provider.RetryAfter,
            innerException: new CompactionEvidence(original), isContextOverflow: provider.IsContextOverflow);
        return original is InferenceException failure
            ? new InferenceException(failure.Code, "Grok context compaction authorization or access failed.", safe) : safe;
    }

    private static Exception CompactionCleanupFailure(Exception original, object owner)
    {
        var evidence = new CompactionEvidence(original);
        Exception safe = original is OperationCanceledException canceled
            ? new OperationCanceledException("Grok context compaction cleanup was interrupted.", evidence, canceled.CancellationToken) : evidence;
        return LlmResourcePreparation.AsCleanupFailure(safe, refusedOwner: owner);
    }

    private sealed class CompactionEvidence(Exception original) : Exception("Grok context compaction HTTP boundary reported a failure.")
    {
        internal Exception Original { get; } = original;
    }

    protected override void ValidateAdditionalRequest(LlmConnection connection, LlmChatRequest request)
    {
        request = ProviderControlPolicy.Resolve(connection, request);
        LlmResourcePreparation.ValidateArguments(request);
        _ = XaiRequestHeaders.Require(connection);
        if (connection.ProviderOptions.Keys.Concat(request.ProviderOptions.Keys).Any(ReservedOptions.Contains))
            throw new ArgumentException("Grok protocol controls use typed request and connection properties, not provider options.", nameof(request));
        if (request.Temperature is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(request), "Grok temperature must be in [0, 2].");
        if ((long)request.Tools.Count + request.HostedTools.Count
            + (request.HostedTools.OfType<LlmRemoteMcpTool>().Any(tool => tool.DeferLoading) ? 1 : 0) > 350)
            throw new NotSupportedException("Grok accepts at most 350 wire tools.");
        if (request.HostedTools.OfType<LlmDocumentSearchTool>().Any(tool => tool.CollectionIds.Count > 10))
            throw new ArgumentException("Grok document search accepts at most 10 collection IDs per tool.", nameof(request));
        foreach (LlmHostedTool tool in request.HostedTools)
        {
            if (tool is LlmWebSearchTool web)
            {
                if (web.AllowedDomains is not null && web.ExcludedDomains is not null)
                    throw new ArgumentException("Grok web search cannot combine allowed and excluded domains.", nameof(request));
                if (web.AllowedDomains?.Count > 5 || web.ExcludedDomains?.Count > 5)
                    throw new ArgumentException("Grok web search accepts at most 5 domains per filter.", nameof(request));
                if (web.Location is not null)
                    throw new NotSupportedException("Grok web search does not accept a user location.");
            }
            else if (tool is XaiXSearchTool social)
            {
                if (social.AllowedHandles is not null && social.ExcludedHandles is not null)
                    throw new ArgumentException("Grok X search cannot combine allowed and excluded handles.", nameof(request));
                if (social.AllowedHandles?.Count > 20 || social.ExcludedHandles?.Count > 20)
                    throw new ArgumentException("Grok X search accepts at most 20 handles per filter.", nameof(request));
            }
            else if (tool is LlmRemoteMcpTool mcp)
            {
                if (!Uri.TryCreate(mcp.ServerUrl, UriKind.Absolute, out Uri? server)
                    || server.Scheme != Uri.UriSchemeHttp && server.Scheme != Uri.UriSchemeHttps)
                    throw new ArgumentException("MCP requires an absolute HTTP or HTTPS server URL.", nameof(request));
                if (mcp.Headers is { } headers)
                {
                    using var message = new HttpRequestMessage { Content = new ByteArrayContent([]) };
                    foreach ((string name, string value) in headers)
                        if (name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n')
                            || !message.Headers.TryAddWithoutValidation(name, value) && !message.Content.Headers.TryAddWithoutValidation(name, value))
                            throw new ArgumentException("MCP contains an invalid header.", nameof(request));
                }
            }
        }
        if (request.Tools.OfType<LlmToolDefinition>().Any(tool => tool.Name == "web_search"))
            throw new ArgumentException("A Grok client function cannot be named web_search.", nameof(request));
        foreach (LlmFileReference file in request.Messages.SelectMany(message => message.Attachments).OfType<LlmFileReference>())
            _ = XaiFileRequestData.Identity(file.File.FileId);
        if (request.Resources is not null && !GetCapabilities(connection, request.ModelId).SupportsPromptDocuments)
            throw new NotSupportedException("This resource destination requires document input support.");
        foreach (LlmAttachment attachment in request.Messages.SelectMany(message => message.Attachments).OfType<LlmAttachment>())
            if (attachment.Kind == LlmBinaryKind.Image && attachment.Data.Length > MaxImageBytes)
                throw new ArgumentException("Grok input images must not exceed 20 MiB.", nameof(request));
        if (GetCapabilities(connection, request.ModelId).RichContentSupported.Images == LlmRichContentCarriage.Native)
            foreach (LlmToolOutputPart part in request.Messages.SelectMany(message => message.ToolResults.OfType<LlmToolResult>())
                .SelectMany(result => result.Output.Parts))
                if (part is LlmToolOutputBinary { Kind: LlmBinaryKind.Image } binary && binary.Data.Length > MaxImageBytes
                    || part is LlmToolOutputResource { BodyKind: LlmBinaryKind.Image, Data: { } data } && data.Length > MaxImageBytes)
                    throw new ArgumentException("Grok tool input images must not exceed 20 MiB.", nameof(request));
        foreach (LlmToolDefinition tool in request.Tools.OfType<LlmToolDefinition>().Where(tool => tool.Strict))
            XaiSchemaLimits.ValidateStrictTool(tool.InputSchema);
    }

    protected override JsonObject BuildPayload(LlmConnection connection, LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement, bool stream, LlmResourcePreparation? resources = null)
    {
        request = ProviderControlPolicy.Resolve(connection, request);

        XaiModelProfile profile = XaiModelProfiles.Get(connection, request.ModelId);
        if (request.ReasoningLevel is null && profile.DefaultReasoningLevel is { } defaultLevel
            && GetCapabilities(connection, request.ModelId).ReasoningLevels.Contains(defaultLevel))
            request = request with { ReasoningLevel = defaultLevel };
        JsonObject payload = base.BuildPayload(connection, request, enforcement, stream, resources);
        if (request.HostedTools.Count > 0)
        {
            var tools = payload["tools"] as JsonArray ?? new JsonArray();
            if (payload["tools"] is null) payload["tools"] = tools;
            foreach (LlmHostedTool tool in request.HostedTools) tools.Add(item: BuildHostedTool(tool));
            if (request.HostedTools.OfType<LlmRemoteMcpTool>().Any(tool => tool.DeferLoading))
                tools.Add(item: new JsonObject { ["type"] = "tool_search" });
        }
        if (request.MaxHostedTurns is { } turns) payload["max_turns"] = turns;
        // Primitive nodes avoid Add<T>'s dependency on default serializer metadata.
        if (request.HostedTools.OfType<LlmDocumentSearchTool>().Any() || HasFileInput(payload["input"]))
            ((JsonArray)payload["include"]!).Add((JsonNode?)"file_search_call.results");
        if (request.HostedTools.OfType<LlmWebSearchTool>().Any())
            ((JsonArray)payload["include"]!).Add((JsonNode?)"web_search_call.action.sources");
        if (request.HostedTools.OfType<LlmCodeExecutionTool>().Any())
            ((JsonArray)payload["include"]!).Add((JsonNode?)"code_interpreter_call.outputs");
        ((JsonArray)payload["include"]!).Add((JsonNode?)"no_inline_citations");
        if (request.ProviderControls is XaiMultiAgentControls { AgentCount: { } count })
            payload["reasoning"] = new JsonObject
            {
                ["effort"] = XaiModelProfiles.MultiAgentEffort(profile.MultiAgent!, count)
            };
        payload["service_tier"] = (connection.Xai?.ServiceTier ?? XaiServiceTier.Default) == XaiServiceTier.Priority ? "priority" : "default";
        return payload;
    }

    private static JsonObject BuildHostedTool(LlmHostedTool tool)
    {
        switch (tool)
        {
            case LlmCodeExecutionTool:
                return new JsonObject { ["type"] = "code_interpreter" };
            case LlmImageGenerationTool image:
                return new JsonObject
                {
                    ["type"] = "image_generation",
                    ["action"] = image.Action switch
                    {
                        LlmImageGenerationAction.Auto => "auto",
                        LlmImageGenerationAction.Generate => "generate",
                        LlmImageGenerationAction.Edit => "edit",
                        _ => throw new NotSupportedException("Unknown image action.")
                    }
                };
            case LlmRemoteMcpTool mcp:
                var remote = new JsonObject
                {
                    ["type"] = "mcp",
                    ["server_url"] = mcp.ServerUrl,
                    ["server_label"] = mcp.ServerLabel,
                    ["defer_loading"] = mcp.DeferLoading
                };
                if (mcp.ServerDescription is not null) remote["server_description"] = mcp.ServerDescription;
                AddSearchFilter(remote, "allowed_tools", mcp.AllowedTools);
                if (mcp.Headers is not null)
                    remote["headers"] = new JsonObject(mcp.Headers.Select(pair =>
                        KeyValuePair.Create<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value))));
                return remote;
            case LlmDocumentSearchTool document:
                {
                    var search = new JsonObject
                    {
                        ["type"] = "file_search",
                        ["vector_store_ids"] = new JsonArray(document.CollectionIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
                    };
                    if (document.MaxResults is { } maximum) search["max_num_results"] = maximum;
                    return search;
                }
            case LlmWebSearchTool web:
                {
                    var search = new JsonObject
                    {
                        ["type"] = "web_search",
                        ["enable_image_understanding"] = web.IncludeImageUnderstanding,
                        ["enable_image_search"] = web.IncludeImageSearch
                    };
                    AddSearchFilter(search, "allowed_domains", web.AllowedDomains);
                    AddSearchFilter(search, "excluded_domains", web.ExcludedDomains);
                    return search;
                }
            case XaiXSearchTool social:
                {
                    var search = new JsonObject
                    {
                        ["type"] = "x_search",
                        ["enable_image_understanding"] = social.IncludeImageUnderstanding,
                        ["enable_video_understanding"] = social.IncludeVideoUnderstanding
                    };
                    AddSearchFilter(search, "allowed_x_handles", social.AllowedHandles);
                    AddSearchFilter(search, "excluded_x_handles", social.ExcludedHandles);
                    if (social.FromDate is { } from) search["from_date"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (social.ToDate is { } to) search["to_date"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    return search;
                }
            default:
                throw new NotSupportedException("Grok does not implement this hosted-tool descriptor.");
        }
    }

    private static void AddSearchFilter(JsonObject search, string name, IReadOnlyList<string>? values)
    {
        if (values is not null)
            search[name] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    }

    private static bool HasFileInput(JsonNode? node) => node switch
    {
        JsonObject item => item["type"] is JsonValue type && type.TryGetValue<string>(out string? name) && name == "input_file"
            || item.Any(property => HasFileInput(property.Value)),
        JsonArray items => items.Any(HasFileInput),
        _ => false
    };

    protected override bool ReasonsByDefault(LlmConnection connection, string modelId) =>
        XaiModelProfiles.Get(connection, modelId).DefaultReasoningLevel is not null;

    protected override LlmStructuredOutputEnforcement? ResolveStructuredOutputEnforcement(
        LlmConnection connection, LlmChatRequest request, LlmProviderCapabilities capabilities) =>
        XaiSchemaLimits.ResolveEnforcement(request.StructuredOutput, capabilities);

    protected override void ApplyRequestHeaders(HttpRequestMessage message, LlmConnection connection, LlmChatRequest request) =>
        XaiRequestHeaders.Apply(message, connection, message.Method == HttpMethod.Post && GetCapabilities(connection, request.ModelId).SupportsStreaming);

    public async Task<LlmTokenization> TokenizeAsync(LlmConnection connection, string modelId, string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(text);
        _ = XaiRequestHeaders.Require(connection);
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        LlmTokenization result;
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ProviderHttp.BuildUri(connection, "tokenize-text"));
            XaiRequestHeaders.Apply(request, connection, false);
            // Plain text and model identity are opaque data; changing either changes the provider's tokens.
            var payload = new JsonObject { ["model"] = modelId, ["text"] = text };
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> secrets = connection.AdditionalHeaders.Values.Concat(request.Headers.SelectMany(header => header.Value));
            await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, LlmCredential.Parse(connection.Credential), secrets, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("token_ids", out JsonElement entries)
                    || entries.ValueKind != JsonValueKind.Array)
                    throw InvalidTokens();
                var tokens = new List<LlmToken>(entries.GetArrayLength());
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("token_id", out JsonElement id) || !id.TryGetUInt32(out uint number)
                        || !entry.TryGetProperty("string_token", out JsonElement display) || display.ValueKind != JsonValueKind.String
                        || !entry.TryGetProperty("token_bytes", out JsonElement bytes) || bytes.ValueKind != JsonValueKind.Array)
                        throw InvalidTokens();
                    var data = new byte[bytes.GetArrayLength()];
                    int index = 0;
                    foreach (JsonElement item in bytes.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!item.TryGetByte(out byte value)) throw InvalidTokens();
                        data[index++] = value;
                    }
                    tokens.Add(new(number, display.GetString()!, data));
                }
                cancellationToken.ThrowIfCancellationRequested();
                result = new LlmTokenization(tokens.ToArray());
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
            {
                throw InvalidTokens();
            }

            LlmProviderException InvalidTokens() => ProtocolException(
                "Grok tokenization returned an invalid token sequence.", response, string.Empty, LlmCredential.Parse(connection.Credential),
                extraSecrets: secrets);
        }
        return consumption.Publish(result);
    }

    public override async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        if (!GetCapabilities(connection).SupportsModelListing) return [];
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken);
        void Fence() { consumption.Token.ThrowIfCancellationRequested(); consumption.Fence(); }
        IReadOnlyList<JsonElement> generic = await XaiCatalog.ReadAsync(Http, connection, ModelCatalogPart.Generic, Fence, consumption.Token);
        Fence();
        IReadOnlyList<JsonElement> language = await XaiCatalog.ReadAsync(Http, connection, ModelCatalogPart.Chat, Fence, consumption.Token);
        Fence();
        return consumption.Publish(ProjectCatalog(connection, ModelCatalogFamily.Chat, generic, language));
    }

    Task<IReadOnlyList<JsonElement>> ILlmModelCatalogClient.ReadCatalogPartAsync(LlmConnection snapshot, ModelCatalogPart part,
        Action ownerFence, CancellationToken cancellationToken) => XaiCatalog.ReadAsync(Http, snapshot, part, ownerFence, cancellationToken);

    IReadOnlyList<LlmModel> ILlmModelCatalogClient.ProjectCatalog(LlmConnection snapshot, ModelCatalogFamily family,
        IReadOnlyList<JsonElement> generic, IReadOnlyList<JsonElement> specific) => ProjectCatalog(snapshot, family, generic, specific);

    private IReadOnlyList<LlmModel> ProjectCatalog(LlmConnection snapshot, ModelCatalogFamily family,
        IReadOnlyList<JsonElement> generic, IReadOnlyList<JsonElement> specific)
    {
        IReadOnlyList<LlmModel> results = XaiCatalog.Project(generic, specific);
        if (family != ModelCatalogFamily.Chat) return results;
        LlmConnection published = snapshot with { Catalog = results };
        return Array.AsReadOnly(results.Select(model => model with { Capabilities = GetCapabilities(published, model.Id) }).ToArray());
    }

}
