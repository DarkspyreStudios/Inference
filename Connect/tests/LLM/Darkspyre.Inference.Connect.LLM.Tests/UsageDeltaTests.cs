using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class UsageDeltaTests
{
    [Fact]
    public void ObservationsDistinguishMissingFromZeroAndReplaceChangedFields()
    {
        var deltas = new UsageDeltas(true);
        Assert.Null(deltas.Next(null));
        var zero = new LlmUsage(0, null, null);
        Assert.Equal(zero, deltas.Next(zero)?.Cumulative);
        Assert.Null(deltas.Next(zero with { }));
        LlmUsage changed = zero with { OutputTokens = 0, TotalTokens = 0, CachedInputTokens = 0 };
        Assert.Equal(changed, deltas.Next(changed)?.Cumulative);
        Assert.Null(new UsageDeltas(false).Next(changed));
        Assert.False(new LlmCapabilities().SupportsUsageDeltas);
        Assert.False(new LlmProviderCapabilities().SupportsUsageDeltas);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnthropicReplacesCumulativeCountsAndSuppressesDuplicates(bool enabled)
    {
        string wire = Sse(
            """{"type":"message_start","message":{"id":"m1","usage":{"input_tokens":7,"cache_read_input_tokens":1,"cache_creation_input_tokens":2,"output_tokens":1}}}""",
            """{"type":"message_delta","usage":{"output_tokens":2}}""",
            """{"type":"message_delta","usage":{"output_tokens":2}}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":4}}""",
            """{"type":"message_stop"}""",
            """{"type":"message_delta","usage":{"output_tokens":99}}""");
        using var http = new HttpClient(new Handler(wire));
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection(LlmTransport.AnthropicMessages) with
        { CapabilityOverrides = new() { SupportsUsageDeltas = enabled } };
        List<LlmStreamEvent> events = await Read(client.StreamAsync(connection, Request("claude-sonnet-4-6")));
        LlmUsageDeltaEvent[] usage = [.. events.OfType<LlmUsageDeltaEvent>()];
        Assert.Equal(enabled ? 3 : 0, usage.Length);
        if (enabled)
        {
            Assert.Equal(new LlmUsage(10, null, null) { CachedInputTokens = 1, CacheWriteInputTokens = 2 }, usage[0].Cumulative);
            Assert.Equal(new int?[] { null, 2, 4 }, usage.Select(value => value.Cumulative.OutputTokens));
        }
        Assert.Equal(14, Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result.Usage?.TotalTokens);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GeminiUsesOneNormalizationForRunningAndFinalUsage(bool enabled)
    {
        string wire = Sse(
            """{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":0,"totalTokenCount":10}}""",
            """{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":0,"totalTokenCount":10}}""",
            """{"candidates":[{"content":{"parts":[{"text":"hello"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":2,"thoughtsTokenCount":3,"cachedContentTokenCount":4,"totalTokenCount":15}}""");
        using var http = new HttpClient(new Handler(wire));
        var connection = Connection(LlmTransport.GoogleGeminiApi) with
        { Endpoint = GeminiApiLlmClient.DefaultEndpoint, CapabilityOverrides = new() { SupportsUsageDeltas = enabled } };
        List<LlmStreamEvent> events = await Read(new GeminiApiLlmClient(http).StreamAsync(connection, Request("gemini-3-flash-preview")));
        LlmUsage expected = new(10, 5, 15) { ReasoningOutputTokens = 3, CachedInputTokens = 4 };
        Assert.Equal(expected, Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result.Usage);
        LlmUsageDeltaEvent[] usage = [.. events.OfType<LlmUsageDeltaEvent>()];
        Assert.Equal(enabled ? 2 : 0, usage.Length);
        if (enabled)
        {
            Assert.Equal(new(10, 0, 10), usage[0].Cumulative);
            Assert.Equal(expected, usage[1].Cumulative);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompatibleContinuousUsageIsExplicitlyOptedIn(bool enabled)
    {
        string wire = Sse(
            """{"choices":[],"usage":{"prompt_tokens":8,"completion_tokens":0,"total_tokens":8}}""",
            """{"choices":[],"usage":{"prompt_tokens":8,"completion_tokens":0,"total_tokens":8}}""",
            """{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":8,"completion_tokens":2,"total_tokens":10}}""",
            "[DONE]");
        var handler = new Handler(wire);
        using var http = new HttpClient(handler);
        var connection = Connection(LlmTransport.OpenAiCompatibleChat) with
        { CapabilityOverrides = enabled ? new() { SupportsUsageDeltas = true } : null };
        List<LlmStreamEvent> events = await Read(new OpenAiCompatibleChatLlmClient(http).StreamAsync(connection, Request("model")));
        Assert.Equal(enabled ? 2 : 0, events.OfType<LlmUsageDeltaEvent>().Count());
        Assert.Equal(10, Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result.Usage?.TotalTokens);
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        JsonElement options = body.RootElement.GetProperty("stream_options");
        Assert.True(options.GetProperty("include_usage").GetBoolean());
        Assert.Equal(enabled, options.TryGetProperty("continuous_usage_stats", out _));
    }

    [Theory]
    [InlineData(LlmTransport.AnthropicMessages, "claude-sonnet-4-6", """{"type":"message","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":1}}""")]
    [InlineData(LlmTransport.GoogleGeminiApi, "gemini-3-flash-preview", """{"candidates":[{"content":{"parts":[{"text":"ok"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":1,"totalTokenCount":3}}""")]
    [InlineData(LlmTransport.OpenAiCompatibleChat, "model", """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}""")]
    public async Task NonStreamingHasOnlyFinalUsage(LlmTransport transport, string model, string reply)
    {
        var handler = new Handler(reply, "application/json");
        using var http = new HttpClient(handler);
        ILlmClient client = transport switch
        {
            LlmTransport.AnthropicMessages => new AnthropicMessagesLlmClient(http),
            LlmTransport.GoogleGeminiApi => new GeminiApiLlmClient(http),
            _ => new OpenAiCompatibleChatLlmClient(http)
        };
        var connection = Connection(transport) with
        {
            Endpoint = transport == LlmTransport.GoogleGeminiApi ? GeminiApiLlmClient.DefaultEndpoint : "https://provider.test/v1",
            CapabilityOverrides = new() { SupportsStreaming = false, SupportsUsageDeltas = true }
        };
        List<LlmStreamEvent> events = await Read(client.StreamAsync(connection, Request(model)));
        Assert.Empty(events.OfType<LlmUsageDeltaEvent>());
        Assert.Equal(3, Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result.Usage?.TotalTokens);
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public async Task CancellationAfterUsageDoesNotSynthesizeACompletion()
    {
        using var http = new HttpClient(new Handler(Sse(
            """{"type":"message_start","message":{"usage":{"input_tokens":7}}}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}""",
            """{"type":"message_stop"}""")));
        using var cancellation = new CancellationTokenSource();
        await using var iterator = new AnthropicMessagesLlmClient(http).StreamAsync(
            Connection(LlmTransport.AnthropicMessages), Request("claude-sonnet-4-6"), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.IsType<LlmUsageDeltaEvent>(iterator.Current);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task ErrorAfterUsageDoesNotSynthesizeACompletion()
    {
        using var http = new HttpClient(new Handler(Sse(
            """{"type":"message_start","message":{"usage":{"input_tokens":7}}}""",
            """{"type":"error","error":{"type":"overloaded_error","message":"busy"}}""")));
        var seen = new List<LlmStreamEvent>();
        await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            await foreach (var item in new AnthropicMessagesLlmClient(http).StreamAsync(
                Connection(LlmTransport.AnthropicMessages), Request("claude-sonnet-4-6")))
            { seen.Add(item); }
        });
        Assert.IsType<LlmUsageDeltaEvent>(Assert.Single(seen));
    }

    private static LlmConnection Connection(LlmTransport transport) => new()
    {
        Version = "1",
        Transport = transport,
        Endpoint = "https://provider.test/v1",
        Credential = new LlmCredential { ApiKey = "fixture-key" }.Serialize()
    };
    private static LlmChatRequest Request(string model) => new() { ModelId = model, Messages = [new("user", "hello")] };
    private static string Sse(params string[] events) => string.Concat(events.Select(value => "data: " + value + "\n\n"));
    private static async Task<List<LlmStreamEvent>> Read(IAsyncEnumerable<LlmStreamEvent> source)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (var item in source) { events.Add(item); }
        return events;
    }

    private sealed class Handler(string response, string mediaType = "text/event-stream") : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, mediaType) };
        }
    }
}
