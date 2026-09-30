using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexProtocolTests
{
    private const string Gemini = "google/gemini-3-flash-preview";
    private const string Claude = "anthropic/claude-sonnet-4-6";
    private static LlmConnection Connection => new()
    {
        Version = "1",
        Transport = LlmTransport.GoogleVertexAi,
        Endpoint = "https://aiplatform.googleapis.com",
        Vertex = new() { ProjectId = "test-project", Location = "global" }
    };
    private static LlmToolDefinition Tool => new()
    {
        Name = "run",
        InputSchema = JsonElement.Parse("""{"type":"object","properties":{"command":{"type":"string"},"timeout":{"type":"integer"}},"required":["command"]}""")
    };

    [Fact]
    public async Task SamplingRestrictionsFailBeforeAuthenticationAndClaudeUsesSharedWireFields()
    {
        var handler = new Handler(
            """{"type":"message_start","message":{"id":"m"}}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""",
            """{"type":"message_stop"}""");
        var authentication = new Authentication();
        using var http = new HttpClient(handler);
        var client = new VertexLlmClient(http, authentication);
        foreach (var request in new[]
        {
            new LlmChatRequest { ModelId = Gemini, PresencePenalty = 2 },
            new LlmChatRequest { ModelId = Gemini, FrequencyPenalty = -2.1 },
            new LlmChatRequest { ModelId = Claude, ReasoningLevel = LlmReasoningLevel.High, TopK = 17 },
            new LlmChatRequest { ModelId = Claude, ReasoningLevel = LlmReasoningLevel.High, TopP = .94 }
        })
        {
            if (request.ModelId == Claude)
                await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection, request));
            else
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Complete(client, Connection, request));
        }
        Assert.Equal(0, authentication.Calls);
        Assert.Empty(handler.Bodies);
        await Complete(client, Connection, new()
        { ModelId = Claude, Messages = [new("user", "hello")], ReasoningLevel = LlmReasoningLevel.None, TopP = .8, TopK = 17, StopSequences = ["stop"] });
        JsonElement body = JsonElement.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(.8, body.GetProperty("top_p").GetDouble());
        Assert.Equal(17, body.GetProperty("top_k").GetInt32());
        Assert.Equal("stop", body.GetProperty("stop_sequences")[0].GetString());
    }

    [Fact]
    public async Task ClaudePreservesMixedStrictToolsAndIndependentOutputSchema()
    {
        var handler = new Handler(
            """{"type":"message_start","message":{"id":"m"}}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""",
            """{"type":"message_stop"}""");
        var client = new VertexLlmClient(new HttpClient(handler), new Authentication());
        await Complete(client, Connection, new()
        {
            ModelId = Claude,
            Messages = [new("user", "hello")],
            Tools = [Tool, Tool with { Name = "strict_run", Strict = true }],
            ToolChoice = new() { Mode = LlmToolChoiceMode.Auto },
            StructuredOutput = new() { Name = "result", Schema = Tool.InputSchema }
        });
        var payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Null(payload["tools"]?[0]?["strict"]);
        Assert.Equal(true, (bool?)payload["tools"]?[1]?["strict"]);
        Assert.Equal("auto", (string?)payload["tool_choice"]?["type"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Tool.InputSchema.GetRawText()), payload["tools"]?[0]?["input_schema"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Tool.InputSchema.GetRawText()), payload["output_config"]?["format"]?["schema"]));
    }

    [Fact]
    public void UnsupportedGeminiSchemaRequiresExplicitFallback()
    {
        var request = new LlmStructuredOutputRequest
        { Name = "result", Schema = JsonElement.Parse("""{"type":"string","pattern":"^a"}""") };
        Assert.Throws<NotSupportedException>(() => VertexSchemas.Enforcement(request, false));
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly,
            VertexSchemas.Enforcement(request with { Fallback = LlmStructuredOutputFallback.LocalValidation }, false));
    }

    [Fact]
    public async Task CatalogPaginationKeepsAccessUnknownAndFiltersUnreviewedModels()
    {
        var handler = new Handler();
        handler.CatalogReplies.Enqueue("""{"publisherModels":[{"name":"publishers/google/models/unreviewed"}],"nextPageToken":"page+2"}""");
        handler.CatalogReplies.Enqueue("""{"publisherModels":[{"name":"publishers/google/models/gemini-3-flash-preview"}]}""");
        handler.CatalogReplies.Enqueue("""{"publisherModels":[{"name":"publishers/anthropic/models/claude-sonnet-4-6"}]}""");
        var client = new VertexLlmClient(new HttpClient(handler), new Authentication());
        var models = await client.ListModelsAsync(Connection);
        Assert.Equal(2, models.Count);
        Assert.All(models, model => Assert.Equal(LlmModelAvailability.Unknown, model.Availability));
        Assert.Contains("pageToken=page%2B2", handler.CatalogUris[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailureRetainsStatusWithoutProviderBody()
    {
        var handler = new Handler() { Status = HttpStatusCode.Forbidden };
        var client = new VertexLlmClient(new HttpClient(handler), new Authentication());
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Complete(client, Connection, new() { ModelId = Gemini }));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-error", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("STOP", LlmCompletionStatus.Completed)]
    [InlineData("MAX_TOKENS", LlmCompletionStatus.Incomplete)]
    [InlineData("SAFETY", LlmCompletionStatus.Refused)]
    [InlineData("NEW_REASON", LlmCompletionStatus.Unknown)]
    public async Task GeminiTerminalReasonsRemainDistinct(string reason, LlmCompletionStatus expected)
    {
        string frame = JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = reason } } });
        var client = new VertexLlmClient(new HttpClient(new Handler(frame)), new Authentication());
        var result = await Complete(client, Connection, new() { ModelId = Gemini });
        Assert.Equal(expected, result.Status);
        if (expected != LlmCompletionStatus.Completed) { Assert.Null(result.ContinuationState); }
    }

    [Theory]
    [InlineData("""{"error":{"message":"private-provider-error"}}""")]
    [InlineData("""{"candidates":[{"finishReason":"MALFORMED_FUNCTION_CALL"}]}""")]
    [InlineData("not-json")]
    public async Task InvalidStreamsFailWithoutRawProviderContent(string frame)
    {
        var client = new VertexLlmClient(new HttpClient(new Handler(frame)), new Authentication());
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Complete(client, Connection, new() { ModelId = Gemini }));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-provider-error", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameNameResultsAreRestoredToTheirOriginalCallOrder()
    {
        var client = new VertexLlmClient(new HttpClient(new Handler()), new Authentication());
        var prepared = await client.PrepareAsync(Connection, default);
        var content = JsonNode.Parse("""[{"functionCall":{"name":"run","args":{"command":"first"}},"connectCallId":"one","thoughtSignature":"signature"},{"functionCall":{"name":"run","args":{"command":"second"}},"connectCallId":"two"}]""")!.AsArray();
        var state = new VertexContinuationCodec().Encode(prepared, Gemini, content);
        var request = new LlmChatRequest
        {
            ModelId = Gemini,
            Messages = [new("assistant", "") { ContinuationState = state,
                ToolCalls = [new("one", "run", """{"command":"first"}"""), new("two", "run", """{"command":"second"}""")] },
                new("user", "") { ToolResults = [new("two", LlmToolOutput.FromText("second-result")), new("one", LlmToolOutput.FromText("first-result"))] }]
        };
        var payload = GeminiRequest.Build(prepared, request, null,
            output => ToolOutputProjector.Project(output, RichContentRoutes.TextOnly, RichContentRoutes.TextOnly),
            GeminiModelProfiles.Get("gemini-3-flash-preview", catalogThinking: null), reasoningText: true);
        Assert.Equal("first-result", (string?)payload["contents"]?[1]?["parts"]?[0]?["functionResponse"]?["response"]?["output"]);
        Assert.Equal("second-result", (string?)payload["contents"]?[1]?["parts"]?[1]?["functionResponse"]?["response"]?["output"]);
    }

    [Fact]
    public async Task ManagedGeminiRetainsSignedToolHistoryAcrossCaptureAndImport()
    {
        var handler = new Handler();
        handler.Replies.Enqueue(["""{"candidates":[{"content":{"parts":[{"functionCall":{"name":"run","args":{"command":"pwd"}},"thoughtSignature":"tool-signature"}]},"finishReason":"STOP"}]}"""]);
        string done = """{"candidates":[{"content":{"parts":[{"text":"done","thoughtSignature":"text-signature"}]},"finishReason":"STOP"}]}""";
        handler.Replies.Enqueue([done]);
        handler.Replies.Enqueue([done]);
        var client = new VertexLlmClient(new HttpClient(handler), new Authentication());
        var provider = new LlmProviderService(new Source(), [client]);
        var agent = new ManagedVertexAgentClient(client, provider);
        int calls = 0;
        var options = new LlmAgentSessionOptions
        {
            ModelId = Gemini,
            Tools = [new() { Name = Tool.Name, InputSchema = Tool.InputSchema }],
            ToolHandler = (_, _) => { calls++; return Task.FromResult(LlmToolOutput.FromText("/tmp")); }
        };
        await using var session = await agent.StartSessionAsync("fixture", Connection, options);
        await foreach (var item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Run pwd")] }))
        { if (item is LlmAgentTurnCompletedEvent end) { Assert.Equal(LlmAgentTurnStatus.Completed, end.Status); } }
        Assert.Equal(1, calls);
        var capture = await session.CaptureContextAsync(new() { IncludeAttachmentData = true });
        var restored = JsonSerializer.Deserialize<LlmAgentContext>(JsonSerializer.Serialize(capture))!;
        Assert.Contains(restored.Entries, entry => entry.Message.ContinuationState?.Payload.Contains("tool-signature", StringComparison.Ordinal) == true);
        await using var reopened = await agent.StartSessionAsync("fixture", Connection, options with { Context = restored });
        await foreach (var item in reopened.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Continue without tools")] }))
        { if (item is LlmAgentTurnCompletedEvent end) { Assert.Equal(LlmAgentTurnStatus.Completed, end.Status); } }
        Assert.Equal(1, calls);
        Assert.Contains("tool-signature", handler.Bodies[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeminiPreservesNonStrictSchemasSignedCallsAndUsageTrailers()
    {
        var handler = new Handler(
            """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"run","args":{"command":"pwd"}},"thoughtSignature":"signed"}]},"finishReason":"STOP"}]}""",
            """{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":4,"thoughtsTokenCount":3,"cachedContentTokenCount":2,"totalTokenCount":17}}""");
        var client = new VertexLlmClient(new HttpClient(handler), new Authentication());
        LlmChatResult first = await Complete(client, Connection, new() { ModelId = Gemini, Tools = [Tool], ToolChoice = new(), Messages = [new("user", "Run pwd")] });
        Assert.Equal(LlmCompletionStatus.RequiresAction, first.Status);
        Assert.Equal(new LlmUsage(10, 7, 17) { CachedInputTokens = 2, ReasoningOutputTokens = 3 }, first.Usage);
        JsonObject payload = JsonNode.Parse(handler.Bodies[0])!.AsObject();
        Assert.Equal("AUTO", (string?)payload["toolConfig"]?["functionCallingConfig"]?["mode"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Tool.InputSchema.GetRawText()), payload["tools"]?[0]?["functionDeclarations"]?[0]?["parametersJsonSchema"]));
        Assert.DoesNotContain("strict", handler.Bodies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("aiplatform.googleapis.com", handler.Uris[0].Host);
        Assert.Contains("/publishers/google/models/gemini-3-flash-preview:streamGenerateContent", handler.Uris[0].AbsolutePath, StringComparison.Ordinal);
        var assistant = new LlmChatMessage("assistant", first.Text) { ContinuationState = first.ContinuationState, ToolCalls = first.ToolCalls };
        var prepared = await client.PrepareAsync(Connection, default);
        JsonObject next = GeminiRequest.Build(prepared, new()
        {
            ModelId = Gemini,
            Messages = [new("user", "Run pwd"), assistant,
                new("tool", "") { ToolResults = [new(first.ToolCalls[0].Id, new LlmToolOutput { Parts = [new LlmToolOutputText("/tmp")] })] }]
        }, null, output => ToolOutputProjector.Project(output, new(), new()), GeminiModelProfiles.Get("gemini-3-flash-preview", catalogThinking: null), reasoningText: true);
        Assert.Equal("signed", (string?)next["contents"]?[1]?["parts"]?[0]?["thoughtSignature"]);
        Assert.DoesNotContain("connectCallId", next.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("run", (string?)next["contents"]?[2]?["parts"]?[0]?["functionResponse"]?["name"]);
        Assert.Throws<ArgumentException>(() => new VertexContinuationCodec().DecodeMessage(assistant with { Content = "changed" }, prepared, Gemini));
    }

    [Fact]
    public async Task ClaudeUsesVertexEnvelopeAndNormalizesCacheCounters()
    {
        var handler = new Handler(
            """{"type":"message_start","message":{"id":"m","usage":{"input_tokens":10,"cache_read_input_tokens":5,"cache_creation_input_tokens":2}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"OK"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}""",
            """{"type":"message_stop"}""");
        var client = new VertexLlmClient(new HttpClient(handler), new Authentication());
        var result = await Complete(client, Connection, new() { ModelId = Claude, Messages = [new("user", "hello")], Tools = [Tool] });
        var payload = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Null(payload["model"]);
        Assert.Equal("vertex-2023-10-16", (string?)payload["anthropic_version"]);
        Assert.Null(payload["tools"]?[0]?["strict"]);
        Assert.Equal("OK", result.Text);
        Assert.Equal(new LlmUsage(17, 3, 20) { CachedInputTokens = 5, CacheWriteInputTokens = 2 }, result.Usage);
        Assert.NotNull(result.ContinuationState);
        Assert.Null(result.Diagnostics.RawFinalResponseJson);
        Assert.Equal("Bearer test-token", Assert.Single(handler.Authorization));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task QualifiedClaudeProfileKeepsItsRequestAndSignedStateWithDisplayDisabled(bool streaming, bool display)
    {
        string[] reply = streaming
            ? ["""{"type":"message_start","message":{"id":"m"}}""",
                """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Plan."}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"signed"}}""",
                """{"type":"content_block_stop","index":0}""",
                """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":"Answer."}}""",
                """{"type":"content_block_stop","index":1}""",
                """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""",
                """{"type":"message_stop"}"""]
            : ["""{"type":"message","content":[{"type":"thinking","thinking":"Plan.","signature":"signed"},{"type":"text","text":"Answer."}],"stop_reason":"end_turn"}"""];
        var handler = new Handler(reply) { Json = !streaming };
        var client = new VertexLlmClient(new HttpClient(handler), new Authentication());
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(Connection with
        {
            CapabilityOverrides = new() { SupportsStreaming = streaming, SupportsReasoningText = display }
        }, new() { ModelId = Claude, Messages = [new("user", "hello")], ReasoningLevel = LlmReasoningLevel.High }))
        { events.Add(item); }
        Assert.Equal(display ? 1 : 0, events.OfType<LlmReasoningDeltaEvent>().Count());
        LlmChatResult result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        Assert.Equal("Answer.", result.Text);
        Assert.Contains("signed", result.ContinuationState!.Payload, StringComparison.Ordinal);
        JsonNode payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Equal("adaptive", (string?)payload["thinking"]?["type"]);
        Assert.Null(payload["thinking"]?["display"]);
        Assert.Equal("high", (string?)payload["output_config"]?["effort"]);
    }

    [Fact]
    public async Task StreamingOffCallsTheCompleteOperations()
    {
        LlmConnection connection = Connection with { CapabilityOverrides = new() { SupportsStreaming = false } };
        var claudeHandler = new Handler("""
            {"id":"m","type":"message","role":"assistant","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":1}}
            """) { Json = true };
        LlmChatResult claude = await Complete(new VertexLlmClient(new HttpClient(claudeHandler), new Authentication()), connection,
            new() { ModelId = Claude, Messages = [new("user", "hello")] });
        Assert.EndsWith("/publishers/anthropic/models/claude-sonnet-4-6:rawPredict", claudeHandler.Uris[0].AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(false, (bool?)JsonNode.Parse(claudeHandler.Bodies[0])?["stream"]);
        Assert.Equal("OK", claude.Text);

        var geminiHandler = new Handler("""{"candidates":[{"content":{"parts":[{"text":"hi"}]},"finishReason":"STOP"}]}""") { Json = true };
        LlmChatResult gemini = await Complete(new VertexLlmClient(new HttpClient(geminiHandler), new Authentication()), connection,
            new() { ModelId = Gemini, Messages = [new("user", "hello")] });
        Assert.EndsWith("/publishers/google/models/gemini-3-flash-preview:generateContent", geminiHandler.Uris[0].AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(string.Empty, geminiHandler.Uris[0].Query);
        Assert.Equal("hi", gemini.Text);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, false)]
    public async Task GeminiRejectsOnlyExplicitUnsupportedControls(bool strict, bool? parallel)
    {
        var handler = new Handler();
        var client = new VertexLlmClient(new HttpClient(handler), new Authentication());
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection, new()
        { ModelId = Gemini, Tools = [Tool with { Strict = strict }], AllowParallelToolCalls = parallel }));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task IdentityChangeFailsBeforeDispatch()
    {
        var authentication = new Authentication();
        var handler = new Handler();
        var client = new VertexLlmClient(new HttpClient(handler), authentication);
        var pinned = await client.PrepareAsync(Connection, default);
        authentication.Scope = "different-principal";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(client, pinned, new() { ModelId = Gemini }));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task PrematureEofAndProviderErrorsNeverBecomeCompletions()
    {
        var handler = new Handler("""{"candidates":[{"content":{"parts":[{"text":"partial"}]}}]}""");
        var client = new VertexLlmClient(new HttpClient(handler), new Authentication());
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Complete(client, Connection, new() { ModelId = Gemini }));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("partial", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("google/../../model")]
    [InlineData("other/model")]
    [InlineData("anthropic/model?key=secret")]
    public void ModelIdsCannotChangeRouting(string id) => Assert.Throws<ArgumentException>(() => VertexEndpoints.ParseModel(id));

    private static async Task<LlmChatResult> Complete(VertexLlmClient client, LlmConnection connection, LlmChatRequest request)
    {
        var completions = new List<LlmChatResult>();
        await foreach (var item in client.StreamAsync(connection, request))
        { if (item is LlmChatCompletionEvent completion) { completions.Add(completion.Result); } }
        return Assert.Single(completions);
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(Connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Vertex must not replace host credentials.");
    }

    private sealed class Authentication : IVertexAuthentication
    {
        public string Scope { get; set; } = "stable-principal";
        public int Calls { get; private set; }
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new VertexAccess("test-token", Scope)); }
    }

    private sealed class Handler(params string[] frames) : HttpMessageHandler
    {
        public Queue<string[]> Replies { get; } = new();
        public Queue<string> CatalogReplies { get; } = new();
        public List<Uri> CatalogUris { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        /// <summary>True answers with the frames as one JSON body, as the complete operations do.</summary>
        public bool Json { get; init; }
        public List<string> Bodies { get; } = [];
        public List<Uri> Uris { get; } = [];
        public List<string?> Authorization { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Status != HttpStatusCode.OK) { return new(Status) { Content = new StringContent("private-error") }; }
            if (request.Method == HttpMethod.Get)
            {
                CatalogUris.Add(request.RequestUri!);
                if (CatalogReplies.Count > 0) { return new(HttpStatusCode.OK) { Content = new StringContent(CatalogReplies.Dequeue(), Encoding.UTF8, "application/json") }; }
                string publisher = request.RequestUri!.AbsolutePath.Contains("/google/", StringComparison.Ordinal) ? "google" : "anthropic";
                string native = publisher == "google" ? "gemini-3-flash-preview" : "claude-sonnet-4-6";
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    { publisherModels = new[] { new { name = $"publishers/{publisher}/models/{native}" } } }), Encoding.UTF8, "application/json")
                };
            }
            Uris.Add(request.RequestUri!);
            Authorization.Add(request.Headers.Authorization?.ToString());
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            string[] reply = Replies.Count > 0 ? Replies.Dequeue() : frames;
            return new(HttpStatusCode.OK)
            {
                Content = Json
                    ? new StringContent(string.Concat(reply), Encoding.UTF8, "application/json")
                    : new StringContent(string.Concat(reply.Select(frame => "data: " + frame + "\n\n")), Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
