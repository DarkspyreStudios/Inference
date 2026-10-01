using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class PromptCacheTests
{
    private const string Claude = "claude-sonnet-4-6";
    private static LlmChatRequest Request(string model = Claude) => new()
    { ModelId = model, Messages = [new("user", "hello")], Instructions = "instructions" };
    private static LlmToolDefinition Tool(string name) => new()
    { Name = name, InputSchema = JsonElement.Parse("""{"type":"object"}""") };
    private static LlmConnection Connection(LlmTransport transport, bool streaming) => new()
    {
        Version = "1",
        Transport = transport,
        Endpoint = "https://provider.invalid",
        Credential = new LlmCredential { ApiKey = "fixture-key" }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };
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
    public async Task AnthropicMarkersUseTheSameGenerationAndCountPayload(bool streaming, LlmPromptCacheScope scope,
        LlmPromptCacheRetention retention)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection(client.Transport, streaming);
        var request = Request() with
        {
            Tools = [Tool("first"), Tool("last")],
            Messages = [new("system", "first"), new("developer", "last"), new("system", ""), new("user", "hello")],
            PromptCache = new() { Scope = scope, Retention = retention }
        };
        var result = await Complete(client, connection, request);
        var count = await client.CountInputTokensAsync(connection, request);
        JsonNode generated = JsonNode.Parse(handler.Bodies[0])!;
        JsonNode counted = JsonNode.Parse(handler.Bodies[1])!;
        AssertMarker(generated["system"]![2]!["cache_control"], retention);
        Assert.Null(generated["system"]![0]!["cache_control"]);
        Assert.Null(generated["system"]![1]!["cache_control"]);
        Assert.Null(generated["system"]![3]!["cache_control"]);
        Assert.All(generated["tools"]!.AsArray(), tool => Assert.Null(tool!["cache_control"]));
        Assert.All(generated["messages"]!.AsArray(), message =>
            Assert.All(message!["content"]!.AsArray(), block => Assert.Null(block!["cache_control"])));
        if (scope == LlmPromptCacheScope.Conversation) AssertMarker(generated["cache_control"], retention);
        else Assert.Null(generated["cache_control"]);
        Assert.True(JsonNode.DeepEquals(generated["system"], counted["system"]));
        Assert.True(JsonNode.DeepEquals(generated["tools"], counted["tools"]));
        Assert.True(JsonNode.DeepEquals(generated["messages"], counted["messages"]));
        Assert.True(JsonNode.DeepEquals(generated["cache_control"], counted["cache_control"]));
        Assert.Null(counted["max_tokens"]);
        Assert.Null(counted["stream"]);
        Assert.Equal(37, count.InputTokens);
        Assert.Null(count.CachedInputTokens);
        Assert.Null(result.Usage);
        var capabilities = client.GetCapabilities(connection, Claude).PromptCache;
        Assert.False(capabilities.Automatic);
        Assert.True(capabilities.AcceptsHints);
        Assert.Equal(2, capabilities.Retentions.Count);
        Assert.Equal("last", request.Messages[1].Content);
        Assert.Empty(request.ProviderOptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnthropicEmptySystemFallsBackToTheLastClientOrNativeTool(bool native)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var request = Request() with
        {
            Instructions = null,
            Messages = [new("system", ""), new("user", "hello")],
            Tools = [Tool("first"), Tool("last")],
            PromptCache = new() { Scope = LlmPromptCacheScope.Prefix },
            ProviderOptions = native ? Options(AnthropicTools.OptionName,
                """{"server_tools":[{"type":"web_search_20250305","name":"web_search"}]}""") : new Dictionary<string, JsonElement>()
        };
        await Complete(new AnthropicMessagesLlmClient(http), Connection(LlmTransport.AnthropicMessages, false), request);
        JsonNode payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Null(payload["system"]![0]!["cache_control"]);
        AssertMarker(payload["tools"]![native ? 2 : 1]!["cache_control"], LlmPromptCacheRetention.Default);
        Assert.Null(payload["tools"]![0]!["cache_control"]);
        Assert.Null(payload["cache_control"]);
        Assert.DoesNotContain("cache_control", native ? request.ProviderOptions[AnthropicTools.OptionName].GetRawText() : "");
    }

    [Theory]
    [InlineData(LlmPromptCacheScope.Prefix)]
    [InlineData(LlmPromptCacheScope.Conversation)]
    public async Task AnthropicWithoutAPrefixNeverEditsTheMessageBlocks(LlmPromptCacheScope scope)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Complete(new AnthropicMessagesLlmClient(http), Connection(LlmTransport.AnthropicMessages, false), Request() with
        { Instructions = null, PromptCache = new() { Scope = scope } });
        JsonNode payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Null(payload["system"]);
        Assert.Null(payload["tools"]);
        Assert.Null(payload["messages"]![0]!["content"]![0]!["cache_control"]);
        Assert.Equal(scope == LlmPromptCacheScope.Conversation, payload["cache_control"] is not null);
    }

    [Theory]
    [MemberData(nameof(Mappings))]
    public async Task AnthropicHintCollidesWithRawCacheOptionsInGenerationAndCount(bool connectionLevel,
        LlmPromptCacheScope scope, LlmPromptCacheRetention retention)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var options = Options("cache_control", """{"type":"ephemeral"}""");
        var connection = Connection(client.Transport, false) with
        { ProviderOptions = connectionLevel ? options : new Dictionary<string, JsonElement>() };
        var request = Request() with
        {
            PromptCache = new() { Scope = scope, Retention = retention },
            ProviderOptions = connectionLevel ? new Dictionary<string, JsonElement>() : options
        };
        Assert.Equal("Provider option 'cache_control' cannot replace a standard request field.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(client, connection, request))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CountInputTokensAsync(connection, request));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task AnthropicNoHintPreservesExistingRawCachePolicyAndOtherwiseAddsNothing()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection(client.Transport, false);
        await Complete(client, connection, Request() with { Tools = [Tool("run")] });
        Assert.DoesNotContain("cache_control", handler.Bodies[0]);
        var request = Request() with { ProviderOptions = Options("cache_control", """{"type":"ephemeral","ttl":"1h"}""") };
        await Complete(client, connection, request);
        await client.CountInputTokensAsync(connection, request);
        AssertMarker(JsonNode.Parse(handler.Bodies[1])!["cache_control"], LlmPromptCacheRetention.Extended);
        AssertMarker(JsonNode.Parse(handler.Bodies[2])!["cache_control"], LlmPromptCacheRetention.Extended);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnthropicSignedToolContinuationRemainsUntouchedAcrossGenerationAndCount(bool streaming)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection(client.Transport, streaming);
        const string content = """
            [{"type":"thinking","thinking":"retained","signature":"opaque-signature"},
             {"type":"redacted_thinking","data":"opaque-redacted"},
             {"type":"tool_use","id":"call","name":"run","input":{}}]
            """;
        var state = ProviderContinuationStateCodec.Encode(connection, Claude, JsonNode.Parse(content)!.AsArray());
        var request = Request() with
        {
            Tools = [Tool("run")],
            PromptCache = new() { Retention = LlmPromptCacheRetention.Extended },
            Messages = [new("user", "first"), new("assistant", "")
                { ContinuationState = state, ToolCalls = [new("call", "run", "{}")] },
                new("tool", "") { ToolResults = [new("call", LlmToolOutput.FromText("result"))] }]
        };
        await Complete(client, connection, request);
        await client.CountInputTokensAsync(connection, request);
        foreach (string body in handler.Bodies)
        {
            JsonNode payload = JsonNode.Parse(body)!;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(content), payload["messages"]![1]!["content"]));
            AssertMarker(payload["cache_control"], LlmPromptCacheRetention.Extended);
            AssertMarker(payload["system"]![0]!["cache_control"], LlmPromptCacheRetention.Extended);
        }
        Assert.Equal(state.Payload, request.Messages[1].ContinuationState!.Payload);
    }

    [Theory]
    [MemberData(nameof(Mappings))]
    public async Task ResponsesOnlyExtendedAddsOptionsAndKeepsConversationAffinity(bool streaming, LlmPromptCacheScope scope,
        LlmPromptCacheRetention retention)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(http);
        var connection = Connection(client.Transport, streaming);
        var request = Request("gpt-5.6") with { ConversationKey = "conversation", PromptCache = new() { Scope = scope, Retention = retention } };
        var baseline = await Complete(client, connection, request with { PromptCache = null });
        var hinted = await Complete(client, connection, request);
        JsonObject normal = JsonNode.Parse(handler.Bodies[0])!.AsObject();
        JsonObject payload = JsonNode.Parse(handler.Bodies[1])!.AsObject();
        Assert.Equal("conversation", (string?)payload["prompt_cache_key"]);
        if (retention == LlmPromptCacheRetention.Extended)
        {
            Assert.Equal("30m", (string?)payload["prompt_cache_options"]!["ttl"]);
            Assert.Single(payload["prompt_cache_options"]!.AsObject());
            payload.Remove("prompt_cache_options");
        }
        else Assert.Null(payload["prompt_cache_options"]);
        Assert.True(JsonNode.DeepEquals(normal, payload));
        Assert.Null(baseline.Usage);
        Assert.Null(hinted.Usage);
    }

    [Theory]
    [InlineData("gpt-5.6", true)]
    [InlineData("gpt-6.1-sol", true)]
    [InlineData("gpt-6-astra", true)]
    [InlineData("gpt-6-luna", true)]
    [InlineData("gpt-5.5", false)]
    [InlineData("gpt-4.1", false)]
    [InlineData("gpt-4o", false)]
    [InlineData("unknown", false)]
    public async Task ResponsesHintSupportComesFromTheVerifiedModelProfile(string model, bool acceptsHints)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(http);
        var connection = Connection(client.Transport, false);
        await Complete(client, connection, Request(model) with { PromptCache = new() { Retention = LlmPromptCacheRetention.Extended } });
        JsonNode payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Equal(acceptsHints, payload["prompt_cache_options"] is not null);
        var cache = client.GetCapabilities(connection, model).PromptCache;
        Assert.True(cache.Automatic);
        Assert.Equal(acceptsHints, cache.AcceptsHints);
        Assert.Equal(acceptsHints ? 2 : 0, cache.Retentions.Count);
    }

    [Theory]
    [MemberData(nameof(Mappings))]
    public async Task ResponsesHandlesRawConflictsEvenForDefaultRetention(bool connectionLevel, LlmPromptCacheScope scope,
        LlmPromptCacheRetention retention)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var options = Options("prompt_cache_options", """{"ttl":"30m"}""");
        var connection = Connection(LlmTransport.OpenAiResponses, false) with
        { ProviderOptions = connectionLevel ? options : new Dictionary<string, JsonElement>() };
        var request = Request("gpt-5.6") with
        { PromptCache = new() { Scope = scope, Retention = retention }, ProviderOptions = connectionLevel ? new Dictionary<string, JsonElement>() : options };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(new OpenAiResponsesLlmClient(http), connection, request));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData("gpt-5.6", false)]
    [InlineData("unknown", true)]
    public async Task ResponsesNoHintOrUnsupportedHintPreservesRawOptions(string model, bool hint)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Complete(new OpenAiResponsesLlmClient(http), Connection(LlmTransport.OpenAiResponses, false), Request(model) with
        {
            PromptCache = hint ? new() : null,
            ProviderOptions = Options("prompt_cache_options", """{"ttl":"30m"}""")
        });
        Assert.Equal("30m", (string?)JsonNode.Parse(Assert.Single(handler.Bodies))!["prompt_cache_options"]!["ttl"]);
    }

    [Fact]
    public async Task CodexIgnoresTheHintWithoutLosingItsOwnResponsesProjection()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        var connection = Connection(client.Transport, true) with
        { Credential = new LlmCredential { OAuth = new() { AccessToken = "fixture-token", AccountId = "account" } }.Serialize() };
        var request = Request("gpt-5.6") with { ConversationKey = "conversation" };
        await Complete(client, connection, request);
        await Complete(client, connection, request with { PromptCache = new() { Retention = LlmPromptCacheRetention.Extended } });
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.DoesNotContain("prompt_cache_options", handler.Bodies[1]);
        AssertAutomaticOnly(client.GetCapabilities(connection, request.ModelId).PromptCache);
        AssertAutomaticOnly(ClaudeCliLlmClient.ResolveCapabilities(new()
        { Version = "1", Transport = LlmTransport.AnthropicClaudeCli, Endpoint = "claude" }, Claude).PromptCache);
    }

    [Fact]
    public void InheritedResponsesBuildersDoNotApplyOpenAiOptionsToOtherTransports()
    {
        using var http = new HttpClient();
        var client = new ForeignResponses(http);
        var request = Request("gpt-5.6") with { PromptCache = new() { Retention = LlmPromptCacheRetention.Extended } };
        JsonObject payload = client.Build(Connection(client.Transport, true), request);
        Assert.Null(payload["prompt_cache_options"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponsesEncryptedContinuationIsNotMarkedOrRewritten(bool streaming)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(http);
        var connection = Connection(client.Transport, streaming);
        const string content = """[{"type":"reasoning","id":"reasoning","encrypted_content":"opaque","summary":[]}]""";
        var state = ProviderContinuationStateCodec.Encode(connection, "gpt-5.6", JsonNode.Parse(content)!.AsArray());
        var request = Request("gpt-5.6") with
        {
            PromptCache = new() { Retention = LlmPromptCacheRetention.Extended },
            Messages = [new("user", "first"), new("assistant", "") { ContinuationState = state }, new("user", "continue")]
        };
        await Complete(client, connection, request);
        JsonNode payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(content)![0], payload["input"]![1]));
        Assert.Equal(state.Payload, request.Messages[1].ContinuationState!.Payload);
        Assert.Equal("30m", (string?)payload["prompt_cache_options"]!["ttl"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClaudeCliHintDoesNotChangeArgumentsInputOrReportedUsage(bool streaming)
    {
        var runner = new Runner();
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new ClaudeCliLlmClient(http, runner);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = client.Transport,
            Endpoint = "claude",
            CapabilityOverrides = new() { SupportsStreaming = streaming, SupportsReasoningText = false }
        };
        var request = Request();
        var baseline = await Complete(client, connection, request);
        var hinted = await Complete(client, connection, request with { PromptCache = new() { Retention = LlmPromptCacheRetention.Extended } });
        Assert.Equal(runner.Invocations[0].Arguments, runner.Invocations[1].Arguments);
        Assert.Equal(runner.Invocations[0].StandardInput, runner.Invocations[1].StandardInput);
        Assert.Equal(runner.Invocations[0].Executable, runner.Invocations[1].Executable);
        Assert.Equal(baseline.Usage, hinted.Usage);
        Assert.Null(hinted.Usage!.CachedInputTokens);
        Assert.Null(hinted.Usage.CacheWriteInputTokens);
        Assert.Empty(handler.Bodies);
        Assert.All(runner.Invocations, invocation => Assert.False(Directory.Exists(invocation.WorkingDirectory)));
        AssertAutomaticOnly(client.GetCapabilities(connection, Claude).PromptCache);
    }

    [Theory]
    [InlineData(LlmTransport.AnthropicMessages)]
    [InlineData(LlmTransport.OpenAiResponses)]
    public async Task ProviderMarkerRejectionSurfacesWithoutRetryOrFallback(LlmTransport transport)
    {
        using var handler = new Handler { Status = HttpStatusCode.BadRequest };
        using var http = new HttpClient(handler);
        LlmClientBase client = transport == LlmTransport.AnthropicMessages ? new AnthropicMessagesLlmClient(http) : new OpenAiResponsesLlmClient(http);
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Complete(client, Connection(transport, false),
            Request(transport == LlmTransport.AnthropicMessages ? Claude : "gpt-5.6") with
            { PromptCache = new() { Retention = LlmPromptCacheRetention.Extended } }));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task CancelledAnthropicCountingDoesNotDispatchOrMutateTheHint()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var request = Request() with { PromptCache = new() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnthropicMessagesLlmClient(http)
            .CountInputTokensAsync(Connection(LlmTransport.AnthropicMessages, false), request, cancellation.Token));
        Assert.Empty(handler.Bodies);
        Assert.Equal(LlmPromptCacheScope.Conversation, request.PromptCache!.Scope);
    }

    private static void AssertAutomaticOnly(LlmPromptCacheCapabilities cache)
    { Assert.True(cache.Automatic); Assert.False(cache.AcceptsHints); Assert.Empty(cache.Retentions); }
    private static void AssertMarker(JsonNode? marker, LlmPromptCacheRetention retention)
    {
        Assert.Equal("ephemeral", (string?)marker!["type"]);
        Assert.Equal(retention == LlmPromptCacheRetention.Extended ? "1h" : null, (string?)marker["ttl"]);
        Assert.Equal(retention == LlmPromptCacheRetention.Extended ? 2 : 1, marker.AsObject().Count);
    }
    private static Dictionary<string, JsonElement> Options(string field, string value) => new() { [field] = JsonElement.Parse(value) };
    private static async Task<LlmChatResult> Complete(LlmClientBase client, LlmConnection connection, LlmChatRequest request)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (var item in client.StreamAsync(connection, request)) events.Add(item);
        return Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
    }
    private sealed class ForeignResponses(HttpClient http) : ResponsesLlmClientBase(http)
    {
        public override LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;
        public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;
        protected override LlmProviderCapabilities DefaultCapabilities => new();
        internal JsonObject Build(LlmConnection connection, LlmChatRequest request) => BuildPayload(connection, request, null, true);
    }
    private sealed class Runner : IClaudeCliRunner
    {
        internal List<ClaudeCliInvocation> Invocations { get; } = [];
        public Task<ClaudeCliProcessResult> RunAsync(ClaudeCliInvocation invocation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(invocation);
            return Task.FromResult(new ClaudeCliProcessResult(0,
                """{"type":"result","subtype":"success","is_error":false,"result":"done","usage":{"input_tokens":1,"output_tokens":1}}""", ""));
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            if (Status != HttpStatusCode.OK) return new(Status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (request.RequestUri!.AbsolutePath.EndsWith("count_tokens", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"input_tokens":37}""", Encoding.UTF8, "application/json") };
            var payload = JsonElement.Parse(body);
            bool streaming = payload.GetProperty("stream").GetBoolean();
            bool anthropic = payload.TryGetProperty("messages", out _);
            string response = anthropic
                ? """{"id":"m","type":"message","content":[],"stop_reason":"end_turn"}"""
                : """{"id":"r","status":"completed","output":[]}""";
            if (streaming) response = anthropic
                ? "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n"
                : "data: {\"type\":\"response.completed\",\"response\":" + response + "}\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
