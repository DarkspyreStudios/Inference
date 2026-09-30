using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal sealed class VertexLlmClient(HttpClient http, IVertexAuthentication authentication)
    : LlmClientBase(http), ILlmConnectionPreparation, ILlmContextEstimator
{
    public override LlmTransport Transport => LlmTransport.GoogleVertexAi;
    public override LlmProviderDefinition ProviderDefinition => VertexProviderDefinition.Value;
    protected override LlmProviderCapabilities DefaultCapabilities => new() { SupportsModelListing = true };
    protected override bool OffersNonStreamingRequests => true;
    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => Capabilities(connection, modelId);

    internal static LlmProviderCapabilities Capabilities(LlmConnection connection, string? modelId)
    {
        VertexEndpoints.Validate(connection);
        LlmProviderCapabilities capabilities = modelId is null
            ? VertexModelProfiles.All[0].Capabilities with { RichContentSupported = RichContentRoutes.Anthropic }
            : VertexModelProfiles.Get(modelId, connection.Vertex!.Location).Capabilities;
        capabilities = LlmCapabilityOverrideLookup.Apply(capabilities, connection, modelId);
        if (capabilities.SupportsJsonObject || capabilities.SupportsAccountUsage
            || modelId is not null && capabilities.ReasoningLevels.Any(level =>
                !VertexModelProfiles.Get(modelId, connection.Vertex!.Location).ReasoningLevels.Contains(level)))
        { throw new NotSupportedException("Vertex capability overrides cannot enable unimplemented controls."); }
        return capabilities;
    }

    public async Task<LlmConnection> PrepareAsync(LlmConnection connection, CancellationToken cancellationToken)
    {
        VertexAccess access = await authentication.ResolveAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection with { ContinuationCodec = new VertexContinuationCodec(), ResolvedContinuationScope = access.Scope };
    }

    public override async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        VertexModelProfile profile = VertexModelProfiles.Get(request.ModelId, connection.Vertex?.Location ?? string.Empty);
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        ValidateVertexRequest(request, profile, capabilities);
        VertexAccess access = await authentication.ResolveAsync(connection, cancellationToken).ConfigureAwait(false);
        if (connection.ResolvedContinuationScope is { } pinned && pinned != access.Scope)
        { throw new InvalidOperationException("The Google credential identity changed before dispatch."); }
        connection = connection with { ContinuationCodec = new VertexContinuationCodec(), ResolvedContinuationScope = access.Scope };
        (request, int omitted) = ContinuationStateProjection.Project(request, connection);
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is { } structured && !capabilities.SupportsJsonSchema
            ? StructuredOutput.ResolveEnforcement(structured, capabilities)
            : VertexSchemas.Enforcement(request.StructuredOutput, profile.IsClaude);
        IAsyncEnumerable<LlmStreamEvent> source = profile.IsClaude
            ? new VertexClaudeProtocol(Http, access.Token).StreamAsync(connection, request, cancellationToken)
            : GeminiStream.ReadAsync(Http, GeminiMessage(connection, request,
                GeminiRequest.Build(connection, request, enforcement, output => ProjectToolOutput(connection, request.ModelId, output),
                    GeminiReasoning.Get(VertexEndpoints.ParseModel(request.ModelId).Model, catalogThinking: null), capabilities.SupportsReasoningText),
                access.Token, capabilities.SupportsStreaming), GeminiRoute, connection, request, enforcement,
                capabilities.SupportsStreaming, capabilities.SupportsUsageDeltas, capabilities.SupportsReasoningText, cancellationToken);
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
                    Transport, error is LlmProviderException provider ? provider.StatusCode : null);
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
        VertexAccess access = await authentication.ResolveAsync(connection, cancellationToken).ConfigureAwait(false);
        if (connection.ResolvedContinuationScope is { } pinned && pinned != access.Scope)
        { throw new InvalidOperationException("The Google credential identity changed before model discovery."); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string publisher in new[] { "google", "anthropic" })
        {
            string? page = null;
            var seenPages = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                string path = $"{VertexEndpoints.BaseEndpoint}/v1beta1/publishers/{publisher}/models?pageSize=100"
                    + (page is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(page));
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                CheckStatus(response);
                try
                {
                    using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    JsonElement root = document.RootElement;
                    if (root.TryGetProperty("publisherModels", out var models))
                    {
                        foreach (JsonElement model in models.EnumerateArray())
                        {
                            string? name = LlmJson.GetString(model, "name");
                            string prefix = $"publishers/{publisher}/models/";
                            if (name?.StartsWith(prefix, StringComparison.Ordinal) == true) { names.Add(publisher + "/" + name[prefix.Length..]); }
                        }
                    }
                    page = LlmJson.GetString(root, "nextPageToken");
                    if (!string.IsNullOrEmpty(page) && !seenPages.Add(page)) { throw new JsonException(); }
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException)
                { throw new LlmProviderException("Vertex model discovery returned an invalid catalog page.", Transport); }
            } while (!string.IsNullOrEmpty(page));
        }
        return VertexModelProfiles.All.Where(profile => names.Contains(profile.Id) && profile.Locations.Contains(connection.Vertex!.Location))
            .Select(profile => new LlmModel(profile.Id, profile.Id, VertexEndpoints.ParseModel(profile.Id).Publisher, GetCapabilities(connection, profile.Id))
            {
                MaxContextTokens = profile.ContextTokens,
                MaxOutputTokens = profile.OutputTokens,
                Availability = LlmModelAvailability.Unknown,
                AvailabilityReason = "Publisher catalog entry; invocation access has not been tested."
            }).ToArray();
    }

    public string? EstimateDiagnostic => "Local text estimate; opaque reasoning and media occupancy are unknown. This is not a provider admission check.";
    public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
    {
        ValidateRequest(connection, request, GetCapabilities(connection, request.ModelId));
        if (request.Messages.Any(message => message.ContinuationState is not null || message.Attachments.Count != 0
            || message.ToolResults.Any(result => result.Output.Parts.Any(part => part is not LlmToolOutputText)))) { return null; }
        string projection = JsonSerializer.Serialize(request);
        return new(LocalTokenCount.Count(projection));
    }

    private static readonly GeminiRoute GeminiRoute = new(LlmTransport.GoogleVertexAi, "Vertex", "vertex_", CheckStatus,
        (connection, modelId, parts) => new VertexContinuationCodec().Encode(connection, modelId, parts));

    private static HttpRequestMessage GeminiMessage(LlmConnection connection, LlmChatRequest request, JsonObject payload, string token,
        bool stream)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, VertexEndpoints.Generation(connection, request.ModelId, stream))
        { Content = JsonContent.Create(payload) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return message;
    }

    internal static void CheckStatus(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmProviderException("Vertex rejected the operation. Verify credentials, model access, request configuration and quota.",
                LlmTransport.GoogleVertexAi, response.StatusCode);
        }
    }

    private static void ValidateVertexRequest(LlmChatRequest request, VertexModelProfile profile, LlmProviderCapabilities capabilities)
    {
        if (request.ProviderOptions.Count != 0) { throw new NotSupportedException("Vertex accepts only declared request controls."); }
        if (request.MaxOutputTokens > profile.OutputTokens || request.Temperature is < 0 or > 2
            || profile.IsClaude && request.Temperature > 1)
        { throw new ArgumentException("The output limit or temperature exceeds the selected model's range."); }
        if (request.Tools.Any(tool => tool.Strict) && capabilities.Tools.StrictInputSchema == LlmStrictInputSchemaSupport.Unsupported)
        { throw new NotSupportedException("This model does not offer strict tool-schema enforcement. Ordinary non-strict tools are supported."); }
        if (request.AllowParallelToolCalls is not null && !capabilities.Tools.SupportsParallelCallControl)
        { throw new NotSupportedException("This route cannot enforce parallel tool-call control."); }
        foreach (LlmAttachment attachment in request.Messages.SelectMany(message => message.Attachments))
        {
            if (!profile.IsClaude)
            {
                if (!GeminiCapabilities.AcceptsAttachment(attachment)) { throw new NotSupportedException("Unsupported Vertex attachment media type or size."); }
                continue;
            }
            string mime = LlmAttachments.ResolveMediaType(attachment);
            bool supported = attachment.Kind == LlmBinaryKind.Image
                ? mime is "image/png" or "image/jpeg" or "image/webp" or "image/gif"
                : mime is "application/pdf" or "text/plain";
            if (!supported || attachment.Data.Length > 5 * 1024 * 1024) { throw new NotSupportedException("Unsupported Vertex attachment media type or size."); }
        }
    }
}
