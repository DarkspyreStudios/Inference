using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AnthropicToolContinuationTests
{
    private const string Model = "claude-sonnet-4-6";
    private const string Native = """{"server_tools":[{"type":"code_execution_20260120","name":"code_execution"}]}""";
    private const string Content = """
        [{"type":"server_tool_use","id":"srv_code","name":"code_execution","input":{"code":"protected-code"}},
         {"type":"tool_use","id":"client_1","name":"run","input":{},"caller":{"type":"code_execution_20260120","tool_id":"srv_code"}},
         {"type":"tool_use","id":"client_2","name":"direct","input":{},"caller":{"type":"direct"}}]
        """;
    private static LlmConnection Connection(bool streaming = false) => new()
    {
        Version = "1",
        Transport = LlmTransport.AnthropicMessages,
        Endpoint = "https://api.anthropic.invalid",
        Credential = new LlmCredential { ApiKey = "fixture-key" }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };
    private static LlmToolResult Result(string id) => new(id, LlmToolOutput.FromText("result"));

    private static LlmChatRequest Request(LlmConnection connection, string? content = null)
    {
        JsonArray blocks = JsonNode.Parse(content ?? Content)!.AsArray();
        LlmToolCall[] calls = blocks.Where(block => (string?)block!["type"] == "tool_use")
            .Select(block => new LlmToolCall((string)block!["id"]!, (string)block["name"]!, block["input"]!.ToJsonString())).ToArray();
        var state = AnthropicContinuation.WithContainer(ProviderContinuationStateCodec.Encode(connection, Model,
            blocks), "container-current");
        return new()
        {
            ModelId = Model,
            Tools = [new() { Name = "run", InputSchema = JsonElement.Parse("""{"type":"object"}""") },
                new() { Name = "direct", InputSchema = JsonElement.Parse("""{"type":"object"}""") }],
            ProviderOptions = new Dictionary<string, JsonElement> { [AnthropicTools.OptionName] = JsonElement.Parse(Native) },
            Messages = [new("user", "start"), new("assistant", "") { ContinuationState = state, ToolCalls = calls },
                new("tool", "") { ToolResults = [Result("client_2"), Result("client_1")] }]
        };
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ProgrammaticResultsPreserveTextPartsOrderAndClientIdentities(bool streaming, bool textBlocks)
    {
        using var handler = new Handler(streaming);
        using var http = new HttpClient(handler);
        var connection = Connection(streaming);
        var request = Request(connection);
        if (textBlocks)
            request = request with
            {
                Messages = [request.Messages[0], request.Messages[1], request.Messages[2] with
            { ToolResults = [Result("client_2"), new("client_1", new() { Parts = [new LlmToolOutputText("first"), new LlmToolOutputText("second")] })] }]
            };
        await Complete(new AnthropicMessagesLlmClient(http), connection, request);
        JsonNode payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Equal("container-current", (string?)payload["container"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Content), payload["messages"]![1]!["content"]));
        JsonArray results = payload["messages"]![2]!["content"]!.AsArray();
        Assert.Equal("client_2", (string?)results[0]!["tool_use_id"]);
        Assert.Equal("client_1", (string?)results[1]!["tool_use_id"]);
        if (textBlocks)
        {
            Assert.Equal("first", (string?)results[1]!["content"]![0]!["text"]);
            Assert.Equal("second", (string?)results[1]!["content"]![1]!["text"]);
        }
        else Assert.Equal("result", (string?)results[1]!["content"]);
        Assert.Equal("container-current", AnthropicContinuation.ReadPayload(request.Messages[1].ContinuationState!.Payload).ContainerId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("text")]
    [InlineData("attachment")]
    [InlineData("intervening")]
    [InlineData("extra-user")]
    [InlineData("image-result")]
    [InlineData("document-result")]
    [InlineData("no-container")]
    [InlineData("raw-container")]
    [InlineData("no-native")]
    [InlineData("client-native-spoof")]
    [InlineData("old-native-version")]
    [InlineData("caller-id")]
    [InlineData("duplicate-call")]
    public async Task InvalidProgrammaticContinuationsFailWithoutDispatchOrPayloadDisclosure(string variant)
    {
        using var handler = new Handler(false);
        using var http = new HttpClient(handler);
        var connection = Connection();
        var request = Request(connection);
        LlmChatMessage reply = request.Messages[2];
        switch (variant)
        {
            case "missing": request = request with { Messages = request.Messages.Take(2).ToArray() }; break;
            case "partial": reply = reply with { ToolResults = [Result("client_1")] }; break;
            case "duplicate": reply = reply with { ToolResults = [Result("client_1"), Result("client_1")] }; break;
            case "unknown": reply = reply with { ToolResults = [Result("client_1"), Result("unknown")] }; break;
            case "text": reply = reply with { Content = "extra" }; break;
            case "attachment": reply = reply with { Role = "user", Attachments = [new() { Kind = LlmBinaryKind.Image, FileName = "fixture.png", MediaType = "image/png", Data = new byte[] { 1 } }] }; break;
            case "intervening": request = request with { Messages = [request.Messages[0], request.Messages[1], new("user", "intervening"), reply] }; break;
            case "extra-user": request = request with { Messages = [.. request.Messages, new("user", "extra")] }; break;
            case "image-result":
            case "document-result":
                reply = reply with
                {
                    ToolResults = [Result("client_2"), new("client_1", new()
                { Parts = [new LlmToolOutputBinary(variant == "image-result" ? LlmBinaryKind.Image : LlmBinaryKind.Document,
                    variant == "image-result" ? "image/png" : "application/pdf", new byte[] { 1 })] })]
                };
                break;
            case "no-container":
            case "raw-container":
                request = request with
                {
                    Messages = [request.Messages[0], request.Messages[1] with
                { ContinuationState = ProviderContinuationStateCodec.Encode(connection, Model, JsonNode.Parse(Content)!.AsArray()) }, reply]
                };
                if (variant == "raw-container") request = request with
                {
                    ProviderOptions = new Dictionary<string, JsonElement>
                    { [AnthropicTools.OptionName] = JsonElement.Parse(Native), ["container"] = JsonElement.Parse("\"unretained-container\"") }
                };
                break;
            case "no-native": request = request with { ProviderOptions = new Dictionary<string, JsonElement>() }; break;
            case "client-native-spoof":
                request = request with
                { ProviderOptions = new Dictionary<string, JsonElement>(), Tools = [.. request.Tools, new() { Name = "code_execution", InputSchema = JsonElement.Parse("""{"type":"object"}""") }] }; break;
            case "old-native-version":
                request = request with
                {
                    ProviderOptions = new Dictionary<string, JsonElement>
                    { [AnthropicTools.OptionName] = JsonElement.Parse("""{"server_tools":[{"type":"code_execution_20250522","name":"code_execution"}]}""") }
                }; break;
            case "caller-id": request = Request(connection, Content.Replace("\"tool_id\":\"srv_code\"", "\"tool_id\":\"missing\"", StringComparison.Ordinal)); break;
            case "duplicate-call": request = Request(connection, Content.Replace("client_2", "client_1", StringComparison.Ordinal)); break;
        }
        if (variant is "partial" or "duplicate" or "unknown" or "text" or "attachment" or "image-result" or "document-result")
            request = request with { Messages = [request.Messages[0], request.Messages[1], reply] };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Complete(new AnthropicMessagesLlmClient(http), connection, request));
        Assert.Contains("Claude", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Bodies);
        Assert.DoesNotContain("protected-code", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-key", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task CompletedHistoryDoesNotRequireCurrentNativeToolsOrContainer()
    {
        using var handler = new Handler(false);
        using var http = new HttpClient(handler);
        var connection = Connection();
        var request = Request(connection);
        LlmContinuationState history = ProviderContinuationStateCodec.Encode(connection, Model, JsonNode.Parse(Content)!.AsArray());
        request = request with
        {
            Tools = [],
            ProviderOptions = new Dictionary<string, JsonElement>(),
            Messages = [request.Messages[0], request.Messages[1] with { ContinuationState = history }, request.Messages[2],
                new("assistant", "finished"), new("user", "next")]
        };
        await Complete(new AnthropicMessagesLlmClient(http), connection, request);
        JsonNode payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Null(payload["tools"]);
        Assert.Null(payload["container"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Content), payload["messages"]![1]!["content"]));
    }

    [Fact]
    public async Task ProgrammaticCallerCanReferToAnInvocationRetainedInAnEarlierMessage()
    {
        using var handler = new Handler(false);
        using var http = new HttpClient(handler);
        var connection = Connection();
        var request = Request(connection);
        JsonArray original = JsonNode.Parse(Content)!.AsArray();
        LlmContinuationState invocation = ProviderContinuationStateCodec.Encode(connection, Model, new JsonArray(original[0]!.DeepClone()));
        var clients = new JsonArray(original[1]!.DeepClone(), original[2]!.DeepClone());
        LlmContinuationState later = AnthropicContinuation.WithContainer(ProviderContinuationStateCodec.Encode(connection, Model, clients), "container-current");
        request = request with
        {
            Messages = [request.Messages[0], new("assistant", "") { ContinuationState = invocation },
            new("user", "continue"), request.Messages[1] with { ContinuationState = later }, request.Messages[2]]
        };
        await Complete(new AnthropicMessagesLlmClient(http), connection, request);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScopeAndModelMismatchFailBeforeContinuationValidation(bool changeModel)
    {
        using var handler = new Handler(false);
        using var http = new HttpClient(handler);
        var connection = Connection();
        var request = Request(connection);
        if (changeModel) request = request with { ModelId = "claude-opus-4-6" };
        else connection = connection with { Credential = new LlmCredential { ApiKey = "different-account" }.Serialize() };
        await Assert.ThrowsAsync<ArgumentException>(() => Complete(new AnthropicMessagesLlmClient(http), connection, request));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task ExplicitUnboundOmissionPrecedesNativeHistoryValidation()
    {
        using var handler = new Handler(false);
        using var http = new HttpClient(handler);
        var connection = Connection();
        var request = Request(connection);
        request = request with
        {
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
            Messages =
            [request.Messages[0], request.Messages[1] with { ContinuationState = request.Messages[1].ContinuationState! with { Payload = "invalid-unbound-payload" } }, request.Messages[2]]
        };
        connection = connection with { Credential = new LlmCredential { ApiKey = "different-account" }.Serialize() };
        await using var service = new LlmProviderService(new Source(connection), [new AnthropicMessagesLlmClient(http)]);
        var results = new List<LlmChatResult>();
        await foreach (LlmStreamEvent item in service.StreamAsync("fixture", request))
            if (item is LlmChatCompletionEvent completion) results.Add(completion.Result);
        Assert.Equal(1, Assert.Single(results).OmittedContinuationStateCount);
        Assert.DoesNotContain("invalid-unbound-payload", Assert.Single(handler.Bodies), StringComparison.Ordinal);
        Assert.Equal("invalid-unbound-payload", request.Messages[1].ContinuationState!.Payload);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedDirectAndDeferredNativeCallsRequireResultOnlyRepliesButKeepRichDirectResults(bool valid)
    {
        string content = Content.Replace("\"type\":\"code_execution_20260120\",\"tool_id\":\"srv_code\"", "\"type\":\"direct\"", StringComparison.Ordinal);
        using var handler = new Handler(false);
        using var http = new HttpClient(handler);
        var connection = Connection();
        var request = Request(connection, content);
        request = request with
        {
            Messages = [request.Messages[0], request.Messages[1], request.Messages[2] with
        { Content = valid ? "" : "extra", ToolResults = [Result("client_2"), new("client_1", new()
        { Parts = [new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", new byte[] { 1 })] })] }]
        };
        if (valid)
        {
            await Complete(new AnthropicMessagesLlmClient(http), connection, request);
            Assert.Equal("image", (string?)JsonNode.Parse(Assert.Single(handler.Bodies))!["messages"]![2]!["content"]![1]!["content"]![0]!["type"]);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Complete(new AnthropicMessagesLlmClient(http), connection, request));
            Assert.Empty(handler.Bodies);
        }
    }

    [Fact]
    public async Task OrdinaryDirectCallsRetainAdditionalUserText()
    {
        using var handler = new Handler(false);
        using var http = new HttpClient(handler);
        var connection = Connection();
        JsonArray content = JsonNode.Parse(Content)!.AsArray();
        content.RemoveAt(0);
        content[0]!["caller"] = new JsonObject { ["type"] = "direct" };
        var request = Request(connection, content.ToJsonString());
        request = request with
        {
            ProviderOptions = new Dictionary<string, JsonElement>(),
            Messages =
            [request.Messages[0], request.Messages[1], request.Messages[2] with { Content = "extra user input" }]
        };
        await Complete(new AnthropicMessagesLlmClient(http), connection, request);
        Assert.Equal("extra user input", (string?)JsonNode.Parse(Assert.Single(handler.Bodies))!["messages"]![2]!["content"]![2]!["text"]);
    }

    [Fact]
    public async Task ProviderCountingUsesTheSameContinuationPreflightWithoutDispatch()
    {
        using var handler = new Handler(false);
        using var http = new HttpClient(handler);
        var connection = Connection();
        var request = Request(connection);
        request = request with { Messages = [request.Messages[0], request.Messages[1], request.Messages[2] with { Content = "extra" }] };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new AnthropicMessagesLlmClient(http).CountInputTokensAsync(connection, request));
        Assert.Contains("tool-result-only", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RawThinkingCannotOverrideTypedDisabledReasoningAtEitherScope(bool connectionLevel)
    {
        using var handler = new Handler(false);
        using var http = new HttpClient(handler);
        var options = new Dictionary<string, JsonElement> { ["thinking"] = JsonElement.Parse("""{"type":"enabled","budget_tokens":1024}""") };
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(new AnthropicMessagesLlmClient(http),
            connectionLevel ? Connection() with { ProviderOptions = options } : Connection(),
            new()
            {
                ModelId = Model,
                ReasoningLevel = LlmReasoningLevel.None,
                ProviderOptions = connectionLevel ? new Dictionary<string, JsonElement>() : options
            }));
        Assert.Empty(handler.Bodies);
    }

    private static async Task Complete(AnthropicMessagesLlmClient client, LlmConnection connection, LlmChatRequest request)
    { await foreach (var unused in client.StreamAsync(connection, request)) { } }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("API-key credentials are not refreshed.");
    }

    private sealed class Handler(bool streaming) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json") };
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            string response = streaming
                ? "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n"
                : """{"type":"message","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn"}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
