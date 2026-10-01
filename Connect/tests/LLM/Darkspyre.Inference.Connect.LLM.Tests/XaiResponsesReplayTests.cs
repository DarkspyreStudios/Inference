using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiResponsesReplayTests
{
    private const string Cipher = "opaque-encrypted-context";
    private static readonly int[] OpaqueOrder = [3, 1, 2];
    private const string Message = """
        {"type":"message","id":"message-id","role":"assistant","status":"completed","content":[{"type":"output_text","text":"answer","annotations":[{"type":"url_citation","url":"https://source.test","start_index":0,"end_index":6}]}],"extra_metadata":{"kept":true}}
        """;
    private const string Reasoning = """
        {"type":"reasoning","id":"reason-id","encrypted_content":"opaque-encrypted-context","summary":[{"type":"summary_text","text":"public summary"}],"provider_metadata":{"version":3}}
        """;
    private const string Function = """
        {"type":"function_call","id":"function-id","call_id":"call-id","name":"lookup","arguments":"{\"key\":\"value\"}","encrypted_content":"encrypted-tool-context","extra_metadata":{"kept":true}}
        """;

    [Theory]
    [InlineData("web_search_call")]
    [InlineData("x_search_call")]
    [InlineData("code_interpreter_call")]
    [InlineData("code_execution_call")]
    [InlineData("file_search_call")]
    [InlineData("mcp_call")]
    [InlineData("image_generation_call")]
    [InlineData("view_image_call")]
    [InlineData("view_x_video_call")]
    public void EveryScopedHostedItemRemainsOpaqueAndOrdered(string type)
    {
        JsonArray output = Output(Reasoning, JsonSerializer.Serialize(new
        {
            type,
            id = "hosted-id",
            encrypted_content = Cipher,
            unknown_provider_fields = new { url = "javascript:opaque-not-executed", command = "not executed", results = OpaqueOrder }
        }), Message);
        JsonArray expected = (JsonArray)output.DeepClone();
        LlmConnection connection = Connection();
        LlmContinuationState state = ProviderContinuationStateCodec.Encode(connection, "grok-4.7", output);
        output[1]!["id"] = "changed-after-encode";
        JsonArray restored = ProviderContinuationStateCodec.Decode(state, connection, "grok-4.7");
        Assert.True(JsonNode.DeepEquals(expected, restored));
        restored[1]!["id"] = "changed-after-decode";
        Assert.True(JsonNode.DeepEquals(expected, ProviderContinuationStateCodec.Decode(state, connection, "grok-4.7")));
        Assert.DoesNotContain(Cipher, state.ToString());
        Assert.DoesNotContain("hosted-id", state.ToString());
    }

    [Fact]
    public void ReasoningMessageAndFunctionMetadataRoundTripWithoutProjectionLoss()
    {
        var output = Output(Reasoning, Message, Function);
        var connection = Connection();
        var state = ProviderContinuationStateCodec.Encode(connection, "grok-4.7", output);
        var message = new LlmChatMessage("assistant", "answer")
        {
            ContinuationState = state,
            ToolCalls = [new LlmToolCall("call-id", "lookup", "{\"key\":\"value\"}")]
        };
        Assert.True(JsonNode.DeepEquals(output, ProviderContinuationStateCodec.DecodeMessage(message, connection, "grok-4.7")));
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.DecodeMessage(message with { Content = "changed" }, connection, "grok-4.7"));
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.DecodeMessage(message with { ToolCalls = [] }, connection, "grok-4.7"));
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.DecodeMessage(message with
        { ToolCalls = [new LlmToolCall("call-id", "changed", "{}")] }, connection, "grok-4.7"));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("region")]
    [InlineData("transport")]
    [InlineData("model")]
    [InlineData("format")]
    public void BindingRejectsDifferentAccountRegionTransportModelOrFormat(string difference)
    {
        LlmConnection connection = Connection();
        LlmContinuationState state = ProviderContinuationStateCodec.Encode(connection, "grok-4.7", Output(Message));
        LlmConnection other = difference switch
        {
            "account" => connection with { Credential = Credential("new-token", "other-account") },
            "region" => connection with { Endpoint = "https://us.api.x.ai/v1" },
            "transport" => connection with { Transport = LlmTransport.OpenAiResponses },
            _ => connection
        };
        if (difference == "format") state = state with { FormatVersion = 1 };
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.Decode(state, other, difference == "model" ? "other-model" : "grok-4.7"));
    }

    [Fact]
    public void SameAccountRotationAndVersionChangeDoNotInvalidateContext()
    {
        LlmConnection connection = Connection();
        LlmContinuationState state = ProviderContinuationStateCodec.Encode(connection, "grok-4.7", Output(Message));
        LlmConnection rotated = connection with { Version = "v2", Credential = Credential("new-token", "account") };
        Assert.True(ProviderContinuationStateCodec.IsBound(state, rotated, "grok-4.7"));
        Assert.True(JsonNode.DeepEquals(Output(Message), ProviderContinuationStateCodec.Decode(state, rotated, "grok-4.7")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"type\":\"message\",\"role\":\"user\",\"content\":[]}")]
    [InlineData("{\"type\":\"message\",\"role\":\"assistant\"}")]
    [InlineData("{\"type\":\"message\",\"role\":\"assistant\",\"content\":{}}")]
    [InlineData("{\"type\":\"message\",\"role\":\"assistant\",\"content\":[null]}")]
    [InlineData("{\"type\":\"function_call\",\"name\":\"lookup\",\"arguments\":\"{}\"}")]
    [InlineData("{\"type\":\"function_call\",\"call_id\":\"call\",\"name\":\"lookup\",\"arguments\":\"[]\"}")]
    public void OpaqueOutputStillRejectsInvalidAssistantOrFunctionShape(string item)
    {
        Assert.Throws<ArgumentException>(() => ProviderContinuationStateCodec.Encode(Connection(), "grok-4.7", Output(item)));
    }

    [Theory]
    [InlineData(LlmTransport.XaiResponses, "future_unknown_item")]
    [InlineData(LlmTransport.OpenAiResponses, "web_search_call")]
    [InlineData(LlmTransport.OpenAiCodex, "x_search_call")]
    public void UnsupportedItemsDoNotExpandOtherTransports(LlmTransport transport, string type)
    {
        LlmConnection connection = Connection() with { Transport = transport };
        string item = JsonSerializer.Serialize(new { type, id = "item-id" });
        Assert.Throws<NotSupportedException>(() => ProviderContinuationStateCodec.Encode(connection, "grok-4.7", Output(item)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TerminalOutputAloneRetainsHostedItemsAndReplaysFullOutput(bool streaming)
    {
        JsonArray output = Output(Reasoning, Hosted(), Message);
        string response = Response(output);
        using var handler = new Handler(streaming ? Sse(Terminal(response)) : Json(response));
        using var http = new HttpClient(handler);
        var client = new Probe(http);
        LlmConnection connection = Connection(streaming);
        List<LlmStreamEvent> events = await Read(client.StreamAsync(connection, Request()));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal("answer", result.Text);
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.True(JsonNode.DeepEquals(output, ProviderContinuationStateCodec.Decode(result.ContinuationState!, connection, "grok-4.7")));
        Assert.Equal(10, result.ContinuationState!.InputTokens);
        Assert.Equal(3, result.ContinuationState.OutputTokens);
        Assert.DoesNotContain(Cipher, result.Diagnostics!.RawFinalResponseJson);
        Assert.DoesNotContain(Cipher, string.Join(" ", events.Select(item => item.ToString())));
        Assert.DoesNotContain(Cipher, result.ToString());
        await Read(client.StreamAsync(connection, Request() with
        {
            Messages = [new("user", "first"), new("assistant", result.Text) { ContinuationState = result.ContinuationState }, new("user", "next")]
        }));
        using JsonDocument sent = JsonDocument.Parse(handler.Body!);
        JsonArray input = (JsonArray)JsonNode.Parse(sent.RootElement.GetProperty("input").GetRawText())!;
        Assert.True(JsonNode.DeepEquals(output, new JsonArray([.. input.Skip(1).Take(output.Count).Select(item => item?.DeepClone())])));
        Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("reasoning.encrypted_content", sent.RootElement.GetProperty("include")[0].GetString());
        Assert.False(sent.RootElement.TryGetProperty("previous_response_id", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedFunctionAndHostedOutputStillUsesOneClientToolExchange(bool streaming)
    {
        string response = Response(Output(Reasoning, Hosted(), Function));
        using var handler = new Handler(streaming ? Sse(Terminal(response)) : Json(response));
        using var http = new HttpClient(handler);
        var client = new Probe(http);
        LlmConnection connection = Connection(streaming);
        var events = await Read(client.StreamAsync(connection, Request()));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.RequiresAction, result.Status);
        Assert.Equal("call-id", Assert.Single(result.ToolCalls).Id);
        Assert.Single(events.OfType<LlmToolCallStartedEvent>());
        Assert.Single(events.OfType<LlmToolCallArgumentsDeltaEvent>());
        Assert.Single(events.OfType<LlmToolCallCompletedEvent>());
        await Read(client.StreamAsync(connection, Request() with
        {
            Messages = [new("user", "first"), new("assistant", "") { ContinuationState = result.ContinuationState, ToolCalls = result.ToolCalls },
                new("user", "") { ToolResults = [new LlmToolResult("call-id", LlmToolOutput.FromText("tool result"))] }]
        }));
        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement input = sent.RootElement.GetProperty("input");
        Assert.Equal("file_search_call", input[2].GetProperty("type").GetString());
        Assert.Equal("function_call_output", input[4].GetProperty("type").GetString());
        Assert.Equal("tool result", input[4].GetProperty("output").GetString());
    }

    [Fact]
    public async Task EmptyTerminalOutputDoesNotResurrectItemDoneData()
    {
        string itemDone = "data: " + JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 0, item = JsonElement.Parse(Function) }) + "\n\n";
        using var handler = new Handler(Sse(itemDone + Terminal(Response([]))));
        using var http = new HttpClient(handler);
        var events = await Read(new Probe(http).StreamAsync(Connection(), Request()));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Empty(result.ToolCalls);
        Assert.Empty(events.OfType<LlmToolCallStartedEvent>());
        Assert.Empty(events.OfType<LlmToolCallArgumentsDeltaEvent>());
        Assert.Empty(events.OfType<LlmToolCallCompletedEvent>());
        Assert.Null(result.ContinuationState);
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddedWholeFunctionOrArgumentDeltasCompleteOnceFromTerminal(bool deltas)
    {
        JsonObject addedCall = (JsonObject)JsonNode.Parse(Function)!;
        if (deltas) addedCall["arguments"] = "";
        string added = Event(new { type = "response.output_item.added", output_index = 2, item = addedCall });
        string arguments = deltas
            ? Event(new { type = "response.function_call_arguments.delta", output_index = 2, delta = "{\"key\":" })
                + Event(new { type = "response.function_call_arguments.delta", output_index = 2, delta = "\"value\"}" })
            : "";
        JsonObject staleDone = (JsonObject)JsonNode.Parse(Function)!;
        staleDone["arguments"] = "{\"stale\":true}";
        string done = Event(new { type = "response.output_item.done", output_index = 2, item = staleDone });
        using var handler = new Handler(Sse(added + arguments + done + done + Terminal(Response(Output(Reasoning, Hosted(), Function)))));
        using var http = new HttpClient(handler);
        var events = await Read(new Probe(http).StreamAsync(Connection(), Request()));
        Assert.Equal("call-id", Assert.Single(events.OfType<LlmToolCallStartedEvent>()).ToolCallId);
        var argumentEvents = events.OfType<LlmToolCallArgumentsDeltaEvent>().ToArray();
        Assert.Equal(deltas ? 2 : 1, argumentEvents.Length);
        Assert.Equal("{\"key\":\"value\"}", string.Concat(argumentEvents.Select(item => item.Delta)));
        LlmToolCall completed = Assert.Single(events.OfType<LlmToolCallCompletedEvent>()).ToolCall;
        Assert.Equal("{\"key\":\"value\"}", completed.ArgumentsJson);
        Assert.Equal(completed, Assert.Single(Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.ToolCalls));
    }

    [Fact]
    public async Task EmptyTerminalOutputAlsoControlsFinalTextAndRefusal()
    {
        string partial = Event(new { type = "response.output_text.delta", delta = "partial" })
            + Event(new { type = "response.refusal.delta", delta = "partial refusal" });
        using var handler = new Handler(Sse(partial + Terminal(Response([]))));
        using var http = new HttpClient(handler);
        var events = await Read(new Probe(http).StreamAsync(Connection(), Request()));
        Assert.Single(events.OfType<LlmTextDeltaEvent>());
        Assert.Single(events.OfType<LlmRefusalDeltaEvent>());
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Empty(result.Text);
        Assert.Null(result.Refusal);
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpaqueHostedContentIsNotProjectedAsAssistantText(bool streaming)
    {
        JsonObject hosted = (JsonObject)JsonNode.Parse(Hosted())!;
        hosted["content"] = JsonNode.Parse("""[{"type":"output_text","text":"opaque-encrypted-context"}]""");
        string response = Response(new JsonArray(hosted, JsonNode.Parse(Message)));
        using var handler = new Handler(streaming ? Sse(Terminal(response)) : Json(response));
        using var http = new HttpClient(handler);
        var events = await Read(new Probe(http).StreamAsync(Connection(streaming), Request()));
        Assert.Equal("answer", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        Assert.DoesNotContain(Cipher, string.Join(" ", events.Select(item => item.ToString())));
    }

    [Theory]
    [InlineData("completed", null, LlmCompletionStatus.Completed)]
    [InlineData("incomplete", "max_output_tokens", LlmCompletionStatus.Incomplete)]
    [InlineData("incomplete", "max_prompt_tokens", LlmCompletionStatus.Incomplete)]
    [InlineData("incomplete", "max_time_limit", LlmCompletionStatus.Incomplete)]
    [InlineData("incomplete", "context_length_exceeded", LlmCompletionStatus.Incomplete)]
    [InlineData("incomplete", "content_filter", LlmCompletionStatus.Unknown)]
    [InlineData("incomplete", "future_reason", LlmCompletionStatus.Unknown)]
    [InlineData("incomplete", null, LlmCompletionStatus.Incomplete)]
    [InlineData("failed", null, LlmCompletionStatus.Failed)]
    [InlineData("cancelled", null, LlmCompletionStatus.Incomplete)]
    [InlineData("in_progress", null, LlmCompletionStatus.Unknown)]
    [InlineData("queued", null, LlmCompletionStatus.Unknown)]
    [InlineData("future_status", null, LlmCompletionStatus.Unknown)]
    [InlineData(null, null, LlmCompletionStatus.Unknown)]
    public async Task BothResponseModesMapStatusesAndIncompleteReasonsIdentically(string? status, string? reason, LlmCompletionStatus expected)
    {
        foreach (bool streaming in new[] { true, false })
        {
            string response = Response(Output(Message), status, reason);
            using var handler = new Handler(streaming ? Sse(Terminal(response)) : Json(response));
            using var http = new HttpClient(handler);
            var events = await Read(new Probe(http).StreamAsync(Connection(streaming), Request()));
            LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
            Assert.Equal(expected, result.Status);
            Assert.Equal(reason, result.IncompleteReason);
            if (expected is LlmCompletionStatus.Incomplete or LlmCompletionStatus.Failed or LlmCompletionStatus.Unknown)
                Assert.Null(result.ContinuationState);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContextExhaustionReturnsIncompleteWithoutRetryOrRawErrorLeak(bool streaming)
    {
        string response = JsonSerializer.Serialize(new { status = "failed", output = Array.Empty<object>(), error = new { code = "context_length_exceeded", encrypted_content = Cipher, message = "context exhausted " + Cipher } });
        using var handler = new Handler(streaming ? Sse(Terminal(response, "response.failed")) : Json(response));
        using var http = new HttpClient(handler);
        var events = await Read(new Probe(http).StreamAsync(Connection(streaming), Request()));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Incomplete, result.Status);
        Assert.Equal("context_length_exceeded", result.IncompleteReason);
        Assert.Null(result.ContinuationState);
        Assert.DoesNotContain(Cipher, result.Diagnostics!.RawFinalResponseJson);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TopLevelContextErrorAlsoEndsWithOneIncompleteCompletion()
    {
        using var handler = new Handler(Sse("data: {\"type\":\"error\",\"code\":\"context_length_exceeded\"}\n\n"));
        using var http = new HttpClient(handler);
        var events = await Read(new Probe(http).StreamAsync(Connection(), Request()));
        Assert.Equal(LlmCompletionStatus.Incomplete, Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Status);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    public async Task SharedResponsesUsesTheXaiClassifierWithoutRetry(int status)
    {
        using var handler = new Handler(new((HttpStatusCode)status) { Content = new StringContent("access-secret") });
        using var http = new HttpClient(handler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => Read(new Probe(http).StreamAsync(Connection(), Request())));
        Assert.Equal(status == 401 ? InferenceFailureCode.AuthenticationRequired : InferenceFailureCode.AccessDenied, error.Code);
        Assert.DoesNotContain("access-secret", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MalformedJsonDoesNotExposeCiphertextOrParserInnerException(bool streaming)
    {
        const string malformed = "{\"encrypted_content\":\"opaque-encrypted-context\",broken";
        using var handler = new Handler(streaming ? Sse("data: " + malformed + "\n\n") : Json(malformed));
        using var http = new HttpClient(handler);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => Read(new Probe(http).StreamAsync(Connection(streaming), Request())));
        Assert.Contains("Grok Responses", error.Message);
        Assert.DoesNotContain(Cipher, error.ToString() + error.SanitizedResponseBody);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonObjectJsonIsAProtocolFailureInBothModes(bool streaming)
    {
        string body = JsonSerializer.Serialize(Cipher);
        using var handler = new Handler(streaming ? Sse("data: " + body + "\n\n") : Json(body));
        using var http = new HttpClient(handler);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => Read(new Probe(http).StreamAsync(Connection(streaming), Request())));
        Assert.Contains("malformed", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Cipher, error.ToString() + error.SanitizedResponseBody);
    }

    [Fact]
    public async Task StreamErrorsRedactNamedCiphertextReflectedInMessage()
    {
        string errorBody = Event(new { type = "error", error = new { code = "provider_failure", encrypted_content = Cipher, message = "failed " + Cipher } });
        using var handler = new Handler(Sse(errorBody));
        using var http = new HttpClient(handler);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => Read(new Probe(http).StreamAsync(Connection(), Request())));
        Assert.Equal("provider_failure", error.ProviderCode);
        Assert.DoesNotContain(Cipher, error.ToString() + error.SanitizedResponseBody);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true, "function_call")]
    [InlineData(false, "function_call")]
    [InlineData(true, "message")]
    [InlineData(false, "message")]
    public async Task InvalidTerminalItemsFailWithoutOpaqueParserExceptions(bool streaming, string type)
    {
        JsonObject invalid = type == "function_call"
            ? (JsonObject)JsonNode.Parse(Function)! : (JsonObject)JsonNode.Parse(Message)!;
        invalid["encrypted_content"] = Cipher;
        invalid[type == "function_call" ? "arguments" : "content"] = Cipher;
        string response = Response(new JsonArray(invalid));
        using var handler = new Handler(streaming ? Sse(Terminal(response)) : Json(response));
        using var http = new HttpClient(handler);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => Read(new Probe(http).StreamAsync(Connection(streaming), Request())));
        Assert.Contains("invalid terminal output", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Cipher, error.ToString() + error.SanitizedResponseBody);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnknownEventDoesNotCompleteOrExposeEncryptedDeltaAndDuplicateTerminalStopsOnce()
    {
        string terminal = Terminal(Response(Output(Message)));
        using var handler = new Handler(Sse("data: {\"type\":\"future.encrypted_event\",\"delta\":\"opaque-encrypted-context\"}\n\n" + terminal + terminal));
        using var http = new HttpClient(handler);
        var events = await Read(new Probe(http).StreamAsync(Connection(), Request()));
        Assert.Single(events.OfType<LlmChatCompletionEvent>());
        Assert.DoesNotContain(Cipher, string.Join(" ", events.Select(item => item.ToString())));
    }

    [Fact]
    public void NamedCiphertextIsRedactedAlsoWhenReflectedElsewhereInAValidEnvelope()
    {
        string body = JsonSerializer.Serialize(new { output = new[] { new { encrypted_content = Cipher } }, message = Cipher, details = new[] { Cipher } });
        string safe = SecretRedactor.Sanitize(body, null);
        Assert.DoesNotContain(Cipher, safe);
        Assert.Contains("[REDACTED]", safe);
        using JsonDocument document = JsonDocument.Parse(safe);
        Assert.Equal("[REDACTED]", document.RootElement.GetProperty("message").GetString());
    }

    private static JsonArray Output(params string[] items) => new([.. items.Select(item => JsonNode.Parse(item))]);
    private static string Hosted() => """{"type":"file_search_call","id":"search-id","encrypted_content":"opaque-encrypted-context","results":[{"file_id":"file-id","score":0.9,"text":"source text"}]}""";
    private static string Response(JsonArray output, string? status = "completed", string? reason = null) => JsonSerializer.Serialize(new
    {
        id = "response-id",
        status,
        output,
        incomplete_details = reason is null ? null : new { reason },
        usage = new { input_tokens = 10, output_tokens = 3, total_tokens = 13 }
    });
    private static string Terminal(string response, string type = "response.completed") => "data: " + JsonSerializer.Serialize(new { type, response = JsonElement.Parse(response) }) + "\n\n";
    private static string Event<T>(T value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static LlmChatRequest Request() => new() { ModelId = "grok-4.7", Messages = [new("user", "first")] };
    private static string Credential(string access = "access-secret", string account = "account") => new LlmCredential { OAuth = new() { AccessToken = access, AccountId = account } }.Serialize();
    private static LlmConnection Connection(bool streaming = true) => new()
    {
        Version = "v1",
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Credential = Credential(),
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };
    private static async Task<List<LlmStreamEvent>> Read(IAsyncEnumerable<LlmStreamEvent> stream)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in stream) events.Add(item);
        return events;
    }

    // This probe exercises the shared pipeline without registering a concrete provider.
    private sealed class Probe(HttpClient http) : ResponsesLlmClientBase(http)
    {
        public override LlmTransport Transport => LlmTransport.XaiResponses;
        public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.GrokSubscription;
        protected override bool OffersNonStreamingRequests => true;
        protected override bool ReasonsByDefault(LlmConnection connection, string modelId) => false;
        protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
        {
            SupportsStreaming = true,
            SupportsReasoningText = true,
            SupportsStreamingUsage = true,
            SupportsToolUse = true,
            Tools = new() { SupportsArgumentStreaming = true }
        };
    }

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            // Each request owns a fresh response even when the controlled payload is unchanged.
            return new(response.StatusCode)
            {
                Content = new StringContent(await response.Content.ReadAsStringAsync(cancellationToken), Encoding.UTF8,
                    response.Content.Headers.ContentType!.MediaType!)
            };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) response.Dispose();
            base.Dispose(disposing);
        }
    }
}
