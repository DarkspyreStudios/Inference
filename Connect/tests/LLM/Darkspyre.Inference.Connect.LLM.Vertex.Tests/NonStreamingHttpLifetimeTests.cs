using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class NonStreamingHttpLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task CompletedResponseReleasesRequestAndBodyAndHasOneTerminalEvent(int route)
    {
        using var content = new BodyContent(Reply(route));
        using var handler = new Handler(content);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var events = new List<LlmStreamEvent>();

        await ConsumeAsync(Client(route, http).StreamAsync(Connection(route), Request(route)), events).WaitAsync(Bound);

        Assert.Equal("ready", Assert.Single(events.OfType<LlmTextDeltaEvent>()).Delta);
        Assert.IsType<LlmChatCompletionEvent>(events[^1]);
        Assert.Single(events.OfType<LlmChatCompletionEvent>());
        await AssertReleasedAsync(handler, content);
        AssertNonStreamingRoute(route, handler);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task AbandoningMappedEventsReleasesTheNonStreamingRequestAndBody(int route)
    {
        using var content = new BodyContent(Reply(route));
        using var handler = new Handler(content);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        IAsyncEnumerator<LlmStreamEvent> enumerator = Client(route, http)
            .StreamAsync(Connection(route), Request(route)).GetAsyncEnumerator();
        try
        {
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(Bound));
            Assert.IsType<LlmTextDeltaEvent>(enumerator.Current);
            Assert.False(content.Disposed);
        }
        finally
        {
            await enumerator.DisposeAsync().AsTask().WaitAsync(Bound);
        }

        await AssertReleasedAsync(handler, content);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task CancellationDuringBodyReadReleasesOwnershipWithoutEmittingCompletion(int route)
    {
        using var content = new BodyContent(Reply(route), block: true);
        using var handler = new Handler(content);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cancellation = new CancellationTokenSource();
        var events = new List<LlmStreamEvent>();
        Task reading = ConsumeAsync(Client(route, http).StreamAsync(Connection(route), Request(route), cancellation.Token), events);
        try
        {
            await content.Started.Task.WaitAsync(Bound);
            Assert.False(reading.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(Bound));
            Assert.True(content.ReadToken.IsCancellationRequested);
            Assert.Empty(events);
            await AssertReleasedAsync(handler, content);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await reading.WaitAsync(Bound); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task MalformedBodyReleasesOwnershipWithoutFabricatingCompletion(int route)
    {
        using var content = new BodyContent("{malformed");
        using var handler = new Handler(content);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var events = new List<LlmStreamEvent>();

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            ConsumeAsync(Client(route, http).StreamAsync(Connection(route), Request(route)), events).WaitAsync(Bound));

        Assert.True(failure is LlmProviderException or JsonException, failure.ToString());
        Assert.Empty(events);
        await AssertReleasedAsync(handler, content);
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<LlmStreamEvent> source, List<LlmStreamEvent> events)
    {
        await foreach (LlmStreamEvent item in source) events.Add(item);
    }

    private static async Task AssertReleasedAsync(Handler handler, BodyContent content)
    {
        Assert.True(content.Disposed);
        HttpContent requestContent = Assert.IsAssignableFrom<HttpContent>(handler.RequestContent);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => requestContent.ReadAsStringAsync());
        Assert.Equal(1, handler.Calls);
    }

    private static void AssertNonStreamingRoute(int route, Handler handler)
    {
        if (route is 3 or 4)
        {
            Assert.EndsWith(":generateContent", handler.Uri!.AbsolutePath, StringComparison.Ordinal);
            Assert.DoesNotContain("alt=sse", handler.Uri.Query, StringComparison.Ordinal);
        }
        else
        {
            using JsonDocument sent = JsonDocument.Parse(handler.Body!);
            Assert.False(sent.RootElement.GetProperty("stream").GetBoolean());
            if (route == 1) Assert.False(sent.RootElement.TryGetProperty("stream_options", out _));
            if (route == 5) Assert.EndsWith(":rawPredict", handler.Uri!.AbsolutePath, StringComparison.Ordinal);
        }
    }

    private static ILlmClient Client(int route, HttpClient http) => route switch
    {
        0 => new OpenAiResponsesLlmClient(http),
        1 => new OpenAiCompatibleChatLlmClient(http),
        2 => new AnthropicMessagesLlmClient(http),
        3 => new GeminiApiLlmClient(http),
        _ => new VertexLlmClient(http, new Authentication())
    };

    private static LlmConnection Connection(int route) => new()
    {
        Version = "v1",
        Transport = route switch
        {
            0 => LlmTransport.OpenAiResponses,
            1 => LlmTransport.OpenAiCompatibleChat,
            2 => LlmTransport.AnthropicMessages,
            3 => LlmTransport.GoogleGeminiApi,
            _ => LlmTransport.GoogleVertexAi
        },
        Endpoint = route switch
        {
            3 => GeminiApiLlmClient.DefaultEndpoint,
            4 or 5 => VertexEndpoints.BaseEndpoint,
            _ => "https://provider.test/v1"
        },
        Credential = route is 4 or 5 ? null : new LlmCredential { ApiKey = "controlled-key" }.Serialize(),
        Vertex = route is 4 or 5 ? new() { ProjectId = "project", Location = "global" } : null,
        CapabilityOverrides = new() { SupportsStreaming = false }
    };

    private static LlmChatRequest Request(int route) => new()
    {
        ModelId = route switch
        {
            0 => "gpt-test",
            2 => "claude-sonnet-4-6",
            3 => "gemini-3-flash-preview",
            4 => "google/gemini-3-flash-preview",
            5 => "anthropic/claude-sonnet-4-6",
            _ => "model"
        },
        Messages = [new("user", "hello")]
    };

    private static string Reply(int route) => route switch
    {
        0 => """{"id":"response","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ready"}]}]}""",
        1 => """{"choices":[{"index":0,"message":{"role":"assistant","content":"ready"},"finish_reason":"stop"}]}""",
        2 or 5 => """{"id":"message","type":"message","role":"assistant","content":[{"type":"text","text":"ready"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""",
        _ => """{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"ready"}]},"finishReason":"STOP"}]}"""
    };

    private sealed class Authentication : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new VertexAccess("controlled-token", "controlled-principal"));
        }
    }

    private sealed class Handler(BodyContent content) : HttpMessageHandler
    {
        public HttpContent? RequestContent { get; private set; }
        public string? Body { get; private set; }
        public Uri? Uri { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestContent = request.Content;
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class BodyContent(string body, bool block = false) : HttpContent
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(body);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReadToken { get; private set; }
        public bool Disposed { get; private set; }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return !block;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
            ReadToken = cancellationToken;
            Started.TrySetResult();
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await stream.WriteAsync(_bytes, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
