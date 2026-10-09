using Darkspyre.Inference.Common;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal sealed partial class VertexLlmClient(HttpClient http, IVertexAuthentication authentication)
    : LlmClientBase(http), ILlmConnectionPreparation, ILlmContextEstimator, ILlmProviderTokenCounter, ILlmContextCacheAdapter
{
    public override LlmTransport Transport => LlmTransport.GoogleVertexAi;
    public override LlmProviderDefinition ProviderDefinition => VertexProviderDefinition.Value;
    protected override LlmProviderCapabilities DefaultCapabilities => new() { SupportsModelListing = InferenceSupport.Supported };
    protected override bool OffersNonStreamingRequests => true;
    protected override bool SupportsResourcePreparation => true;
    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => Capabilities(connection, modelId);

    internal static LlmProviderCapabilities Capabilities(LlmConnection connection, string? modelId)
    {
        VertexEndpoints.Validate(connection);
        LlmProviderCapabilities capabilities = modelId is null
            ? VertexModelProfiles.Get("google/gemini-3-flash-preview").Capabilities with { RichContentSupported = RichContentRoutes.Anthropic }
            : VertexModelProfiles.Get(modelId).Capabilities;
        bool express = connection.Vertex!.AuthenticationMode == LlmGoogleAuthenticationMode.ApiKey;
        if (express && modelId is not null && VertexModelProfiles.Get(modelId).IsClaude)
            throw new NotSupportedException("Vertex express mode serves Gemini models only.");
        if (express) capabilities = capabilities with { SupportsModelListing = InferenceSupport.Unsupported };
        capabilities = capabilities with
        {
            SupportsExplicitCache = (!express && connection.CacheBindings is not null
            && (modelId is null || !VertexModelProfiles.Get(modelId).IsClaude) ? InferenceSupport.Supported : InferenceSupport.Unsupported)
        };
        LlmProviderCapabilities implemented = capabilities;
        capabilities = LlmCapabilityOverrideLookup.Apply(capabilities, connection, modelId);
        if ((capabilities.SupportsJsonObject != InferenceSupport.Unsupported) || (capabilities.SupportsAccountUsage != InferenceSupport.Unsupported)
            || (capabilities.SupportsExplicitCache != InferenceSupport.Unsupported) && (implemented.SupportsExplicitCache == InferenceSupport.Unsupported)
            || (capabilities.SupportsTemperature != InferenceSupport.Unsupported) && (implemented.SupportsTemperature == InferenceSupport.Unsupported)
            || (capabilities.SupportsTopP != InferenceSupport.Unsupported) && (implemented.SupportsTopP == InferenceSupport.Unsupported)
            || (capabilities.SupportsTopK != InferenceSupport.Unsupported) && (implemented.SupportsTopK == InferenceSupport.Unsupported)
            || (capabilities.SupportsSeed != InferenceSupport.Unsupported) && (implemented.SupportsSeed == InferenceSupport.Unsupported)
            || (capabilities.SupportsPresencePenalty != InferenceSupport.Unsupported) && (implemented.SupportsPresencePenalty == InferenceSupport.Unsupported)
            || (capabilities.SupportsFrequencyPenalty != InferenceSupport.Unsupported) && (implemented.SupportsFrequencyPenalty == InferenceSupport.Unsupported)
            || capabilities.MaxStopSequences > implemented.MaxStopSequences
            || modelId is not null && capabilities.ReasoningLevels.Any(level =>
                !VertexModelProfiles.Get(modelId).ReasoningLevels.Contains(level))
            || express && (capabilities.SupportsModelListing != InferenceSupport.Unsupported))
        { throw new NotSupportedException("Vertex capability overrides cannot enable unimplemented controls."); }
        return capabilities;
    }

    public async Task<LlmConnection> PrepareAsync(LlmConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeInputAdmission.Validate(connection);
        VertexAccess access = await ResolveAccessAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection with { ContinuationCodec = new VertexContinuationCodec(), ResolvedContinuationScope = access.Scope };
    }

    private (VertexModelProfile Profile, LlmProviderCapabilities Capabilities) ValidateSelectedRequest(
        LlmConnection connection, LlmChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        VertexModelProfile profile = VertexModelProfiles.Get(request.ModelId);
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities, profile.IsClaude ? AnthropicTools.Resolve(connection, request).NativeNames : null);
        ValidateVertexRequest(connection, request, profile, capabilities);
        return (profile, capabilities);
    }

    private (VertexModelProfile Profile, LlmProviderCapabilities Capabilities) PreflightRequest(
        LlmConnection connection, LlmChatRequest request)
    {
        var selected = ValidateSelectedRequest(connection, request);
        if (request.ExplicitCache is { } cache)
        {
            ValidateCachedSuffix(request);
            if (connection.CacheAdmission is not { } admission || admission.Owner != connection.CacheBindings)
                throw new ArgumentException("The selected cache requires admission through its binding owner.", nameof(request));
            admission.Check(connection, cache);
        }
        return selected;
    }

    private async Task<(VertexModelProfile Profile, LlmProviderCapabilities Capabilities, VertexAccess Access,
        LlmConnection Connection, LlmChatRequest Request, LlmStructuredOutputEnforcement? Enforcement, int Omitted)> PrepareRequestAsync(
        LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken)
    {
        request = ClientTools.Snapshot(request);
        var (profile, capabilities) = PreflightRequest(connection, request);
        VertexAccess access = await ResolveAccessAsync(connection, cancellationToken).ConfigureAwait(false);
        if (connection.ResolvedContinuationScope is { } pinned && pinned != access.Scope)
        { throw new InvalidOperationException("The Google credential identity changed before dispatch."); }
        connection = connection with { ContinuationCodec = new VertexContinuationCodec(), ResolvedContinuationScope = access.Scope };
        (request, int omitted) = ContinuationStateProjection.Project(request, connection);
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is { } structured && (capabilities.SupportsJsonSchema == InferenceSupport.Unsupported)
            ? StructuredOutput.ResolveEnforcement(structured, capabilities)
            : VertexSchemas.Enforcement(request, capabilities, profile.IsClaude);
        return (profile, capabilities, access, connection, request, enforcement, omitted);
    }

    public async Task<LlmProviderInputTokenCount> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        Uri endpoint = VertexEndpoints.CountTokens(connection, request.ModelId);
        ExplicitCaches.ValidateProviderTokenCount(connection, request);
        if (request.Resources is not null)
            throw new NotSupportedException("The Vertex count route does not materialize caller-owned resources.");
        if (VertexModelProfiles.Get(request.ModelId).IsClaude) AnthropicMessagesLlmClient.ValidateTokenCountOptions(connection, request);
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        var built = await PrepareRequestAsync(connection, request, cancellationToken).ConfigureAwait(false);
        LlmProviderInputTokenCount result;
        if (built.Profile.IsClaude)
            result = await new VertexClaudeProtocol(Http, built.Access)
                .CountInputTokensAsync(built.Connection, built.Request, cancellationToken).ConfigureAwait(false);
        else
        {
            JsonObject payload = GeminiRequest.Build(built.Connection, built.Request, built.Enforcement,
                output => ProjectToolOutput(built.Connection, request.ModelId, output),
                GeminiModelProfiles.Get(VertexEndpoints.ParseModel(request.ModelId).Model, catalogThinking: null), (built.Capabilities.SupportsReasoningText != InferenceSupport.Unsupported));
            payload.Remove("toolConfig");
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = RequestJson.Content(payload) };
            built.Access.Authorize(message);
            result = await ProviderTokenCounting.SendAsync(Http, message, Transport, request.ModelId, false, cancellationToken).ConfigureAwait(false);
        }
        return consumption.Publish(result with { OmittedContinuationStateCount = checked(result.OmittedContinuationStateCount + built.Omitted) });
    }

    public override IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
        CancellationToken cancellationToken = default) => request.Resources is null
            ? StreamPreparedAsync(connection, request, cancellationToken)
            : LlmResourcePreparation.StreamInlineAsync(request, input => { _ = PreflightRequest(connection, input); },
                (input, token) => StreamPreparedAsync(connection, input, token), cancellationToken);

    private async IAsyncEnumerable<LlmStreamEvent> StreamPreparedAsync(LlmConnection connection, LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        var (profile, capabilities, access, prepared, projected, enforcement, omitted) =
            await PrepareRequestAsync(connection, request, cancellationToken).ConfigureAwait(false);
        connection = prepared;
        request = projected;
        IAsyncEnumerable<LlmStreamEvent> source = profile.IsClaude
            ? new VertexClaudeProtocol(Http, access).StreamAsync(connection, request, cancellationToken)
            : GeminiStream.ReadAsync(Http, GeminiMessage(connection, request,
                GeminiRequest.Build(connection, request, enforcement, output => ProjectToolOutput(connection, request.ModelId, output),
                    GeminiModelProfiles.Get(VertexEndpoints.ParseModel(request.ModelId).Model, catalogThinking: null), (capabilities.SupportsReasoningText != InferenceSupport.Unsupported),
                    streamArguments: (capabilities.SupportsStreaming != InferenceSupport.Unsupported)),
                access, capabilities.SupportsStreaming != InferenceSupport.Unsupported), GeminiRoute, connection, request, enforcement,
                capabilities.SupportsStreaming != InferenceSupport.Unsupported, capabilities.SupportsUsageDeltas != InferenceSupport.Unsupported,
                capabilities.SupportsReasoningText != InferenceSupport.Unsupported, cancellationToken);
        await using IAsyncEnumerator<LlmStreamEvent> iterator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool moved;
            try { moved = await iterator.MoveNextAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is LlmProviderException or HttpRequestException or JsonException or InvalidOperationException)
            {
                // Native payloads and tokens must not escape via nested exceptions or diagnostics.
                throw new LlmProviderException("Vertex generation failed. Check the HTTP status, model access and request configuration.",
                    Transport, error is LlmProviderException provider ? provider.StatusCode : null,
                    retryAfter: error is LlmProviderException failure ? failure.RetryAfter : null,
                    isContextOverflow: error is LlmProviderException { IsContextOverflow: true });
            }
            if (!moved) { yield break; }
            connection.CacheAdmission?.Check(connection, request.ExplicitCache);
            yield return consumption.Publish<LlmStreamEvent>(iterator.Current is LlmChatCompletionEvent completion
                ? new LlmChatCompletionEvent(completion.Result with
                {
                    OmittedContinuationStateCount = checked(completion.Result.OmittedContinuationStateCount + omitted),
                    Diagnostics = completion.Result.Diagnostics with { RawFinalResponseJson = null }
                }) : iterator.Current);
        }
    }

    public override async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VertexEndpoints.Validate(connection);
        if (connection.Vertex!.AuthenticationMode == LlmGoogleAuthenticationMode.ApiKey) return [];
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        VertexAccess access = await ResolveAccessAsync(connection, cancellationToken).ConfigureAwait(false);
        if (connection.ResolvedContinuationScope is { } pinned && pinned != access.Scope)
        { throw new InvalidOperationException("The Google credential identity changed before model discovery."); }
        var catalog = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (string publisher in new[] { "google", "anthropic" })
        {
            string? page = null;
            var seenPages = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                string path = $"{VertexEndpoints.BaseEndpoint}/v1beta1/publishers/{publisher}/models?pageSize=100"
                    + (page is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(page));
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                access.Authorize(request);
                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (publisher == "anthropic" && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) break;
                await CheckStatusAsync(response, cancellationToken).ConfigureAwait(false);
                consumption.Fence();
                try
                {
                    using JsonDocument document = JsonDocument.Parse(await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false));
                    JsonElement root = document.RootElement;
                    if (root.TryGetProperty("publisherModels", out var models))
                    {
                        foreach (JsonElement model in models.EnumerateArray())
                        {
                            string? name = LlmJson.GetString(model, "name");
                            string prefix = $"publishers/{publisher}/models/";
                            if (name?.StartsWith(prefix, StringComparison.Ordinal) == true)
                            {
                                catalog[publisher + "/" + name[prefix.Length..]] = model.Clone();
                            }
                        }
                    }
                    page = LlmJson.GetString(root, "nextPageToken");
                    if (!string.IsNullOrEmpty(page) && !seenPages.Add(page)) { throw new JsonException(); }
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException)
                { throw new LlmProviderException("Vertex model discovery returned an invalid catalog page.", Transport); }
            } while (!string.IsNullOrEmpty(page));
        }
        var result = new List<LlmModel>();
        foreach (var (id, entry) in catalog.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            VertexModelProfile profile;
            try { profile = VertexModelProfiles.Get(id); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { continue; }
            string? stage = LlmJson.GetString(entry, "launchStage");
            string? safeStage = stage is "EXPERIMENTAL" or "PRIVATE_PREVIEW" or "PUBLIC_PREVIEW" or "GA" ? stage : null;
            result.Add(new LlmModel(profile.Id, profile.Id, VertexEndpoints.ParseModel(profile.Id).Publisher, GetCapabilities(connection, profile.Id))
            {
                MaxContextTokens = profile.ContextTokens,
                MaxOutputTokens = profile.OutputTokens,
                CatalogEntry = entry,
                Availability = LlmModelAvailability.Unknown,
                AvailabilityReason = safeStage is null ? "Publisher catalog entry; invocation access has not been tested."
                    : $"Publisher catalog entry ({safeStage}); invocation access has not been tested."
            });
        }
        return consumption.Publish<IReadOnlyList<LlmModel>>(result);
    }

    public string? EstimateDiagnostic => "Local text estimate; opaque reasoning and media occupancy are unknown. This is not a provider admission check.";
    public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
    {
        _ = ValidateSelectedRequest(connection, request);
        if (request.Resources is not null) return null;
        if (request.ExplicitCache is not null) { ValidateCachedSuffix(request); return null; }
        if (request.Messages.Any(message => message.ContinuationState is not null || message.Attachments.Count != 0
            || ClientTools.Functions(message.ToolResults).Any(result => result.Output.Parts.Any(part => part is not LlmToolOutputText)))) { return null; }
        string projection = JsonSerializer.Serialize(request, ConnectLlmJson.ChatRequest);
        return new(LocalTokenCount.Count(projection));
    }

    private static readonly GeminiRoute GeminiRoute = new(LlmTransport.GoogleVertexAi, "Vertex", "vertex_", CheckStatusAsync,
        (connection, modelId, parts) => new VertexContinuationCodec().Encode(connection, modelId, parts));

    private static HttpRequestMessage GeminiMessage(LlmConnection connection, LlmChatRequest request, JsonObject payload, VertexAccess access,
        bool stream)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, VertexEndpoints.Generation(connection, request.ModelId, stream))
        { Content = RequestJson.Content(payload) };
        access.Authorize(message);
        return message;
    }

    internal static Task CheckStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        GoogleProviderFailure.ThrowIfUnsuccessfulAsync(response, LlmTransport.GoogleVertexAi,
            "Vertex rejected the operation. Verify credentials, model access, request configuration and quota.",
            response.Headers.RetryAfter?.Delta, cancellationToken);

    private static void ValidateVertexRequest(LlmConnection connection, LlmChatRequest request, VertexModelProfile profile, LlmProviderCapabilities capabilities)
    {
        if (!profile.IsClaude && (connection.ProviderOptions.ContainsKey(AnthropicTools.OptionName) || request.ProviderOptions.ContainsKey(AnthropicTools.OptionName)))
        { throw new NotSupportedException("Vertex accepts only declared request controls."); }
        if (profile.IsClaude)
        {
            AnthropicPromptCache.ValidateOptions(connection, request);
            AnthropicModelProfiles.ValidateSampling(request, AnthropicModelProfiles.Resolve(VertexEndpoints.ParseModel(request.ModelId).Model, null));
            _ = AnthropicSchemas.Enforcement(request, capabilities);
        }
        else if (request.PresencePenalty is < -2 or >= 2 || request.FrequencyPenalty is < -2 or >= 2)
            throw new ArgumentOutOfRangeException(nameof(request), "Vertex Gemini penalties must be in [-2, 2).");
        if (request.MaxOutputTokens > profile.OutputTokens || request.Temperature is < 0 or > 2
            || profile.IsClaude && request.Temperature > 1)
        { throw new ArgumentException("The output limit or temperature exceeds the selected model's range."); }
        if (request.Tools.Any(tool => tool is LlmToolDefinition { Strict: true }) && capabilities.Tools.StrictInputSchema == LlmStrictInputSchemaSupport.Unsupported)
        { throw new NotSupportedException("This model does not offer strict tool-schema enforcement. Ordinary non-strict tools are supported."); }
        if (request.AllowParallelToolCalls is not null && (capabilities.Tools.SupportsParallelCallControl == InferenceSupport.Unsupported))
        { throw new NotSupportedException("This route cannot enforce parallel tool-call control."); }
        foreach (LlmAttachment attachment in request.Messages.SelectMany(message => message.Attachments)
            .Where(attachment => attachment is not LlmResourceAttachment).Select(LlmAttachments.RequireBinary))
        {
            if (!profile.IsClaude)
            {
                if (!GeminiCapabilities.AcceptsAttachment(attachment)) { throw new NotSupportedException("Unsupported Vertex attachment media type or size."); }
                continue;
            }
            if (attachment.Data.Length > 5 * 1024 * 1024) { throw new NotSupportedException("Unsupported Vertex attachment media type or size."); }
            _ = AnthropicAttachments.BuildContentBlock(attachment);
        }
    }
}
