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
internal sealed class GrokResponsesLlmClient(HttpClient http, TimeProvider time) : ResponsesLlmClientBase(http), ILlmCatalogCapabilities,
    IExternalLlmAuthenticationProbe, ILlmCredentialAdmissionPolicy, ILlmAccountUsageClient, ILlmStepRetryPolicy,
    ILlmUnauthorizedRenewal, ILlmSessionHostedTools
{
    private const int MaxImageBytes = 20 * 1024 * 1024;

    // The subscription proxy's request limits.
    private const int MaxWireTools = 350;
    private const int MaxDomains = 5;

    private static readonly FrozenSet<string> ReservedOptions = new[]
    {
        "model", "input", "stream", "store", "include", "previous_response_id", "service_tier", "instructions",
        "temperature", "top_p", "top_k", "seed", "presence_penalty", "frequency_penalty", "stop", "stop_sequences",
        "max_output_tokens", "reasoning", "reasoning_effort", "tools", "tool_choice", "parallel_tool_calls", "text",
        "prompt_cache_key", "background", "deferred", "agent_count", "max_turns", "no_inline_citations", "stream_tool_calls"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The CLI's backend-search tools, added per step only when the model offers their kind.</summary>
    public IReadOnlyList<LlmHostedTool> SessionHostedTools { get; } = [new LlmWebSearchTool(), new LlmSocialSearchTool()];

    /// <summary>The CLI's retry decision for this step's failure and resend counts.</summary>
    public LlmStepRetry? Decide(Exception error, LlmStepRetryContext context) =>
        XaiStepRetryPolicy.Decide(error, context, Random.Shared.NextDouble);

    protected override TimeProvider StreamClock => time;
    protected override TimeSpan? StreamIdleTimeout(LlmConnection connection, string modelId) =>
        XaiModelProfiles.Get(connection, modelId).IdleTimeout ?? TimeSpan.FromSeconds(600);
    protected override bool RetriesStreamError(JsonElement errorEvent) => true;
    protected override Exception? ObserveStreamEvent(string? type, JsonElement root, HttpResponseMessage response) =>
        type == "response.doom_loop_check" && XaiRepetitionCheck.IsConfident(root) ? RepetitionFailure(response) : null;
    protected override Exception? ObserveTerminalResponse(JsonElement terminalEvent, HttpResponseMessage response) =>
        XaiRepetitionCheck.IsConfident(LlmJson.GetObject(terminalEvent, "response") ?? terminalEvent) ? RepetitionFailure(response) : null;

    private LlmProviderException RepetitionFailure(HttpResponseMessage response) =>
        new("Grok kept repeating itself; the turn stopped.", Transport, response.StatusCode, "grok_repetition_loop");

    public override LlmTransport Transport => LlmTransport.XaiResponses;

    public override LlmProviderDefinition ProviderDefinition => GrokDefinition.Value;

    protected override bool OffersUsageDeltas => false;

    protected override bool SupportsResourcePreparation => true;

    /// <summary>Grok offers images and model-listed backend web and X search, with no service tiers.</summary>
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
        HostedTools = new[] { LlmHostedToolKind.ImageGeneration, LlmHostedToolKind.WebSearch, LlmHostedToolKind.SocialSearch }.ToFrozenSet(),
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
        XaiModelProfile profile = XaiModelProfiles.Get(connection, modelId);
        LlmProviderCapabilities defaults = DefaultCapabilities with { ReasoningLevels = profile.ReasoningLevels };
        LlmProviderCapabilities capabilities = LlmCapabilityOverrideLookup.Apply(defaults, connection, modelId, OffersUsageDeltas);
        return capabilities with
        {
            SupportsTopK = InferenceSupport.Unsupported,
            SupportsSeed = InferenceSupport.Unsupported,
            SupportsPresencePenalty = InferenceSupport.Unsupported,
            SupportsFrequencyPenalty = InferenceSupport.Unsupported,
            // The Responses request schema publishes no string-stop field.
            MaxStopSequences = 0,
            ServiceTiers = new HashSet<LlmServiceTier>().ToFrozenSet(),
            Tools = capabilities.Tools with { SupportsArgumentStreaming = profile.StreamsToolCalls ? InferenceSupport.Supported : InferenceSupport.Unsupported },
            HostedTools = capabilities.HostedTools.Intersect(profile.SearchesOnBackend ? DefaultCapabilities.HostedTools
                : new HashSet<LlmHostedToolKind> { LlmHostedToolKind.ImageGeneration }).ToFrozenSet()
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
        XaiRequestHeaders.ApplyAccount(request, connection);
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

    protected override void ApplyRequestHeaders(HttpRequestMessage message, LlmConnection connection, LlmChatRequest request)
    {
        XaiRequestHeaders.Apply(message, connection,
            GetCapabilities(connection, request.ModelId).SupportsStreaming != InferenceSupport.Unsupported);
        XaiRepetitionCheck.Apply(message);
    }

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
        if ((long)request.Tools.Count + request.HostedTools.Count > MaxWireTools)
            throw new NotSupportedException($"Grok accepts at most {MaxWireTools} wire tools.");
        foreach (LlmHostedTool tool in request.HostedTools)
        {
            switch (tool)
            {
                case LlmWebSearchTool web:
                    if (web.AllowedDomains is { Count: > 0 } && web.ExcludedDomains is { Count: > 0 })
                        throw new ArgumentException("Grok web search cannot combine allowed and excluded domains.", nameof(request));
                    if (web.AllowedDomains?.Count > MaxDomains || web.ExcludedDomains?.Count > MaxDomains)
                        throw new ArgumentException($"Grok web search accepts at most {MaxDomains} domains per filter.", nameof(request));
                    if (web.IncludeImageSearch || web.IncludeImageUnderstanding)
                        throw new ArgumentException("Grok web search accepts only allowed or excluded domains.", nameof(request));
                    if (web.Location is not null)
                        throw new NotSupportedException("Grok web search does not accept a user location.");
                    break;
                case LlmSocialSearchTool social:
                    if (social.AllowedHandles.Count > 0 || social.ExcludedHandles.Count > 0
                        || social.IncludeImageUnderstanding || social.IncludeVideoUnderstanding)
                        throw new ArgumentException("Grok X search accepts only a date range.", nameof(request));
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
    /// The Responses payload with model-listed reasoning and argument streaming, and the caller's hosted-turn cap.
    /// </summary>
    protected override JsonObject BuildPayload(LlmConnection connection, LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement, bool stream, LlmResourcePreparation? resources = null)
    {
        request = request with
        {
            ReasoningLevel = XaiModelProfiles.MapReasoning(GetCapabilities(connection, request.ModelId).ReasoningLevels, request.ReasoningLevel)
        };
        JsonObject payload = base.BuildPayload(connection, request, enforcement, stream, resources);
        if (XaiModelProfiles.Get(connection, request.ModelId).StreamsToolCalls) payload["stream_tool_calls"] = true;
        if (request.MaxHostedTurns is { } turns) payload["max_turns"] = turns;
        return payload;
    }

    /// <summary>The CLI's web-search domain filters and X-search date range; images use the shared entry.</summary>
    protected override JsonObject WriteHostedTool(LlmHostedTool tool)
    {
        switch (tool)
        {
            case LlmWebSearchTool web:
                var search = new JsonObject { ["type"] = "web_search" };
                if (web.AllowedDomains is { Count: > 0 } allowed) search["filters"] = new JsonObject { ["allowed_domains"] = Strings(allowed) };
                if (web.ExcludedDomains is { Count: > 0 } excluded) search["filters"] = new JsonObject { ["excluded_domains"] = Strings(excluded) };
                return search;
            case LlmSocialSearchTool social:
                var posts = new JsonObject { ["type"] = "x_search" };
                if (social.FromDate is { } from) posts["from_date"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (social.ToDate is { } to) posts["to_date"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return posts;
            default:
                return base.WriteHostedTool(tool);
        }
    }

    /// <summary>Search sources stay inline in reply text; no hosted include suppresses them (D18).</summary>
    protected override IReadOnlyList<string> HostedIncludes(IReadOnlyList<LlmHostedTool> tools) => [];
}
