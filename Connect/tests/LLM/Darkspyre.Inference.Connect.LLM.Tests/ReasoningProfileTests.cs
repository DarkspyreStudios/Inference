using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ReasoningProfileTests
{
    private static readonly LlmReasoningLevel[] LowToMaximum =
    [
        LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High,
        LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum
    ];

    private static readonly LlmReasoningLevel[] Every =
    [
        LlmReasoningLevel.None, LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium,
        LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum
    ];

    private const string ResponsesCompleted = """
        data: {"type":"response.completed","response":{"id":"resp_ok","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}}

        """;

    private const string AnthropicReady = """
        data: {"type":"message_start","message":{"id":"msg_ok","usage":{"input_tokens":3}}}

        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"ready"}}

        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

        data: {"type":"message_stop"}

        """;

    /// <summary>
    /// A recorded Models API page: a known model whose effort reports no xhigh, and a future model outside the family
    /// table that reports adaptive thinking with effort low to max.
    /// </summary>
    private const string AnthropicModelsPage = """
        {"data":[
          {"id":"claude-opus-5","display_name":"Claude Opus 5","created_at":"2026-01-01T00:00:00Z","max_input_tokens":1000000,"max_tokens":128000,
           "capabilities":{"effort":{"supported":true,"low":{"supported":true},"medium":{"supported":true},"high":{"supported":true},"max":{"supported":true},"xhigh":null},
             "thinking":{"supported":true,"types":{"adaptive":{"supported":true},"enabled":{"supported":false}}},
             "structured_outputs":{"supported":true}}},
          {"id":"claude-future-9","display_name":"Claude Future 9","created_at":"2026-09-01T00:00:00Z","max_input_tokens":2000000,"max_tokens":256000,
           "capabilities":{"effort":{"supported":true,"low":{"supported":true},"medium":{"supported":true},"high":{"supported":true},"max":{"supported":true},"xhigh":{"supported":true}},
             "thinking":{"supported":true,"types":{"adaptive":{"supported":true},"enabled":{"supported":false}}},
             "structured_outputs":{"supported":false}}}
        ],"has_more":false,"last_id":"claude-future-9"}
        """;

    [Theory]
    [InlineData("gpt-6.1-sol", new[] { LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum }, false)]
    [InlineData("gpt-6-astra", new[] { LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum }, false)]
    [InlineData("gpt-6-luna", new[] { LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum }, true)]
    [InlineData("gpt-5.6-mini", new[] { LlmReasoningLevel.None, LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum }, true)]
    [InlineData("gpt-5.5", new[] { LlmReasoningLevel.None, LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum }, true)]
    [InlineData("gpt-4.1", new LlmReasoningLevel[0], true)]
    [InlineData("gpt-4o-mini", new LlmReasoningLevel[0], true)]
    [InlineData("gpt-unlisted", new[] { LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh }, true)]
    public void OpenAiProfilesSetLevelsAndSamplingPerModel(string modelId, LlmReasoningLevel[] levels, bool temperature)
    {
        var client = new OpenAiResponsesLlmClient(new HttpClient(new Recording(_ => Sse(ResponsesCompleted))));

        LlmProviderCapabilities capabilities = client.GetCapabilities(ResponsesConnection(), modelId);

        Assert.Equal(levels, capabilities.ReasoningLevels.Order());
        Assert.Equal(temperature, capabilities.SupportsTemperature);
    }

    [Fact]
    public async Task OpenAiSendsNoneAndMaxAndAllowsTemperatureOnlyWithNone()
    {
        var handler = new Recording(_ => Sse(ResponsesCompleted));
        var client = new OpenAiResponsesLlmClient(new HttpClient(handler));

        await DrainAsync(client.StreamAsync(ResponsesConnection(), Request("gpt-5.5") with
        {
            ReasoningLevel = LlmReasoningLevel.None,
            Temperature = 0.2
        }));
        JsonNode none = JsonNode.Parse(handler.Bodies[^1])!;
        await DrainAsync(client.StreamAsync(ResponsesConnection(), Request("gpt-5.5") with { ReasoningLevel = LlmReasoningLevel.Maximum }));
        JsonNode maximum = JsonNode.Parse(handler.Bodies[^1])!;

        Assert.Equal("none", (string?)none["reasoning"]?["effort"]);
        Assert.Equal(0.2, (double?)none["temperature"]);
        Assert.Equal("max", (string?)maximum["reasoning"]?["effort"]);
        await Assert.ThrowsAsync<NotSupportedException>(() => DrainAsync(client.StreamAsync(ResponsesConnection(),
            Request("gpt-5.5") with { ReasoningLevel = LlmReasoningLevel.Low, Temperature = 0.2 })));
        await Assert.ThrowsAsync<NotSupportedException>(() => DrainAsync(client.StreamAsync(ResponsesConnection(),
            Request("gpt-5.5") with { Temperature = 0.2 })));
        await Assert.ThrowsAsync<NotSupportedException>(() => DrainAsync(client.StreamAsync(ResponsesConnection(),
            Request("gpt-6-astra") with { ReasoningLevel = LlmReasoningLevel.Minimal, Temperature = 0.2 })));
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task CodexOffersTheLevelsItsCatalogListsForEachModel()
    {
        const string catalog = """
            {"models":[
              {"slug":"gpt-5.6-codex","display_name":"GPT-5.6 Codex","context_window":400000,
               "supported_reasoning_levels":[{"effort":"low","description":"Fast"},{"effort":"medium","description":"Balanced"},{"effort":"xhigh","description":"Deep"},{"effort":"ultra","description":"Unknown to Connect"}]},
              {"slug":"legacy","display_name":"Legacy","context_window":100000}
            ]}
            """;
        var client = new OpenAiCodexLlmClient(new HttpClient(new Recording(_ => Json(catalog))));
        LlmConnection connection = DirectCodexTests.Connection();

        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(connection);
        LlmConnection attached = connection with { Catalog = models };

        Assert.Equal(
            [LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.ExtraHigh],
            client.GetCapabilities(attached, "gpt-5.6-codex").ReasoningLevels.Order());
        Assert.Equal(
            [LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh],
            client.GetCapabilities(attached, "legacy").ReasoningLevels.Order());
        Assert.Equal(
            [LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh],
            client.GetCapabilities(connection, "gpt-5.6-codex").ReasoningLevels.Order());
    }

    [Theory]
    [InlineData("claude-fable-5-1", false, false)]
    [InlineData("claude-mythos-5-1", false, false)]
    [InlineData("claude-fable-5", false, true)]
    [InlineData("claude-mythos-5", false, true)]
    [InlineData("claude-opus-5-5", false, false)]
    [InlineData("claude-sonnet-5-5", false, false)]
    [InlineData("claude-opus-5", true, true)]
    [InlineData("claude-sonnet-5", true, true)]
    [InlineData("claude-opus-4-8", true, true)]
    [InlineData("claude-opus-4-7", true, true)]
    public void AnthropicAdaptiveFamiliesOfferNoneOnlyWhereThinkingCanBeDisabled(string modelId, bool offersNone, bool forcedToolChoice)
    {
        AnthropicModelProfile profile = AnthropicModelProfiles.Resolve(modelId, null);

        Assert.Equal(offersNone ? [LlmReasoningLevel.None, .. LowToMaximum] : LowToMaximum, profile.ReasoningLevels.Order());
        Assert.Equal(forcedToolChoice, profile.AllowsForcedToolChoice);
        Assert.False(profile.AllowsSampling);
        AnthropicReasoningFields high = AnthropicModelProfiles.Fields(profile, LlmReasoningLevel.High, 8192);
        Assert.Equal("""{"type":"adaptive"}""", high.Thinking!.ToJsonString());
        Assert.Equal("""{"effort":"high"}""", high.OutputConfig!.ToJsonString());
        Assert.Null(high.MaxTokens);
        if (offersNone)
        {
            Assert.Equal("""{"type":"disabled"}""", AnthropicModelProfiles.Fields(profile, LlmReasoningLevel.None, 8192).Thinking!.ToJsonString());
        }
    }

    [Fact]
    public void AnthropicOlderFamiliesMapEffortsBudgetsAndOmittedThinking()
    {
        AnthropicModelProfile adaptive46 = AnthropicModelProfiles.Resolve("claude-sonnet-4-6", null);
        AnthropicModelProfile effortOnly = AnthropicModelProfiles.Resolve("claude-opus-4-5-20251101", null);
        AnthropicModelProfile budget = AnthropicModelProfiles.Resolve("claude-haiku-4-5", null);

        Assert.Equal(
            [LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.Maximum],
            adaptive46.ReasoningLevels.Order());
        Assert.Equal([LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High], effortOnly.ReasoningLevels.Order());
        Assert.Equal([LlmReasoningLevel.None, .. LowToMaximum], budget.ReasoningLevels.Order());
        Assert.True(adaptive46.AllowsSampling && effortOnly.AllowsSampling && budget.AllowsSampling);
        Assert.Equal(new AnthropicReasoningFields(null, null, null), AnthropicModelProfiles.Fields(adaptive46, LlmReasoningLevel.None, 8192));
        AnthropicReasoningFields effort = AnthropicModelProfiles.Fields(effortOnly, LlmReasoningLevel.Medium, 8192);
        Assert.Null(effort.Thinking);
        Assert.Equal("""{"effort":"medium"}""", effort.OutputConfig!.ToJsonString());
        AnthropicReasoningFields budgeted = AnthropicModelProfiles.Fields(budget, LlmReasoningLevel.High, 8192);
        Assert.Equal("""{"type":"enabled","budget_tokens":16384}""", budgeted.Thinking!.ToJsonString());
        Assert.Null(budgeted.OutputConfig);
        Assert.Equal(8192 + 16_384, budgeted.MaxTokens);
        Assert.Equal(new AnthropicReasoningFields(null, null, null), AnthropicModelProfiles.Fields(budget, LlmReasoningLevel.None, 8192));
        Assert.Empty(AnthropicModelProfiles.Resolve("claude-test", null).ReasoningLevels);
    }

    [Fact]
    public async Task AnthropicCatalogEntriesSetLevelsForKnownAndFutureModels()
    {
        var client = new AnthropicMessagesLlmClient(new HttpClient(new Recording(_ => Json(AnthropicModelsPage))));

        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(AnthropicConnection());
        LlmConnection attached = AnthropicConnection() with { Catalog = models };

        LlmModel future = Assert.Single(models, model => model.Id == "claude-future-9");
        Assert.Equal(LowToMaximum, future.Capabilities!.ReasoningLevels.Order());
        Assert.Equal((2_000_000L, 256_000L), (future.MaxContextTokens!.Value, future.MaxOutputTokens!.Value));
        Assert.Equal(LowToMaximum, client.GetCapabilities(attached, "claude-future-9").ReasoningLevels.Order());
        Assert.Equal(
            [LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.Maximum],
            client.GetCapabilities(attached, "claude-opus-5").ReasoningLevels.Order());
        AnthropicModelProfile profile = AnthropicModelProfiles.Resolve("claude-future-9", future.CatalogEntry);
        Assert.Equal((false, true, false), (profile.AllowsSampling, profile.AllowsForcedToolChoice, profile.SupportsStructuredOutputs!.Value));
    }

    [Fact]
    public async Task TheServiceListsTheAnthropicCatalogOnceAndFallsBackToTheFamilyTable()
    {
        var handler = new Recording(request => request.Method == HttpMethod.Get ? Json(AnthropicModelsPage) : Sse(AnthropicReady));
        var service = new LlmProviderService(new Source(AnthropicConnection()), [new AnthropicMessagesLlmClient(new HttpClient(handler))]);

        LlmProviderCapabilities future = await service.GetModelCapabilitiesAsync("anthropic", "claude-future-9");
        LlmProviderCapabilities opus = await service.GetModelCapabilitiesAsync("anthropic", "claude-opus-5");
        await service.CompleteAsync("anthropic", Request("claude-future-9") with { ReasoningLevel = LlmReasoningLevel.ExtraHigh });

        Assert.Equal(LowToMaximum, future.ReasoningLevels.Order());
        Assert.DoesNotContain(LlmReasoningLevel.ExtraHigh, opus.ReasoningLevels);
        Assert.Equal(1, handler.Methods.Count(method => method == HttpMethod.Get));
        Assert.Equal("xhigh", (string?)JsonNode.Parse(handler.Bodies[^1])!["output_config"]?["effort"]);

        var failing = new Recording(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") }
            : Sse(AnthropicReady));
        var fallback = new LlmProviderService(new Source(AnthropicConnection()), [new AnthropicMessagesLlmClient(new HttpClient(failing))]);

        LlmProviderCapabilities family = await fallback.GetModelCapabilitiesAsync("anthropic", "claude-opus-5");
        LlmChatResult result = await fallback.CompleteAsync("anthropic", Request("claude-opus-5") with { ReasoningLevel = LlmReasoningLevel.ExtraHigh });

        Assert.Equal([LlmReasoningLevel.None, .. LowToMaximum], family.ReasoningLevels.Order());
        Assert.Equal("ready", result.Text);
    }

    [Fact]
    public async Task GeminiTwoPointFiveUsesThinkingBudgets()
    {
        var handler = new Recording(_ => Sse("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n"));
        var client = new GeminiApiLlmClient(new HttpClient(handler));

        await DrainAsync(client.StreamAsync(GeminiConnection(), Request("gemini-2.5-flash") with { ReasoningLevel = LlmReasoningLevel.None }));
        JsonNode none = JsonNode.Parse(handler.Bodies[^1])!["generationConfig"]!["thinkingConfig"]!;
        await DrainAsync(client.StreamAsync(GeminiConnection(), Request("gemini-2.5-pro") with { ReasoningLevel = LlmReasoningLevel.High }));
        JsonNode high = JsonNode.Parse(handler.Bodies[^1])!["generationConfig"]!["thinkingConfig"]!;

        Assert.Equal("""{"thinkingBudget":0}""", none.ToJsonString());
        Assert.Equal("""{"thinkingBudget":32768,"includeThoughts":true}""", high.ToJsonString());
        Assert.Equal(
            [LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High],
            client.GetCapabilities(GeminiConnection(), "gemini-2.5-pro").ReasoningLevels.Order());
        Assert.Equal(
            [LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High],
            client.GetCapabilities(GeminiConnection(), "gemini-2.5-flash-lite").ReasoningLevels.Order());
    }

    [Theory]
    [InlineData("gemini-3.8-flash", new[] { LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High })]
    [InlineData("gemini-3.7-flash-preview", new[] { LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High })]
    [InlineData("gemini-3.6-flash", new[] { LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High })]
    [InlineData("gemini-3.5-flash-lite", new[] { LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High })]
    [InlineData("gemini-3.1-pro-preview", new[] { LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High })]
    [InlineData("gemini-3-flash-preview", new[] { LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High })]
    [InlineData("gemini-3-pro-preview", new[] { LlmReasoningLevel.Low, LlmReasoningLevel.High })]
    public void GeminiThreeOffersEachModelsThinkingLevels(string modelId, LlmReasoningLevel[] levels)
    {
        GeminiReasoningProfile profile = GeminiReasoning.Get(modelId, catalogThinking: null);

        Assert.Equal(GeminiThinkingControl.Level, profile.Control);
        Assert.Equal(levels, profile.Levels.Order());
        Assert.Equal(
            """{"thinkingLevel":"HIGH","includeThoughts":true}""",
            GeminiReasoning.ThinkingConfig(profile, LlmReasoningLevel.High).ToJsonString());
    }

    [Fact]
    public void AGeminiCatalogEntryWithoutThinkingOffersNoLevels()
    {
        var client = new GeminiApiLlmClient(new HttpClient(new Recording(_ => Json("{}"))));
        using JsonDocument entry = JsonDocument.Parse("""{"name":"models/gemini-3-flash-preview","thinking":false}""");
        LlmConnection attached = GeminiConnection() with
        {
            Catalog = [new LlmModel("gemini-3-flash-preview", "Gemini 3 Flash") { CatalogEntry = entry.RootElement.Clone() }]
        };

        Assert.Empty(client.GetCapabilities(attached, "gemini-3-flash-preview").ReasoningLevels);
        Assert.NotEmpty(client.GetCapabilities(GeminiConnection(), "gemini-3-flash-preview").ReasoningLevels);
    }

    private static LlmChatRequest Request(string modelId) => new()
    {
        ModelId = modelId,
        Messages = [new LlmChatMessage("user", "hello")]
    };

    private static LlmConnection ResponsesConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.OpenAiResponses,
        Endpoint = "https://api.openai.test/v1",
        Credential = new LlmCredential { ApiKey = "sk-test-secret" }.Serialize()
    };

    private static LlmConnection AnthropicConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AnthropicMessages,
        Endpoint = "https://api.anthropic.test",
        Credential = new LlmCredential { ApiKey = "anthropic-key" }.Serialize()
    };

    private static LlmConnection GeminiConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.GoogleGeminiApi,
        Endpoint = GeminiApiLlmClient.DefaultEndpoint,
        Credential = new LlmCredential { ApiKey = "gemini-key" }.Serialize()
    };

    private static HttpResponseMessage Sse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/event-stream")
    };

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private static async Task DrainAsync(IAsyncEnumerable<LlmStreamEvent> events)
    {
        await foreach (LlmStreamEvent _ in events)
        {
        }
    }

    private sealed class Recording(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return respond(request);
        }
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(connection);

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
