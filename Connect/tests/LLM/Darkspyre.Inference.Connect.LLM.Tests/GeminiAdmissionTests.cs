using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GeminiAdmissionTests
{
    [Theory]
    [InlineData(false, "gemini-3-pro-preview", LlmReasoningLevel.Medium, "LOW", null)]
    [InlineData(false, "gemini-3-flash-preview", LlmReasoningLevel.None, "MINIMAL", null)]
    [InlineData(false, "gemini-3.8-flash", LlmReasoningLevel.Minimal, "LOW", null)]
    [InlineData(false, "gemini-2.5-flash", LlmReasoningLevel.ExtraHigh, null, 24576)]
    [InlineData(false, "gemini-2.5-flash-lite", LlmReasoningLevel.Minimal, null, 0)]
    [InlineData(false, "gemini-2.5-pro", LlmReasoningLevel.None, null, 1024)]
    [InlineData(false, "gemini-unfamiliar", LlmReasoningLevel.Maximum, "HIGH", null)]
    [InlineData(true, "gemini-3-pro-preview", LlmReasoningLevel.Medium, "LOW", null)]
    [InlineData(true, "gemini-3-flash-preview", LlmReasoningLevel.None, "MINIMAL", null)]
    [InlineData(true, "gemini-2.5-flash", LlmReasoningLevel.ExtraHigh, null, 24576)]
    [InlineData(true, "gemini-2.5-pro", LlmReasoningLevel.None, null, 1024)]
    [InlineData(true, "gemini-unfamiliar", LlmReasoningLevel.Maximum, "HIGH", null)]
    public async Task DesiredEffortReachesGenerationInTheModelsNativeForm(
        bool vertex, string model, LlmReasoningLevel effort, string? word, int? budget)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        ILlmClient client = Client(http, vertex);
        LlmConnection connection = Connection(vertex);
        var request = new LlmChatRequest
        { ModelId = vertex ? "google/" + model : model, Messages = [new("user", "answer")], ReasoningLevel = effort, MaxOutputTokens = 123 };

        LlmChatResult? result = null;
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request))
            if (item is LlmChatCompletionEvent completion) result = completion.Result;

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement generation = sent.RootElement.GetProperty("generationConfig");
        JsonElement thinking = generation.GetProperty("thinkingConfig");
        Assert.Equal(word is not null, thinking.TryGetProperty("thinkingLevel", out JsonElement level));
        if (word is not null) Assert.Equal(word, level.GetString());
        Assert.Equal(budget is not null, thinking.TryGetProperty("thinkingBudget", out JsonElement tokens));
        if (budget is not null) Assert.Equal(budget, tokens.GetInt32());
        Assert.Equal(budget != 0, thinking.TryGetProperty("includeThoughts", out JsonElement include) && include.GetBoolean());
        Assert.Equal(123, generation.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(effort, request.ReasoningLevel);
        Assert.Equal("OK", result!.Text);
        Assert.Equal(1, handler.Calls);
        if (model == "gemini-unfamiliar") Assert.Equal(InferenceSupport.Unknown, client.GetCapabilities(connection, request.ModelId).SupportsReasoning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountingAndGenerationUseTheSameEffectiveNativeChoices(bool vertex)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        ILlmClient client = Client(http, vertex);
        LlmConnection connection = Connection(vertex) with
        { CapabilityOverrides = Connection(vertex).CapabilityOverrides! with { ReasoningLevels = LlmReasoningEffort.Levels(LlmReasoningLevel.Low) } };
        var request = new LlmChatRequest
        { ModelId = vertex ? "google/gemini-unfamiliar" : "gemini-unfamiliar", Messages = [new("user", "answer")], ReasoningLevel = LlmReasoningLevel.Maximum };

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
        using var generation = JsonDocument.Parse(handler.Body!);
        JsonElement expected = generation.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.Equal("LOW", expected.GetProperty("thinkingLevel").GetString());

        _ = await ((ILlmProviderTokenCounter)client).CountInputTokensAsync(connection, request);
        using var counting = JsonDocument.Parse(handler.Body!);
        JsonElement counted = vertex ? counting.RootElement : counting.RootElement.GetProperty("generateContentRequest");
        Assert.Equal(expected.GetRawText(), counted.GetProperty("generationConfig").GetProperty("thinkingConfig").GetRawText());
        Assert.Equal(2, handler.Calls);
        Assert.Equal(LlmReasoningLevel.Maximum, request.ReasoningLevel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCatalogThinkingDoesNotBecomeAnExplicitNoThinkingFact(bool negative)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new GeminiApiLlmClient(http);
        LlmConnection connection = Connection(false) with
        {
            Catalog = [new("gemini-unfamiliar", "Model")
            { CatalogEntry = JsonElement.Parse(negative ? """{"thinking":false}""" : """{"name":"models/gemini-unfamiliar"}""") }],
            CapabilityOverrides = Connection(false).CapabilityOverrides! with
            { ReasoningLevels = negative ? LlmReasoningEffort.Levels(LlmReasoningLevel.Low) : null }
        };
        var request = new LlmChatRequest
        { ModelId = "gemini-unfamiliar", Messages = [new("user", "answer")], ReasoningLevel = LlmReasoningLevel.High };
        Assert.Equal(negative ? InferenceSupport.Unsupported : InferenceSupport.Unknown, client.GetCapabilities(connection, request.ModelId).SupportsReasoning);

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(!negative, sent.RootElement.GetProperty("generationConfig").TryGetProperty("thinkingConfig", out _));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullEffortRetainsTheProviderDefaultWithoutSelectingALevel(bool vertex)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        ILlmClient client = Client(http, vertex);
        var request = new LlmChatRequest
        { ModelId = vertex ? "google/gemini-2.5-pro" : "gemini-2.5-pro", Messages = [new("user", "answer")] };

        await foreach (LlmStreamEvent _ in client.StreamAsync(Connection(vertex), request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement thinking = sent.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.False(thinking.TryGetProperty("thinkingLevel", out _));
        Assert.False(thinking.TryGetProperty("thinkingBudget", out _));
        Assert.True(thinking.GetProperty("includeThoughts").GetBoolean());
        Assert.Null(request.ReasoningLevel);
    }

    private static ILlmClient Client(HttpClient http, bool vertex) => vertex
        ? new VertexLlmClient(http, new VertexAuthenticationBoundary()) : new GeminiApiLlmClient(http);

    private static LlmConnection Connection(bool vertex) => new()
    {
        Version = "1", Transport = vertex ? LlmTransport.GoogleVertexAi : LlmTransport.GoogleGeminiApi,
        Endpoint = vertex ? "https://aiplatform.googleapis.com" : GeminiApiLlmClient.DefaultEndpoint,
        Credential = vertex ? null : new LlmCredential { ApiKey = "test-key" }.Serialize(),
        Vertex = vertex ? new() { ProjectId = "project", Location = "global", AuthenticationMode = LlmGoogleAuthenticationMode.ApplicationDefault } : null,
        CapabilityOverrides = new() { SupportsStreaming = InferenceSupport.Unsupported }
    };

    private sealed class VertexAuthenticationBoundary : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(new VertexAccess("test-token", "test-scope"));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string response = request.RequestUri!.AbsolutePath.EndsWith(":countTokens", StringComparison.Ordinal)
                ? """{"totalTokens":12}"""
                : """{"candidates":[{"finishReason":"STOP","content":{"role":"model","parts":[{"text":"OK"}]}}]}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
