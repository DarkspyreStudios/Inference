using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Grok conversations on the xAI Responses API, signed in with a Grok subscription's OAuth credential. Grok compacts
/// conversations through <c>responses/compact</c> and counts new text with <c>tokenize-text</c>.
/// </summary>
internal sealed class GrokResponsesLlmClient(HttpClient http) : ResponsesLlmClientBase(http), ILlmCatalogCapabilities,
    IExternalLlmAuthenticationProbe, ILlmCredentialAdmissionPolicy, ILlmTextTokenizerClient, ILlmProviderCompaction
{
    private const int MaxImageBytes = 20 * 1024 * 1024;

    // xAI's published request limits.
    private const int MaxWireTools = 350;
    private const int MaxCollections = 10;
    private const int MaxDomains = 5;
    private const int MaxHandles = 20;

    private static readonly FrozenSet<string> ReservedOptions = new[]
    {
        "model", "input", "stream", "store", "include", "previous_response_id", "service_tier", "instructions",
        "temperature", "top_p", "top_k", "seed", "presence_penalty", "frequency_penalty", "stop", "stop_sequences",
        "max_output_tokens", "reasoning", "reasoning_effort", "tools", "tool_choice", "parallel_tool_calls", "text",
        "prompt_cache_key", "background", "deferred", "agent_count", "max_turns", "no_inline_citations"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<LlmServiceTier> SpeedTiers =
        new[] { LlmServiceTier.Standard, LlmServiceTier.Fast }.ToFrozenSet();

    public override LlmTransport Transport => LlmTransport.XaiResponses;

    public override LlmProviderDefinition ProviderDefinition => GrokDefinition.Value;

    protected override bool OffersUsageDeltas => false;

    protected override bool SupportsResourcePreparation => true;

    /// <summary>Grok's feature list (B6): the OpenAI API column wherever Grok works the same way, plus X search,
    /// agent counts, the speed tier and provider compaction.</summary>
    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        // The native cap excludes reasoning and function tokens; it does not bound total output.
        OutputLimitEnforcement = LlmOutputLimitEnforcement.Unknown,
        SupportsStreaming = InferenceSupport.Supported,
        SupportsStreamingUsage = InferenceSupport.Supported,
        SupportsReasoningText = InferenceSupport.Supported,
        SupportsModelListing = InferenceSupport.Supported,
        SupportsAccountUsage = InferenceSupport.Unsupported,
        SupportsJsonSchema = InferenceSupport.Supported,
        SupportsJsonObject = InferenceSupport.Supported,
        SupportsToolUse = InferenceSupport.Supported,
        SupportsAgentSessions = InferenceSupport.Supported,
        SupportsAgentToolUse = InferenceSupport.Supported,
        SupportsAgentTurnInstructions = InferenceSupport.Supported,
        SupportsPromptDocuments = InferenceSupport.Supported,
        Agent = ManagedAbilities with { RichContentSupported = RichContentRoutes.Responses, CompactionSide = LlmCompactionSide.Provider },
        HostedTools = new[] { LlmHostedToolKind.ImageGeneration, LlmHostedToolKind.WebSearch, LlmHostedToolKind.SocialSearch,
            LlmHostedToolKind.CodeExecution, LlmHostedToolKind.DocumentSearch, LlmHostedToolKind.RemoteMcp }.ToFrozenSet(),
        ServiceTiers = SpeedTiers,
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.None, LlmToolChoiceMode.Required, LlmToolChoiceMode.Specific }.ToFrozenSet(),
            SupportsParallelCalls = InferenceSupport.Supported,
            SupportsParallelCallControl = InferenceSupport.Supported,
            SupportsArgumentStreaming = InferenceSupport.Supported
        },
        RichContentSupported = RichContentRoutes.Responses
    };

    void ILlmCredentialAdmissionPolicy.ValidateCredential(LlmConnection connection) => _ = XaiRequestHeaders.Require(connection);

    /// <summary>Signed in when the connection carries a usable Grok OAuth credential. Nothing is sent.</summary>
    public Task<ExternalAuthenticationState> CheckAuthenticationAsync(LlmConnection connection,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(XaiAuthorization.IsValid(LlmCredential.Parse(connection.Credential))
            ? ExternalAuthenticationState.SignedIn : ExternalAuthenticationState.SignedOut);

    /// <summary>The model's feature list from its catalog facts and Grok's model-family profile (B4, B6, B15).</summary>
    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        XaiRequestHeaders.ValidateEndpoint(connection);
        XaiModelProfile profile = XaiModelProfiles.Get(connection, modelId);
        LlmRichContentSupported richContent = DefaultCapabilities.RichContentSupported with
        {
            Images = profile.AcceptsImages != InferenceSupport.Unsupported ? LlmRichContentCarriage.Native : LlmRichContentCarriage.Unsupported
        };
        LlmProviderCapabilities defaults = DefaultCapabilities with
        {
            ReasoningLevels = profile.ReasoningLevels,
            SupportsTemperature = profile.AllowsSampling,
            SupportsTopP = profile.AllowsSampling,
            SupportsPromptImages = profile.AcceptsImages,
            RichContentSupported = richContent,
            Agent = DefaultCapabilities.Agent with { RichContentSupported = richContent }
        };
        LlmProviderCapabilities capabilities = LlmCapabilityOverrideLookup.Apply(defaults, connection, modelId, OffersUsageDeltas);
        return capabilities with
        {
            // A multi-agent model's effort is its agent count: it offers counts and no reasoning levels.
            ReasoningLevels = profile.MultiAgent is null ? capabilities.ReasoningLevels : LlmReasoningEffort.Levels(),
            AgentCounts = profile.MultiAgent?.AgentCounts ?? [],
            SupportsPromptImages = LlmProviderCapabilities.Intersect(capabilities.SupportsPromptImages, profile.AcceptsImages),
            SupportsTopK = InferenceSupport.Unsupported,
            SupportsSeed = InferenceSupport.Unsupported,
            SupportsPresencePenalty = InferenceSupport.Unsupported,
            SupportsFrequencyPenalty = InferenceSupport.Unsupported,
            // The Responses request schema publishes no string-stop field.
            MaxStopSequences = 0,
            HostedTools = capabilities.HostedTools.Intersect(DefaultCapabilities.HostedTools).ToFrozenSet()
        };
    }

    /// <summary>The account's chat models: xAI's generic list merged into its language-model list (B4).</summary>
    public override async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken);
        void Fence() { consumption.Token.ThrowIfCancellationRequested(); consumption.Fence(); }
        IReadOnlyList<JsonElement> generic = await XaiCatalog.ReadAsync(Http, connection, ModelCatalogPart.Generic, Fence, consumption.Token)
            .ConfigureAwait(false);
        Fence();
        IReadOnlyList<JsonElement> language = await XaiCatalog.ReadAsync(Http, connection, ModelCatalogPart.Chat, Fence, consumption.Token)
            .ConfigureAwait(false);
        Fence();
        IReadOnlyList<LlmModel> models = XaiCatalog.Project(generic, language);
        LlmConnection published = connection with { Catalog = models };
        return consumption.Publish<IReadOnlyList<LlmModel>>(
            Array.AsReadOnly(models.Select(model => model with { Capabilities = GetCapabilities(published, model.Id) }).ToArray()));
    }

    protected override LlmAccountUsageSnapshot? ObserveAccountUsage(HttpResponseMessage response) => null;

    protected override LlmUsage? ReadUsage(JsonElement root) => XaiUsage.Enrich(base.ReadUsage(root), root);

    protected override Task ThrowIfUnsuccessfulAsync(HttpResponseMessage response, LlmCredential? credential,
        IReadOnlyList<string> secrets, CancellationToken cancellationToken) =>
        XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, credential, secrets, cancellationToken);

    protected override void ApplyRequestHeaders(HttpRequestMessage message, LlmConnection connection, LlmChatRequest request) =>
        XaiRequestHeaders.Apply(message, connection,
            GetCapabilities(connection, request.ModelId).SupportsStreaming != InferenceSupport.Unsupported);

    protected override bool ReasonsByDefault(LlmConnection connection, string modelId) =>
        XaiModelProfiles.Get(connection, modelId).DefaultReasoningLevel is not null;

    protected override LlmStructuredOutputEnforcement? ResolveStructuredOutputEnforcement(
        LlmConnection connection, LlmChatRequest request, LlmProviderCapabilities capabilities) =>
        XaiSchemaLimits.ResolveEnforcement(request.StructuredOutput, capabilities);

    /// <summary>xAI's request limits and the controls Grok refuses. Hosted-tool kinds were checked against the feature
    /// list before this runs.</summary>
    protected override void ValidateAdditionalRequest(LlmConnection connection, LlmChatRequest request)
    {
        _ = XaiRequestHeaders.Require(connection);
        if (connection.ProviderOptions.Keys.Concat(request.ProviderOptions.Keys).Any(ReservedOptions.Contains))
            throw new ArgumentException("Grok protocol controls use typed request and connection properties, not provider options.", nameof(request));
        if (request.Temperature is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(request), "Grok temperature must be in [0, 2].");
        if ((long)request.Tools.Count + request.HostedTools.Count
            + (request.HostedTools.OfType<LlmRemoteMcpTool>().Any(tool => tool.DeferLoading) ? 1 : 0) > MaxWireTools)
            throw new NotSupportedException($"Grok accepts at most {MaxWireTools} wire tools.");
        foreach (LlmHostedTool tool in request.HostedTools)
        {
            switch (tool)
            {
                case LlmDocumentSearchTool document when document.CollectionIds.Count > MaxCollections:
                    throw new ArgumentException($"Grok document search accepts at most {MaxCollections} collection IDs per tool.", nameof(request));
                case LlmWebSearchTool web:
                    if (web.AllowedDomains is { Count: > 0 } && web.ExcludedDomains is { Count: > 0 })
                        throw new ArgumentException("Grok web search cannot combine allowed and excluded domains.", nameof(request));
                    if (web.AllowedDomains?.Count > MaxDomains || web.ExcludedDomains?.Count > MaxDomains)
                        throw new ArgumentException($"Grok web search accepts at most {MaxDomains} domains per filter.", nameof(request));
                    if (web.Location is not null)
                        throw new NotSupportedException("Grok web search does not accept a user location.");
                    break;
                case LlmSocialSearchTool social:
                    if (social.AllowedHandles.Count > 0 && social.ExcludedHandles.Count > 0)
                        throw new ArgumentException("Grok X search cannot combine allowed and excluded handles.", nameof(request));
                    if (social.AllowedHandles.Count > MaxHandles || social.ExcludedHandles.Count > MaxHandles)
                        throw new ArgumentException($"Grok X search accepts at most {MaxHandles} handles per filter.", nameof(request));
                    break;
                case LlmRemoteMcpTool mcp:
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
                    break;
            }
        }
        if (request.Tools.OfType<LlmToolDefinition>().Any(tool => tool.Name == "web_search"))
            throw new ArgumentException("A Grok client function cannot be named web_search.", nameof(request));
        if (request.Resources is not null && GetCapabilities(connection, request.ModelId).SupportsPromptDocuments == InferenceSupport.Unsupported)
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
    }

    /// <summary>
    /// The Responses payload with Grok's reasoning, agent count (B15), speed tier (B16), tool search for deferred MCP
    /// servers and hosted-turn cap. A turn that names no agent count or tier takes the connection's saved one.
    /// </summary>
    protected override JsonObject BuildPayload(LlmConnection connection, LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement, bool stream, LlmResourcePreparation? resources = null)
    {
        XaiModelProfile profile = XaiModelProfiles.Get(connection, request.ModelId);
        int? agents = profile.MultiAgent is null ? null : request.AgentCount ?? connection.Xai?.MultiAgent?.AgentCount;
        request = request with
        {
            ReasoningLevel = XaiModelProfiles.MapReasoning(profile,
                GetCapabilities(connection, request.ModelId).ReasoningLevels, request.ReasoningLevel),
            ServiceTier = request.ServiceTier is null or LlmServiceTier.Auto
                ? (connection.Xai?.ServiceTier ?? XaiServiceTier.Default) == XaiServiceTier.Priority ? LlmServiceTier.Fast : LlmServiceTier.Standard
                : request.ServiceTier
        };
        JsonObject payload = base.BuildPayload(connection, request, enforcement, stream, resources);
        if (request.HostedTools.OfType<LlmRemoteMcpTool>().Any(tool => tool.DeferLoading))
            ((JsonArray)payload["tools"]!).Add(item: new JsonObject { ["type"] = "tool_search" });
        if (request.MaxHostedTurns is { } turns) payload["max_turns"] = turns;
        if (agents is { } count) payload["reasoning"] = new JsonObject { ["effort"] = profile.MultiAgent!.Effort(count) };
        return payload;
    }

    /// <summary>Grok's wire shape where it differs from OpenAI's (Tech Design 5.3): web search filters and image
    /// switches, X search, code execution without a container, and MCP without require_approval.</summary>
    protected override JsonObject WriteHostedTool(LlmHostedTool tool)
    {
        switch (tool)
        {
            case LlmWebSearchTool web:
                var search = new JsonObject
                {
                    ["type"] = "web_search",
                    ["enable_image_understanding"] = web.IncludeImageUnderstanding,
                    ["enable_image_search"] = web.IncludeImageSearch
                };
                if (web.AllowedDomains is { Count: > 0 } allowed) search["allowed_domains"] = Strings(allowed);
                if (web.ExcludedDomains is { Count: > 0 } excluded) search["excluded_domains"] = Strings(excluded);
                return search;
            case LlmSocialSearchTool social:
                var posts = new JsonObject
                {
                    ["type"] = "x_search",
                    ["enable_image_understanding"] = social.IncludeImageUnderstanding,
                    ["enable_video_understanding"] = social.IncludeVideoUnderstanding
                };
                if (social.AllowedHandles.Count > 0) posts["allowed_x_handles"] = Strings(social.AllowedHandles);
                if (social.ExcludedHandles.Count > 0) posts["excluded_x_handles"] = Strings(social.ExcludedHandles);
                if (social.FromDate is { } from) posts["from_date"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (social.ToDate is { } to) posts["to_date"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return posts;
            case LlmCodeExecutionTool:
                return new JsonObject { ["type"] = "code_interpreter" };
            case LlmRemoteMcpTool mcp:
                JsonObject remote = base.WriteHostedTool(mcp);
                remote.Remove("require_approval");
                if (mcp.ServerDescription is not null) remote["server_description"] = mcp.ServerDescription;
                if (mcp.DeferLoading) remote["defer_loading"] = true;
                return remote;
            default:
                return base.WriteHostedTool(tool);
        }
    }

    /// <summary>The shared include entries, and always no_inline_citations: citations arrive as annotations, not text.</summary>
    protected override IReadOnlyList<string> HostedIncludes(IReadOnlyList<LlmHostedTool> tools) =>
        [.. base.HostedIncludes(tools), "no_inline_citations"];

    /// <summary>
    /// The local estimate, with the text of the messages after the last provider-reported reply counted by Grok's
    /// tokenizer (B12). Images and tool definitions stay local. A failed tokenizer call keeps the local count of that text.
    /// </summary>
    public override async ValueTask<ContextEstimate?> EstimateAsync(LlmConnection connection, LlmChatRequest request,
        IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> resources,
        CancellationToken cancellationToken)
    {
        ContextEstimate local = LocalEstimate(connection, request, resources);
        string tail = UnreportedText(request.Messages);
        if (tail.Length == 0) return local;
        long counted;
        try
        {
            counted = (await TokenizeAsync(connection, request.ModelId, tail, cancellationToken).ConfigureAwait(false)).Tokens.Count;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return local;
        }
        return local with { Tokens = checked(Math.Max(0, local.Tokens - LocalTokenCount.Count(tail)) + counted) };
    }

    /// <summary>The text of the messages after the last reply whose continuation state carries a reported count.</summary>
    internal static string UnreportedText(IReadOnlyList<LlmChatMessage> messages)
    {
        int start = 0;
        for (int index = messages.Count - 1; index >= 0; index--)
            if (messages[index].ContinuationState?.InputTokens is not null) { start = index + 1; break; }
        var text = new StringBuilder();
        foreach (LlmChatMessage message in messages.Skip(start))
        {
            if (message.Content.Length > 0) text.Append(message.Content).Append('\n');
            foreach (LlmToolCall call in message.ToolCalls.OfType<LlmToolCall>()) text.Append(call.ArgumentsJson).Append('\n');
            foreach (LlmToolOutputText part in message.ToolResults.OfType<LlmToolResult>()
                .SelectMany(result => result.Output.Parts).OfType<LlmToolOutputText>())
                text.Append(part.Text).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Every Grok model compacts through <c>responses/compact</c>; a failed call falls back to the host summary.</summary>
    public bool CompactsProviderSide(LlmConnection connection, string modelId) => true;

    /// <summary>
    /// Sends the request's input, with its instructions as a leading system item, to <c>responses/compact</c> and returns
    /// one entry whose continuation state is the returned compaction item (B10, B11).
    /// </summary>
    public async Task<LlmAgentContext> CompactAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken)
    {
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken);
        cancellationToken = consumption.Token;
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        LlmStructuredOutputEnforcement? enforcement = ResolveStructuredOutputEnforcement(connection, request, capabilities);
        CancellationToken token = cancellationToken;
        LlmResourcePreparation? resources = request.Resources is null ? null : await LlmResourcePreparation.PrepareInlineAsync(request,
            () => token.ThrowIfCancellationRequested(), cancellationToken, includeSelection: false).ConfigureAwait(false);
        try
        {
            if (resources is not null) request = resources.Request;
            var input = (JsonArray)BuildPayload(connection, request, enforcement, stream: false, resources)["input"]!.DeepClone();
            if (request.Instructions is { Length: > 0 } instructions)
                input.Insert(0, new JsonObject { ["role"] = "system", ["content"] = instructions });
            var payload = new JsonObject { ["model"] = request.ModelId, ["input"] = input };
            using var message = new HttpRequestMessage(HttpMethod.Post, ProviderHttp.BuildUri(connection, "responses/compact"))
            {
                Content = RequestJson.Content(payload)
            };
            XaiRequestHeaders.Apply(message, connection, stream: false);
            LlmCredential? credential = LlmCredential.Parse(connection.Credential);
            string[] secrets = [.. connection.AdditionalHeaders.Values, .. message.Headers.SelectMany(header => header.Value)];
            using HttpResponseMessage response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            await XaiErrorClassifier.ThrowIfUnsuccessfulCompactionAsync(response, credential, secrets, cancellationToken).ConfigureAwait(false);
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
            JsonArray output = ReadCompaction(body) ?? throw ProtocolException("Grok returned an invalid compaction response.",
                response, string.Empty, credential, extraSecrets: secrets);
            LlmContinuationState state = ProviderContinuationStateCodec.Encode(connection, request.ModelId, output);
            var entry = new LlmAgentContextEntry(Guid.NewGuid().ToString("N"), ConversationCompactor.SummaryTurnId,
                new LlmChatMessage("assistant", string.Empty) { ContinuationState = state });
            return consumption.Publish(new LlmAgentContext { Entries = [entry] });
        }
        finally
        {
            if (resources is not null) await resources.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The output of a <c>response.compaction</c> body holding exactly one compaction item, or null.</summary>
    internal static JsonArray? ReadCompaction(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root || root["object"]?.GetValue<string>() != "response.compaction"
                || root["output"] is not JsonArray { Count: 1 } output
                || output[0] is not JsonObject item || item["type"]?.GetValue<string>() != "compaction"
                || item["encrypted_content"]?.GetValue<string>() is not { Length: > 0 })
                return null;
            return (JsonArray)output.DeepClone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Counts <paramref name="text"/> with the model's tokenizer through <c>tokenize-text</c>.</summary>
    public async Task<LlmTokenization> TokenizeAsync(LlmConnection connection, string modelId, string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(text);
        _ = XaiRequestHeaders.Require(connection);
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken);
        cancellationToken = consumption.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, ProviderHttp.BuildUri(connection, "tokenize-text"))
        {
            Content = RequestJson.Content(new JsonObject { ["model"] = modelId, ["text"] = text })
        };
        XaiRequestHeaders.Apply(request, connection, false);
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        string[] secrets = [.. connection.AdditionalHeaders.Values, .. request.Headers.SelectMany(header => header.Value)];
        HttpResponseMessage sent;
        try
        {
            sent = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw XaiErrorClassifier.HttpSendFailure(error, credential, secrets);
        }
        using HttpResponseMessage response = sent;
        await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, credential, secrets, cancellationToken).ConfigureAwait(false);
        string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
        LlmTokenization result = ReadTokens(body)
            ?? throw ProtocolException("Grok tokenization returned an invalid token sequence.", response, string.Empty, credential,
                extraSecrets: secrets);
        return consumption.Publish(result);
    }

    /// <summary>The tokens of a tokenize-text body, or null when it is not a valid token list.</summary>
    private static LlmTokenization? ReadTokens(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("token_ids", out JsonElement entries)
                || entries.ValueKind != JsonValueKind.Array)
                return null;
            var tokens = new List<LlmToken>(entries.GetArrayLength());
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("token_id", out JsonElement id) || !id.TryGetUInt32(out uint number)
                    || !entry.TryGetProperty("string_token", out JsonElement display) || display.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("token_bytes", out JsonElement bytes) || bytes.ValueKind != JsonValueKind.Array)
                    return null;
                var data = new byte[bytes.GetArrayLength()];
                int index = 0;
                foreach (JsonElement item in bytes.EnumerateArray())
                {
                    if (!item.TryGetByte(out byte value)) return null;
                    data[index++] = value;
                }
                tokens.Add(new(number, display.GetString()!, data));
            }
            return new LlmTokenization(tokens.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
