using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class GeminiProtocolConformanceTests
{
    private const string Model = "gemini-3-flash-preview";
    private const string Reply = """{"candidates":[{"content":{"parts":[{"text":"done"}]},"finishReason":"STOP"}]}""";
    private static readonly byte[] Image = [1, 2, 3];
    private static readonly byte[] Pdf = Encoding.UTF8.GetBytes("%PDF-fixture");

    [Theory]
    [InlineData("{\"$defs\":{\"s\":{\"type\":\"string\"}},\"$ref\":\"#/$defs/s\"}")]
    [InlineData("{\"type\":[\"string\",\"null\"]}")]
    [InlineData("{\"prefixItems\":[{\"type\":\"string\"}]}")]
    [InlineData("{\"oneOf\":[{\"type\":\"string\"},{\"type\":\"number\"}]}")]
    [InlineData("{\"$id\":\"urn:test\",\"$anchor\":\"name\",\"type\":\"string\"}")]
    public void ApiKeywordsDoNotWidenTheVertexDialect(string schema)
    {
        var request = new LlmStructuredOutputRequest { Name = "test", Schema = JsonElement.Parse(schema) };
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, GeminiSchemas.Enforcement(request, "Gemini", GeminiSchemaDialect.GeminiApi));
        Assert.Throws<NotSupportedException>(() => GeminiSchemas.Enforcement(request, "Vertex", GeminiSchemaDialect.Vertex));
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly,
            GeminiSchemas.Enforcement(request with { Fallback = LlmStructuredOutputFallback.LocalValidation }, "Vertex", GeminiSchemaDialect.Vertex));
    }

    [Theory]
    [InlineData("{\"$ref\":\"#/s\",\"description\":\"ignored\"}")]
    [InlineData("{\"$ref\":\"https://example.com/schema\"}")]
    [InlineData("{\"$defs\":[]}")]
    [InlineData("{\"prefixItems\":[{\"pattern\":\"^x\"}]}")]
    [InlineData("{\"type\":[\"string\",1]}")]
    [InlineData("{\"$anchor\":2}")]
    [InlineData("{\"enum\":[true]}")]
    public void UnsupportedApiSchemaRequiresExplicitLocalValidation(string schema)
    {
        var request = new LlmStructuredOutputRequest { Name = "test", Schema = JsonElement.Parse(schema) };
        Assert.Throws<NotSupportedException>(() => GeminiSchemas.Enforcement(request, "Gemini", GeminiSchemaDialect.GeminiApi));
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly,
            GeminiSchemas.Enforcement(request with { Fallback = LlmStructuredOutputFallback.LocalValidation }, "Gemini", GeminiSchemaDialect.GeminiApi));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OptionsStayInGenerationConfigAndRequestOverridesConnection(bool vertex, bool stream)
    {
        using var handler = new Handler(Reply, stream);
        using var http = new HttpClient(handler);
        LlmConnection connection = Connection(vertex, stream) with
        {
            ProviderOptions = new Dictionary<string, JsonElement>
            { ["mediaResolution"] = JsonElement.Parse("\"MEDIA_RESOLUTION_LOW\"") }
        };
        var request = new LlmChatRequest
        {
            ModelId = vertex ? "google/" + Model : Model,
            PresencePenalty = .25,
            ProviderOptions = new Dictionary<string, JsonElement> { ["mediaResolution"] = JsonElement.Parse("\"MEDIA_RESOLUTION_HIGH\"") }
        };
        await Drain(Client(http, vertex).StreamAsync(connection, request));
        JsonNode body = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Null(body["mediaResolution"]);
        Assert.Null(body["presencePenalty"]);
        Assert.Equal("MEDIA_RESOLUTION_HIGH", (string?)body["generationConfig"]!["mediaResolution"]);
        Assert.Equal(.25, (double?)body["generationConfig"]!["presencePenalty"]);
        Assert.Equal("MEDIA_RESOLUTION_LOW", connection.ProviderOptions["mediaResolution"].GetString());
        Assert.Equal(.25, request.PresencePenalty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionsCannotReplaceTypedGenerationFields(bool vertex)
    {
        using var handler = new Handler(Reply, true);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(Client(http, vertex).StreamAsync(Connection(vertex, true), new()
        {
            ModelId = vertex ? "google/" + Model : Model,
            Temperature = .5,
            ProviderOptions = new Dictionary<string, JsonElement> { ["temperature"] = JsonElement.Parse("1.0") }
        })));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeSchemaIsUnchangedInResponseFormatAndCountRequest(bool vertex, bool stream)
    {
        const string schema = """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""";
        var request = new LlmChatRequest
        {
            ModelId = vertex ? "google/" + Model : Model,
            StructuredOutput = new() { Name = "result", Schema = JsonElement.Parse(schema) },
            ProviderOptions = new Dictionary<string, JsonElement> { ["responseLogprobs"] = JsonElement.Parse("true") }
        };
        using var handler = new Handler(Reply, stream);
        using var http = new HttpClient(handler);
        await Drain(Client(http, vertex).StreamAsync(Connection(vertex, stream), request));
        JsonNode generation = JsonNode.Parse(handler.Bodies[0])!["generationConfig"]!;
        JsonNode format = vertex ? generation["responseFormat"]![0]! : generation["responseFormat"]!;
        Assert.Equal("APPLICATION_JSON", (string?)format["text"]!["mimeType"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(schema), format["text"]!["schema"]));
        Assert.Null(generation["responseJsonSchema"]);
        Assert.Null(generation["responseMimeType"]);
        using var countHandler = new Handler("{\"totalTokens\":17}", false);
        using var countHttp = new HttpClient(countHandler);
        var counter = (ILlmProviderTokenCounter)Client(countHttp, vertex);
        Assert.Equal(17, (await counter.CountInputTokensAsync(Connection(vertex, stream), request)).InputTokens);
        JsonNode count = JsonNode.Parse(countHandler.Bodies[0])!;
        if (!vertex) { count = count["generateContentRequest"]!; }
        Assert.True(JsonNode.DeepEquals(generation, count["generationConfig"]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LocalSchemaFallbackRetainsOriginalConstraintsWithoutNativeFormat(bool vertex, bool stream)
    {
        const string reply = """{"candidates":[{"content":{"parts":[{"text":"\"b\""}]},"finishReason":"STOP"}]}""";
        using var handler = new Handler(reply, stream);
        using var http = new HttpClient(handler);
        var request = new LlmChatRequest
        {
            ModelId = vertex ? "google/" + Model : Model,
            StructuredOutput = new() { Name = "result", Schema = JsonElement.Parse("{\"type\":\"string\",\"pattern\":\"^a\"}"), Fallback = LlmStructuredOutputFallback.LocalValidation }
        };
        var events = await Drain(Client(http, vertex).StreamAsync(Connection(vertex, stream), request));
        LlmStructuredOutputValidation validation = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.StructuredOutput!;
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly, validation.Enforcement);
        Assert.True(validation.WasEvaluated);
        Assert.False(validation.IsValid);
        Assert.NotEmpty(validation.Errors);
        Assert.Null(JsonNode.Parse(handler.Bodies[0])!["generationConfig"]!["responseFormat"]);
        Assert.Equal("^a", request.StructuredOutput.Schema.GetProperty("pattern").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitImportedParallelHistoryUsesOneSignatureAndNativeMedia(bool vertex, bool stream)
    {
        using var handler = new Handler(Reply, stream);
        using var http = new HttpClient(handler);
        LlmClientBase client = Client(http, vertex);
        var request = new LlmChatRequest
        {
            ModelId = vertex ? "google/" + Model : Model,
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
            Messages =
            [
                new("assistant", "") { ToolCalls = [new LlmToolCall("first", "lookup", "{}"), new LlmToolCall("second", "lookup", "{}")] },
                new("tool", "")
                {
                    ToolResults =
                    [
                        new LlmToolResult("second", LlmToolOutput.FromText("second")),
                        new LlmToolResult("first", new()
                        {
                            IsError = true,
                            Parts = [new LlmToolOutputText("media"),
                                new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", Image, "../a b.png"),
                                new LlmToolOutputBinary(LlmBinaryKind.Document, "application/pdf", Pdf, new string('x', 180))]
                        })
                    ]
                }
            ]
        };
        var events = await Drain(client.StreamAsync(Connection(vertex, stream), request));
        Assert.Equal("done", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        JsonNode contents = JsonNode.Parse(Assert.Single(handler.Bodies))!["contents"]!;
        Assert.Equal(GeminiRequest.DummyThoughtSignature, (string?)contents[0]!["parts"]![0]!["thoughtSignature"]);
        Assert.Null(contents[0]!["parts"]![1]!["thoughtSignature"]);
        Assert.Null(contents[0]!["parts"]![0]!["functionCall"]!["id"]);
        JsonNode function = contents[1]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("media", (string?)function["response"]!["error"]);
        Assert.Equal("second", (string?)contents[1]!["parts"]![1]!["functionResponse"]!["response"]!["output"]);
        JsonArray parts = function["parts"]!.AsArray();
        Assert.Equal(2, parts.Count);
        Assert.Equal("1-.._a_b.png", (string?)parts[0]!["inlineData"]!["displayName"]);
        Assert.Equal(Convert.ToBase64String(Image), (string?)parts[0]!["inlineData"]!["data"]);
        Assert.Equal(Convert.ToBase64String(Pdf), (string?)parts[1]!["inlineData"]!["data"]);
        Assert.Equal(128, ((string)parts[1]!["inlineData"]!["displayName"]!).Length);
        for (int index = 0; index < parts.Count; index++)
        {
            Assert.Equal((string?)parts[index]!["inlineData"]!["displayName"], (string?)function["response"]!["attachments"]![index]!["$ref"]);
        }
        Assert.False(client.GetCapabilities(Connection(vertex, stream), request.ModelId).RichContentSupported.PreservesOrder);
        Assert.Equal(LlmRichContentCarriage.Native, client.GetCapabilities(Connection(vertex, stream), request.ModelId).Agent.RichContentSupported.Images);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultHistoryPolicyRejectsUnsignedCallsBeforeDispatch(bool vertex)
    {
        using var handler = new Handler(Reply, true);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Drain(Client(http, vertex).StreamAsync(Connection(vertex, true), new()
        {
            ModelId = vertex ? "google/" + Model : Model,
            Messages = [new("assistant", "") { ToolCalls = [new LlmToolCall("call", "run", "{}")] },
                new("tool", "") { ToolResults = [new LlmToolResult("call", LlmToolOutput.FromText("ok"))] }]
        })));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Gemini25ImportedCallsHaveNoDummySignatureAndOmitUnsupportedBinaries()
    {
        using var handler = new Handler(Reply, true);
        using var http = new HttpClient(handler);
        var client = new GeminiApiLlmClient(http);
        await Drain(client.StreamAsync(Connection(false, true), new()
        {
            ModelId = "gemini-2.5-flash",
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
            Messages = [new("assistant", "") { ToolCalls = [new LlmToolCall("call", "run", "{}")] },
                new("tool", "") { ToolResults = [new LlmToolResult("call", new() { Parts = [new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", Image)] })] }]
        }));
        Assert.DoesNotContain("thoughtSignature", handler.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("inlineData", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Equal(LlmRichContentCarriage.Unsupported, client.GetCapabilities(Connection(false, true), "gemini-2.5-flash").RichContentSupported.Images);
        Assert.Equal(LlmRichContentCarriage.Native, client.GetCapabilities(Connection(false, true)).RichContentSupported.Images);
    }

    [Theory]
    [InlineData("MALFORMED_FUNCTION_CALL", LlmCompletionStatus.Failed)]
    [InlineData("UNEXPECTED_TOOL_CALL", LlmCompletionStatus.Failed)]
    [InlineData("TOO_MANY_TOOL_CALLS", LlmCompletionStatus.Failed)]
    [InlineData("IMAGE_SAFETY", LlmCompletionStatus.Refused)]
    [InlineData("NEW_REASON", LlmCompletionStatus.Unknown)]
    public async Task TerminalModelOutcomesPreserveTextButNeverCompleteCalls(string reason, LlmCompletionStatus status)
    {
        foreach (bool vertex in new[] { false, true })
        {
            foreach (bool stream in new[] { false, true })
            {
                string reply = JsonSerializer.Serialize(new
                {
                    candidates = new[] { new { content = new { parts = new object[]
                { new { text = "partial" }, new { functionCall = new { name = "run", args = new { } } } } }, finishReason = reason } }
                });
                using var handler = new Handler(reply, stream);
                using var http = new HttpClient(handler);
                var events = await Drain(Client(http, vertex).StreamAsync(Connection(vertex, stream), new() { ModelId = vertex ? "google/" + Model : Model }));
                LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
                Assert.Equal("partial", result.Text);
                Assert.Equal(status, result.Status);
                Assert.Equal(reason, result.FinishReason);
                Assert.Empty(result.ToolCalls);
                Assert.Empty(events.OfType<LlmToolCallCompletedEvent>());
                Assert.Null(result.ContinuationState);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedTerminalArgumentsDoNotCrashTheParser(bool vertex)
    {
        using var handler = new Handler("""{"candidates":[{"content":{"parts":[{"text":"partial"},{"functionCall":{"args":"invalid"}}]},"finishReason":"MALFORMED_FUNCTION_CALL"}]}""", true);
        using var http = new HttpClient(handler);
        var events = await Drain(Client(http, vertex).StreamAsync(Connection(vertex, true), new() { ModelId = vertex ? "google/" + Model : Model }));
        Assert.Equal(LlmCompletionStatus.Failed, Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Status);
        Assert.Empty(events.OfType<LlmToolCallStartedEvent>());
    }

    [Theory]
    [InlineData(false, "MALFORMED_FUNCTION_CALL")]
    [InlineData(true, "MALFORMED_FUNCTION_CALL")]
    [InlineData(false, "STOP")]
    [InlineData(true, "STOP")]
    public async Task InvalidCallWaitsForTheAuthoritativeTerminalReason(bool vertex, string finish)
    {
        const string partial = """{"candidates":[{"content":{"parts":[{"text":"partial"},{"functionCall":{"args":"invalid"}}]}}]}""";
        string terminal = JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = finish } } });
        using var handler = new Handler(partial + "\n" + terminal, true);
        using var http = new HttpClient(handler);
        Task<List<LlmStreamEvent>> operation = Drain(Client(http, vertex).StreamAsync(Connection(vertex, true), new() { ModelId = vertex ? "google/" + Model : Model }));
        if (finish == "STOP")
        {
            LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => operation);
            Assert.DoesNotContain("partial", error.ToString(), StringComparison.Ordinal);
        }
        else
        {
            var events = await operation;
            LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
            Assert.Equal(LlmCompletionStatus.Failed, result.Status);
            Assert.Equal("partial", result.Text);
            Assert.Empty(result.ToolCalls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolPromptCountsReplaceObservationsAndBindFinalContinuationUsage(bool vertex)
    {
        const string first = """{"candidates":[{"content":{"parts":[{"text":"done"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":10,"toolUsePromptTokenCount":7,"candidatesTokenCount":4,"thoughtsTokenCount":3,"totalTokenCount":24}}""";
        const string second = """{"usageMetadata":{"toolUsePromptTokenCount":9,"totalTokenCount":26}}""";
        using var handler = new Handler(first + "\n" + second, true);
        using var http = new HttpClient(handler);
        var events = await Drain(Client(http, vertex).StreamAsync(Connection(vertex, true), new() { ModelId = vertex ? "google/" + Model : Model }));
        Assert.Equal(new int?[] { 17, 19 }, events.OfType<LlmUsageDeltaEvent>().Select(item => item.Cumulative.InputTokens));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(new LlmUsage(19, 7, 26) { ReasoningOutputTokens = 3 }, result.Usage);
        Assert.Equal(19, result.ContinuationState!.InputTokens);
    }

    private static LlmClientBase Client(HttpClient http, bool vertex) => vertex ? new VertexLlmClient(http, new FixedAuthentication()) : new GeminiApiLlmClient(http);
    private static LlmConnection Connection(bool vertex, bool stream) => new()
    {
        Version = "1",
        Transport = vertex ? LlmTransport.GoogleVertexAi : LlmTransport.GoogleGeminiApi,
        Endpoint = vertex ? "https://aiplatform.googleapis.com" : GeminiApiLlmClient.DefaultEndpoint,
        Vertex = vertex ? new() { ProjectId = "p", Location = "global" } : null,
        Credential = vertex ? null : new LlmCredential { ApiKey = "k" }.Serialize(),
        CapabilityOverrides = stream ? null : new() { SupportsStreaming = false }
    };
    private static async Task<List<LlmStreamEvent>> Drain(IAsyncEnumerable<LlmStreamEvent> source)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in source) { events.Add(item); }
        return events;
    }
    private sealed class Handler(string reply, bool stream) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(stream ? string.Concat(reply.Split('\n').Select(frame => "data: " + frame + "\n\n")) : reply,
                Encoding.UTF8, stream ? "text/event-stream" : "application/json")
            };
        }
    }
    private sealed class FixedAuthentication : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken) => Task.FromResult(new VertexAccess("token", "scope"));
    }
}
