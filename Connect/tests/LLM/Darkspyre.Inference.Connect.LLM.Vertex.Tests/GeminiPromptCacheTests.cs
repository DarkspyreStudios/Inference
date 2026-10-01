using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class GeminiPromptCacheTests
{
    [Theory]
    [InlineData(false, false, LlmPromptCacheScope.Prefix)]
    [InlineData(false, true, LlmPromptCacheScope.Prefix)]
    [InlineData(false, false, LlmPromptCacheScope.Conversation)]
    [InlineData(false, true, LlmPromptCacheScope.Conversation)]
    [InlineData(true, false, LlmPromptCacheScope.Prefix)]
    [InlineData(true, true, LlmPromptCacheScope.Prefix)]
    [InlineData(true, false, LlmPromptCacheScope.Conversation)]
    [InlineData(true, true, LlmPromptCacheScope.Conversation)]
    public async Task ImplicitOnlyRoutesIgnoreHintsInGenerationAndExactCounting(bool vertex, bool streaming, LlmPromptCacheScope scope)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        ILlmClient client = vertex ? new VertexLlmClient(http, new Authentication()) : new GeminiApiLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = vertex ? LlmTransport.GoogleVertexAi : LlmTransport.GoogleGeminiApi,
            Endpoint = vertex ? "https://aiplatform.googleapis.com" : GeminiApiLlmClient.DefaultEndpoint,
            Credential = vertex ? null : new LlmCredential { ApiKey = "test-key" }.Serialize(),
            Vertex = vertex ? new() { ProjectId = "project", Location = "global" } : null,
            CapabilityOverrides = new() { SupportsStreaming = streaming }
        };
        var request = new LlmChatRequest
        {
            ModelId = vertex ? "google/gemini-3-flash-preview" : "gemini-3-flash-preview",
            Instructions = "prefix",
            Messages = [new("user", "hello")],
            Tools = [new() { Name = "run", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }]
        };
        LlmPromptCacheCapabilities capabilities = client.GetCapabilities(connection, request.ModelId).PromptCache;
        Assert.True(capabilities.Automatic);
        Assert.False(capabilities.AcceptsHints);
        Assert.Empty(capabilities.Retentions);
        LlmChatResult plain = await Complete(client, connection, request);
        LlmChatRequest hinted = request with { PromptCache = new() { Scope = scope, Retention = LlmPromptCacheRetention.Extended } };
        LlmChatResult cached = await Complete(client, connection, hinted);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.Equal("done", cached.Text);
        Assert.Equal(plain.Usage, cached.Usage);
        Assert.Null(cached.Usage!.CachedInputTokens);
        Assert.Null(cached.Usage.CacheWriteInputTokens);
        var counter = (ILlmProviderTokenCounter)client;
        Assert.Equal(4, (await counter.CountInputTokensAsync(connection, request)).InputTokens);
        Assert.Equal(4, (await counter.CountInputTokensAsync(connection, hinted)).InputTokens);
        Assert.Equal(handler.Bodies[2], handler.Bodies[3]);
        Assert.DoesNotContain("cachedContent", handler.Bodies[1], StringComparison.Ordinal);
    }

    private static async Task<LlmChatResult> Complete(ILlmClient client, LlmConnection connection, LlmChatRequest request)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) { events.Add(item); }
        return Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
    }

    private sealed class Authentication : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new VertexAccess("test-token", "stable-principal"));
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            bool count = request.RequestUri!.AbsolutePath.EndsWith(":countTokens", StringComparison.Ordinal);
            bool streaming = request.RequestUri.AbsolutePath.EndsWith(":streamGenerateContent", StringComparison.Ordinal);
            const string reply = """{"candidates":[{"content":{"role":"model","parts":[{"text":"done"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1,"totalTokenCount":2}}""";
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(count ? "{\"totalTokens\":4}" : streaming ? "data: " + reply + "\n\n" : reply,
                    Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            };
        }
    }
}
