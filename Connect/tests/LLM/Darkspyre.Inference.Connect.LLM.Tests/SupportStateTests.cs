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
    [Fact]
    public void SupportEvidenceRoundTripsThroughOwnedMetadata()
    {
        const InferenceSupport support = InferenceSupport.Unsupported;
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

    [Theory]
    [InlineData(LlmReasoningLevel.Maximum, new[] { LlmReasoningLevel.None, LlmReasoningLevel.Low }, LlmReasoningLevel.Low)]
    public void ReasoningMapsToNearestOfferedLevel(LlmReasoningLevel requested, LlmReasoningLevel[] offered, LlmReasoningLevel expected)
    {
        var capabilities = new LlmProviderCapabilities { ReasoningLevels = LlmReasoningEffort.Levels(offered) };

        LlmChatRequest sent = LlmRequestNormalizer.Normalize(DirectCodexTests.Connection(),
            new() { ModelId = "model", ReasoningLevel = requested }, capabilities);

        Assert.Equal(expected, sent.ReasoningLevel);
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

    /// <summary>On OpenAI's endpoint a reasoning model drops temperature unless the level is None, and a non-reasoning
    /// model gets no reasoning object (B4, S3).</summary>
    [Theory]
    [InlineData("gpt-5.5", LlmReasoningLevel.Medium, false, true)]
    public async Task TemperatureIsDroppedOnAReasoningModel(string model, LlmReasoningLevel? level, bool temperature, bool reasoning)
    {
        JsonNode sent = await SentAsync(OpenAiResponsesLlmClient.DefaultEndpoint, model, level, 0.5);

        Assert.Equal((temperature, reasoning), (sent.AsObject().ContainsKey("temperature"), sent.AsObject().ContainsKey("reasoning")));
    }

    /// <summary>A requested tier is sent as the nearest tier the model offers; Auto and a model without tiers send none (B13, S11).</summary>
    [Theory]
    [InlineData("gpt-5.5", LlmServiceTier.Economy, "flex")]
    public async Task ServiceTierMapsToTheNearestOffered(string model, LlmServiceTier? tier, string? sent)
    {
        JsonNode payload = await SentAsync(OpenAiResponsesLlmClient.DefaultEndpoint, model, null, null, tier);

        Assert.Equal(sent, payload["service_tier"]?.GetValue<string>());
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

    [Theory]
    [InlineData("café<|special|>")]
    public async Task NativeTokenizerUsesTheExistingFacadeAndPreservesExactPieces(string text)
    {
        using var handler = new TokenizerHandler("""{"tokens":[{"id":8,"piece":"café"},{"id":9,"piece":[195]},{"id":9,"piece":[169]}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(http);
        LlmConnection connection = TokenizerConnection();
        await using var providers = new LlmProviderService(new Source(connection), [client]);

        LlmTokenization result = await providers.TokenizeAsync("native", "qwen3.8-flash-next", text);

        Assert.Equal(connection.LlamaCppTokenizationEndpoint, handler.Endpoint);
        using JsonDocument payload = JsonDocument.Parse(handler.Payload!);
        Assert.Equal(5, payload.RootElement.EnumerateObject().Count());
        Assert.Equal("qwen3.8-flash-next", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal(text, payload.RootElement.GetProperty("content").GetString());
        Assert.False(payload.RootElement.GetProperty("add_special").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("parse_special").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("with_pieces").GetBoolean());
        Assert.Equal(new uint[] { 8, 9, 9 }, result.Tokens.Select(token => token.Id));
        Assert.Equal("café", result.Tokens[0].Text);
        Assert.Equal(Encoding.UTF8.GetBytes("café"), result.Tokens[0].Bytes.ToArray());
        Assert.Null(result.Tokens[1].Text);
        Assert.Null(result.Tokens[2].Text);
        Assert.Equal(new byte[] { 195 }, result.Tokens[1].Bytes.ToArray());
        Assert.Equal(new byte[] { 169 }, result.Tokens[2].Bytes.ToArray());
        string json = JsonSerializer.Serialize(result, ConnectLlmJson.Tokenization);
        var restored = JsonSerializer.Deserialize(json, ConnectLlmJson.Tokenization)!;
        Assert.Null(restored.Tokens[1].Text);
        Assert.Equal(new byte[] { 195 }, restored.Tokens[1].Bytes.ToArray());
        string configured = JsonSerializer.Serialize(connection, ConnectLlmJson.Connection);
        Assert.Equal(connection.LlamaCppTokenizationEndpoint,
            JsonSerializer.Deserialize(configured, ConnectLlmJson.Connection)!.LlamaCppTokenizationEndpoint);
    }

    private static LlmConnection TokenizerConnection() => new()
    {
        Version = "1",
        Transport = LlmTransport.OpenAiResponses,
        Endpoint = "http://native.test/v1",
        LlamaCppTokenizationEndpoint = new Uri("http://native.test/tokenize")
    };

    private sealed class TokenizerHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public Uri? Endpoint { get; private set; }
        public string? Payload { get; private set; }
        public string? Authorization { get; private set; }
        public Dictionary<string, string> Headers { get; private set; } = [];
        public string RequestId { get; init; } = "native-request";
        public bool WaitForCancellation { get; init; }
        public HttpRequestException? Failure { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            Endpoint = request.RequestUri;
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.Parameter;
            Headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(',', header.Value));
            Entered.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (Failure is { } failure) throw failure;
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.Add("x-request-id", RequestId);
            return response;
        }
    }

    private static LlmConnection Connection() => new()
    {
        Version = "1",
        Transport = LlmTransport.OpenAiCompatibleChat,
        Endpoint = "https://compatible.example.test/v1",
        Credential = new LlmCredential { ApiKey = "fixture-key" }.Serialize()
    };

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string id, CancellationToken token = default) => Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string id, string credential, string version, CancellationToken token = default) => throw new InvalidOperationException("This fixture does not change credentials.");
    }
}
