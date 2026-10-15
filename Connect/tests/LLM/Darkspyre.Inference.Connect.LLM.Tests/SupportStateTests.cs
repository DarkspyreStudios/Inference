using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class SupportStateTests
{
    [Theory]
    [InlineData(InferenceSupport.Unknown)]
    [InlineData(InferenceSupport.Supported)]
    [InlineData(InferenceSupport.Unsupported)]
    public void SupportEvidenceRoundTripsThroughOwnedMetadata(InferenceSupport support)
    {
        var common = new LlmCapabilities
        {
            SupportsJsonSchema = support,
            Tools = new() { SupportsParallelCalls = support },
            PromptCache = new() { Automatic = support, AcceptsHints = support }
        };
        string commonJson = JsonSerializer.Serialize(common, InferenceJson.LlmCapabilities);
        using var document = JsonDocument.Parse(commonJson);
        Assert.Equal(support.ToString(), document.RootElement.GetProperty("SupportsJsonSchema").GetString());
        var restoredCommon = JsonSerializer.Deserialize(commonJson, InferenceJson.LlmCapabilities)!;
        Assert.Equal(support, restoredCommon.SupportsJsonSchema);
        Assert.Equal(support, restoredCommon.Tools.SupportsParallelCalls);
        Assert.Equal(support, restoredCommon.PromptCache.Automatic);
        Assert.Equal(support, restoredCommon.PromptCache.AcceptsHints);

        var connection = Connection() with { CapabilityOverrides = new() { SupportsJsonSchema = support } };
        string connectionJson = JsonSerializer.Serialize(connection, ConnectLlmJson.Connection);
        Assert.Equal(support, JsonSerializer.Deserialize(connectionJson, ConnectLlmJson.Connection)!.CapabilityOverrides!.SupportsJsonSchema);

        var files = new LlmFileCapabilities { Upload = support, PublicUrls = support };
        string filesJson = JsonSerializer.Serialize(files, ConnectLlmJson.LlmFileCapabilities);
        var restoredFiles = JsonSerializer.Deserialize(filesJson, ConnectLlmJson.LlmFileCapabilities)!;
        Assert.Equal(support, restoredFiles.Upload);
        Assert.Equal(support, restoredFiles.PublicUrls);
    }

    [Fact]
    public void NullInheritsButExplicitUnknownAndUnsupportedRemainDistinct()
    {
        var capabilities = new LlmProviderCapabilities { SupportsJsonSchema = InferenceSupport.Supported };
        Assert.Equal(InferenceSupport.Supported, capabilities.Apply(new()).SupportsJsonSchema);
        Assert.Equal(InferenceSupport.Unknown, capabilities.Apply(new() { SupportsJsonSchema = InferenceSupport.Unknown }).SupportsJsonSchema);
        Assert.Equal(InferenceSupport.Unsupported, capabilities.Apply(new() { SupportsJsonSchema = InferenceSupport.Unsupported }).SupportsJsonSchema);
        Assert.Equal(InferenceSupport.Unknown, new LlmProviderCapabilities().Apply(null).SupportsJsonSchema);
    }

    [Fact]
    public void MissingModelFactsDoNotBecomeNegativeEvidence()
    {
        Assert.Equal(InferenceSupport.Unknown, OpenAiModelProfiles.Get("unlisted-model").SupportsPromptCacheOptions);
    }

    [Fact]
    public async Task FeatureMarksMatchB31()
    {
        // Codex reports what its managed session declares; Claude's sessions report NativeAgentContract.Claude.
        using var http = new HttpClient();
        var codex = new OpenAiCodexLlmClient(http);
        LlmConnection connection = DirectCodexTests.Connection();
        await using ManagedAgentSession session = await ManagedAgentSession.CreateAsync("test-connection", connection, new() { ModelId = "model" }, [],
            codex, (_, _) => throw new InvalidOperationException("Capabilities send no request."),
            model => codex.GetCapabilities(connection, model), new LearnedModelContext());
        LlmAgentCapabilities codexMarks = await session.GetCapabilitiesAsync();
        LlmAgentCapabilities claudeMarks = NativeAgentContract.Claude;
        const InferenceSupport Yes = InferenceSupport.Supported, No = InferenceSupport.Unsupported, Unknown = InferenceSupport.Unknown;

        Assert.Equal([LlmHostedToolKind.ImageGeneration], codexMarks.HostedTools);
        Assert.Empty(claudeMarks.HostedTools);
        Assert.Equal(LlmOperationSupport.Unsupported, codexMarks.ResourceObservation);
        Assert.Equal(LlmOperationSupport.Supported, claudeMarks.ResourceObservation);
        (string Name, Func<LlmAgentCapabilities, InferenceSupport> Read, InferenceSupport Codex, InferenceSupport Claude)[] rows =
        [
            ("SupportsShellTools", marks => marks.SupportsShellTools, No, No),
            ("SupportsContextCapture", marks => marks.SupportsContextCapture, Yes, No),
            ("SupportsContextReplacement", marks => marks.SupportsContextReplacement, Yes, No),
            ("SupportsContextStatus", marks => marks.SupportsContextStatus, Yes, Yes),
            ("SupportsCompactionHandoff", marks => marks.SupportsCompactionHandoff, No, No),
            ("SupportsNativeResume", marks => marks.SupportsNativeResume, No, Yes),
            ("SupportsSteering", marks => marks.SupportsSteering, Yes, No),
            ("SupportsInterrupt", marks => marks.SupportsInterrupt, Yes, Yes),
            ("SupportsExternalToolHandoff", marks => marks.SupportsExternalToolHandoff, Yes, Unknown),
            ("SupportsFallbackModels", marks => marks.SupportsFallbackModels, No, Unknown),
            ("SupportsTurnCap", marks => marks.SupportsTurnCap, Yes, Unknown),
            ("SupportsSpendCap", marks => marks.SupportsSpendCap, No, Unknown),
            ("SupportsCompactNow", marks => marks.SupportsCompactNow, Yes, Yes)
        ];
        Assert.Equal(rows.Select(row => (row.Name, row.Codex, row.Claude)),
            rows.Select(row => (row.Name, row.Read(codexMarks), row.Read(claudeMarks))));
    }

    [Theory]
    [InlineData(LlmReasoningLevel.Maximum, new[] { LlmReasoningLevel.None, LlmReasoningLevel.Low }, LlmReasoningLevel.Low)]
    [InlineData(LlmReasoningLevel.Medium, new[] { LlmReasoningLevel.None, LlmReasoningLevel.High }, LlmReasoningLevel.None)]
    [InlineData(LlmReasoningLevel.None, new[] { LlmReasoningLevel.Low, LlmReasoningLevel.High }, LlmReasoningLevel.Low)]
    [InlineData(LlmReasoningLevel.Maximum, new LlmReasoningLevel[0], LlmReasoningLevel.Maximum)]
    public void ReasoningMapsToNearestOfferedLevel(LlmReasoningLevel requested, LlmReasoningLevel[] offered, LlmReasoningLevel expected)
    {
        var capabilities = new LlmProviderCapabilities { ReasoningLevels = LlmReasoningEffort.Levels(offered) };

        LlmChatRequest sent = LlmRequestNormalizer.Normalize(DirectCodexTests.Connection(),
            new() { ModelId = "model", ReasoningLevel = requested }, capabilities);

        Assert.Equal(expected, sent.ReasoningLevel);
    }

    /// <summary>A connection with no key counts as authenticated and lists the service's models (S10).</summary>
    [Fact]
    public async Task OpenAiApiKeylessConnectionIsAuthenticated()
    {
        using var handler = new DirectCodexTests.Handler(_ => ModelList("""{"data":[{"id":"qwen3","context_length":131072}]}"""));
        var client = new OpenAiResponsesLlmClient(new HttpClient(handler));
        await using var providers = new LlmProviderService(new Source(DirectCodexTests.OpenAiConnection("http://local.test/v1")), [client]);

        Assert.True(await providers.IsAuthenticatedAsync("local"));
        LlmModel model = Assert.Single(await providers.ListModelsAsync("local"));
        Assert.Equal(131072, model.MaxContextTokens);
        Assert.False(handler.Headers.ContainsKey("Authorization"));
    }

    /// <summary>Published limits fill an unlisted window on OpenAI's endpoint only; a listed window stays (B6).</summary>
    [Fact]
    public async Task OpenAiListingTakesPublishedLimitsOnlyOnOpenAi()
    {
        using var handler = new DirectCodexTests.Handler(_ => ModelList("""{"data":[{"id":"gpt-4o"},{"id":"gpt-5.5","context_length":5000}]}"""));
        var client = new OpenAiResponsesLlmClient(new HttpClient(handler));

        Dictionary<string, LlmModel> openAi = (await client.ListModelsAsync(
            DirectCodexTests.OpenAiConnection(OpenAiResponsesLlmClient.DefaultEndpoint))).ToDictionary(model => model.Id);
        Dictionary<string, LlmModel> local = (await client.ListModelsAsync(
            DirectCodexTests.OpenAiConnection("http://local.test/v1"))).ToDictionary(model => model.Id);

        Assert.Equal((128_000L, 16_384L), (openAi["gpt-4o"].MaxContextTokens, openAi["gpt-4o"].MaxOutputTokens));
        Assert.Equal(5000, openAi["gpt-5.5"].MaxContextTokens);
        Assert.Null(local["gpt-4o"].MaxContextTokens);
        Assert.Equal(5000, local["gpt-5.5"].MaxContextTokens);
    }

    /// <summary>Another endpoint gets no OpenAI facts; the spend cap is Unsupported everywhere (B11, B12).</summary>
    [Fact]
    public void OtherEndpointMarksServiceFeaturesUnknown()
    {
        var client = new OpenAiResponsesLlmClient(new HttpClient());
        LlmProviderCapabilities local = client.GetCapabilities(DirectCodexTests.OpenAiConnection("http://local.test/v1"), "gpt-5.5");
        LlmProviderCapabilities openAi = client.GetCapabilities(DirectCodexTests.OpenAiConnection(OpenAiResponsesLlmClient.DefaultEndpoint), "gpt-5.5");

        Assert.Equal(
            [InferenceSupport.Unknown, InferenceSupport.Unknown, InferenceSupport.Unknown, InferenceSupport.Unknown, InferenceSupport.Unknown, InferenceSupport.Unknown],
            new[] { local.SupportsStreaming, local.SupportsJsonSchema, local.SupportsToolUse, local.SupportsPromptImages,
                local.SupportsTemperature, local.PromptCache.Automatic });
        Assert.Equal(LlmOutputLimitEnforcement.Unknown, local.OutputLimitEnforcement);
        Assert.Empty(local.ReasoningLevels);
        Assert.False(local.SamplingOnlyWithoutReasoning);
        Assert.Equal([LlmHostedToolKind.ImageGeneration], local.HostedTools);
        Assert.Equal(InferenceSupport.Supported, openAi.SupportsStreaming);
        Assert.NotEmpty(openAi.ReasoningLevels);
        Assert.Equal(InferenceSupport.Unsupported, local.Agent.SupportsSpendCap);
        Assert.Equal(InferenceSupport.Unsupported, openAi.Agent.SupportsSpendCap);
    }

    /// <summary>On OpenAI's endpoint a reasoning model drops temperature unless the level is None, and a non-reasoning
    /// model gets no reasoning object (B4, S3).</summary>
    [Theory]
    [InlineData("gpt-5.5", LlmReasoningLevel.Medium, false, true)]
    [InlineData("gpt-5.5", null, false, true)]
    [InlineData("gpt-5.5", LlmReasoningLevel.None, true, true)]
    [InlineData("gpt-4o", LlmReasoningLevel.Medium, true, false)]
    public async Task TemperatureIsDroppedOnAReasoningModel(string model, LlmReasoningLevel? level, bool temperature, bool reasoning)
    {
        JsonNode sent = await SentAsync(OpenAiResponsesLlmClient.DefaultEndpoint, model, level, 0.5);

        Assert.Equal((temperature, reasoning), (sent.AsObject().ContainsKey("temperature"), sent.AsObject().ContainsKey("reasoning")));
    }

    /// <summary>Only a verified reasoning profile on OpenAI's endpoint asks for reasoning when the request names no level (B4, 6.5).</summary>
    [Theory]
    [InlineData(OpenAiResponsesLlmClient.DefaultEndpoint, "gpt-5.5", true)]
    [InlineData(OpenAiResponsesLlmClient.DefaultEndpoint, "unlisted-model", false)]
    [InlineData("http://local.test/v1", "gpt-5.5", false)]
    public async Task OnlyAVerifiedOpenAiReasoningProfileReasonsByDefault(string endpoint, string model, bool reasons)
    {
        JsonNode sent = await SentAsync(endpoint, model, null, null);

        Assert.Equal(reasons, sent.AsObject().ContainsKey("reasoning"));
    }

    /// <summary>A requested tier is sent as the nearest tier the model offers; Auto and a model without tiers send none (B13, S11).</summary>
    [Theory]
    [InlineData("gpt-5.5", LlmServiceTier.Economy, "flex")]
    [InlineData("gpt-5.5", LlmServiceTier.Standard, "default")]
    [InlineData("gpt-5.5", LlmServiceTier.Fast, "priority")]
    [InlineData("gpt-4o", LlmServiceTier.Economy, "default")]
    [InlineData("gpt-5.5", LlmServiceTier.Auto, null)]
    [InlineData("gpt-5.5", null, null)]
    [InlineData("unlisted-model", LlmServiceTier.Fast, null)]
    public async Task ServiceTierMapsToTheNearestOffered(string model, LlmServiceTier? tier, string? sent)
    {
        JsonNode payload = await SentAsync(OpenAiResponsesLlmClient.DefaultEndpoint, model, null, null, tier);

        Assert.Equal(sent, payload["service_tier"]?.GetValue<string>());
    }

    /// <summary>Codex offers no tiers, so a requested tier is dropped and the turn runs (S11).</summary>
    [Fact]
    public async Task ServiceTierMapsToTheNearestOfferedOnCodex()
    {
        using var handler = new DirectCodexTests.Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new OpenAiCodexLlmClient(new HttpClient(handler));

        JsonNode payload = await SentAsync(client, handler, DirectCodexTests.Connection(), "model", null, null, LlmServiceTier.Fast);

        Assert.False(payload.AsObject().ContainsKey("service_tier"));
    }

    /// <summary>The Service tiers switch is on for OpenAI's endpoint and off elsewhere unless set (B13, S11 step 4).</summary>
    [Theory]
    [InlineData(OpenAiResponsesLlmClient.DefaultEndpoint, null, 4)]
    [InlineData(OpenAiResponsesLlmClient.DefaultEndpoint, InferenceSupport.Unsupported, 0)]
    [InlineData("http://local.test/v1", null, 0)]
    [InlineData("http://local.test/v1", InferenceSupport.Supported, 4)]
    public void ServiceTiersSwitchFollowsTheEndpoint(string endpoint, InferenceSupport? setting, int offered)
    {
        var client = new OpenAiResponsesLlmClient(new HttpClient());
        LlmConnection connection = DirectCodexTests.OpenAiConnection(endpoint) with
        { CapabilityOverrides = new() { SupportsServiceTiers = setting } };

        Assert.Equal(offered, client.GetCapabilities(connection, "gpt-5.5").ServiceTiers.Count);
    }

    /// <summary>The payload the OpenAI API client sends for a normalized request.</summary>
    private static async Task<JsonNode> SentAsync(string endpoint, string model, LlmReasoningLevel? level, double? temperature,
        LlmServiceTier? tier = null)
    {
        using var handler = new DirectCodexTests.Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new OpenAiResponsesLlmClient(new HttpClient(handler));
        return await SentAsync(client, handler, DirectCodexTests.OpenAiConnection(endpoint), model, level, temperature, tier);
    }

    /// <summary>The payload <paramref name="client"/> sends for a normalized request.</summary>
    private static async Task<JsonNode> SentAsync(ResponsesLlmClientBase client, DirectCodexTests.Handler handler,
        LlmConnection connection, string model, LlmReasoningLevel? level, double? temperature, LlmServiceTier? tier)
    {
        LlmChatRequest request = LlmRequestNormalizer.Normalize(connection,
            new() { ModelId = model, Messages = [new("user", "hello")], ReasoningLevel = level, Temperature = temperature, ServiceTier = tier },
            client.GetCapabilities(connection, model));
        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
        return JsonNode.Parse(handler.Body!)!;
    }

    private static HttpResponseMessage ModelList(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LlmConnection Connection() => new()
    {
        Version = "1",
        Transport = LlmTransport.OpenAiCompatibleChat,
        Endpoint = "https://compatible.example.test/v1",
        Credential = new LlmCredential { ApiKey = "fixture-key" }.Serialize()
    };

    private sealed class SchemaHandler : HttpMessageHandler
    {
        public string? Payload { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Payload = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.Host is "generativelanguage.googleapis.com" or "aiplatform.googleapis.com"
                    ? "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"{}\"}]},\"finishReason\":\"STOP\"}]}"
                    : request.RequestUri.Host is "api.x.ai" or "api.openai.com"
                    ? "{\"id\":\"response-fixture\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"{}\"}]}]}"
                    : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string id, CancellationToken token = default) => Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string id, string credential, string version, CancellationToken token = default) => throw new InvalidOperationException("This fixture does not change credentials.");
    }
}
