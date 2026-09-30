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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            string body;
            if (!streaming)
            {
                body = new JsonObject
                {
                    ["id"] = "msg_fixture",
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = JsonNode.Parse(Content),
                    ["stop_reason"] = reason,
                    ["usage"] = new JsonObject { ["input_tokens"] = 7, ["output_tokens"] = 4 }
                }.ToJsonString();
            }
            else
            {
                var events = new List<JsonObject>
                {
                    new() { ["type"] = "message_start", ["message"] = new JsonObject
                    { ["id"] = "msg_fixture", ["usage"] = new JsonObject { ["input_tokens"] = 7 } } }
                };
                JsonArray content = JsonNode.Parse(Content)!.AsArray();
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
                    if ((string?)block["type"] == "server_tool_use")
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
                events.Add(new()
                {
                    ["type"] = "message_delta",
                    ["delta"] = new JsonObject { ["stop_reason"] = reason },
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
