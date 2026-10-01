using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexPromptCacheTests
{
    private const string Claude = "anthropic/claude-sonnet-4-6";
    private static LlmConnection Connection(bool streaming) => new()
    {
        Version = "1",
        Transport = LlmTransport.GoogleVertexAi,
        Endpoint = "https://aiplatform.googleapis.com",
        Vertex = new() { ProjectId = "project", Location = "global" },
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };
    private static LlmChatRequest Request => new() { ModelId = Claude, Messages = [new("user", "hello")] };
    private static readonly bool[] Modes = [true, false];
    public static TheoryData<bool, LlmPromptCacheScope, LlmPromptCacheRetention> Mappings
    {
        get
        {
            var data = new TheoryData<bool, LlmPromptCacheScope, LlmPromptCacheRetention>();
            foreach (bool stream in Modes)
                foreach (var scope in Enum.GetValues<LlmPromptCacheScope>())
                    foreach (var retention in Enum.GetValues<LlmPromptCacheRetention>())
                        data.Add(stream, scope, retention);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Mappings))]
    public async Task ClaudeGenerationAndCountShareHintMarkersAndRetention(bool streaming, LlmPromptCacheScope scope,
        LlmPromptCacheRetention retention)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var authentication = new Authentication();
        var client = new VertexLlmClient(http, authentication);
        var connection = Connection(streaming);
        var request = Request with
        {
            Instructions = "prefix",
            PromptCache = new() { Scope = scope, Retention = retention },
            Tools = [new() { Name = "run", InputSchema = JsonElement.Parse("""{"type":"object"}""") }]
        };
        LlmPromptCacheCapabilities cache = client.GetCapabilities(connection, Claude).PromptCache;
        Assert.False(cache.Automatic);
        Assert.True(cache.AcceptsHints);
        Assert.Equal(2, cache.Retentions.Count);
        var result = await Complete(client, connection, request);
        var count = await client.CountInputTokensAsync(connection, request);
        JsonNode generated = JsonNode.Parse(handler.Bodies[0])!;
        JsonNode counted = JsonNode.Parse(handler.Bodies[1])!;
        Assert.Equal("ephemeral", (string?)generated["system"]![0]!["cache_control"]!["type"]);
        Assert.Equal(retention == LlmPromptCacheRetention.Extended ? "1h" : null,
            (string?)generated["system"]![0]!["cache_control"]!["ttl"]);
        Assert.Null(generated["tools"]![0]!["cache_control"]);
        Assert.Equal(scope == LlmPromptCacheScope.Conversation, generated["cache_control"] is not null);
        foreach (string field in new[] { "system", "tools", "messages", "cache_control" })
            Assert.True(JsonNode.DeepEquals(generated[field], counted[field]));
        Assert.Null(generated["model"]);
        Assert.Equal("claude-sonnet-4-6", (string?)counted["model"]);
        Assert.Equal("vertex-2023-10-16", (string?)generated["anthropic_version"]);
        Assert.Null(counted["anthropic_version"]);
        Assert.Null(counted["stream"]);
        Assert.Null(counted["max_tokens"]);
        Assert.Equal(37, count.InputTokens);
        Assert.Null(count.CachedInputTokens);
        Assert.Null(result.Usage);
        Assert.Equal(2, authentication.Calls);
        Assert.All(handler.Authorization, header => Assert.Equal("Bearer test-token", header));
    }

    [Theory]
    [MemberData(nameof(Mappings))]
    public async Task ClaudeHintAndRawCacheOptionsFailBeforeCredentialResolutionAndHttp(bool connectionLevel, LlmPromptCacheScope scope,
        LlmPromptCacheRetention retention)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var authentication = new Authentication();
        var client = new VertexLlmClient(http, authentication);
        var options = new Dictionary<string, JsonElement> { ["cache_control"] = JsonElement.Parse("""{"type":"ephemeral"}""") };
        var connection = Connection(false) with { ProviderOptions = connectionLevel ? options : new Dictionary<string, JsonElement>() };
        var request = Request with
        { PromptCache = new() { Scope = scope, Retention = retention }, ProviderOptions = connectionLevel ? new Dictionary<string, JsonElement>() : options };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(client, connection, request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CountInputTokensAsync(connection, request));
        Assert.Empty(handler.Bodies);
        Assert.Equal(0, authentication.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClaudeSignedContinuationAndToolOnlyPrefixRetainTheirOwnedData(bool streaming)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new VertexLlmClient(http, new Authentication());
        LlmConnection connection = await client.PrepareAsync(Connection(streaming), default);
        const string content = """
            [{"type":"thinking","thinking":"retained","signature":"opaque-signature"},
             {"type":"redacted_thinking","data":"opaque-redacted"},
             {"type":"tool_use","id":"call","name":"run","input":{}}]
            """;
        var state = ProviderContinuationStateCodec.Encode(connection, Claude, JsonNode.Parse(content)!.AsArray());
        var request = Request with
        {
            PromptCache = new() { Retention = LlmPromptCacheRetention.Extended },
            Tools = [new() { Name = "run", InputSchema = JsonElement.Parse("""{"type":"object"}""") }],
            Messages = [new("system", ""), new("user", "hello"), new("assistant", "")
                { ContinuationState = state, ToolCalls = [new("call", "run", "{}")] },
                new("tool", "") { ToolResults = [new("call", LlmToolOutput.FromText("result"))] }]
        };
        await Complete(client, connection, request);
        await client.CountInputTokensAsync(connection, request);
        foreach (string body in handler.Bodies)
        {
            JsonNode payload = JsonNode.Parse(body)!;
            Assert.Null(payload["system"]![0]!["cache_control"]);
            Assert.Equal("1h", (string?)payload["tools"]![0]!["cache_control"]!["ttl"]);
            Assert.Equal("1h", (string?)payload["cache_control"]!["ttl"]);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(content), payload["messages"]![1]!["content"]));
        }
        Assert.Equal(state.Payload, request.Messages[2].ContinuationState!.Payload);
    }

    [Fact]
    public async Task ClaudeWithoutHintsKeepsItsExistingWireAndRawPolicy()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new VertexLlmClient(http, new Authentication());
        var connection = Connection(false);
        await Complete(client, connection, Request);
        Assert.DoesNotContain("cache_control", handler.Bodies[0]);
        await Complete(client, connection, Request with
        { ProviderOptions = new Dictionary<string, JsonElement> { ["cache_control"] = JsonElement.Parse("""{"type":"ephemeral"}""") } });
        Assert.Equal("ephemeral", (string?)JsonNode.Parse(handler.Bodies[1])!["cache_control"]!["type"]);
    }

    private static async Task<LlmChatResult> Complete(VertexLlmClient client, LlmConnection connection, LlmChatRequest request)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (var item in client.StreamAsync(connection, request)) events.Add(item);
        return Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
    }
    private sealed class Authentication : IVertexAuthentication
    {
        internal int Calls { get; private set; }
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new VertexAccess("test-token", "stable-principal"));
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        internal List<string?> Authorization { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            Authorization.Add(request.Headers.Authorization?.ToString());
            bool count = request.RequestUri!.AbsolutePath.Contains("/count-tokens:", StringComparison.Ordinal);
            bool streaming = !count && JsonElement.Parse(body).GetProperty("stream").GetBoolean();
            string response = count ? """{"input_tokens":37}""" : streaming
                ? "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n"
                : """{"id":"m","type":"message","content":[],"stop_reason":"end_turn"}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
