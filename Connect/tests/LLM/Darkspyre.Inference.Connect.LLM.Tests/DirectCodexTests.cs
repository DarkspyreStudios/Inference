using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class DirectCodexTests
{
    [Fact]
    public async Task UnknownMappedControlsReachTheDirectEndpointUnchanged()
    {
        using var handler = new Handler(_ => Sse(Completed));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        LlmConnection connection = Connection();
        LlmProviderCapabilities capabilities = client.GetCapabilities(connection, "model");
        Assert.Equal(InferenceSupport.Unknown, capabilities.SupportsTemperature);
        Assert.Equal(InferenceSupport.Unknown, capabilities.SupportsTopP);
        Assert.Equal(LlmOutputLimitEnforcement.Unknown, capabilities.OutputLimitEnforcement);

        LlmChatResult result = await Complete(client, connection, Request() with
        { Temperature = 0.7, TopP = 0.5, MaxOutputTokens = 123 });

        Assert.Equal("Hello", result.Text);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(0.7, sent.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.5, sent.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(123, sent.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("/backend-api/codex/responses", handler.Uri!.AbsolutePath);
        Assert.Equal("0.159.3", handler.Headers["version"]);
        Assert.Equal("Darkspyre.Inference.Connect.LLM/0.159.3", handler.Headers["User-Agent"]);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CatalogAndGenerationUseTheSameCompatibilityPinAndSelectedModelFacts()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        int generations = 0;
        using var handler = new Handler(request =>
        {
            Assert.Equal("0.159.3", Assert.Single(request.Headers.GetValues("version")));
            Assert.Equal("Darkspyre.Inference.Connect.LLM/0.159.3", request.Headers.UserAgent.ToString());
            Assert.Equal("account", Assert.Single(request.Headers.GetValues("ChatGPT-Account-ID")));
            Assert.Equal("darkspyre-inference-connect", Assert.Single(request.Headers.GetValues("originator")));
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("token", request.Headers.Authorization.Parameter);
            string path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (path == "/backend-api/codex/models")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("?client_version=0.159.3", request.RequestUri.Query);
                return Json("""
                    {"models":[
                        {"slug":"model","display_name":"Selected model","context_window":null,"max_context_window":65536,
                         "supported_reasoning_levels":[{"effort":"low"}],"supports_reasoning_summary_parameter":false,"input_modalities":["text"]},
                        {"slug":"hidden","visibility":"hide","context_window":100000}
                    ]}
                    """);
            }
            if (path == "/backend-api/wham/usage")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(string.Empty, request.RequestUri.Query);
                return Json("""
                    {"plan_type":"plus","rate_limit":{"allowed":true,"limit_reached":false,
                     "primary_window":{"used_percent":25,"limit_window_seconds":18000,"reset_at":2000000000}}}
                    """);
            }
            Assert.Equal("/backend-api/codex/responses", path);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("conversation", Assert.Single(request.Headers.GetValues("session-id")));
            Assert.Equal("text/event-stream", request.Headers.Accept.ToString());
            generations++;
            return Sse(Completed);
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        await using var providers = new LlmProviderService(new Source(Connection()), [client]);

        LlmModel model = Assert.Single(await providers.ListModelsAsync("codex"));
        Assert.Equal("model", model.Id);
        Assert.Equal("Selected model", model.DisplayName);
        Assert.Equal(65536, model.MaxContextTokens);
        LlmProviderCapabilities capabilities = await providers.GetModelCapabilitiesAsync("codex", model.Id);
        Assert.Equal(LlmReasoningLevel.Low, Assert.Single(capabilities.ReasoningLevels));
        Assert.Equal(InferenceSupport.Unsupported, capabilities.SupportsReasoningText);
        Assert.Equal(InferenceSupport.Unsupported, capabilities.SupportsPromptImages);

        LlmChatRequest prompt = Request() with { ReasoningLevel = LlmReasoningLevel.Low, ConversationKey = "conversation" };
        LlmChatResult result = await providers.CompleteAsync("codex", prompt);
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("Hello", result.Text);
        using (JsonDocument sent = JsonDocument.Parse(handler.Body!))
        {
            Assert.Equal("model", sent.RootElement.GetProperty("model").GetString());
            Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
            Assert.Equal("low", sent.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.False(sent.RootElement.GetProperty("reasoning").TryGetProperty("summary", out _));
        }
        Assert.Equal(1, generations);
        await providers.CompleteAsync("codex", prompt with { ReasoningLevel = LlmReasoningLevel.High });
        using (JsonDocument sent = JsonDocument.Parse(handler.Body!))
        {
            Assert.Equal("low", sent.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        }
        Assert.Equal(2, generations);

        LlmAccountUsageSnapshot usage = await providers.GetAccountUsageAsync("codex");
        Assert.Equal("plus", usage.Plan);
        Assert.Equal(0.25, Assert.Single(usage.Quotas).UsedFraction);
        Assert.Equal(3, paths.Count);
    }

    [Theory]
    [InlineData(LlmReasoningLevel.Maximum, "high", "high")]
    [InlineData(LlmReasoningLevel.Maximum, "", "max")]
    [InlineData(null, "", null)]
    public async Task AdapterMapsDesiredEffortWithoutChangingTheCallerRequest(
        LlmReasoningLevel? requested, string nativeLevels, string? expected)
    {
        using var handler = new Handler(_ => Sse(Completed));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        var presets = new JsonArray(nativeLevels.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => (JsonNode)new JsonObject { ["effort"] = value }).ToArray());
        LlmConnection connection = Connection() with
        {
            Catalog = nativeLevels.Length == 0 ? null : [new("model", "Model")
            { CatalogEntry = JsonElement.Parse(new JsonObject { ["supported_reasoning_levels"] = presets }.ToJsonString()) }]
        };
        LlmChatRequest request = Request() with { ReasoningLevel = requested };
        if (nativeLevels.Length == 0)
            Assert.Equal(InferenceSupport.Unknown, client.GetCapabilities(connection, "model").SupportsReasoning);

        // The facade normalizes every request before its client runs; the client sends the normalized level.
        LlmChatResult result = await Complete(client, connection,
            LlmRequestNormalizer.Normalize(connection, request, client.GetCapabilities(connection, request.ModelId)));

        Assert.Equal("Hello", result.Text);
        Assert.Equal(requested, request.ReasoningLevel);
        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement reasoning = sent.RootElement.GetProperty("reasoning");
        Assert.Equal(expected is not null, reasoning.TryGetProperty("effort", out JsonElement effort));
        if (expected is not null) Assert.Equal(expected, effort.GetString());
        Assert.Equal(expected != "none", reasoning.TryGetProperty("summary", out _));
        Assert.Equal(1, handler.Calls);
    }

    internal const string Completed = """
        data: {"type":"response.output_text.delta","delta":"Hello"}

        data: {"type":"response.completed","response":{"id":"response","status":"completed","output":[{"type":"message","id":"msg","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"Hello"}]}],"usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}}

        """;

    [Fact]
    public async Task ExperimentalContextModelsTakeTheCatalogMaximum()
    {
        using var handler = new Handler(_ => Json("""
            {"models":[
                {"slug":"wide","context_window":272000,"max_context_window":872000,"supports_experimental_context":true},
                {"slug":"plain","context_window":272000,"max_context_window":872000,"supports_experimental_context":false}
            ]}
            """));
        var client = new OpenAiCodexLlmClient(new HttpClient(handler));

        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(Connection());

        Assert.Equal(872000, Assert.Single(models, model => model.Id == "wide").MaxContextTokens);
        Assert.Equal(272000, Assert.Single(models, model => model.Id == "plain").MaxContextTokens);
    }

    [Fact]
    public async Task ReportedTokensTravelWithTheStateButTheEstimateCountsTheActualRequest()
    {
        using var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json("{\"models\":[{\"slug\":\"model\",\"context_window\":100000}]}")
            : Sse(ReasonedToolCall.Replace("\"id\":\"r\"}", "\"id\":\"r\",\"usage\":{\"input_tokens\":1200,\"output_tokens\":80,\"total_tokens\":1280}}",
                StringComparison.Ordinal)));
        var client = new OpenAiCodexLlmClient(new HttpClient(handler));
        LlmChatResult first = await Complete(client, Connection(), Request());
        Assert.Equal((1200L, 80L), (first.ContinuationState!.InputTokens, first.ContinuationState.OutputTokens));
        LlmChatRequest followup = Request() with
        {
            Messages = [new("user", "first"),
                new("assistant", first.Text) { ToolCalls = first.ToolCalls, ContinuationState = first.ContinuationState },
                new("user", "") { ToolResults = [new LlmToolResult("call", LlmToolOutput.FromText("answer"))] }]
        };

        ContextEstimate estimate = (await client.EstimateAsync(Connection(), followup, ResourceSizes.None, CancellationToken.None))!.Value;

        // The earlier request's 1,280 reported tokens are not this request; the estimate tokenizes what is sent.
        Assert.False(estimate.IncludesProviderReport);
        Assert.InRange(estimate.Tokens, 1, 1279);
    }

    private const string ReasonedToolCall = """
        data: {"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc","call_id":"call","name":"echo","arguments":"{}"}}

        data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"reason","encrypted_content":"opaque","summary":[{"type":"summary_text","text":"Thinking"}]}}

        data: {"type":"response.completed","response":{"status":"completed","output":[],"id":"r"}}

        """;

    [Fact]
    public async Task FilesAndResourcesAreCounted()
    {
        using var handler = new Handler(_ => Json("{\"models\":[{\"slug\":\"model\",\"context_window\":272000}]}"));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        var reference = new InferenceResourceReference("workspace", "notes");
        var resources = new Dictionary<InferenceResourceReference, InferenceResourceDescriptor?>
        {
            [reference] = new(reference, "session", "notes.txt", "text/plain", 4000)
        };
        var image = new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "chart.png", MediaType = "image/png", Data = new byte[] { 1, 2, 3 } };
        var output = new LlmToolOutput { Parts = [new LlmToolOutputText("done"), new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", new byte[] { 4, 5 }, "shot.png")] };
        LlmChatMessage[] Messages(bool media) =>
        [
            new("assistant", "") { ToolCalls = [new LlmToolCall("call", "echo", "{}")] },
            new("user", "") { ToolResults = [new LlmToolResult("call", media ? output : output with { Parts = [output.Parts[0]] })] },
            new("user", "Compare these") { Attachments = media ? [image] : [] }
        ];
        var plain = new LlmChatRequest { ModelId = "model", Messages = Messages(media: false) };

        long baseline = (await client.EstimateAsync(Connection(), plain, ResourceSizes.None, CancellationToken.None))!.Value.Tokens;
        long counted = (await client.EstimateAsync(Connection(), plain with { Messages = Messages(media: true) }, resources, CancellationToken.None))!.Value.Tokens;

        // Two images at 1,600 each and the selected text resource at four bytes per token.
        Assert.Equal(baseline + 1_600 + 1_600 + 1_000, counted);
    }

    [Fact]
    public async Task CompactionAccountingRetainsCodexEstimateDespiteOmittedAttachmentHistory()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/backend-api/codex/models", request.RequestUri!.AbsolutePath);
            return Json("{\"models\":[{\"slug\":\"model\",\"context_window\":272000}]}");
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        LlmConnection connection = Connection();
        LlmContinuationState state = CodexReplay.Codec.Encode(connection, "model",
            new JsonArray(new JsonObject
            {
                ["type"] = "compaction",
                ["encrypted_content"] = new string('a', 2000)
            })) with
        { InputTokens = 1200, OutputTokens = 80 };
        LlmChatMessage[] messages =
        [
            new("user", "Earlier question") { OmittedAttachments = [new(LlmBinaryKind.Document, "reference.txt", 0)] },
            new("assistant", "") { ContinuationState = state },
            new("user", "Next question")
        ];
        var request = new LlmChatRequest { ModelId = "model", Messages = messages, Instructions = "Answer briefly." };
        var policy = new LlmAgentContextPolicy { ReservedOutputTokens = 8000 };
        LlmContextMeasurementSource source = LlmContextMeasurementSource.Estimated;
        ContextEstimate expected = (await client.EstimateAsync(connection, request, ResourceSizes.None, CancellationToken.None))!.Value;
        var context = new LlmAgentContext
        {
            Revision = 7,
            Entries = messages.Select((message, index) => new LlmAgentContextEntry("entry-" + index, "turn", message)).ToArray()
        };
        await using var session = await ManagedAgentSession.CreateAsync("test-connection", connection, new()
        {
            ModelId = "model",
            Context = context,
            Instructions = request.Instructions,
            ExecutionOptions = new() { ContextPolicy = policy }
        }, [new("model", "Model") { MaxContextTokens = 272000 }], client,
            (_, _) => throw new InvalidOperationException("Accounting must not generate a response."),
            model => client.GetCapabilities(connection, model), new LearnedModelContext());
        LlmRequestAccounting measured = await session.MeasureContextAsync(context);
        LlmAgentContextStatus status = await session.GetContextStatusAsync();

        // The local estimate plus the margin every estimated measurement carries.
        Assert.Equal(expected.Tokens + ManagedAgentPolicy.EstimateMargin, measured.UsedContextTokens);
        Assert.Equal(expected.Tokens + ManagedAgentPolicy.EstimateMargin, status.UsedContextTokens);
        Assert.Equal(source, measured.Source);
        Assert.Equal(source, status.Source);
        Assert.Equal(7, measured.ContextRevision);
        Assert.False(measured.NeedsCompaction);
        Assert.Single((await session.CaptureContextAsync()).Entries[0].Message.OmittedAttachments);
        Assert.Single(messages[0].OmittedAttachments);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task OmitCarriesAnotherModelsToolHistoryWithoutItsStateAndReportsTheCount()
    {
        (Handler handler, LlmProviderService providers, LlmChatRequest followup) = await OtherModelFollowupAsync();
        using (handler)
        {
            LlmChatResult result = await providers.CompleteAsync("codex", followup);

            using var sent = JsonDocument.Parse(handler.Body!);
            string?[] types = [.. sent.RootElement.GetProperty("input").EnumerateArray()
                .Select(item => item.TryGetProperty("type", out JsonElement type) ? type.GetString() : "message")];
            Assert.DoesNotContain("reasoning", types);
            Assert.Contains("function_call", types);
            Assert.Contains("function_call_output", types);
            Assert.DoesNotContain("opaque", handler.Body!, StringComparison.Ordinal);
            Assert.Equal(1, result.OmittedContinuationStateCount);
            Assert.NotNull(followup.Messages[1].ContinuationState);
        }
    }

    private const string LongName = "mcp.server/look up";

    /// <summary>A reply with reasoning state and one call under the alias of <see cref="LongName"/>.</summary>
    private static string AliasedToolCall() => ReasonedToolCall.Replace("\"name\":\"echo\"",
        $"\"name\":\"{LlmToolNames.Alias(LongName)}\"", StringComparison.Ordinal);

    private static LlmToolDefinition LongNameTool() =>
        new() { Name = LongName, InputSchema = JsonElement.Parse("""{"type":"object"}""") };

    [Fact]
    public async Task InvalidToolNameIsAliasedAndMappedBack()
    {
        string alias = LlmToolNames.Alias(LongName);
        using var handler = new Handler(_ => Sse(AliasedToolCall()));
        var client = new OpenAiCodexLlmClient(new HttpClient(handler));
        LlmChatRequest request = Request() with
        {
            Tools = [LongNameTool()],
            ToolChoice = new LlmToolChoice { Mode = LlmToolChoiceMode.Specific, Name = LongName },
            Messages = [new("user", "first"),
                new("assistant", "") { ToolCalls = [new LlmToolCall("earlier", LongName, "{}")] },
                new("user", "") { ToolResults = [new LlmToolResult("earlier", LlmToolOutput.FromText("answer"))] }]
        };

        List<LlmStreamEvent> events = await Read(client.StreamAsync(Connection(), request));

        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(alias, sent.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal(alias, sent.RootElement.GetProperty("tool_choice").GetProperty("name").GetString());
        Assert.Equal(alias, sent.RootElement.GetProperty("input").EnumerateArray()
            .Single(item => item.TryGetProperty("type", out JsonElement type) && type.GetString() == "function_call")
            .GetProperty("name").GetString());
        Assert.DoesNotContain(LongName, handler.Body!, StringComparison.Ordinal);
        Assert.Equal(LongName, Assert.Single(events.OfType<LlmToolCallStartedEvent>()).Name);
        Assert.Equal(LongName, Assert.Single(events.OfType<LlmToolCallCompletedEvent>()).ToolCall.Name);
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LongName, Assert.IsType<LlmToolCall>(Assert.Single(result.ToolCalls)).Name);
    }

    [Fact]
    public async Task AliasedCallsSurviveTheContinuationCheck()
    {
        using var handler = new Handler(_ => Sse(AliasedToolCall()));
        var client = new OpenAiCodexLlmClient(new HttpClient(handler));
        LlmChatRequest request = Request() with { Tools = [LongNameTool()] };
        LlmChatResult first = await Complete(client, Connection(), request);
        LlmChatMessage assistant = new("assistant", first.Text) { ToolCalls = first.ToolCalls, ContinuationState = first.ContinuationState };

        (LlmAgentContext imported, int omitted) = ManagedAgentContext.Import(new()
        {
            Entries = [new("u", "t", new("user", "hello")), new("a", "t", assistant)]
        }, Connection(), "model", codec: client.ContinuationCodec);

        Assert.Equal(0, omitted);
        Assert.NotNull(imported.Entries[1].Message.ContinuationState);
        handler.Response = _ => Sse(Completed);
        LlmChatResult next = await Complete(client, Connection(), request with
        {
            Messages = [new("user", "hello"), assistant,
                new("user", "") { ToolResults = [new LlmToolResult("call", LlmToolOutput.FromText("answer"))] }]
        });
        Assert.Equal("Hello", next.Text);
        Assert.Contains("opaque", handler.Body!, StringComparison.Ordinal); // The state was replayed, not dropped.
    }

    [Fact]
    public async Task ToolSchemaIsSentUnchangedWithStrictFalse()
    {
        const string schema = """{"type":"object","properties":{"path":{"type":"string"}}}""";
        using var handler = new Handler(_ => Sse(Completed));
        var client = new OpenAiCodexLlmClient(new HttpClient(handler));

        await Complete(client, Connection(), Request() with
        {
            Tools = [new LlmToolDefinition { Name = "read", Description = "Reads a file.", InputSchema = JsonElement.Parse(schema) }]
        });

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement tool = Assert.Single(sent.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read", tool.GetProperty("name").GetString());
        Assert.Equal("Reads a file.", tool.GetProperty("description").GetString());
        Assert.Equal(schema, tool.GetProperty("parameters").GetRawText());
        Assert.Equal(JsonValueKind.False, tool.GetProperty("strict").ValueKind);
    }

    /// <summary>An image_generation_call item is saved as a session resource and reported as an image output.</summary>
    [Fact]
    public async Task ImageGenerationCommitsAResource()
    {
        using var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json("{\"models\":[{\"slug\":\"model\",\"context_window\":272000}]}")
            : Sse("""
                data: {"type":"response.output_item.added","output_index":0,"item":{"type":"image_generation_call","id":"ig","status":"in_progress"}}

                data: {"type":"response.output_item.done","output_index":0,"item":{"type":"image_generation_call","id":"ig","status":"completed","result":"AQID","output_format":"webp"}}

                data: {"type":"response.output_item.done","output_index":1,"item":{"type":"message","id":"m","role":"assistant","content":[{"type":"output_text","text":"Here it is."}]}}

                data: {"type":"response.completed","response":{"status":"completed","output":[],"id":"r"}}

                """));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        LlmConnection connection = Connection();
        ResourcePreparationTests.GeneratedWrite? written = null;
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 0, []), [])
        {
            WriteFactory = create => written = new("workspace", create, () => { })
        };
        await using var session = await ManagedAgentSession.CreateAsync("test-connection", connection, new()
        {
            ModelId = "model",
            HostedTools = [new LlmImageGenerationTool()],
            CallerServices = new() { Resources = access }
        }, [new("model", "Model") { MaxContextTokens = 272000 }], client,
            (request, token) => client.StreamAsync(connection, request, token),
            model => client.GetCapabilities(connection, model), new LearnedModelContext());

        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Draw a cat")] }))
            events.Add(item);

        Assert.Contains(JsonNode.Parse(handler.Body!)!["tools"]!.AsArray(), tool => tool!["type"]!.GetValue<string>() == "image_generation");
        Assert.Equal([1, 2, 3], written!.CommittedBytes);
        LlmAgentImageOutputEvent image = Assert.Single(events.OfType<LlmAgentImageOutputEvent>());
        Assert.Equal(written.Reference, image.Image.Resource);
        Assert.Equal("image/webp", image.MediaType);
        Assert.Null(Assert.Single(events.OfType<LlmAgentHostedToolCallCompletedEvent>()).Call.Output);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
    }

    /// <summary>A 429 through resource access is retried with its attempt, wait and error, and the third ends the turn
    /// with the provider's message.</summary>
    [Fact]
    public async Task BusyRefusalThroughResourceAccessIsRetriedWithProgress()
    {
        using var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json("{\"models\":[{\"slug\":\"model\",\"context_window\":272000}]}")
            : new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("{\"error\":{\"message\":\"Rate limit reached.\",\"code\":\"rate_limit_exceeded\"}}", Encoding.UTF8, "application/json") });
        await using ManagedAgentSession session = await ResourceSession(handler, EmptyAccess());

        List<LlmAgentEvent> events = await RunAsync(session, "hello");

        Assert.Equal(3, handler.Bodies.Count);
        Assert.Single(handler.Bodies.Distinct());
        LlmAgentResponseRecoveryEvent[] recoveries = [.. events.OfType<LlmAgentResponseRecoveryEvent>()];
        Assert.Equal(new int?[] { 2, 3 }, recoveries.Select(item => item.NextAttempt));
        Assert.Equal(new TimeSpan?[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500) }, recoveries.Select(item => item.Delay));
        Assert.All(recoveries, item => Assert.Contains("Rate limit reached.", item.Error, StringComparison.Ordinal));
        LlmAgentTurnCompletedEvent completed = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, completed.Reason);
        Assert.Contains("Rate limit reached.", completed.Error, StringComparison.Ordinal);
    }

    /// <summary>A too-long refusal through resource access compacts the history and sends the step once more.</summary>
    [Fact]
    public async Task OverflowThroughResourceAccessCompactsAndResends()
    {
        int posts = 0;
        using var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json("{\"models\":[{\"slug\":\"model\",\"context_window\":272000}]}")
            : ++posts == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("{\"error\":{\"message\":\"Your input exceeds the context window of this model.\",\"code\":\"context_length_exceeded\"}}", Encoding.UTF8, "application/json") }
                : Sse(Completed));
        var history = new LlmAgentContext
        {
            Entries = [new("t1-user", "t1", new("user", new string('a', 600))), new("t1-assistant", "t1", new("assistant", "first answer"))]
        };
        await using ManagedAgentSession session = await ResourceSession(handler, EmptyAccess(), history);

        List<LlmAgentEvent> events = await RunAsync(session, "hello");

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Single(events.OfType<LlmAgentCompactedEvent>());
        Assert.DoesNotContain(new string('a', 600), handler.Bodies[^1], StringComparison.Ordinal);
    }

    /// <summary>An image committed in one turn is not sent again as a user attachment in the next.</summary>
    [Fact]
    public async Task SelectedImageIsNotResentAsAnAttachment()
    {
        int posts = 0;
        using var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json("{\"models\":[{\"slug\":\"model\",\"context_window\":272000}]}")
            : Sse(++posts == 1 ? ImageReply : Completed));
        var generated = new InferenceResourceReference("workspace", "generated");
        var content = new List<(InferenceResourceReference Reference, string Name, byte[] Bytes)>();
        ResourcePreparationTests.Resources? access = null;
        access = new ResourcePreparationTests.Resources(new("workspace", "session", 0, []), content)
        {
            WriteFactory = create => new ResourcePreparationTests.GeneratedWrite("workspace", create, () =>
            {
                content.Add((generated, "image.webp", [1, 2, 3]));
                access!.Selection = new("workspace", "session", 4, [generated]);
            })
        };
        await using ManagedAgentSession session = await ResourceSession(handler, access);

        _ = await RunAsync(session, "Draw a cat");
        List<LlmAgentEvent> events = await RunAsync(session, "Describe it");

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Equal([generated], access.Selection.SelectedResources);
        Assert.Equal(2, handler.Bodies.Count);
        // The image travels in the replayed image_generation_call item, never as an attachment on the new prompt.
        JsonNode prompt = JsonNode.Parse(handler.Bodies[1])!["input"]!.AsArray()[^1]!;
        Assert.Equal("user", prompt["role"]!.GetValue<string>());
        Assert.Equal("Describe it", prompt["content"]!.GetValue<string>());
    }

    /// <summary>An OpenAI API image item is kept in continuation state and replayed on the next request (S9).</summary>
    [Fact]
    public async Task OpenAiImageCallIsKeptInContinuationState()
    {
        using var handler = new Handler(_ => Sse(ImageReply));
        var client = new OpenAiResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = OpenAiConnection("http://local.test/v1");

        LlmChatResult first = await Complete(client, connection, Request() with { HostedTools = [new LlmImageGenerationTool()] });

        Assert.NotNull(first.ContinuationState);
        handler.Response = _ => Sse(Completed);
        _ = await Complete(client, connection, Request() with
        {
            Messages = [new("user", "Draw a cat"),
                new("assistant", first.Text) { ContinuationState = first.ContinuationState }, new("user", "Describe it")]
        });
        JsonNode image = JsonNode.Parse(handler.Body!)!["input"]!.AsArray()
            .Single(item => item!["type"]?.GetValue<string>() == "image_generation_call")!;
        Assert.Equal("AQID", image["result"]!.GetValue<string>());
    }

    /// <summary>Text full of characters the wire JSON escapes, such as a tool result holding JSON, is counted as the
    /// model reads it, not as its escape sequences.</summary>
    [Fact]
    public async Task EscapedTextIsEstimatedAtItsDecodedSize()
    {
        var client = new OpenAiCodexLlmClient(new HttpClient(new Handler(_ => Sse(Completed))));
        string text = string.Concat(Enumerable.Repeat("{\"key\":\"value+<&>\"} ", 500));
        LlmChatRequest request = Request();

        long empty = (await client.EstimateAsync(Connection(), request, ResourceSizes.None, CancellationToken.None))!.Value.Tokens;
        long full = (await client.EstimateAsync(Connection(), request with { Messages = [new("user", text)] }, ResourceSizes.None, CancellationToken.None))!.Value.Tokens;

        Assert.InRange(full - empty, 0, LocalTokenCount.Count(text) * 11 / 10);
    }

    /// <summary>Every history message, with or without attachments, is sent as a typed message item; the local llama.cpp
    /// server refuses an untyped one with "Cannot determine type of 'item'".</summary>
    [Fact]
    public async Task HistoryMessagesCarryTheMessageType()
    {
        using var handler = new Handler(_ => Sse(Completed));
        var client = new OpenAiCodexLlmClient(new HttpClient(handler));
        LlmChatRequest request = Request() with
        {
            Messages = [new("user", "first"), new("assistant", "answer"),
                new("user", "look") { Attachments = [new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "a.png", MediaType = "image/png", Data = new byte[] { 1, 2, 3 } }] }]
        };

        _ = await Complete(client, Connection(), request);

        JsonArray input = JsonNode.Parse(handler.Body!)!["input"]!.AsArray();
        Assert.Equal(3, input.Count);
        Assert.All(input, item => Assert.Equal("message", item!["type"]?.GetValue<string>()));
    }

    /// <summary>A temperature refused as unsupported is dropped, the step sent once more, and the refusal remembered
    /// for that connection and model only (S3a).</summary>
    [Fact]
    public async Task RefusedTemperatureIsDroppedResentAndRemembered()
    {
        int posts = 0;
        using var handler = new Handler(_ => ++posts == 1
            ? Refusal("""{"error":{"message":"Unsupported parameter: 'temperature' is not supported with this model.","type":"invalid_request_error","param":"temperature","code":"unsupported_parameter"}}""")
            : Sse(Completed));
        var learned = new LearnedModelContext();
        LlmAgentTurnRequest turn = new() { Input = [new LlmAgentTextContent("hello")], Temperature = 0.7 };
        await using (ManagedAgentSession session = await OpenAiSession(handler, "openrouter", learned))
        {
            List<LlmAgentEvent> first = await RunAsync(session, turn);
            List<LlmAgentEvent> second = await RunAsync(session, turn);

            Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(first.OfType<LlmAgentTurnCompletedEvent>()).Reason);
            Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(second.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        }
        await using (ManagedAgentSession other = await OpenAiSession(handler, "other-service", learned))
            _ = await RunAsync(other, turn);

        bool[] sentTemperature = [.. handler.Bodies.Select(body => JsonNode.Parse(body)!.AsObject().ContainsKey("temperature"))];
        Assert.Equal([true, false, false, true], sentTemperature);
    }

    /// <summary>The ChatGPT device code opens the verification page with the code filled in when the provider returns
    /// a complete verification URI, and the plain device page otherwise.</summary>
    [Fact]
    public async Task DeviceCodeUsesTheCompleteVerificationUriWhenReturned()
    {
        string body = """{"device_auth_id":"auth-1","user_code":"ABCD-EFGH","interval":5}""";
        using var handler = new Handler(_ => Json(body));
        var authenticator = new CodexAuthenticator(new HttpClient(handler));

        LlmDeviceCode plain = await authenticator.StartAsync();
        body = """{"device_auth_id":"auth-1","user_code":"ABCD-EFGH","interval":5,"verification_uri_complete":"https://auth.openai.com/codex/device?user_code=ABCD-EFGH"}""";
        LlmDeviceCode complete = await authenticator.StartAsync();

        Assert.Equal("https://auth.openai.com/codex/device", plain.VerificationUrl);
        Assert.Equal("https://auth.openai.com/codex/device?user_code=ABCD-EFGH", complete.VerificationUrl);
    }

    /// <summary>A reasoning value refused as unsupported is dropped from the resent request and the next turn (B4).</summary>
    [Fact]
    public async Task RefusedReasoningIsDroppedResentAndRemembered()
    {
        int posts = 0;
        using var handler = new Handler(_ => ++posts == 1
            ? Refusal("""{"error":{"message":"Unsupported parameter: 'reasoning.effort' is not supported with this model.","type":"invalid_request_error","param":"reasoning.effort","code":"unsupported_parameter"}}""")
            : Sse(Completed));
        LlmAgentTurnRequest turn = new() { Input = [new LlmAgentTextContent("hello")], ReasoningLevel = LlmReasoningLevel.High };
        await using ManagedAgentSession session = await OpenAiSession(handler, "local", new LearnedModelContext());

        List<LlmAgentEvent> first = await RunAsync(session, turn);
        _ = await RunAsync(session, turn);

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(first.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        bool[] sentReasoning = [.. handler.Bodies.Select(body => JsonNode.Parse(body)!.AsObject().ContainsKey("reasoning"))];
        Assert.Equal([true, false, false], sentReasoning);
    }

    private static HttpResponseMessage Refusal(string body) => new(HttpStatusCode.BadRequest)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>An OpenAI API connection at <paramref name="endpoint"/> with no key.</summary>
    internal static LlmConnection OpenAiConnection(string endpoint) =>
        new() { Version = "1", Transport = LlmTransport.OpenAiResponses, Endpoint = endpoint };

    /// <summary>An OpenAI API session on another endpoint that asks for images and holds resource access, as the Host opens it.</summary>
    private static Task<ManagedAgentSession> OpenAiSession(Handler handler, string connectionId, LearnedModelContext learned)
    {
        var client = new OpenAiResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = OpenAiConnection("https://openrouter.test/api/v1");
        return ManagedAgentSession.CreateAsync(connectionId, connection, new()
        {
            ModelId = "model",
            HostedTools = [new LlmImageGenerationTool()],
            CallerServices = new() { Resources = EmptyAccess() }
        }, [new("model", "Model") { MaxContextTokens = 128000 }], client,
            (request, token) => client.StreamAsync(connection, request, token),
            model => client.GetCapabilities(connection, model), learned);
    }

    private static async Task<List<LlmAgentEvent>> RunAsync(ManagedAgentSession session, LlmAgentTurnRequest turn)
    {
        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(turn)) events.Add(item);
        return events;
    }

    private const string ImageReply = """
        data: {"type":"response.output_item.added","output_index":0,"item":{"type":"image_generation_call","id":"ig","status":"in_progress"}}

        data: {"type":"response.output_item.done","output_index":0,"item":{"type":"image_generation_call","id":"ig","status":"completed","result":"AQID","output_format":"webp"}}

        data: {"type":"response.output_item.done","output_index":1,"item":{"type":"message","id":"m","role":"assistant","content":[{"type":"output_text","text":"Here it is."}]}}

        data: {"type":"response.completed","response":{"status":"completed","output":[],"id":"r"}}

        """;

    private static ResourcePreparationTests.Resources EmptyAccess() => new(new("workspace", "session", 0, []), []);

    /// <summary>A Codex session over <paramref name="handler"/> that asks for images and holds <paramref name="access"/>.</summary>
    private static Task<ManagedAgentSession> ResourceSession(Handler handler, IInferenceResourceAccess access, LlmAgentContext? context = null)
    {
        var client = new OpenAiCodexLlmClient(new HttpClient(handler));
        LlmConnection connection = Connection();
        return ManagedAgentSession.CreateAsync("test-connection", connection, new()
        {
            ModelId = "model",
            Context = context,
            HostedTools = [new LlmImageGenerationTool()],
            CallerServices = new() { Resources = access }
        }, [new("model", "Model") { MaxContextTokens = 272000 }], client,
            (request, token) => client.StreamAsync(connection, request, token),
            model => client.GetCapabilities(connection, model), new LearnedModelContext());
    }

    private static async Task<List<LlmAgentEvent>> RunAsync(ManagedAgentSession session, string text)
    {
        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent(text)] }))
            events.Add(item);
        return events;
    }

    /// <summary>A model reply with reasoning state and a tool call, then a follow-up history for another model.</summary>
    private static async Task<(Handler Handler, LlmProviderService Providers, LlmChatRequest Followup)> OtherModelFollowupAsync()
    {
        var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json("{\"models\":[{\"slug\":\"model\",\"context_window\":100000},{\"slug\":\"other\",\"context_window\":100000}]}")
            : Sse(ReasonedToolCall));
        var client = new OpenAiCodexLlmClient(new HttpClient(handler));
        var providers = new LlmProviderService(new Source(Connection()), [client], [new RefreshAuthenticator()]);
        LlmChatResult first = await providers.CompleteAsync("codex", Request());
        handler.Response = request => request.Method == HttpMethod.Get
            ? Json("{\"models\":[{\"slug\":\"other\",\"context_window\":100000}]}")
            : Sse(Completed);
        LlmChatRequest followup = Request() with
        {
            ModelId = "other",
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
            Messages = [new("user", "first"),
                new("assistant", first.Text) { ToolCalls = first.ToolCalls, ContinuationState = first.ContinuationState },
                new("user", "") { ToolResults = [new LlmToolResult("call", LlmToolOutput.FromText("answer"))] }]
        };
        return (handler, providers, followup);
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public LlmConnection Connection { get; set; } = connection;
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(Connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Connection.Version, expectedVersion);
            Connection = Connection with { Credential = credentialJson, Version = Guid.NewGuid().ToString() };
            return Task.CompletedTask;
        }
    }

    private sealed class RefreshAuthenticator : ILlmAuthenticator
    {
        public LlmTransport Transport => LlmTransport.OpenAiCodex;
        public int Calls { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(credential with { OAuth = credential.OAuth! with { AccessToken = "fresh-" + Calls.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
        }
    }

    internal static LlmConnection Connection() => new()
    {
        Version = "1",
        Transport = LlmTransport.OpenAiCodex,
        Endpoint = "https://provider.test/backend-api/codex",
        Credential = Credential("account")
    };
    private static string Credential(string account) => new LlmCredential
    { OAuth = new() { AccessToken = "token", AccountId = account } }.Serialize();
    private static LlmChatRequest Request() => new() { ModelId = "model", Messages = [new("user", "hello")] };
    internal static HttpResponseMessage Sse(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static async Task<LlmChatResult> Complete(ResponsesLlmClientBase client, LlmConnection connection, LlmChatRequest request) =>
        Assert.Single((await Read(client.StreamAsync(connection, request))).OfType<LlmChatCompletionEvent>()).Result;
    private static async Task<List<LlmStreamEvent>> Read(IAsyncEnumerable<LlmStreamEvent> source)
    {
        var result = new List<LlmStreamEvent>();
        await foreach (var item in source) { result.Add(item); }
        return result;
    }

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Response { get; set; } = response;
        public string? Body { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Dictionary<string, string> Headers { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public int Calls { get; private set; }
        /// <summary>Every request body, in order.</summary>
        public List<string> Bodies { get; } = [];
        public List<string> AuthorizationHistory { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Body is not null) { Bodies.Add(Body); }
            Headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            if (Headers.TryGetValue("Authorization", out var authorization)) { AuthorizationHistory.Add(authorization); }
            return Response(request);
        }
    }
}
