using System.Collections.Frozen;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>OAuth-only Grok Responses adapter. The facade owns its published chat catalog.</summary>
internal sealed class XaiResponsesLlmClient(HttpClient http) : ResponsesLlmClientBase(http), ILlmCatalogCapabilities,
    ILlmTextTokenizerClient, ILlmCredentialAdmissionPolicy, ILlmModelCatalogClient
{
    void ILlmCredentialAdmissionPolicy.ValidateCredential(LlmConnection connection) => _ = XaiRequestHeaders.Require(connection);

    private const int MaxImageBytes = 20 * 1024 * 1024;
    private static readonly FrozenSet<string> ReservedOptions = new[]
    {
        "model", "input", "stream", "store", "include", "previous_response_id", "service_tier", "instructions",
        "temperature", "top_p", "top_k", "seed", "presence_penalty", "frequency_penalty", "stop", "stop_sequences",
        "max_output_tokens", "reasoning", "reasoning_effort", "tools", "tool_choice", "parallel_tool_calls", "text",
        "prompt_cache_key", "background", "deferred", "agent_count", "max_turns"
    }.ToFrozenSet(StringComparer.Ordinal);

    public override LlmTransport Transport => LlmTransport.XaiResponses;
    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.GrokSubscription;
    protected override bool OffersNonStreamingRequests => true;
    protected override bool OffersUsageDeltas => false;
    protected override bool SupportsResourcePreparation => true;
    protected override ResponsesHostedToolObserver CreateHostedToolObserver(Func<Exception> malformed) => new XaiDocumentSearchObserver(malformed);

    // xAI quota names and units require its own qualified evidence; OpenAI provenance is not interchangeable.
    protected override LlmAccountUsageSnapshot? ObserveAccountUsage(HttpResponseMessage response) => null;

    protected override LlmUsage? ReadUsage(JsonElement root)
    {
        if (base.ReadUsage(root) is not { } observed || LlmJson.GetObject(root, "usage") is not { } usage)
        {
            return null;
        }

        Dictionary<LlmHostedToolKind, int>? counts = null;
        if (LlmJson.GetObject(usage, "server_side_tool_usage_details") is { } details)
        {
            counts = [];
            foreach ((string field, LlmHostedToolKind kind) in new[]
            {
                ("web_search_calls", LlmHostedToolKind.WebSearch),
                ("x_search_calls", LlmHostedToolKind.SocialSearch),
                ("code_interpreter_calls", LlmHostedToolKind.CodeExecution),
                ("mcp_calls", LlmHostedToolKind.RemoteMcp),
                ("image_generation_calls", LlmHostedToolKind.ImageGeneration)
            })
            {
                if (ReadCount(details, field) is { } count) counts.Add(kind, count);
            }
            // The documented file_search_calls/document_search_calls pair has no qualified neutral mapping.
        }

        return observed with
        {
            ProviderReportedCostUsd = ReadCost(usage),
            HostedToolCalls = counts,
            HostedToolCallCount = ReadCount(usage, "num_server_side_tools_used"),
            HostedSourcesUsed = ReadCount(usage, "num_sources_used")
        };
    }

    private static int? ReadCount(JsonElement usage, string field) =>
        usage.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int count) && count >= 0 ? count : null;

    private static decimal? ReadCost(JsonElement usage)
    {
        if (!usage.TryGetProperty("cost_in_usd_ticks", out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !BigInteger.TryParse(value.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger ticks)
            || ticks.Sign < 0) return null;

        // Construct the decimal at tick scale; arithmetic division can round otherwise valid observations.
        byte scale = 10;
        BigInteger maximum = new(decimal.MaxValue);
        while (ticks > maximum && scale > 0)
        {
            BigInteger reduced = BigInteger.DivRem(ticks, 10, out BigInteger remainder);
            if (!remainder.IsZero) return null;
            ticks = reduced;
            scale--;
        }
        if (ticks > maximum) return null;
        int[] bits = decimal.GetBits((decimal)ticks);
        return new decimal(bits[0], bits[1], bits[2], false, scale);
    }

    internal XaiFileClient CreateFileClient() => new(Http);
    internal HttpClient SharedHttp => Http;

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
        HostedTools = new[] { LlmHostedToolKind.DocumentSearch }.ToFrozenSet(),
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
            MaxStopSequences = 0
        };
    }

    internal void PreflightResourceRequest(LlmConnection connection, LlmChatRequest request)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        ValidateModelSampling(connection, request);
        _ = ResolveStructuredOutputEnforcement(connection, request, capabilities);
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
        if (request.Tools.Count > 350)
            throw new NotSupportedException("Grok accepts at most 350 client tools.");
        if (request.HostedTools.OfType<LlmDocumentSearchTool>().Any(tool => tool.CollectionIds.Count > 10))
            throw new ArgumentException("Grok document search accepts at most 10 collection IDs per tool.", nameof(request));
        if (ClientTools.Functions(request.Tools).Any(tool => tool.Name == "web_search"))
            throw new ArgumentException("A Grok client function cannot be named web_search.", nameof(request));
        foreach (LlmFileReference file in request.Messages.SelectMany(message => message.Attachments).OfType<LlmFileReference>())
            _ = XaiFileRequestData.Identity(file.File.FileId);
        if (request.Resources is not null && !GetCapabilities(connection, request.ModelId).SupportsPromptDocuments)
            throw new NotSupportedException("This resource destination requires document input support.");
        foreach (LlmAttachment attachment in request.Messages.SelectMany(message => message.Attachments).OfType<LlmAttachment>())
            if (attachment.Kind == LlmBinaryKind.Image && attachment.Data.Length > MaxImageBytes)
                throw new ArgumentException("Grok input images must not exceed 20 MiB.", nameof(request));
        if (GetCapabilities(connection, request.ModelId).RichContentSupported.Images == LlmRichContentCarriage.Native)
            foreach (LlmToolOutputPart part in request.Messages.SelectMany(message => ClientTools.Functions(message.ToolResults))
                .SelectMany(result => result.Output.Parts))
                if (part is LlmToolOutputBinary { Kind: LlmBinaryKind.Image } binary && binary.Data.Length > MaxImageBytes
                    || part is LlmToolOutputResource { BodyKind: LlmBinaryKind.Image, Data: { } data } && data.Length > MaxImageBytes)
                    throw new ArgumentException("Grok tool input images must not exceed 20 MiB.", nameof(request));
        foreach (LlmToolDefinition tool in ClientTools.Functions(request.Tools).Where(tool => tool.Strict))
            if (!XaiSchemaLimits.Fits(tool.InputSchema))
                throw new NotSupportedException("A strict Grok tool schema exceeds the published numeric guarantees. The schema is not weakened or sent as non-strict.");
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
            foreach (LlmDocumentSearchTool tool in request.HostedTools.Cast<LlmDocumentSearchTool>())
            {
                var search = new JsonObject
                {
                    ["type"] = "file_search",
                    ["vector_store_ids"] = new JsonArray(tool.CollectionIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
                };
                if (tool.MaxResults is { } maximum) search["max_num_results"] = maximum;
                tools.Add(search);
            }
        }
        if (request.MaxHostedTurns is { } turns) payload["max_turns"] = turns;
        if (request.HostedTools.Count > 0 || HasFileInput(payload["input"]))
            ((JsonArray)payload["include"]!).Add("file_search_call.results");
        if (request.ProviderControls is XaiMultiAgentControls { AgentCount: { } count })
            payload["reasoning"] = new JsonObject
            {
                ["effort"] = XaiModelProfiles.MultiAgentEffort(profile.MultiAgent!, count)
            };
        payload["service_tier"] = (connection.Xai?.ServiceTier ?? XaiServiceTier.Default) == XaiServiceTier.Priority ? "priority" : "default";
        return payload;
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
        request.StructuredOutput is { } output ? StructuredOutput.ResolveEnforcement(output,
            XaiSchemaLimits.Fits(output.Schema) ? capabilities : capabilities with { SupportsJsonSchema = false, SupportsJsonObject = false }) : null;

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
            await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, LlmCredential.Parse(connection.Credential), [], cancellationToken)
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
                "Grok tokenization returned an invalid token sequence.", response, string.Empty, LlmCredential.Parse(connection.Credential));
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
