using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AnthropicTurnTests
{
    private const string Model = "claude-sonnet-4-6";
    private const string Content = """
        [{"type":"thinking","thinking":"protected-thought","signature":"protected-signature"},
         {"type":"text","text":"Working","citations":[{"type":"web_search_result_location","url":"https://example.invalid","title":"Fixture","cited_text":"source","encrypted_index":"protected-index"}]},
         {"type":"server_tool_use","id":"srv_1","name":"web_search","input":{"query":"fixture"}},
         {"type":"web_search_tool_result","tool_use_id":"srv_1","content":[{"type":"web_search_result","url":"https://example.invalid","title":"Fixture","encrypted_content":"protected-result"}]}]
        """;

    private static LlmConnection Connection(bool streaming = true) => new()
    {
        Version = "1",
        Transport = LlmTransport.AnthropicMessages,
        Endpoint = "https://api.anthropic.invalid",
        Credential = new LlmCredential { ApiKey = "fixture-key" }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };

    private static LlmChatRequest Request => new() { ModelId = Model, Messages = [new("user", "start")] };
    private static LlmToolDefinition Tool => new() { Name = "run", InputSchema = JsonElement.Parse("""{"type":"object"}""") };
    private const string MixedContent = """
        [{"type":"text","text":"Working"},
         {"type":"server_tool_use","id":"srv_code","name":"code_execution","input":{"code":"await run()"}},
         {"type":"tool_use","id":"client_1","name":"run","input":{"name":"fixture"},"caller":{"type":"code_execution_20260120","tool_id":"srv_code"}}]
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContainerIdentitySurvivesPausedCaptureSerializationReplayAndScopeChecks(bool streaming)
    {
        using var handler = new Handler(streaming, "pause_turn")
        {
            InitialContainer = JsonNode.Parse("""{"id":"initial-container"}"""),
            Container = JsonNode.Parse("""{"id":"container-current","expires_at":"2026-10-30T12:00:00Z","discarded":"not-continuation-data"}""")
        };
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection(streaming);
        var events = await Read(client.StreamAsync(connection, Request));
        var result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        LlmContinuationState state = JsonSerializer.Deserialize<LlmContinuationState>(JsonSerializer.Serialize(result.ContinuationState))!;
        Assert.Equal("container-current", AnthropicContinuation.ReadPayload(state.Payload).ContainerId);
        Assert.DoesNotContain("discarded", state.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("expires_at", state.Payload, StringComparison.Ordinal);
        var replay = Request with { Messages = [Request.Messages[0], new("assistant", result.Text) { ContinuationState = state }] };
        await Read(client.StreamAsync(connection, replay));
        Assert.Equal("container-current", (string?)JsonNode.Parse(handler.Bodies[1])!["container"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Content), JsonNode.Parse(handler.Bodies[1])!["messages"]![1]!["content"]));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CountInputTokensAsync(connection, replay));
        Assert.Equal(2, handler.Bodies.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(client.StreamAsync(connection, replay with
        { ProviderOptions = new Dictionary<string, JsonElement> { ["container"] = JsonElement.Parse("\"replacement\"") } })));
        Assert.Equal(2, handler.Bodies.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => Read(client.StreamAsync(connection with
        { Credential = new LlmCredential { ApiKey = "different-account" }.Serialize() }, replay)));
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LastRetainedContainerWinsWithoutMutatingEarlierState(bool streaming)
    {
        using var handler = new Handler(streaming, "end_turn");
        using var http = new HttpClient(handler);
        var connection = Connection(streaming);
        var content = JsonNode.Parse("""[{"type":"text","text":"retained"}]""")!.AsArray();
        var state = ProviderContinuationStateCodec.Encode(connection, Model, content);
        var old = AnthropicContinuation.WithContainer(state, "older-container");
        var latest = AnthropicContinuation.WithContainer(state, "latest-container");
        var request = Request with
        {
            Messages = [Request.Messages[0], new("assistant", "retained") { ContinuationState = old },
            new("user", "next"), new("assistant", "retained") { ContinuationState = latest },
            new("user", "next"), new("assistant", "retained") { ContinuationState = state }, new("user", "continue")]
        };
        await Read(new AnthropicMessagesLlmClient(http).StreamAsync(connection, request));
        Assert.Equal("latest-container", (string?)JsonNode.Parse(Assert.Single(handler.Bodies))!["container"]);
        Assert.Equal("older-container", AnthropicContinuation.ReadPayload(old.Payload).ContainerId);
        Assert.Null(AnthropicContinuation.ReadPayload(state.Payload).ContainerId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpiredContainerFailureDoesNotRetryOrRecreateTheContainer(bool streaming)
    {
        using var handler = new Handler(streaming, "pause_turn")
        { Container = JsonNode.Parse("""{"id":"container-current"}"""), ExpireAfterFirst = true };
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection(streaming);
        var first = Assert.Single((await Read(client.StreamAsync(connection, Request))).OfType<LlmChatCompletionEvent>()).Result;
        var replay = Request with { Messages = [Request.Messages[0], new("assistant", first.Text) { ContinuationState = first.ContinuationState }] };
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Read(client.StreamAsync(connection, replay)));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal("container-current", (string?)JsonNode.Parse(handler.Bodies[1])!["container"]);
        Assert.DoesNotContain("fixture-key", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("expired", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal("container-current", AnthropicContinuation.ReadPayload(first.ContinuationState!.Payload).ContainerId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingStreamContainerUpdateKeepsIdentityAndExplicitNullClearsIt(bool omitDeltaContainer)
    {
        using var handler = new Handler(true, "pause_turn")
        { InitialContainer = JsonNode.Parse("""{"id":"initial-container"}"""), OmitDeltaContainer = omitDeltaContainer };
        using var http = new HttpClient(handler);
        var events = await Read(new AnthropicMessagesLlmClient(http).StreamAsync(Connection(), Request));
        var state = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.ContinuationState!;
        Assert.Equal(omitDeltaContainer ? "initial-container" : null, AnthropicContinuation.ReadPayload(state.Payload).ContainerId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedDeferredServerAndClientCallsReplayWithClientResultsAndContainerOnly(bool streaming)
    {
        using var handler = new Handler(streaming, "tool_use")
        { ContentJson = MixedContent, Container = JsonNode.Parse("""{"id":"container-current"}"""), CompleteAfterFirst = true };
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection(streaming);
        var request = Request with { Tools = [Tool] };
        var events = await Read(client.StreamAsync(connection, request));
        var result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.RequiresAction, result.Status);
        Assert.Equal("client_1", Assert.Single(result.ToolCalls).Id);
        Assert.Equal("client_1", Assert.Single(events.OfType<LlmToolCallStartedEvent>()).ToolCallId);
        Assert.Single(events.OfType<LlmToolCallCompletedEvent>());
        var replay = request with
        {
            Messages = [request.Messages[0], new("assistant", result.Text) { ToolCalls = result.ToolCalls, ContinuationState = result.ContinuationState },
                new("tool", "") { ToolResults = [new("client_1", new() { Parts = [new LlmToolOutputText("client result")] })] }]
        };
        await Read(client.StreamAsync(connection, replay));
        JsonNode payload = JsonNode.Parse(handler.Bodies[1])!;
        Assert.Equal("container-current", (string?)payload["container"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(MixedContent), payload["messages"]![1]!["content"]));
        JsonArray results = payload["messages"]![2]!["content"]!.AsArray();
        Assert.Single(results);
        Assert.Equal("client_1", (string?)results[0]!["tool_use_id"]);
        Assert.Equal("client result", (string?)results[0]!["content"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContradictoryPauseWithPendingClientCallsFailsInsteadOfHidingCallsInState(bool streaming)
    {
        using var handler = new Handler(streaming, "pause_turn") { ContentJson = MixedContent };
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() =>
            Read(client.StreamAsync(Connection(streaming), Request with { Tools = [Tool] })));
        Assert.Contains("pending client tools", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"content\":null}")]
    [InlineData("{\"content\":[],\"container_id\":null}")]
    [InlineData("{\"content\":[],\"container_id\":1}")]
    [InlineData("{\"content\":[],\"container_id\":\" \"}")]
    [InlineData("{\"content\":[],\"unknown\":true}")]
    public void ContinuationRejectsLegacyShapesAndUntrustedMetadata(string payload)
    {
        LlmContinuationState state = ProviderContinuationStateCodec.Encode(Connection(), Model, new JsonArray());
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.Decode(state with { Payload = payload }, Connection(), Model));
    }

    [Theory]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("{\"id\":1}")]
    [InlineData("{}")]
    [InlineData("\"not-container\"")]
    public async Task MalformedNativeContainerIdentityFailsWithoutInventingOne(string container)
    {
        using var handler = new Handler(false, "pause_turn") { Container = JsonNode.Parse(container) };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<LlmProviderException>(() => Read(new AnthropicMessagesLlmClient(http).StreamAsync(Connection(false), Request)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PausedTurnsRetainOrderedStateAndReplayOnceWithoutBecomingToolActions(bool streaming)
    {
        using var handler = new Handler(streaming, "pause_turn");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        LlmConnection connection = Connection(streaming);
        var events = await Read(client.StreamAsync(connection, Request));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Incomplete, result.Status);
        Assert.Equal("pause_turn", result.IncompleteReason);
        Assert.Equal("pause_turn", result.FinishReason);
        Assert.Equal("Working", result.Text);
        Assert.Empty(result.ToolCalls);
        Assert.Empty(events.OfType<LlmToolCallStartedEvent>());
        Assert.Empty(events.OfType<LlmToolCallArgumentsDeltaEvent>());
        Assert.Equal(7, result.Usage!.InputTokens);
        Assert.Equal(4, result.Usage.OutputTokens);
        LlmContinuationState state = Assert.IsType<LlmContinuationState>(result.ContinuationState);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Content), ProviderContinuationStateCodec.Decode(state, connection, Model)));

        var replay = Request with
        {
            Messages = [Request.Messages[0], new("assistant", result.Text) { ContinuationState = state }]
        };
        await Read(client.StreamAsync(connection, replay));
        JsonArray messages = JsonNode.Parse(handler.Bodies[1])!["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Content), messages[1]!["content"]));
        Assert.Equal(state, replay.Messages[1].ContinuationState);

        await Assert.ThrowsAsync<ArgumentException>(() => Read(client.StreamAsync(connection with
        { Credential = new LlmCredential { ApiKey = "other-fixture-key" }.Serialize() }, replay)));
        Assert.Equal(2, handler.Bodies.Count);
        Assert.DoesNotContain("protected", state.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "model_context_window_exceeded")]
    [InlineData(false, "model_context_window_exceeded")]
    [InlineData(true, "max_tokens")]
    [InlineData(false, "max_tokens")]
    [InlineData(true, "future_stop_reason")]
    [InlineData(false, "future_stop_reason")]
    public async Task TruncationAndUnknownReasonsDoNotClaimResumablePausedState(bool streaming, string reason)
    {
        using var handler = new Handler(streaming, reason);
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var events = await Read(client.StreamAsync(Connection(streaming), Request));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        bool truncated = reason != "future_stop_reason";
        Assert.Equal(truncated ? LlmCompletionStatus.Incomplete : LlmCompletionStatus.Unknown, result.Status);
        Assert.Equal(truncated ? reason : null, result.IncompleteReason);
        Assert.Null(result.ContinuationState);
        Assert.Empty(result.ToolCalls);
    }

    [Theory]
    [InlineData("claude-sonnet-4-6", LlmReasoningLevel.High, "temperature")]
    [InlineData("claude-sonnet-4-6", LlmReasoningLevel.High, "required")]
    [InlineData("claude-sonnet-4-6", LlmReasoningLevel.High, "specific")]
    [InlineData("claude-haiku-4-5", LlmReasoningLevel.Low, "temperature")]
    [InlineData("claude-haiku-4-5", LlmReasoningLevel.Low, "required")]
    [InlineData("claude-opus-5", null, "required")]
    [InlineData("claude-sonnet-5-5", null, "required")]
    public async Task ThinkingAndUnsupportedForcedToolCombinationsFailBeforeDispatch(string model,
        LlmReasoningLevel? level, string control)
    {
        using var handler = new Handler(true, "end_turn");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var request = Request with
        {
            ModelId = model,
            ReasoningLevel = level,
            Tools = [Tool],
            Temperature = control == "temperature" ? .5 : null,
            ToolChoice = control == "temperature" ? null : new()
            { Mode = control == "required" ? LlmToolChoiceMode.Required : LlmToolChoiceMode.Specific, Name = control == "specific" ? "run" : null }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(client.StreamAsync(Connection(), request)));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData("claude-sonnet-4-6", LlmReasoningLevel.None, true)]
    [InlineData("claude-opus-4-5", LlmReasoningLevel.Medium, true)]
    [InlineData("claude-opus-5", LlmReasoningLevel.None, false)]
    public async Task DisabledThinkingAndEffortOnlyAllowSupportedForcedToolControls(string model,
        LlmReasoningLevel level, bool sampling)
    {
        using var handler = new Handler(true, "end_turn");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        await Read(client.StreamAsync(Connection(), Request with
        {
            ModelId = model,
            ReasoningLevel = level,
            Tools = [Tool],
            ToolChoice = new() { Mode = LlmToolChoiceMode.Required },
            Temperature = sampling ? .5 : null,
            TopK = sampling ? 17 : null
        }));
        JsonNode payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Equal("any", (string?)payload["tool_choice"]!["type"]);
        if (model == "claude-opus-4-5")
        {
            Assert.Null(payload["thinking"]);
            Assert.Equal("medium", (string?)payload["output_config"]!["effort"]);
        }
    }

    [Theory]
    [InlineData(LlmToolChoiceMode.Auto)]
    [InlineData(LlmToolChoiceMode.None)]
    public async Task ThinkingKeepsNonForcedToolChoices(LlmToolChoiceMode choice)
    {
        using var handler = new Handler(true, "end_turn");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        await Read(client.StreamAsync(Connection(), Request with
        { ReasoningLevel = LlmReasoningLevel.High, Tools = [Tool], ToolChoice = new() { Mode = choice } }));
        Assert.Single(handler.Bodies);
    }

    private static async Task<List<LlmStreamEvent>> Read(IAsyncEnumerable<LlmStreamEvent> source)
    {
        var result = new List<LlmStreamEvent>();
        await foreach (var item in source) result.Add(item);
        return result;
    }

    [Theory]
    [InlineData("web_search_tool_result")]
    [InlineData("web_fetch_tool_result")]
    [InlineData("code_execution_tool_result")]
    [InlineData("bash_code_execution_tool_result")]
    [InlineData("text_editor_code_execution_tool_result")]
    [InlineData("tool_search_tool_result")]
    public void ServerResultReplayRetainsOpaqueObjectsAndCallerMetadata(string type)
    {
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "server_tool_use", ["id"] = "srv_1", ["name"] = "fixture_tool",
                ["input"] = new JsonObject { ["query"] = "opaque" },
                ["caller"] = new JsonObject { ["type"] = "direct" }
            },
            new JsonObject
            {
                ["type"] = type, ["tool_use_id"] = "srv_1",
                ["caller"] = new JsonObject { ["type"] = "code_execution_20260120", ["tool_id"] = "srv_code" },
                ["content"] = new JsonObject { ["type"] = "fixture_result", ["encrypted_content"] = "protected", ["return_code"] = 0 }
            }
        };
        LlmContinuationState state = ProviderContinuationStateCodec.Encode(Connection(), Model, content);
        Assert.True(JsonNode.DeepEquals(content, ProviderContinuationStateCodec.Decode(state, Connection(), Model)));
    }

    [Theory]
    [InlineData("""{"type":"server_tool_use","id":"srv_1","name":"web_search","input":"not-object"}""")]
    [InlineData("""{"type":"web_search_tool_result","tool_use_id":"srv_1","content":"not-blocks"}""")]
    [InlineData("""{"type":"web_search_tool_result","tool_use_id":"","content":[]}""")]
    [InlineData("""{"type":"text","text":"answer","citations":["not-object"]}""")]
    public void MalformedServerReplayStateIsRejected(string json)
    {
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.Encode(Connection(), Model,
            new JsonArray(JsonNode.Parse(json))));
    }

    private sealed class Handler(bool streaming, string reason) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        internal string ContentJson { get; init; } = Content;
        internal JsonNode? Container { get; init; }
        internal JsonNode? InitialContainer { get; init; }
        internal bool CompleteAfterFirst { get; init; }
        internal bool ExpireAfterFirst { get; init; }
        internal bool OmitDeltaContainer { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (ExpireAfterFirst && Bodies.Count > 1) return new(HttpStatusCode.BadRequest)
            { Content = new StringContent("""{"error":{"type":"invalid_request_error","message":"container expired fixture-key","api_key":"fixture-key"}}""", Encoding.UTF8, "application/json") };
            string contentJson = CompleteAfterFirst && Bodies.Count > 1 ? """[{"type":"text","text":"done"}]""" : ContentJson;
            string stopReason = CompleteAfterFirst && Bodies.Count > 1 ? "end_turn" : reason;
            string body;
            if (!streaming)
            {
                body = new JsonObject
                {
                    ["id"] = "msg_fixture",
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = JsonNode.Parse(contentJson),
                    ["stop_reason"] = stopReason,
                    ["container"] = Container?.DeepClone(),
                    ["usage"] = new JsonObject { ["input_tokens"] = 7, ["output_tokens"] = 4 }
                }.ToJsonString();
            }
            else
            {
                var events = new List<JsonObject>
                {
                    new() { ["type"] = "message_start", ["message"] = new JsonObject
                    { ["id"] = "msg_fixture", ["container"] = (InitialContainer ?? Container)?.DeepClone(),
                        ["usage"] = new JsonObject { ["input_tokens"] = 7 } } }
                };
                JsonArray content = JsonNode.Parse(contentJson)!.AsArray();
                for (int index = 0; index < content.Count; index++)
                {
                    JsonObject block = content[index]!.DeepClone().AsObject();
                    JsonNode? input = null;
                    JsonNode? citations = null;
                    if (block["citations"] is { } nativeCitations)
                    {
                        citations = nativeCitations.DeepClone();
                        block["citations"] = new JsonArray();
                    }
                    if ((string?)block["type"] is "server_tool_use" or "tool_use")
                    {
                        input = block["input"]!.DeepClone();
                        block["input"] = new JsonObject();
                    }
                    events.Add(new() { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = block });
                    if (citations is JsonArray citationBlocks)
                    {
                        foreach (JsonNode? citation in citationBlocks)
                            events.Add(new()
                            {
                                ["type"] = "content_block_delta",
                                ["index"] = index,
                                ["delta"] = new JsonObject { ["type"] = "citations_delta", ["citation"] = citation!.DeepClone() }
                            });
                    }
                    if (input is not null)
                    {
                        string json = input.ToJsonString();
                        foreach (string part in new[] { json[..5], json[5..] })
                            events.Add(new()
                            {
                                ["type"] = "content_block_delta",
                                ["index"] = index,
                                ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = part }
                            });
                    }
                    events.Add(new() { ["type"] = "content_block_stop", ["index"] = index });
                }
                var delta = new JsonObject { ["stop_reason"] = stopReason };
                if (!OmitDeltaContainer) delta["container"] = Container?.DeepClone();
                events.Add(new()
                {
                    ["type"] = "message_delta",
                    ["delta"] = delta,
                    ["usage"] = new JsonObject { ["output_tokens"] = 4 }
                });
                events.Add(new() { ["type"] = "message_stop" });
                body = string.Concat(events.Select(item => "data: " + item.ToJsonString() + "\n\n"));
            }
            return new(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
