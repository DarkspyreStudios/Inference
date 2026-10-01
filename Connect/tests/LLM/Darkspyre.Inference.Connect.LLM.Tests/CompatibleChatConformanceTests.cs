using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CompatibleChatConformanceTests
{
    private static readonly LlmChatRequest Request = new() { ModelId = "model", Messages = [new("user", "question")] };
    private static readonly LlmToolDefinition Tool = new() { Name = "lookup", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) };
    private static readonly string[] MediaPartTypes = ["text", "image_url", "file", "text"];

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task PromptOverridesCarryOrderedImagePdfAndUtf8Text(bool streaming, bool modelOverride)
    {
        using var handler = new Handler(streaming);
        using var http = new HttpClient(handler);
        LlmCapabilityOverrides media = new() { SupportsPromptImages = true, SupportsPromptDocuments = true };
        LlmConnection connection = Connection(streaming) with
        {
            CapabilityOverrides = modelOverride ? new() { SupportsStreaming = streaming } : media with { SupportsStreaming = streaming },
            ModelCapabilityOverrides = modelOverride ? new Dictionary<string, LlmCapabilityOverrides> { ["model"] = media } : []
        };
        LlmAttachment png = Attachment(LlmBinaryKind.Image, "figure.png", "image/png", [1, 2, 3]);
        LlmAttachment pdf = Attachment(LlmBinaryKind.Document, "guide.pdf", "application/pdf", [4, 5, 6]);
        LlmAttachment text = Attachment(LlmBinaryKind.Document, "notes.md", "text/markdown", Encoding.UTF8.GetBytes("# Exact text\n\u03b1"));
        await ReadAsync(new OpenAiCompatibleChatLlmClient(http).StreamAsync(connection, Request with
        { Messages = [new("user", "question") { Attachments = [png, pdf, text] }] }));
        JsonArray parts = (JsonArray)handler.Body!["messages"]![0]!["content"]!;
        Assert.Equal(MediaPartTypes, parts.Select(part => part!["type"]!.GetValue<string>()));
        Assert.Equal("question", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,AQID", parts[1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("guide.pdf", parts[2]!["file"]!["filename"]!.GetValue<string>());
        Assert.Equal("data:application/pdf;base64,BAUG", parts[2]!["file"]!["file_data"]!.GetValue<string>());
        Assert.Equal("# Exact text\n\u03b1", parts[3]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoAttachmentsKeepStringContentAndEmptyPromptStaysTextless(bool streaming)
    {
        using var handler = new Handler(streaming);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        await ReadAsync(client.StreamAsync(Connection(streaming), Request));
        Assert.Equal("question", handler.Body!["messages"]![0]!["content"]!.GetValue<string>());
        await ReadAsync(client.StreamAsync(Connection(streaming) with { CapabilityOverrides = new() { SupportsStreaming = streaming, SupportsPromptImages = true } },
            Request with { Messages = [new("user", "") { Attachments = [Attachment(LlmBinaryKind.Image, "x.png", "image/png", [1])] }] }));
        Assert.Single((JsonArray)handler.Body!["messages"]![0]!["content"]!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsupportedOrInvalidAttachmentsFailBeforeDispatch(bool streaming)
    {
        using var handler = new Handler(streaming);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        LlmAttachment image = Attachment(LlmBinaryKind.Image, "x.png", "image/png", [1]);
        await Assert.ThrowsAsync<NotSupportedException>(() => ReadAsync(client.StreamAsync(Connection(streaming), Request with
        { Messages = [new("user", "") { Attachments = [image] }] })));
        LlmConnection enabled = Connection(streaming) with { CapabilityOverrides = new() { SupportsStreaming = streaming, SupportsPromptDocuments = true, SupportsPromptImages = true } };
        await Assert.ThrowsAsync<NotSupportedException>(() => ReadAsync(client.StreamAsync(enabled, Request with
        { Messages = [new("user", "") { Attachments = [Attachment(LlmBinaryKind.Document, "x.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", [1])] }] })));
        await Assert.ThrowsAsync<ArgumentException>(() => ReadAsync(client.StreamAsync(enabled, Request with
        { Messages = [new("user", "") { Attachments = [Attachment(LlmBinaryKind.Document, "x.txt", "text/plain", [0xff])] }] })));
        await Assert.ThrowsAsync<ArgumentException>(() => ReadAsync(client.StreamAsync(enabled, Request with
        { Messages = [new("assistant", "") { Attachments = [image] }] })));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void PromptOverridesNeverEnableBinaryToolResults()
    {
        using var http = new HttpClient(new Handler(true));
        var client = new OpenAiCompatibleChatLlmClient(http);
        LlmProviderCapabilities capabilities = client.GetCapabilities(Connection(true) with
        { CapabilityOverrides = new() { SupportsToolResultImages = true, SupportsToolResultDocuments = true, SupportsPromptImages = true } });
        Assert.True(capabilities.SupportsPromptImages);
        Assert.Equal(client.GetCapabilities(Connection(true)).RichContentSupported, capabilities.RichContentSupported);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", null, null, "max_completion_tokens", true)]
    [InlineData("https://API.OPENAI.COM/v1/", null, null, "max_completion_tokens", true)]
    [InlineData("https://api.openai.com.other.test/v1", null, null, "max_tokens", false)]
    [InlineData("https://localhost:8000/v1", null, null, "max_tokens", false)]
    [InlineData("https://api.openai.com/v1", LlmChatCompletionsOutputLimitField.MaxTokens, false, "max_tokens", false)]
    [InlineData("https://other.test/v1", LlmChatCompletionsOutputLimitField.MaxCompletionTokens, true, "max_completion_tokens", true)]
    public async Task EndpointDefaultsAndExplicitProtocolChoicesApplyInBothModes(string endpoint,
        LlmChatCompletionsOutputLimitField? field, bool? cache, string expectedField, bool sendsCache)
    {
        foreach (bool streaming in new[] { true, false })
        {
            using var handler = new Handler(streaming);
            using var http = new HttpClient(handler);
            await ReadAsync(new OpenAiCompatibleChatLlmClient(http).StreamAsync(Connection(streaming) with
            { Endpoint = endpoint, ChatCompletions = new() { OutputLimitField = field, UsePromptCacheKey = cache } },
                Request with { MaxOutputTokens = 19, ConversationKey = "affinity" }));
            Assert.Equal(19, handler.Body![expectedField]!.GetValue<int>());
            Assert.False(handler.Body.ContainsKey(expectedField == "max_tokens" ? "max_completion_tokens" : "max_tokens"));
            Assert.Equal(sendsCache, handler.Body.ContainsKey("prompt_cache_key"));
            if (sendsCache) Assert.Equal("affinity", handler.Body["prompt_cache_key"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StandardOptionCollisionsAndInvalidFieldRejectBeforeDispatch(bool streaming)
    {
        using var handler = new Handler(streaming);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        LlmConnection connection = Connection(streaming) with { Endpoint = "https://api.openai.com/v1" };
        foreach (string field in new[] { "prompt_cache_key", "max_completion_tokens" })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(client.StreamAsync(connection,
                Request with
                {
                    MaxOutputTokens = 7,
                    ConversationKey = "key",
                    ProviderOptions = new Dictionary<string, JsonElement>
                    { [field] = JsonSerializer.SerializeToElement("conflict") }
                })));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(client.StreamAsync(connection with
            { ProviderOptions = new Dictionary<string, JsonElement> { [field] = JsonSerializer.SerializeToElement("conflict") } },
                Request with { MaxOutputTokens = 7, ConversationKey = "key" })));
        }
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ReadAsync(client.StreamAsync(connection with
        { ChatCompletions = new() { OutputLimitField = (LlmChatCompletionsOutputLimitField)99 } }, Request)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task EmptyConversationKeyAndAbsentOutputCapDoNotAddFields()
    {
        using var handler = new Handler(true);
        using var http = new HttpClient(handler);
        await ReadAsync(new OpenAiCompatibleChatLlmClient(http).StreamAsync(Connection(true) with { Endpoint = "https://api.openai.com/v1" },
            Request with { ConversationKey = "" }));
        Assert.False(handler.Body!.ContainsKey("prompt_cache_key"));
        Assert.False(handler.Body.ContainsKey("max_completion_tokens"));
        Assert.False(handler.Body.ContainsKey("max_tokens"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ArgumentFragmentsWaitForNameAndPreserveCallOrder(bool hasId)
    {
        using var handler = new Handler(true)
        {
            Chunks = [Delta(0, hasId ? "actual" : null, null, "{\"a\":"), Delta(0, null, "lookup", "1"), Delta(0, null, null, "}")]
        };
        using var http = new HttpClient(handler);
        IReadOnlyList<LlmStreamEvent> events = await ReadAsync(new OpenAiCompatibleChatLlmClient(http).StreamAsync(Connection(true), Request with { Tools = [Tool] }));
        LlmToolCallStartedEvent started = Assert.IsType<LlmToolCallStartedEvent>(events[0]);
        Assert.Equal("lookup", started.Name);
        if (hasId) Assert.Equal("actual", started.ToolCallId);
        else Assert.StartsWith("call_", started.ToolCallId, StringComparison.Ordinal);
        Assert.Equal(new LlmToolCallArgumentsDeltaEvent(started.ToolCallId, "{\"a\":"), events[1]);
        Assert.Equal(new LlmToolCallArgumentsDeltaEvent(started.ToolCallId, "1"), events[2]);
        Assert.Equal(new LlmToolCallArgumentsDeltaEvent(started.ToolCallId, "}"), events[3]);
        Assert.Equal(new LlmToolCall(started.ToolCallId, "lookup", "{\"a\":1}"), Assert.IsType<LlmToolCallCompletedEvent>(events[4]).ToolCall);
        Assert.IsType<LlmChatCompletionEvent>(events[5]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OmittedIdsStayDistinctAndReplayUnchangedIncludingLateNativeId(bool streaming)
    {
        using var handler = new Handler(streaming)
        {
            Chunks = [Delta(0, null, "lookup", "{}"), Delta(1, null, "lookup", "{}"), Delta(0, "late-id", null, "")],
            Complete = """{"choices":[{"message":{"content":null,"tool_calls":[{"function":{"name":"lookup","arguments":"{}"}},{"id":"","function":{"name":"lookup","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}"""
        };
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        IReadOnlyList<LlmStreamEvent> events = await ReadAsync(client.StreamAsync(Connection(streaming), Request with { Tools = [Tool] }));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(2, result.ToolCalls.Count);
        Assert.NotEqual(result.ToolCalls[0].Id, result.ToolCalls[1].Id);
        Assert.All(result.ToolCalls, call => Assert.StartsWith("call_", call.Id, StringComparison.Ordinal));
        Assert.Equal(result.ToolCalls.Select(call => call.Id), events.OfType<LlmToolCallStartedEvent>().Select(item => item.ToolCallId));
        Assert.Equal(result.ToolCalls, events.OfType<LlmToolCallCompletedEvent>().Select(item => item.ToolCall));
        await ReadAsync(client.StreamAsync(Connection(streaming), Request with
        {
            Tools = [Tool],
            Messages = [new("assistant", "") { ToolCalls = result.ToolCalls },
                new("tool", "") { ToolResults = [.. result.ToolCalls.Select(call => new LlmToolResult(call.Id, LlmToolOutput.FromText("done")))] }]
        }));
        Assert.Equal(result.ToolCalls[0].Id, handler.Body!["messages"]![0]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(result.ToolCalls[1].Id, handler.Body["messages"]![0]!["tool_calls"]![1]!["id"]!.GetValue<string>());
        Assert.Equal(result.ToolCalls[0].Id, handler.Body["messages"]![1]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal(result.ToolCalls[1].Id, handler.Body["messages"]![2]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChangedRealIdFailsWithProviderProtocolError()
    {
        using var handler = new Handler(true) { Chunks = [Delta(0, "first", "lookup", "{"), Delta(0, "other", null, "}")] };
        using var http = new HttpClient(handler);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => ReadAsync(new OpenAiCompatibleChatLlmClient(http)
            .StreamAsync(Connection(true), Request with { Tools = [Tool] })));
        Assert.Contains("changed a tool-call id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnnamedCallFailsWithoutEarlyArguments()
    {
        using var handler = new Handler(true) { Chunks = [Delta(0, "id", null, "{}")] };
        using var http = new HttpClient(handler);
        var observed = new List<LlmStreamEvent>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (LlmStreamEvent item in new OpenAiCompatibleChatLlmClient(http).StreamAsync(Connection(true), Request with { Tools = [Tool] }))
                observed.Add(item);
        });
        Assert.Empty(observed);
    }

    [Fact]
    public async Task DiscoveryPreservesPositiveReportedLimitsWithoutInventingOpenAiLimits()
    {
        using var handler = new Handler(false)
        {
            Complete = """
                {"data":[
                {"id":"router","context_length":200000,"top_provider":{"context_length":100000,"max_completion_tokens":10000}},
                {"id":"provider","context_length":0,"top_provider":{"context_length":90000,"max_completion_tokens":5000}},
                {"id":"vllm","max_model_len":4294967296},
                {"id":"invalid","context_length":"100","max_model_len":-5,"top_provider":{"context_length":1.5,"max_completion_tokens":0}},
                {"id":"openai"}]}
                """
        };
        using var http = new HttpClient(handler);
        foreach (ILlmClient client in new ILlmClient[] { new OpenAiCompatibleChatLlmClient(http), new OpenAiResponsesLlmClient(http) })
        {
            IReadOnlyList<LlmModel> models = await client.ListModelsAsync(Connection(false) with { Transport = client.Transport });
            LlmModel Model(string id) => Assert.Single(models, model => model.Id == id);
            Assert.Equal(200000, Model("router").MaxContextTokens);
            Assert.Equal(10000, Model("router").MaxOutputTokens);
            Assert.Equal(90000, Model("provider").MaxContextTokens);
            Assert.Equal(5000, Model("provider").MaxOutputTokens);
            Assert.Equal(4294967296, Model("vllm").MaxContextTokens);
            Assert.Null(Model("vllm").MaxOutputTokens);
            Assert.Null(Model("invalid").MaxContextTokens);
            Assert.Null(Model("invalid").MaxOutputTokens);
            Assert.Null(Model("openai").MaxContextTokens);
            Assert.Null(Model("openai").MaxOutputTokens);
        }
    }

    [Fact]
    public async Task CancellationBeforeDispatchDoesNotSendRequest()
    {
        using var handler = new Handler(true);
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(new OpenAiCompatibleChatLlmClient(http)
            .StreamAsync(Connection(true), Request, cancellation.Token)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void GeminiRejectsForeignChatConfiguration()
    {
        using var http = new HttpClient(new Handler(false));
        Assert.Throws<NotSupportedException>(() => new GeminiApiLlmClient(http).GetCapabilities(Connection(false) with
        { Transport = LlmTransport.GoogleGeminiApi, Endpoint = GeminiApiLlmClient.DefaultEndpoint, ChatCompletions = new() }));
    }

    private static LlmAttachment Attachment(LlmBinaryKind kind, string filename, string mediaType, byte[] data) =>
        new() { Kind = kind, FileName = filename, MediaType = mediaType, Data = data };

    private static LlmConnection Connection(bool streaming) => new()
    { Version = "v1", Transport = LlmTransport.OpenAiCompatibleChat, Endpoint = "https://compatible.test/v1", CapabilityOverrides = new() { SupportsStreaming = streaming } };

    private static JsonObject Delta(int index, string? id, string? name, string arguments)
    {
        var call = new JsonObject { ["index"] = index, ["function"] = new JsonObject { ["arguments"] = arguments } };
        if (id is not null) call["id"] = id;
        if (name is not null) call["function"]!["name"] = name;
        return new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(call) } }) };
    }

    private static async Task<IReadOnlyList<LlmStreamEvent>> ReadAsync(IAsyncEnumerable<LlmStreamEvent> source)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in source) events.Add(item);
        return events;
    }

    private sealed class Handler(bool streaming) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public JsonObject? Body { get; private set; }
        public IReadOnlyList<JsonObject> Chunks { get; init; } = [];
        public string Complete { get; init; } = """{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}]}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (request.Content is not null) Body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            string data = streaming ? string.Concat(Chunks.Select(chunk => "data: " + chunk.ToJsonString() + "\n\n"))
                + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n" : Complete;
            return new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
