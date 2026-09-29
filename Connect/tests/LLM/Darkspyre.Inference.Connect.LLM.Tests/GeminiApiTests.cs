using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GeminiApiTests
{
    private const string Flash = "gemini-3-flash-preview";
    private const string Key = "fixture-secret-key";
    private static LlmConnection Connection => new()
    {
        Version = "1",
        Transport = LlmTransport.GoogleGeminiApi,
        Endpoint = GeminiApiLlmClient.DefaultEndpoint,
        Credential = new LlmCredential { ApiKey = Key }.Serialize()
    };
    private static LlmToolDefinition Tool => new()
    {
        Name = "run",
        InputSchema = JsonElement.Parse("""{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}""")
    };

    [Fact]
    public async Task KeyTravelsOnlyInTheHeaderAndRequestUsesTheGenerateContentPath()
    {
        var handler = new Handler("""{"candidates":[{"content":{"parts":[{"text":"hi"}]},"finishReason":"STOP"}]}""");
        var client = new GeminiApiLlmClient(new HttpClient(handler));
        var result = await Complete(client, Connection, new()
        { ModelId = Flash, Messages = [new("user", "hello")], ReasoningLevel = LlmReasoningLevel.Low });
        Assert.Equal("hi", result.Text);
        Uri uri = Assert.Single(handler.Uris);
        Assert.Equal("/v1beta/models/gemini-3-flash-preview:streamGenerateContent", uri.AbsolutePath);
        Assert.Equal("?alt=sse", uri.Query);
        Assert.Equal(Key, Assert.Single(handler.Keys));
        Assert.DoesNotContain(Key, handler.Bodies[0], StringComparison.Ordinal);
        Assert.Equal("LOW", (string?)JsonNode.Parse(handler.Bodies[0])?["generationConfig"]?["thinkingConfig"]?["thinkingLevel"]);
    }

    [Fact]
    public void ConnectionsRejectRoutingChangesAndForeignCredentials()
    {
        Assert.Throws<NotSupportedException>(() => GeminiApiLlmClient.Validate(Connection with { Endpoint = "https://example.com" }));
        Assert.Throws<NotSupportedException>(() => GeminiApiLlmClient.Validate(Connection with { AdditionalHeaders = new Dictionary<string, string> { ["x"] = "y" } }));
        Assert.Throws<NotSupportedException>(() => GeminiApiLlmClient.Validate(Connection with { Vertex = new() { ProjectId = "p", Location = "global" } }));
        Assert.Throws<ArgumentException>(() => GeminiApiLlmClient.Validate(Connection with
        { Credential = new LlmCredential { Google = new() { ServiceAccountJson = "{}" } }.Serialize() }));
        GeminiApiLlmClient.Validate(Connection with { Endpoint = GeminiApiLlmClient.DefaultEndpoint + "/" });
    }

    [Theory]
    [InlineData("models/gemini-3-flash-preview")]
    [InlineData("../gemini")]
    [InlineData("gemini-3-flash?key=secret")]
    [InlineData("Gemini-3")]
    public void ModelIdsCannotChangeRouting(string id) => Assert.Throws<ArgumentException>(() => GeminiApiLlmClient.ParseModel(id));

    [Fact]
    public async Task CatalogFollowsPagesKeepsTextModelsAndMapsLimits()
    {
        var handler = new Handler();
        handler.CatalogReplies.Enqueue("""
            {"models":[
              {"name":"models/gemini-3-flash-preview","displayName":"Gemini 3 Flash","inputTokenLimit":1048576,"outputTokenLimit":65536,"supportedGenerationMethods":["generateContent","countTokens"]},
              {"name":"models/gemini-2.5-flash-preview-tts","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-embedding-001","supportedGenerationMethods":["embedContent"]},
              {"name":"models/imagen-4.0-generate-001","supportedGenerationMethods":["predict"]}
            ],"nextPageToken":"page+2"}
            """);
        handler.CatalogReplies.Enqueue("""
            {"models":[
              {"name":"models/gemini-2.5-pro","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-9-future","supportedGenerationMethods":["generateContent"]}
            ]}
            """);
        var client = new GeminiApiLlmClient(new HttpClient(handler));
        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(Connection);
        Assert.Equal(["gemini-3-flash-preview", "gemini-2.5-pro", "gemini-9-future"], models.Select(model => model.Id));
        LlmModel flash = models[0];
        Assert.Equal("Gemini 3 Flash", flash.DisplayName);
        Assert.Equal(1_048_576, flash.MaxContextTokens);
        Assert.Equal(65_536, flash.MaxOutputTokens);
        Assert.Equal(LlmModelAvailability.Unknown, flash.Availability);
        Assert.Contains(LlmReasoningLevel.Minimal, flash.Capabilities!.ReasoningLevels);
        Assert.Empty(models[1].Capabilities!.ReasoningLevels);
        Assert.True(models[1].Capabilities!.SupportsJsonSchema);
        Assert.Empty(models[2].Capabilities!.ReasoningLevels);
        Assert.False(models[2].Capabilities!.SupportsJsonSchema);
        Assert.Contains("pageToken=page%2B2", handler.CatalogUris[1].Query, StringComparison.Ordinal);
        Assert.All(handler.CatalogUris, uri => Assert.DoesNotContain(Key, uri.ToString(), StringComparison.Ordinal));
        Assert.All(handler.Keys, key => Assert.Equal(Key, key));
    }

    [Fact]
    public async Task CatalogFailureIsAnErrorNotAnEmptyList()
    {
        var client = new GeminiApiLlmClient(new HttpClient(new Handler { Status = HttpStatusCode.Forbidden }));
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => client.ListModelsAsync(Connection));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.DoesNotContain("private-error", error.ToString(), StringComparison.Ordinal);

        var repeating = new Handler();
        repeating.CatalogReplies.Enqueue("""{"models":[],"nextPageToken":"same"}""");
        repeating.CatalogReplies.Enqueue("""{"models":[],"nextPageToken":"same"}""");
        await Assert.ThrowsAsync<LlmProviderException>(() => new GeminiApiLlmClient(new HttpClient(repeating)).ListModelsAsync(Connection));
    }

    [Fact]
    public async Task RateLimitRetainsStatusAndRetryAfterWithoutTheBody()
    {
        var handler = new Handler { Status = HttpStatusCode.TooManyRequests, RetryAfter = TimeSpan.FromSeconds(7) };
        var client = new GeminiApiLlmClient(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Complete(client, Connection, new() { ModelId = Flash }));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-error", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("STOP", LlmCompletionStatus.Completed)]
    [InlineData("MAX_TOKENS", LlmCompletionStatus.Incomplete)]
    [InlineData("SAFETY", LlmCompletionStatus.Refused)]
    [InlineData("NEW_REASON", LlmCompletionStatus.Unknown)]
    public async Task TerminalReasonsRemainDistinct(string reason, LlmCompletionStatus expected)
    {
        string frame = JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = reason } } });
        var client = new GeminiApiLlmClient(new HttpClient(new Handler(frame)));
        var result = await Complete(client, Connection, new() { ModelId = Flash });
        Assert.Equal(expected, result.Status);
        if (expected != LlmCompletionStatus.Completed) { Assert.Null(result.ContinuationState); }
    }

    [Theory]
    [InlineData("""{"error":{"message":"private-provider-error"}}""")]
    [InlineData("""{"candidates":[{"finishReason":"MALFORMED_FUNCTION_CALL"}]}""")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"partial"}]}}]}""")]
    [InlineData("not-json")]
    public async Task InvalidStreamsFailWithoutProviderContent(string frame)
    {
        var client = new GeminiApiLlmClient(new HttpClient(new Handler(frame)));
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Complete(client, Connection, new() { ModelId = Flash }));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-provider-error", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("partial", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedToolCallsContinueAndUsageCountsThoughts()
    {
        var handler = new Handler(
            """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"run","args":{"command":"pwd"}},"thoughtSignature":"signed"}]},"finishReason":"STOP"}]}""",
            """{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":4,"thoughtsTokenCount":3,"cachedContentTokenCount":2,"totalTokenCount":17}}""");
        var client = new GeminiApiLlmClient(new HttpClient(handler));
        LlmChatResult first = await Complete(client, Connection, new() { ModelId = Flash, Tools = [Tool], ToolChoice = new(), Messages = [new("user", "Run pwd")] });
        Assert.Equal(LlmCompletionStatus.RequiresAction, first.Status);
        Assert.StartsWith("gemini_", first.ToolCalls[0].Id, StringComparison.Ordinal);
        Assert.Equal(new LlmUsage(10, 7, 17) { CachedInputTokens = 2, ReasoningOutputTokens = 3 }, first.Usage);
        Assert.Equal(GeminiApiLlmClient.Owner, first.ContinuationState!.ImplementationId);
        Assert.DoesNotContain("strict", handler.Bodies[0], StringComparison.OrdinalIgnoreCase);

        handler.Replies.Enqueue(["""{"candidates":[{"content":{"parts":[{"text":"done"}]},"finishReason":"STOP"}]}"""]);
        var assistant = new LlmChatMessage("assistant", first.Text) { ContinuationState = first.ContinuationState, ToolCalls = first.ToolCalls };
        LlmChatResult second = await Complete(client, Connection, new()
        {
            ModelId = Flash,
            Tools = [Tool],
            Messages = [new("user", "Run pwd"), assistant, new("tool", "") { ToolResults = [new(first.ToolCalls[0].Id, LlmToolOutput.FromText("/tmp"))] }]
        });
        Assert.Equal("done", second.Text);
        JsonNode next = JsonNode.Parse(handler.Bodies[1])!;
        Assert.Equal("signed", (string?)next["contents"]?[1]?["parts"]?[0]?["thoughtSignature"]);
        Assert.DoesNotContain("connectCallId", handler.Bodies[1], StringComparison.Ordinal);
        Assert.Equal("/tmp", (string?)next["contents"]?[2]?["parts"]?[0]?["functionResponse"]?["response"]?["output"]);
    }

    [Fact]
    public async Task ChangedKeyRejectsStoredStateAndPinnedScope()
    {
        var handler = new Handler("""{"candidates":[{"content":{"parts":[{"functionCall":{"name":"run","args":{"command":"pwd"}}}]},"finishReason":"STOP"}]}""");
        var client = new GeminiApiLlmClient(new HttpClient(handler));
        LlmChatResult first = await Complete(client, Connection, new() { ModelId = Flash, Tools = [Tool], Messages = [new("user", "Run pwd")] });
        LlmConnection rotated = Connection with { Credential = new LlmCredential { ApiKey = "another-key" }.Serialize() };
        var assistant = new LlmChatMessage("assistant", first.Text) { ContinuationState = first.ContinuationState, ToolCalls = first.ToolCalls };
        LlmConnection prepared = await client.PrepareAsync(rotated, default);
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.DecodeMessage(assistant, prepared, Flash));

        LlmConnection pinned = await client.PrepareAsync(Connection, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(client, pinned with { Credential = rotated.Credential }, new() { ModelId = Flash }));
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task UnsupportedControlsFailBeforeDispatch()
    {
        var handler = new Handler();
        var client = new GeminiApiLlmClient(new HttpClient(handler));
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection, new() { ModelId = Flash, Tools = [Tool with { Strict = true }] }));
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection, new() { ModelId = Flash, Tools = [Tool], AllowParallelToolCalls = false }));
        await Assert.ThrowsAnyAsync<Exception>(() => Complete(client, Connection, new() { ModelId = "gemini-2.5-pro", ReasoningLevel = LlmReasoningLevel.High }));
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection, new()
        {
            ModelId = Flash,
            StructuredOutput = new() { Name = "result", Schema = JsonElement.Parse("""{"type":"string","pattern":"^a"}""") }
        }));
        Assert.Empty(handler.Bodies);
    }

    private static async Task<LlmChatResult> Complete(GeminiApiLlmClient client, LlmConnection connection, LlmChatRequest request)
    {
        var completions = new List<LlmChatResult>();
        await foreach (var item in client.StreamAsync(connection, request))
        { if (item is LlmChatCompletionEvent completion) { completions.Add(completion.Result); } }
        return Assert.Single(completions);
    }

    private sealed class Handler(params string[] frames) : HttpMessageHandler
    {
        public Queue<string[]> Replies { get; } = new();
        public Queue<string> CatalogReplies { get; } = new();
        public List<Uri> CatalogUris { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public TimeSpan? RetryAfter { get; init; }
        public List<string> Bodies { get; } = [];
        public List<Uri> Uris { get; } = [];
        public List<string?> Keys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Keys.Add(request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null);
            if (Status != HttpStatusCode.OK)
            {
                var failure = new HttpResponseMessage(Status) { Content = new StringContent("private-error " + Key) };
                if (RetryAfter is { } delay) { failure.Headers.RetryAfter = new(delay); }
                return failure;
            }
            if (request.Method == HttpMethod.Get)
            {
                CatalogUris.Add(request.RequestUri!);
                return new(HttpStatusCode.OK) { Content = new StringContent(CatalogReplies.Count > 0 ? CatalogReplies.Dequeue() : """{"models":[]}""", Encoding.UTF8, "application/json") };
            }
            Uris.Add(request.RequestUri!);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Concat((Replies.Count > 0 ? Replies.Dequeue() : frames).Select(frame => "data: " + frame + "\n\n")), Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
