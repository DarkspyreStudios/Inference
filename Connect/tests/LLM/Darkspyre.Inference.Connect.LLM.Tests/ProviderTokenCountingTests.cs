using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderTokenCountingTests
{
    private const string Schema = """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""";
    private const string Key = "fixture-count-secret";

    [Fact]
    public async Task AnthropicCountsTheCanonicalPromptWithoutGenerationFieldsOrOutputUsage()
    {
        const string model = "claude-opus-4-5";
        using var handler = new Handler("""{"input_tokens":4294967296}""",
            """{"type":"message","content":[{"type":"text","text":"{\"answer\":\"ok\"}"}],"stop_reason":"end_turn"}""");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        LlmConnection connection = new()
        {
            Transport = LlmTransport.AnthropicMessages,
            Endpoint = "https://anthropic.invalid/v1",
            Version = "1",
            Credential = new LlmCredential { ApiKey = Key }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = false },
            Catalog = [new(model, model) { CatalogEntry = JsonElement.Parse("""{"capabilities":{"structured_outputs":{"supported":true},"effort":{"supported":true,"medium":{"supported":true}}}}""") }]
        };
        LlmChatRequest request = Request(model) with
        {
            Temperature = 0.5,
            TopP = 0.8,
            TopK = 10,
            StopSequences = ["END"],
            ReasoningLevel = LlmReasoningLevel.Medium,
            Tools = [Request(model).Tools[0] with { Strict = true }]
        };
        string original = JsonSerializer.Serialize(request);
        LlmProviderInputTokenCount count = await client.CountInputTokensAsync(connection, request);
        Assert.Equal(4294967296, count.InputTokens);
        Assert.Equal(model, count.ModelId);
        Assert.Null(count.CachedInputTokens);
        Assert.Null(count.BillableCharacters);
        Assert.Empty(count.InputTokenDetails);
        Assert.Single(handler.Bodies);
        Assert.EndsWith("/v1/messages/count_tokens", handler.Uris[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(Key, handler.Keys[0]);
        Assert.DoesNotContain(Key, handler.Bodies[0], StringComparison.Ordinal);
        await foreach (var _ in client.StreamAsync(connection, request)) { }
        JsonObject generation = JsonNode.Parse(handler.Bodies[1])!.AsObject();
        foreach (string field in new[] { "max_tokens", "stream", "temperature", "top_p", "top_k", "stop_sequences" })
        { Assert.True(generation.Remove(field)); }
        Assert.True(JsonNode.DeepEquals(generation, JsonNode.Parse(handler.Bodies[0])));
        Assert.Equal(original, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task GeminiWrapsTheEntireCanonicalGenerationRequestAndPreservesBreakdowns()
    {
        const string model = "gemini-3-flash-preview";
        using var handler = new Handler("""{"totalTokens":120,"cachedContentTokenCount":0,"promptTokensDetails":[{"modality":"TEXT","tokenCount":20},{"modality":"IMAGE","tokenCount":100},{"modality":"FUTURE","tokenCount":0}],"cacheTokensDetails":[{"modality":"AUDIO","tokenCount":0}]}""",
            """{"candidates":[{"content":{"parts":[{"text":"{\"answer\":\"ok\"}"}]},"finishReason":"STOP"}]}""");
        using var http = new HttpClient(handler);
        var client = new GeminiApiLlmClient(http);
        LlmConnection connection = GeminiConnection;
        LlmChatRequest request = Request(model) with { ReasoningLevel = LlmReasoningLevel.Low };
        string original = JsonSerializer.Serialize(request);
        LlmProviderInputTokenCount count = await client.CountInputTokensAsync(connection, request);
        Assert.Equal(120, count.InputTokens);
        Assert.Equal(0, count.CachedInputTokens);
        Assert.Null(count.BillableCharacters);
        Assert.Equal(new LlmTokenCountDetail(LlmTokenModality.Text, 20), count.InputTokenDetails[0]);
        Assert.Equal(new LlmTokenCountDetail(LlmTokenModality.Image, 100), count.InputTokenDetails[1]);
        Assert.Equal(new LlmTokenCountDetail(LlmTokenModality.Unknown, 0), count.InputTokenDetails[2]);
        Assert.Equal(new LlmTokenCountDetail(LlmTokenModality.Audio, 0), Assert.Single(count.CacheTokenDetails));
        Assert.EndsWith("/v1beta/models/gemini-3-flash-preview:countTokens", handler.Uris[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(Key, handler.Keys[0]);
        Assert.Single(handler.Bodies);
        await foreach (var _ in client.StreamAsync(connection, request)) { }
        JsonObject generation = JsonNode.Parse(handler.Bodies[1])!.AsObject();
        generation["model"] = "models/" + model;
        JsonObject envelope = JsonNode.Parse(handler.Bodies[0])!.AsObject();
        Assert.Single(envelope);
        Assert.True(JsonNode.DeepEquals(generation, envelope["generateContentRequest"]));
        Assert.Equal(original, JsonSerializer.Serialize(request));
        Assert.DoesNotContain(Key, handler.Uris[0].AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicCannotSilentlyDropProviderInputFieldsWhileCounting()
    {
        using var handler = new Handler("""{"input_tokens":10}""", "{}");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = new LlmConnection
        { Version = "1", Transport = LlmTransport.AnthropicMessages, Endpoint = "https://anthropic.invalid", Credential = new LlmCredential { ApiKey = Key }.Serialize() };
        var request = new LlmChatRequest
        { ModelId = "claude-opus-4-5", ProviderOptions = new Dictionary<string, JsonElement> { ["mcp_servers"] = JsonElement.Parse("[]") } };
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CountInputTokensAsync(connection, request));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"totalTokens\":null}")]
    [InlineData("{\"totalTokens\":-1}")]
    [InlineData("{\"totalTokens\":1.5}")]
    [InlineData("{\"totalTokens\":\"100\"}")]
    [InlineData("{\"totalTokens\":9223372036854775808}")]
    [InlineData("{\"totalTokens\":1,\"cachedContentTokenCount\":-1}")]
    [InlineData("{\"totalTokens\":1,\"cachedContentTokenCount\":2}")]
    [InlineData("{\"totalTokens\":1,\"promptTokensDetails\":{}}")]
    [InlineData("{\"totalTokens\":1,\"promptTokensDetails\":[{\"modality\":\"TEXT\",\"tokenCount\":-1}]}")]
    [InlineData("{\"totalTokens\":1,\"promptTokensDetails\":[{\"tokenCount\":1}]}")]
    [InlineData("[]")]
    [InlineData("fixture-count-secret")]
    public async Task MalformedCountsFailWithoutInventedZerosOrRawDiagnostics(string body)
    {
        using var handler = new Handler(body, "{}");
        using var http = new HttpClient(handler);
        var client = new GeminiApiLlmClient(http);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() =>
            client.CountInputTokensAsync(GeminiConnection, new() { ModelId = "gemini-3-flash-preview", Messages = [new("user", "hi")] }));
        Assert.DoesNotContain(Key, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.SanitizedResponseBody);
        Assert.Null(error.InnerException);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task HttpFailurePreservesSafeStatusAndRetryWithoutACompletionOrFallback(HttpStatusCode status)
    {
        using var handler = new Handler(Key, "{}") { Status = status };
        using var http = new HttpClient(handler);
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/count");
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() =>
            ProviderTokenCounting.SendAsync(http, message, LlmTransport.GoogleGeminiApi, "model", false, default));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3), error.RetryAfter);
        Assert.DoesNotContain(Key, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.SanitizedResponseBody);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountRequestsRemainBoundedAndCallerCancellationRemainsCancellation(bool callerCancels)
    {
        using var handler = new WaitingHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/count");
        using var cancellation = new CancellationTokenSource();
        Task<LlmProviderInputTokenCount> pending = ProviderTokenCounting.SendAsync(http, message, LlmTransport.AnthropicMessages,
            "model", true, cancellation.Token, callerCancels ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(30));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (callerCancels)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => pending);
            Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
        }
        Assert.True(handler.Cancelled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineAndCancellationCoverTheBodyAndDisposeTheResponseStream(bool callerCancels)
    {
        using var handler = new BodyHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/count");
        using var cancellation = new CancellationTokenSource();
        Task<LlmProviderInputTokenCount> pending = ProviderTokenCounting.SendAsync(http, message, LlmTransport.AnthropicMessages,
            "model", true, cancellation.Token, callerCancels ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100));
        await handler.Body.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (callerCancels)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => pending);
            Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
        }
        Assert.True(handler.Body.Cancelled);
        Assert.True(handler.Body.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledCountsRejectLateTransportResultsAndSecretFailures(bool fail)
    {
        using var handler = new LateHandler(fail);
        using var http = new HttpClient(handler);
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/count");
        using var cancellation = new CancellationTokenSource();
        Task<LlmProviderInputTokenCount> pending = ProviderTokenCounting.SendAsync(http, message, LlmTransport.AnthropicMessages,
            "model", true, cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await cancellation.CancelAsync();
            handler.Release.TrySetResult();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.DoesNotContain(Key, error.ToString(), StringComparison.Ordinal);
            Assert.Null(error.InnerException);
        }
        finally { handler.Release.TrySetResult(); }
    }

    private static LlmConnection GeminiConnection => new()
    {
        Version = "1",
        Transport = LlmTransport.GoogleGeminiApi,
        Endpoint = GeminiApiLlmClient.DefaultEndpoint,
        Credential = new LlmCredential { ApiKey = Key }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = false }
    };

    private static LlmChatRequest Request(string model) => new()
    {
        ModelId = model,
        Instructions = "Return JSON.",
        MaxOutputTokens = 100,
        Messages = [new("system", "Be precise."), new("user", "Inspect this image.")
        { Attachments = [new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "picture.png", MediaType = "image/png", Data = new byte[] { 1, 2, 3 } }] }],
        Tools = [new() { Name = "run", InputSchema = JsonElement.Parse(Schema) }],
        ToolChoice = new() { Mode = LlmToolChoiceMode.Auto },
        StructuredOutput = new() { Name = "result", Schema = JsonElement.Parse(Schema) }
    };

    private sealed class Handler(string countReply, string generationReply) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        internal List<Uri> Uris { get; } = [];
        internal List<string?> Keys { get; } = [];
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            Keys.Add(request.Headers.TryGetValues("x-api-key", out var anthropic) ? anthropic.Single()
                : request.Headers.TryGetValues("x-goog-api-key", out var gemini) ? gemini.Single() : null);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            bool count = request.RequestUri!.AbsolutePath.Contains("count", StringComparison.Ordinal);
            var response = new HttpResponseMessage(Status)
            { Content = new StringContent(count ? countReply : generationReply, Encoding.UTF8, "application/json") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(3));
            return response;
        }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Cancelled = cancellationToken.IsCancellationRequested; }
            throw new InvalidOperationException();
        }
    }

    private sealed class BodyHandler : HttpMessageHandler
    {
        internal BlockingBody Body { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Body) });
    }

    private sealed class BlockingBody : MemoryStream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled { get; private set; }
        internal bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Cancelled = cancellationToken.IsCancellationRequested; }
            throw new InvalidOperationException();
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class LateHandler(bool fail) : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
            if (fail) throw new HttpRequestException(Key);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"input_tokens\":12}") };
        }
    }
}
