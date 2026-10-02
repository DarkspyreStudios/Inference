using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>The Gemini API (Google AI Studio) over generateContent, authenticated with an API key header.</summary>
internal sealed partial class GeminiApiLlmClient(HttpClient http)
    : LlmClientBase(http), ILlmConnectionPreparation, ILlmContextEstimator, ILlmCatalogCapabilities, ILlmProviderTokenCounter
{
    public const string DefaultEndpoint = "https://generativelanguage.googleapis.com";
    internal const string Owner = "darkspyre.connect.llm.gemini-api";
    private const string RouteName = "Gemini API";
    private const string KeyHeader = "x-goog-api-key";

    private static readonly GeminiContinuationCodec Codec = new(Owner, RouteName);
    private static readonly GeminiRoute Route = new(LlmTransport.GoogleGeminiApi, RouteName, "gemini_", CheckStatus,
        (connection, modelId, parts) => Codec.Encode(connection, modelId, parts));

    // Catalog entries whose output is not text chat. They answer generateContent but not with this protocol's parts.

    public override LlmTransport Transport => LlmTransport.GoogleGeminiApi;
    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.GeminiApi;
    protected override LlmProviderCapabilities DefaultCapabilities => new() { SupportsModelListing = true };
    protected override bool OffersNonStreamingRequests => true;

    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        Validate(connection);
        GeminiModelProfile profile = modelId is null
            ? GeminiModelProfiles.Get(string.Empty, null) with { MultimodalFunctionResponses = true }
            : Reasoning(connection, modelId);
        bool supportsTopK = modelId is not null && ILlmCatalogCapabilities.EntryFor(connection, modelId) is { ValueKind: JsonValueKind.Object } entry
            && entry.TryGetProperty("topK", out JsonElement topK) && topK.ValueKind == JsonValueKind.Number
            && topK.TryGetInt32(out int defaultTopK) && defaultTopK > 0;
        LlmProviderCapabilities implemented = GeminiCapabilities.Create(profile, supportsTopK);
        LlmProviderCapabilities capabilities = implemented;
        if (modelId is not null && !IsReviewed(modelId)) { capabilities = capabilities with { SupportsJsonSchema = false }; }
        capabilities = LlmCapabilityOverrideLookup.Apply(capabilities, connection, modelId);
        if (capabilities.SupportsJsonObject || capabilities.SupportsAccountUsage
            || capabilities.SupportsTemperature && !implemented.SupportsTemperature
            || capabilities.SupportsTopP && !implemented.SupportsTopP
            || capabilities.SupportsTopK && !implemented.SupportsTopK
            || capabilities.SupportsPresencePenalty && !implemented.SupportsPresencePenalty
            || capabilities.SupportsFrequencyPenalty && !implemented.SupportsFrequencyPenalty
            || capabilities.MaxStopSequences > implemented.MaxStopSequences
            || capabilities.ReasoningLevels.Any(level => !profile.Levels.Contains(level)))
        { throw new NotSupportedException("Gemini API capability overrides cannot enable unimplemented controls."); }
        return capabilities;
    }

    public Task<LlmConnection> PrepareAsync(LlmConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(connection);
        return Task.FromResult(connection with { ContinuationCodec = Codec, ResolvedContinuationScope = KeyScope(connection) });
    }

    private (LlmConnection Connection, LlmChatRequest Request, LlmProviderCapabilities Capabilities,
        LlmStructuredOutputEnforcement? Enforcement, JsonObject Payload, int Omitted) BuildRequest(
        LlmConnection connection, LlmChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = ParseModel(request.ModelId);
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        ValidateGeminiRequest(request, capabilities);
        string scope = KeyScope(connection);
        if (connection.ResolvedContinuationScope is { } pinned && pinned != scope)
        { throw new InvalidOperationException("The Gemini API key changed before dispatch."); }
        connection = connection with { ContinuationCodec = Codec, ResolvedContinuationScope = scope };
        (request, int omitted) = ContinuationStateProjection.Project(request, connection);
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is { } structured && !capabilities.SupportsJsonSchema
            ? StructuredOutput.ResolveEnforcement(structured, capabilities)
            : GeminiSchemas.Enforcement(request.StructuredOutput, RouteName, GeminiSchemaDialect.GeminiApi);
        JsonObject payload = GeminiRequest.Build(connection, request, enforcement, output => ProjectToolOutput(connection, request.ModelId, output),
            Reasoning(connection, request.ModelId), capabilities.SupportsReasoningText);
        return (connection, request, capabilities, enforcement, payload, omitted);
    }

    public async Task<LlmProviderInputTokenCount> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var built = BuildRequest(connection, request);
        built.Payload["model"] = "models/" + request.ModelId;
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{DefaultEndpoint}/v1beta/models/{request.ModelId}:countTokens")
        { Content = JsonContent.Create(new JsonObject { ["generateContentRequest"] = built.Payload }) };
        message.Headers.TryAddWithoutValidation(KeyHeader, Key(built.Connection));
        LlmProviderInputTokenCount result = await ProviderTokenCounting.SendAsync(Http, message, Transport, request.ModelId, false,
            cancellationToken).ConfigureAwait(false);
        return result with { OmittedContinuationStateCount = built.Omitted };
    }

    public override async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var built = BuildRequest(connection, request);
        connection = built.Connection;
        request = built.Request;
        var capabilities = built.Capabilities;
        var enforcement = built.Enforcement;
        int omitted = built.Omitted;
        string model = request.ModelId;
        string operation = capabilities.SupportsStreaming ? "streamGenerateContent?alt=sse" : "generateContent";
        var message = new HttpRequestMessage(HttpMethod.Post, $"{DefaultEndpoint}/v1beta/models/{model}:{operation}")
        { Content = JsonContent.Create(built.Payload) };
        message.Headers.TryAddWithoutValidation(KeyHeader, Key(connection));
        IAsyncEnumerable<LlmStreamEvent> source = GeminiStream.ReadAsync(Http, message, Route, connection, request, enforcement,
            capabilities.SupportsStreaming, capabilities.SupportsUsageDeltas, capabilities.SupportsReasoningText, cancellationToken);
        await using IAsyncEnumerator<LlmStreamEvent> iterator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool moved;
            try { moved = await iterator.MoveNextAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (LlmProviderException error) when (error.StatusCode is not null) { throw; }
            catch (Exception error) when (error is LlmProviderException or HttpRequestException or JsonException or InvalidOperationException)
            {
                // Native payloads and the key must not escape through nested exceptions or diagnostics.
                throw new LlmProviderException("Gemini API generation failed. Check the model, request configuration and key.", Transport);
            }
            if (!moved) { yield break; }
            yield return iterator.Current is LlmChatCompletionEvent completion
                ? new LlmChatCompletionEvent(completion.Result with
                {
                    OmittedContinuationStateCount = checked(completion.Result.OmittedContinuationStateCount + omitted),
                    Diagnostics = completion.Result.Diagnostics with { RawFinalResponseJson = null }
                }) : iterator.Current;
        }
    }

    public override async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        Validate(connection);
        var models = new List<LlmModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;
        do
        {
            string path = $"{DefaultEndpoint}/v1beta/models?pageSize=1000" + (page is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(page));
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation(KeyHeader, Key(connection));
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            CheckStatus(response);
            try
            {
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("models", out JsonElement entries))
                {
                    foreach (JsonElement entry in entries.EnumerateArray())
                    {
                        if (Describe(connection, entry) is { } model && seen.Add(model.Id)) { models.Add(model); }
                    }
                }
                page = LlmJson.GetString(root, "nextPageToken");
                if (!string.IsNullOrEmpty(page) && !seenPages.Add(page)) { throw new JsonException(); }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            { throw new LlmProviderException("Gemini API model discovery returned an invalid catalog page.", Transport); }
        } while (!string.IsNullOrEmpty(page));
        return models;
    }

    public string? EstimateDiagnostic => "Local text estimate; opaque reasoning and media occupancy are unknown. This is not a provider admission check.";

    public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
    {
        ValidateRequest(connection, request, GetCapabilities(connection, request.ModelId));
        if (request.Messages.Any(message => message.ContinuationState is not null || message.Attachments.Count != 0
            || message.ToolResults.Any(result => result is not LlmToolResult function
                || function.Output.Parts.Any(part => part is not LlmToolOutputText)))) { return null; }
        return new(LocalTokenCount.Count(JsonSerializer.Serialize(request, ConnectLlmJson.ChatRequest)));
    }

    internal static void Validate(LlmConnection connection)
    {
        RuntimeInputAdmission.Validate(connection);
        if (connection.Transport != LlmTransport.GoogleGeminiApi) { throw new ArgumentException("Wrong Gemini API connection transport."); }
        if (connection.Endpoint.TrimEnd('/') != DefaultEndpoint || connection.AdditionalHeaders.Count != 0
            || connection.Vertex is not null || connection.ChatCompletions is not null)
        {
            throw new NotSupportedException("The Gemini API owns endpoint routing and headers; provider options go into generationConfig.");
        }
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if (credential is { } c && (c.OAuth is not null || c.Aws is not null || c.Google is not null || c.SecretHeaders.Count != 0))
        {
            throw new ArgumentException("Gemini API connections accept only an API key.");
        }
    }

    internal static string ParseModel(string modelId)
    {
        if (!ModelSegment().IsMatch(modelId)) { throw new ArgumentException("Use a bare Gemini API model ID, such as gemini-3-flash-preview."); }
        return modelId;
    }

    internal static void CheckStatus(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) { return; }
        string message = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => "The Gemini API rejected the request. Check the model and request configuration.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The Gemini API rejected the key. Check that it is valid and allowed to use this model.",
            HttpStatusCode.NotFound => "The Gemini API did not find this model.",
            HttpStatusCode.TooManyRequests => "The Gemini API rate limit or quota was reached.",
            _ => "The Gemini API failed to complete the operation."
        };
        throw new LlmProviderException(message, LlmTransport.GoogleGeminiApi, response.StatusCode, retryAfter: response.Headers.RetryAfter?.Delta);
    }

    /// <summary>Families whose reasoning and structured-output behaviour has been reviewed against the Gemini API.</summary>
    internal static bool IsReviewed(string modelId) =>
        modelId.StartsWith("gemini-3", StringComparison.Ordinal) || modelId.StartsWith("gemini-2.5", StringComparison.Ordinal);

    /// <summary>The model's reasoning control, gated on the catalog's <c>thinking</c> flag when the catalog is kept.</summary>
    private static GeminiModelProfile Reasoning(LlmConnection connection, string modelId) =>
        GeminiModelProfiles.Get(ParseModel(modelId), CatalogThinking(connection, modelId));

    private static bool? CatalogThinking(LlmConnection connection, string modelId) =>
        ILlmCatalogCapabilities.EntryFor(connection, modelId) is { ValueKind: JsonValueKind.Object } entry
        && entry.TryGetProperty("thinking", out JsonElement thinking)
        && thinking.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? thinking.GetBoolean()
            : null;

    private LlmModel? Describe(LlmConnection connection, JsonElement entry)
    {
        string? name = LlmJson.GetString(entry, "name");
        if (name?.StartsWith("models/gemini-", StringComparison.Ordinal) != true) { return null; }
        string id = name["models/".Length..];
        if (!ModelSegment().IsMatch(id) || !GeminiModels.IsTextChat(id)) { return null; }
        if (!entry.TryGetProperty("supportedGenerationMethods", out JsonElement methods) || methods.ValueKind != JsonValueKind.Array
            || !methods.EnumerateArray().Any(method => method.ValueKind == JsonValueKind.String && method.GetString() == "generateContent"))
        { return null; }
        var model = new LlmModel(id, LlmJson.GetString(entry, "displayName") ?? id, "google")
        {
            CatalogEntry = entry.Clone(),
            MaxContextTokens = LlmJson.GetInt(entry, "inputTokenLimit"),
            MaxOutputTokens = LlmJson.GetInt(entry, "outputTokenLimit"),
            Availability = LlmModelAvailability.Unknown,
            AvailabilityReason = IsReviewed(id) ? "Catalog entry; invocation access has not been tested."
                : "Catalog entry in an unreviewed family; reasoning control and provider schema enforcement are not offered."
        };
        // The listed capabilities come from this entry, as generation's do once the catalog is kept.
        return model with { Capabilities = GetCapabilities(connection with { Catalog = [model] }, id) };
    }

    private static void ValidateGeminiRequest(LlmChatRequest request, LlmProviderCapabilities capabilities)
    {
        if (request.ProviderOptions.ContainsKey(AnthropicTools.OptionName))
        { throw new NotSupportedException("Gemini does not accept Anthropic tool configuration."); }
        if (request.Temperature is < 0 or > 2) { throw new ArgumentException("The temperature exceeds the Gemini range of 0 to 2."); }
        if (ClientTools.Functions(request.Tools).Any(tool => tool.Strict) && capabilities.Tools.StrictInputSchema == LlmStrictInputSchemaSupport.Unsupported)
        { throw new NotSupportedException("This model does not offer strict tool-schema enforcement. Ordinary non-strict tools are supported."); }
        if (request.AllowParallelToolCalls is not null && !capabilities.Tools.SupportsParallelCallControl)
        { throw new NotSupportedException("This route cannot enforce parallel tool-call control."); }
        if (request.Messages.SelectMany(message => message.Attachments).Select(LlmAttachments.RequireBinary).Any(attachment => !GeminiCapabilities.AcceptsAttachment(attachment)))
        { throw new NotSupportedException("Unsupported Gemini API attachment media type or size."); }
    }

    private static string Key(LlmConnection connection) => LlmCredential.Parse(connection.Credential)?.ApiKey is { Length: > 0 } key
        ? key : throw new LlmProviderException("A Gemini API key is required.", LlmTransport.GoogleGeminiApi, HttpStatusCode.Unauthorized);

    private static string KeyScope(LlmConnection connection) =>
        ProviderContinuationStateCodec.Scope(connection with { ContinuationCodec = null, ResolvedContinuationScope = null });

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelSegment();
}
