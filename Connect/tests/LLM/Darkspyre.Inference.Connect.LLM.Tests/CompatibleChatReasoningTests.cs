using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CompatibleChatReasoningTests
{
    [Theory]
    [InlineData("""{"reasoning_content":"thought"}""", false)]
    [InlineData("""{"reasoning":"thought"}""", false)]
    [InlineData("""{"reasoning_details":[{"type":"reasoning.text","text":"thought"}]}""", false)]
    [InlineData("""{"reasoning_details":[{"type":"reasoning.summary","summary":"thought"}]}""", true)]
    public void ReadsEachPublicReasoningDialect(string json, bool summary)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new LlmReasoningDeltaEvent("thought", summary), new ChatReasoning().Observe(document.RootElement));
    }

    [Fact]
    public void RetainsFragmentsInTypeOrderAndDisplaysOverlappingFieldsOnlyOnce()
    {
        var reader = new ChatReasoning();
        Assert.Equal(new LlmReasoningDeltaEvent("a", false), Observe(reader,
            """{"reasoning":"a","reasoning_details":[{"type":"reasoning.text","index":0,"text":"a","signature":"first","format":"openai-responses-v1","id":"r1","ignored":"wire-extra"}]}"""));
        Assert.Equal(new LlmReasoningDeltaEvent("b", false), Observe(reader,
            """{"reasoning_content":"b","reasoning":"b","reasoning_details":[{"type":"reasoning.text","index":0,"text":"b","signature":"later","id":"r2"}]}"""));
        Assert.Null(Observe(reader, """{"reasoning_details":[{"type":"reasoning.encrypted","index":0,"data":"private-ciphertext"}]}"""));
        Assert.Equal(new LlmReasoningDeltaEvent("c", false), Observe(reader,
            """{"reasoning_details":[{"type":"reasoning.text","text":"c"}]}"""));
        var details = reader.ReasoningDetails!;
        Assert.Equal(3, details.Count);
        Assert.Equal("ab", (string?)details[0]!["text"]);
        Assert.Equal("first", (string?)details[0]!["signature"]);
        Assert.Equal("r1", (string?)details[0]!["id"]);
        Assert.Null(details[0]!["ignored"]);
        Assert.Equal("private-ciphertext", (string?)details[1]!["data"]);
        Assert.Equal("b", reader.ReasoningContent);
        details.Clear();
        Assert.Equal(3, reader.ReasoningDetails!.Count);
    }

    [Theory]
    [InlineData("""{"reasoning_details":[{"type":"unknown","data":"secret"}]}""")]
    [InlineData("""{"reasoning_details":[{"type":"reasoning.text","text":{"secret":"value"}}]}""")]
    [InlineData("""{"reasoning_details":[{"type":"reasoning.text","index":-1}]}""")]
    [InlineData("""{"reasoning_details":[{"type":"reasoning.text","index":2147483648}]}""")]
    [InlineData("""{"reasoning_details":[{"type":"reasoning.text","index":"secret"}]}""")]
    [InlineData("""{"reasoning_details":{}}""")]
    public void MalformedDetailsFailWithoutPrivatePayloadsInTheError(string wire)
    {
        LlmProviderException error = Assert.Throws<LlmProviderException>(() => Observe(new ChatReasoning(), wire));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamingAndCompleteRepliesKeepReasoningBeforeAnswerAndReplayWithTools(bool streaming)
    {
        const string message = """{"content":"answer","reasoning_content":"thought","tool_calls":[{"id":"call-1","type":"function","function":{"name":"echo","arguments":"{\"x\":1}"}}]}""";
        var handler = new Handler(Reply(message, streaming, "tool_calls"), streaming);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        var connection = Connection() with { CapabilityOverrides = new() { SupportsStreaming = streaming } };
        var events = await Read(client.StreamAsync(connection, Request()));
        Assert.Equal(new LlmReasoningDeltaEvent("thought", false), events[0]);
        Assert.Equal(new LlmTextDeltaEvent("answer"), events[1]);
        var result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        Assert.Equal(LlmCompletionStatus.RequiresAction, result.Status);
        Assert.NotNull(result.ContinuationState);
        var retained = new LlmChatMessage("assistant", result.Text)
        { ToolCalls = result.ToolCalls, ContinuationState = result.ContinuationState };
        await Read(client.StreamAsync(connection, Request() with { Messages = [retained] }));
        JsonObject replay = handler.Bodies[^1]["messages"]![0]!.AsObject();
        Assert.Equal("thought", (string?)replay["reasoning_content"]);
        Assert.Equal("call-1", (string?)replay["tool_calls"]![0]!["id"]);
        await Read(client.StreamAsync(connection, Request() with { Tools = [], Messages = [retained] }));
        Assert.Null(handler.Bodies[^1]["messages"]![0]!["reasoning_content"]);
    }

    [Fact]
    public async Task StructuredReasoningTakesReplayPrecedenceAndEncryptedContentIsNeverDisplayed()
    {
        const string message = """{"content":"answer","reasoning_content":"thought","reasoning":"thought","reasoning_details":[{"type":"reasoning.text","text":"thought","signature":"signed"},{"type":"reasoning.encrypted","data":"opaque"}]}""";
        var handler = new Handler(Reply(message, true), true);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        var events = await Read(client.StreamAsync(Connection(), Request()));
        Assert.Equal("thought", Assert.Single(events.OfType<LlmReasoningDeltaEvent>()).Delta);
        var result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        await Read(client.StreamAsync(Connection(), Request() with
        { Messages = [new("assistant", result.Text) { ContinuationState = result.ContinuationState }] }));
        var replay = handler.Bodies[^1]["messages"]![0]!;
        Assert.Null(replay["reasoning_content"]);
        Assert.Equal("opaque", (string?)replay["reasoning_details"]![1]!["data"]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"type":"reasoning.encrypted","data":"opaque"}]""")]
    public async Task EmptyAndEncryptedOnlyDetailsRemainReplayableWithoutPublicReasoning(string details)
    {
        var handler = new Handler(Reply($"{{\"content\":\"answer\",\"reasoning_details\":{details}}}", true), true);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        var events = await Read(client.StreamAsync(Connection(), Request()));
        Assert.Empty(events.OfType<LlmReasoningDeltaEvent>());
        var result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        Assert.NotNull(result.ContinuationState);
        await Read(client.StreamAsync(Connection(), Request() with
        { Messages = [new("assistant", result.Text) { ContinuationState = result.ContinuationState }] }));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(details), handler.Bodies[^1]["messages"]![0]!["reasoning_details"]));
    }

    [Fact]
    public async Task ReplayAcceptsEquivalentToolArgumentObjectsButRejectsChangedArguments()
    {
        const string message = """{"content":"answer","reasoning_content":"thought","tool_calls":[{"id":"call-1","type":"function","function":{"name":"echo","arguments":"{\"x\":1,\"y\":2}"}}]}""";
        var handler = new Handler(Reply(message, true, "tool_calls"), true);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        var events = await Read(client.StreamAsync(Connection(), Request()));
        var result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        var retained = new LlmChatMessage("assistant", result.Text)
        { ContinuationState = result.ContinuationState, ToolCalls = [new("call-1", "echo", "{ \"y\": 2, \"x\": 1 }")] };
        await Read(client.StreamAsync(Connection(), Request() with { Messages = [retained] }));
        await Assert.ThrowsAsync<ArgumentException>(() => Read(client.StreamAsync(Connection(), Request() with
        { Messages = [retained with { ToolCalls = [new("call-1", "echo", "{\"x\":2,\"y\":2}")] }] })));
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task PreparedConnectionCannotBeRepinnedAfterItsCredentialChanges()
    {
        var handler = new Handler(Reply("""{"content":"ok"}""", true), true);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        var prepared = await client.PrepareAsync(Connection(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(client.StreamAsync(prepared with
        { Credential = new LlmCredential { ApiKey = "other" }.Serialize() }, Request())));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    public async Task IncompleteOrRefusedRepliesNeverBecomeReplayable(string finish)
    {
        var handler = new Handler(Reply("""{"content":"partial","reasoning_content":"thought"}""", true, finish), true);
        using var http = new HttpClient(handler);
        var events = await Read(new OpenAiCompatibleChatLlmClient(http).StreamAsync(Connection(), Request()));
        Assert.Null(Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result.ContinuationState);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("endpoint")]
    [InlineData("credential")]
    [InlineData("text")]
    [InlineData("call")]
    [InlineData("field")]
    public async Task RetainedStateIsValidatedBeforeDispatch(string change)
    {
        var handler = new Handler(Reply("""{"content":"answer","reasoning_content":"private-thought"}""", true), true);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        LlmConnection connection = Connection();
        var events = await Read(client.StreamAsync(connection, Request()));
        var result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        var message = new LlmChatMessage("assistant", result.Text) { ContinuationState = result.ContinuationState };
        var request = Request() with { Messages = [message] };
        switch (change)
        {
            case "model": request = request with { ModelId = "other" }; break;
            case "endpoint": connection = connection with { Endpoint = "https://other.test/v1" }; break;
            case "credential": connection = connection with { Credential = new LlmCredential { ApiKey = "other-key" }.Serialize() }; break;
            case "text": request = request with { Messages = [message with { Content = "tampered" }] }; break;
            case "call": request = request with { Messages = [message with { ToolCalls = [new("invented", "echo", "{}")] }] }; break;
            case "field":
                JsonArray payload = JsonNode.Parse(message.ContinuationState!.Payload)!.AsArray();
                payload[0]!["unknown"] = "secret";
                request = request with { Messages = [message with { ContinuationState = message.ContinuationState with { Payload = payload.ToJsonString() } }] };
                break;
        }
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Read(client.StreamAsync(connection, request)));
        Assert.DoesNotContain("private-thought", error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task ExplicitOmissionDropsForeignStateWithoutMutatingTheCallersMessage()
    {
        var handler = new Handler(Reply("""{"content":"ok","reasoning":"thought"}""", true), true);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        var foreign = new LlmChatMessage("assistant", "history")
        { ContinuationState = new() { ImplementationId = "foreign", ModelId = "model", ScopeId = "foreign", Payload = "[]" } };
        var events = await Read(client.StreamAsync(Connection(), Request() with
        { Messages = [foreign], UnboundContinuationState = LlmUnboundContinuationState.Omit }));
        var result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        Assert.Equal(1, result.OmittedContinuationStateCount);
        Assert.Null(result.ContinuationState);
        Assert.NotNull(foreign.ContinuationState);
    }

    private static LlmReasoningDeltaEvent? Observe(ChatReasoning reader, string wire)
    { using var document = JsonDocument.Parse(wire); return reader.Observe(document.RootElement); }
    private static LlmConnection Connection() => new()
    { Version = "1", Transport = LlmTransport.OpenAiCompatibleChat, Endpoint = "https://provider.test/v1", Credential = new LlmCredential { ApiKey = "fixture" }.Serialize() };
    private static LlmChatRequest Request() => new()
    { ModelId = "model", Messages = [new("user", "hi")], Tools = [new() { Name = "echo", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }] };
    private static string Reply(string message, bool streaming, string finish = "stop") => streaming
        ? $"data: {{\"choices\":[{{\"delta\":{message},\"finish_reason\":\"{finish}\"}}]}}\n\ndata: [DONE]\n\n"
        : $"{{\"choices\":[{{\"message\":{message},\"finish_reason\":\"{finish}\"}}]}}";
    private static async Task<List<LlmStreamEvent>> Read(IAsyncEnumerable<LlmStreamEvent> source)
    { var events = new List<LlmStreamEvent>(); await foreach (var item in source) { events.Add(item); } return events; }

    private sealed class Handler(string reply, bool streaming) : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
            return new(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
