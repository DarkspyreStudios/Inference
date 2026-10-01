using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class GeminiArgumentStreamingTests
{
    private const string Model = "google/gemini-3-flash-preview";
    private static LlmToolDefinition Tool => new() { Name = "run", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") };
    private static LlmConnection Connection => new()
    {
        Version = "1",
        Transport = LlmTransport.GoogleVertexAi,
        Endpoint = VertexEndpoints.BaseEndpoint,
        Vertex = new() { ProjectId = "p", Location = "global" }
    };

    [Fact]
    public async Task EarlyStartsTypedFragmentsParallelCallsAndSignaturesReplayAsWholeCalls()
    {
        string[] first =
        [
            Part("""{"functionCall":{"id":"first","name":"run","willContinue":true},"thoughtSignature":"signature"}"""),
            Part("""{"functionCall":{"partialArgs":[{"jsonPath":"$.command","stringValue":"hel","willContinue":true},{"jsonPath":"$.nested['a b'][0]","numberValue":12},{"jsonPath":"$.enabled","boolValue":true},{"jsonPath":"$.nothing","nullValue":null}],"willContinue":true}}"""),
            Part("""{"text":"between"}"""),
            Part("""{"functionCall":{"partialArgs":[{"jsonPath":"$['command']","stringValue":"lo"},{"jsonPath":"$.names[1]","stringValue":"second"}],"willContinue":true}}"""),
            Part("""{"functionCall":{}}"""),
            Part("""{"functionCall":{"id":"second","name":"run","partialArgs":[{"jsonPath":"$.command","stringValue":"world"}],"willContinue":true}}"""),
            Part("""{"functionCall":{}}"""),
            """{"candidates":[{"finishReason":"STOP"}]}"""
        ];
        using var handler = new Handler(first);
        using var http = new HttpClient(handler);
        var client = new VertexLlmClient(http, new Authentication());
        var events = await Drain(client.StreamAsync(Connection, new() { ModelId = Model, Tools = [Tool] }));
        Assert.IsType<LlmToolCallStartedEvent>(events[0]);
        Assert.Equal("first", ((LlmToolCallStartedEvent)events[0]).ToolCallId);
        Assert.Equal("between", Assert.IsType<LlmTextDeltaEvent>(events[1]).Delta);
        Assert.Equal(2, events.OfType<LlmToolCallStartedEvent>().Count());
        Assert.Equal(2, events.OfType<LlmToolCallCompletedEvent>().Count());
        Assert.Empty(events.OfType<LlmToolCallArgumentsDeltaEvent>());
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.RequiresAction, result.Status);
        JsonNode args = JsonNode.Parse(result.ToolCalls[0].ArgumentsJson)!;
        Assert.Equal("hello", (string?)args["command"]);
        Assert.Equal(12, (int?)args["nested"]!["a b"]![0]);
        Assert.True((bool?)args["enabled"]);
        Assert.Null(args["nothing"]);
        Assert.Null(args["names"]![0]);
        Assert.Equal("second", (string?)args["names"]![1]);
        Assert.Equal("world", (string?)JsonNode.Parse(result.ToolCalls[1].ArgumentsJson)!["command"]);
        JsonArray retained = JsonNode.Parse(result.ContinuationState!.Payload)!.AsArray();
        Assert.Equal("signature", (string?)retained[0]!["thoughtSignature"]);
        Assert.Equal("between", (string?)retained[1]!["text"]);
        Assert.DoesNotContain("partialArgs", result.ContinuationState.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("willContinue", result.ContinuationState.Payload, StringComparison.Ordinal);
        Assert.True((bool?)JsonNode.Parse(handler.Bodies[0])!["toolConfig"]!["functionCallingConfig"]!["streamFunctionCallArguments"]);
        Assert.False(client.GetCapabilities(Connection, Model).Tools.SupportsArgumentStreaming);
        handler.Replies.Enqueue(["""{"candidates":[{"content":{"parts":[{"text":"done"}]},"finishReason":"STOP"}]}"""]);
        await Drain(client.StreamAsync(Connection, new()
        {
            ModelId = Model,
            Tools = [Tool],
            Messages =
            [new("assistant", result.Text) { ToolCalls = result.ToolCalls, ContinuationState = result.ContinuationState },
                new("tool", "") { ToolResults = [new("second", LlmToolOutput.FromText("two")), new("first", LlmToolOutput.FromText("one"))] }]
        }));
        JsonNode contents = JsonNode.Parse(handler.Bodies[1])!["contents"]!;
        Assert.Equal("signature", (string?)contents[0]!["parts"]![0]!["thoughtSignature"]);
        Assert.True(JsonNode.DeepEquals(args, contents[0]!["parts"]![0]!["functionCall"]!["args"]));
        Assert.Equal("first", (string?)contents[1]!["parts"]![0]!["functionResponse"]!["id"]);
        Assert.Equal("second", (string?)contents[1]!["parts"]![1]!["functionResponse"]!["id"]);
    }

    [Theory]
    [InlineData("$.x")]
    [InlineData("$['x']")]
    [InlineData("$['quoted\\'name']")]
    [InlineData("$['unicode\\u0061']")]
    public void StringEndMarkersAndFinalFragmentsCloseTheSameCall(string path)
    {
        var state = new GeminiArgumentStream();
        state.Observe(JsonNode.Parse("""{"functionCall":{"name":"run","willContinue":true}}""")!.AsObject(), () => "c", default);
        string fragment = JsonSerializer.Serialize(new { functionCall = new { partialArgs = new[] { new { jsonPath = path, stringValue = "prefix", willContinue = true } }, willContinue = true } });
        state.Observe(JsonNode.Parse(fragment)!.AsObject(), () => "unused", default);
        fragment = JsonSerializer.Serialize(new { functionCall = new { partialArgs = new[] { new { jsonPath = path } } } });
        var completed = state.Observe(JsonNode.Parse(fragment)!.AsObject(), () => "unused", default).Completed!;
        Assert.Equal("prefix", JsonNode.Parse(completed.ArgumentsJson)!.AsObject().Single().Value!.GetValue<string>());
        Assert.False(state.IsOpen);
    }

    [Fact]
    public void AFinalFirstFragmentCreatesOneCompleteWholeCall()
    {
        var state = new GeminiArgumentStream();
        var observation = state.Observe(JsonNode.Parse("""{"functionCall":{"name":"run","partialArgs":[{"jsonPath":"$.x","boolValue":false}]},"thoughtSignature":"signed"}""")!.AsObject(), () => "c", default);
        Assert.True(observation.Started);
        Assert.Equal("{\"x\":false}", observation.Completed!.ArgumentsJson);
        Assert.Equal("signed", (string?)observation.Part["thoughtSignature"]);
        Assert.False(state.IsOpen);
    }

    [Fact]
    public void CancellationAndImpossibleSparseArraysFailBeforePublication()
    {
        var state = new GeminiArgumentStream();
        state.Observe(JsonNode.Parse("""{"functionCall":{"name":"run","willContinue":true}}""")!.AsObject(), () => "c", default);
        var fragment = JsonNode.Parse("""{"functionCall":{"partialArgs":[{"jsonPath":"$.x[2147483647]","numberValue":1}],"willContinue":true}}""")!.AsObject();
        Assert.Throws<LlmProviderException>(() => state.Observe(fragment, () => "unused", default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => state.Observe(fragment, () => "unused", cancellation.Token));
        Assert.True(state.IsOpen);
    }

    [Theory]
    [InlineData("$..x")]
    [InlineData("$[*]")]
    [InlineData("$.x[?(@.y)]")]
    [InlineData("$[0:2]")]
    [InlineData("$[-1]")]
    [InlineData("$[01]")]
    [InlineData("$['unterminated]")]
    [InlineData("$['invalid\\q']")]
    [InlineData("$.0")]
    [InlineData("foreign-secret-path")]
    public void UnsupportedPathsFailWithoutPublishingRawPath(string path)
    {
        var state = new GeminiArgumentStream();
        state.Observe(JsonNode.Parse("""{"functionCall":{"name":"run","willContinue":true}}""")!.AsObject(), () => "c", default);
        string fragment = JsonSerializer.Serialize(new { functionCall = new { partialArgs = new[] { new { jsonPath = path, stringValue = "secret" } }, willContinue = true } });
        LlmProviderException error = Assert.Throws<LlmProviderException>(() => state.Observe(JsonNode.Parse(fragment)!.AsObject(), () => "unused", default));
        Assert.DoesNotContain(path, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("{\"partialArgs\":[{\"jsonPath\":\"$.x\",\"stringValue\":\"a\",\"numberValue\":2}],\"willContinue\":true}")]
    [InlineData("{\"partialArgs\":[{\"jsonPath\":\"$.x\",\"numberValue\":2,\"willContinue\":true}],\"willContinue\":true}")]
    [InlineData("{\"partialArgs\":[{\"jsonPath\":\"$.x\"}],\"willContinue\":true}")]
    [InlineData("{\"name\":\"second\",\"willContinue\":true}")]
    [InlineData("{\"args\":{},\"willContinue\":true}")]
    [InlineData("{\"partialArgs\":[null],\"willContinue\":true}")]
    [InlineData("{\"partialArgs\":[{\"jsonPath\":\"$.x\",\"stringValue\":null}],\"willContinue\":true}")]
    [InlineData("{\"partialArgs\":[{\"jsonPath\":\"$.x\",\"boolValue\":null}],\"willContinue\":true}")]
    [InlineData("{\"partialArgs\":[{\"jsonPath\":\"$.x\",\"nullValue\":1}],\"willContinue\":true}")]
    public void InvalidFragmentsCannotReplaceTheOpenCall(string function)
    {
        var state = new GeminiArgumentStream();
        state.Observe(JsonNode.Parse("""{"functionCall":{"name":"run","willContinue":true}}""")!.AsObject(), () => "c", default);
        Assert.Throws<LlmProviderException>(() => state.Observe(new JsonObject { ["functionCall"] = JsonNode.Parse(function) }, () => "unused", default));
    }

    [Theory]
    [InlineData("STOP")]
    [InlineData("MALFORMED_FUNCTION_CALL")]
    [InlineData("MAX_TOKENS")]
    [InlineData("SAFETY")]
    [InlineData("NEW_REASON")]
    public async Task UnclosedCallsNeverBecomeExecutable(string finish)
    {
        using var handler = new Handler([Part("""{"functionCall":{"name":"run","willContinue":true}}"""),
            JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = finish } } })]);
        using var http = new HttpClient(handler);
        var operation = Drain(new VertexLlmClient(http, new Authentication()).StreamAsync(Connection, new() { ModelId = Model, Tools = [Tool] }));
        if (finish == "STOP") { await Assert.ThrowsAsync<LlmProviderException>(() => operation); }
        else
        {
            var events = await operation;
            Assert.Single(events.OfType<LlmToolCallStartedEvent>());
            Assert.Empty(events.OfType<LlmToolCallCompletedEvent>());
            Assert.Null(Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.ContinuationState);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreviewControlIsVertexStreamingOnly(bool stream)
    {
        using var handler = new Handler(["""{"candidates":[{"finishReason":"STOP"}]}"""]) { Stream = stream };
        using var http = new HttpClient(handler);
        await Drain(new VertexLlmClient(http, new Authentication()).StreamAsync(Connection with { CapabilityOverrides = new() { SupportsStreaming = stream } },
            new() { ModelId = Model, Tools = [Tool], ToolChoice = new() { Mode = LlmToolChoiceMode.None } }));
        JsonNode config = JsonNode.Parse(handler.Bodies[0])!["toolConfig"]!["functionCallingConfig"]!;
        Assert.Equal("NONE", (string?)config["mode"]);
        Assert.Equal(stream ? true : null, (bool?)config["streamFunctionCallArguments"]);
        using var geminiHandler = new Handler(["""{"candidates":[{"finishReason":"STOP"}]}"""]) { Stream = stream };
        using var geminiHttp = new HttpClient(geminiHandler);
        await Drain(new GeminiApiLlmClient(geminiHttp).StreamAsync(new()
        {
            Version = "1",
            Transport = LlmTransport.GoogleGeminiApi,
            Endpoint = GeminiApiLlmClient.DefaultEndpoint,
            Credential = new LlmCredential { ApiKey = "k" }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = stream }
        }, new() { ModelId = Model["google/".Length..], Tools = [Tool] }));
        Assert.DoesNotContain("streamFunctionCallArguments", geminiHandler.Bodies[0], StringComparison.Ordinal);
        Assert.False(GeminiModelProfiles.Get("gemini-3.1-flash-lite", null).StreamedFunctionArguments);
        Assert.False(GeminiModelProfiles.Get("gemini-2.5-flash", null).StreamedFunctionArguments);
    }

    private static string Part(string part) => "{\"candidates\":[{\"content\":{\"parts\":[" + part + "]}}]}";
    private static async Task<List<LlmStreamEvent>> Drain(IAsyncEnumerable<LlmStreamEvent> source)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in source) { events.Add(item); }
        return events;
    }
    private sealed class Handler(string[] frames) : HttpMessageHandler
    {
        public bool Stream { get; init; } = true;
        public Queue<string[]> Replies { get; } = new();
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            string[] reply = Replies.Count > 0 ? Replies.Dequeue() : frames;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(Stream ? string.Concat(reply.Select(frame => "data: " + frame + "\n\n")) : reply[0],
                Encoding.UTF8, Stream ? "text/event-stream" : "application/json")
            };
        }
    }
    private sealed class Authentication : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken) => Task.FromResult(new VertexAccess("token", "scope"));
    }
}
