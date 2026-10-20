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
/// Grok conversations on the Responses API of xAI's subscription proxy, signed in with a Grok subscription's OAuth
/// credential. The proxy serves no compaction or tokenizer route, so the host compacts and estimates Grok conversations.
/// </summary>
internal sealed class GrokResponsesLlmClient(HttpClient http) : ResponsesLlmClientBase(http), ILlmCatalogCapabilities,
    IExternalLlmAuthenticationProbe, ILlmCredentialAdmissionPolicy, ILlmAccountUsageClient
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

    /// <summary>Grok's feature list (B6): the OpenAI API column wherever Grok works the same way, plus X search and the
    /// speed tier.</summary>
    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        // The native cap excludes reasoning and function tokens; it does not bound total output.
        OutputLimitEnforcement = LlmOutputLimitEnforcement.Unknown,
        SupportsStreaming = InferenceSupport.Supported,
        SupportsStreamingUsage = InferenceSupport.Supported,
        SupportsReasoningText = InferenceSupport.Supported,
        SupportsModelListing = InferenceSupport.Supported,
        SupportsAccountUsage = InferenceSupport.Supported,
        SupportsJsonSchema = InferenceSupport.Supported,
        SupportsJsonObject = InferenceSupport.Supported,
        SupportsToolUse = InferenceSupport.Supported,
        SupportsAgentSessions = InferenceSupport.Supported,
        SupportsAgentToolUse = InferenceSupport.Supported,
        SupportsAgentTurnInstructions = InferenceSupport.Supported,
        SupportsPromptDocuments = InferenceSupport.Supported,
        Agent = ManagedAbilities with { RichContentSupported = RichContentRoutes.Responses },
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

    /// <summary>The model's feature list: Grok's defaults with the reasoning levels its catalog entry lists (B4, B6).</summary>
    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        XaiRequestHeaders.ValidateEndpoint(connection);
        LlmProviderCapabilities defaults = DefaultCapabilities with { ReasoningLevels = XaiModelProfiles.Get(connection, modelId).ReasoningLevels };
        LlmProviderCapabilities capabilities = LlmCapabilityOverrideLookup.Apply(defaults, connection, modelId, OffersUsageDeltas);
        return capabilities with
        {
            SupportsTopK = InferenceSupport.Unsupported,
            SupportsSeed = InferenceSupport.Unsupported,
            SupportsPresencePenalty = InferenceSupport.Unsupported,
            SupportsFrequencyPenalty = InferenceSupport.Unsupported,
            // The Responses request schema publishes no string-stop field.
            MaxStopSequences = 0,
            HostedTools = capabilities.HostedTools.Intersect(DefaultCapabilities.HostedTools).ToFrozenSet()
        };
    }

    /// <summary>The account's models from the subscription proxy's model list (B4).</summary>
    public override async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken);
        void Fence() { consumption.Token.ThrowIfCancellationRequested(); consumption.Fence(); }
        IReadOnlyList<JsonElement> rows = await XaiCatalog.ReadAsync(Http, connection, Fence, consumption.Token).ConfigureAwait(false);
        Fence();
        IReadOnlyList<LlmModel> models = XaiCatalog.Project(rows);
        LlmConnection published = connection with { Catalog = models };
        return consumption.Publish<IReadOnlyList<LlmModel>>(
            Array.AsReadOnly(models.Select(model => model with { Capabilities = GetCapabilities(published, model.Id) }).ToArray()));
    }

    /// <summary>The account's credit usage from the subscription proxy's billing route, the read the Grok Build CLI makes for
    /// its usage view. The proxy finds the account by the x-userid header.</summary>
    public async Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        if (GetCapabilities(connection).SupportsAccountUsage == InferenceSupport.Unsupported)
            throw new NotSupportedException("The connection disables account usage.");
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken);
        cancellationToken = consumption.Token;
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        using var request = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.BuildUri(connection, "billing?format=credits"));
        XaiRequestHeaders.Apply(request, connection, false);
        if (credential?.OAuth?.AccountId is { Length: > 0 } userId) request.Headers.TryAddWithoutValidation("x-userid", userId);
        string[] secrets = connection.AdditionalHeaders.Values.Concat(request.Headers.SelectMany(header => header.Value)).ToArray();
        HttpResponseMessage sent;
        try
        {
            sent = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            throw XaiErrorClassifier.HttpSendFailure(error, credential, secrets);
        }
        using HttpResponseMessage response = sent;
        await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, credential, secrets, cancellationToken).ConfigureAwait(false);
        string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
        // The reply holds the account's balances, so a failure names the field and never carries the body.
        string? problem = null;
        LlmAccountUsageSnapshot? result = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) problem = "the reply is not a JSON object";
            else result = XaiAccountUsageParser.Parse(document.RootElement, DateTimeOffset.UtcNow);
        }
        catch (JsonException) { problem = "the reply is not JSON"; }
        catch (FormatException error) { problem = error.Message; }
        if (result is null)
            throw new LlmProviderException($"Grok account usage returned an unrecognized reply: {problem}.", Transport,
                response.StatusCode, "xai_account_usage_unrecognized", ProviderHttp.ReadRequestId(response, credential, secrets));
        return consumption.Publish(result);
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
    /// The Responses payload with Grok's reasoning, speed tier (B16), tool search for deferred MCP servers and hosted-turn
    /// cap. A turn that names no tier takes the connection's saved one.
    /// </summary>
    protected override JsonObject BuildPayload(LlmConnection connection, LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement, bool stream, LlmResourcePreparation? resources = null)
    {
        request = request with
        {
            ReasoningLevel = XaiModelProfiles.MapReasoning(GetCapabilities(connection, request.ModelId).ReasoningLevels, request.ReasoningLevel),
            ServiceTier = request.ServiceTier is null or LlmServiceTier.Auto
                ? (connection.Xai?.ServiceTier ?? XaiServiceTier.Default) == XaiServiceTier.Priority ? LlmServiceTier.Fast : LlmServiceTier.Standard
                : request.ServiceTier
        };
        JsonObject payload = base.BuildPayload(connection, request, enforcement, stream, resources);
        if (request.HostedTools.OfType<LlmRemoteMcpTool>().Any(tool => tool.DeferLoading))
            ((JsonArray)payload["tools"]!).Add(item: new JsonObject { ["type"] = "tool_search" });
        if (request.MaxHostedTurns is { } turns) payload["max_turns"] = turns;
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
}
