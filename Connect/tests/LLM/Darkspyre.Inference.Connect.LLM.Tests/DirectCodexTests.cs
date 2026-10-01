using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class DirectCodexTests
{
    private const string Completed = """
        data: {"type":"response.output_text.delta","delta":"Hello"}

        data: {"type":"response.completed","response":{"id":"response","status":"completed","output":[{"type":"message","id":"msg","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"Hello"}]}],"usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}}

        """;

    [Fact]
    public async Task DirectRequestUsesHostOAuthAndTheResponsesContract()
    {
        using var handler = new Handler(_ => Sse(Completed));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        var result = await Complete(client, Connection(), Request() with
        {
            Instructions = "base",
            ReasoningLevel = LlmReasoningLevel.High,
            ConversationKey = "thread",
            Messages = [new("system", "system"), new("developer", "developer"), new("user", "hello")]
        });
        Assert.Equal("https://provider.test/backend-api/codex/responses", handler.Uri?.AbsoluteUri);
        Assert.Equal("Bearer token", handler.Headers["Authorization"]);
        Assert.Equal("account", handler.Headers["ChatGPT-Account-ID"]);
        Assert.Equal("thread", handler.Headers["session-id"]);
        Assert.Equal("darkspyre-inference-connect", handler.Headers["originator"]);
        Assert.Equal("Darkspyre.Inference.Connect.LLM/0.155.1", handler.Headers["User-Agent"]);
        Assert.Equal("0.155.1", handler.Headers["version"]);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
        Assert.True(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("base\n\nsystem\n\ndeveloper", sent.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("high", sent.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("auto", sent.RootElement.GetProperty("reasoning").GetProperty("summary").GetString());
        Assert.Equal("thread", sent.RootElement.GetProperty("prompt_cache_key").GetString());
        Assert.Single(sent.RootElement.GetProperty("input").EnumerateArray());
        Assert.False(sent.RootElement.TryGetProperty("previous_response_id", out _));
        Assert.Equal("Hello", result.Text);
        Assert.Equal(7, result.Usage?.TotalTokens);
        Assert.Contains("final_answer", result.ContinuationState?.Payload);
        Assert.DoesNotContain("token", result.ContinuationState?.Payload);
        Assert.Equal("authorize-chatgpt", client.ProviderDefinition.SetupSteps[0].Id);
        Assert.True(client.GetCapabilities(Connection()).SupportsToolUse);
        Assert.Equal(LlmAgentOwnership.Host, client.GetCapabilities(Connection()).Agent.ContextOwnership);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LlmReasoningLevel.Medium)]
    public async Task ManagedSubscriptionRequestsSummariesAndDeliversThemBeforeTheReply(LlmReasoningLevel? effort)
    {
        const string reasoning = """
            data: {"type":"response.reasoning_summary_text.delta","delta":"Checking "}

            data: {"type":"response.reasoning_summary_text.delta","delta":"the request"}

            """;
        using var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json("{\"models\":[{\"slug\":\"model\",\"context_window\":100000}]}")
            : Sse(reasoning + "\n" + Completed));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        var source = new Source(Connection());
        await using var providers = new LlmProviderService(source, [client]);
        await using var agents = new LlmAgentProviderService(source, [new ManagedCodexAgentClient(client, providers)]);
        await using var session = await agents.StartSessionAsync("configured", new() { ModelId = "model" });
        var events = new List<LlmAgentEvent>();
        await foreach (var item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")], ReasoningLevel = effort })) { events.Add(item); }

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement sentReasoning = sent.RootElement.GetProperty("reasoning");
        Assert.Equal("auto", sentReasoning.GetProperty("summary").GetString());
        if (effort is null) { Assert.False(sentReasoning.TryGetProperty("effort", out _)); }
        else { Assert.Equal("medium", sentReasoning.GetProperty("effort").GetString()); }
        var summaries = events.OfType<LlmAgentReasoningDeltaEvent>().ToArray();
        Assert.Equal(["Checking ", "the request"], summaries.Select(item => item.Delta));
        Assert.All(summaries, item => Assert.True(item.IsSummary));
        Assert.True(events.IndexOf(summaries[^1]) < events.FindIndex(item => item is LlmAgentTextDeltaEvent));
        Assert.Equal(LlmAgentTurnStatus.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Status);
    }

    [Fact]
    public async Task EmptyTerminalOutputRetainsOrderedCompleteItemsIncludingEncryptedReasoning()
    {
        const string events = """
            data: {"type":"response.reasoning_summary_text.delta","delta":"Thinking"}

            data: {"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc","call_id":"call","name":"echo","arguments":"{}"}}

            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"reason","encrypted_content":"opaque","summary":[{"type":"summary_text","text":"Thinking"}]}}

            data: {"type":"response.completed","response":{"status":"completed","output":[],"id":"r"}}

            """;
        using var handler = new Handler(_ => Sse(events));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        var stream = await Read(client.StreamAsync(Connection(), Request()));
        Assert.Equal("Thinking", Assert.Single(stream.OfType<LlmReasoningDeltaEvent>()).Delta);
        var result = Assert.Single(stream.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.RequiresAction, result.Status);
        Assert.Equal("call", Assert.Single(result.ToolCalls).Id);
        var state = ProviderContinuationStateCodec.Decode(result.ContinuationState!, Connection(), "model");
        Assert.Equal("reasoning", state[0]!["type"]!.GetValue<string>());
        Assert.Equal("function_call", state[1]!["type"]!.GetValue<string>());
        handler.Response = _ => Sse(Completed);
        await Complete(client, Connection(), Request() with
        {
            Messages = [new("user", "first"),
            new("assistant", result.Text) { ToolCalls = result.ToolCalls, ContinuationState = result.ContinuationState },
            new("user", "") { ToolResults = [new("call", LlmToolOutput.FromText("answer"))] }]
        });
        using var replay = JsonDocument.Parse(handler.Body!);
        var input = replay.RootElement.GetProperty("input");
        Assert.Equal("reasoning", input[1].GetProperty("type").GetString());
        Assert.Equal("opaque", input[1].GetProperty("encrypted_content").GetString());
        Assert.Equal("function_call_output", input[3].GetProperty("type").GetString());
    }

    [Fact]
    public async Task OrdinaryAssistantStateIsReplayedOnceAndRejectsModelOrAccountSwitchesBeforeHttp()
    {
        using var handler = new Handler(_ => Sse(Completed));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        var result = await Complete(client, Connection(), Request());
        var followup = Request() with { Messages = [new("assistant", result.Text) { ContinuationState = result.ContinuationState }, new("user", "next")] };
        await Complete(client, Connection(), followup);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(2, sent.RootElement.GetProperty("input").GetArrayLength());
        int before = handler.Calls;
        await Assert.ThrowsAsync<ArgumentException>(() => Complete(client, Connection(), followup with { ModelId = "other" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Complete(client, Connection() with { Credential = Credential("other") }, followup));
        Assert.Equal(before, handler.Calls);
    }

    [Fact]
    public async Task ImagesDocumentsAndToolOutputPartsUseAdapterOwnedWireBlocks()
    {
        using var handler = new Handler(_ => Sse(Completed));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jG9sAAAAASUVORK5CYII=");
        var image = new LlmAttachment { Kind = LlmBinaryKind.Image, FileName = "x.png", Data = png };
        var document = new LlmAttachment { Kind = LlmBinaryKind.Document, FileName = "x.txt", Data = "word"u8.ToArray() };
        var output = new LlmToolOutput
        {
            Parts =
            [
                new LlmToolOutputText("read"),
                new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", png, "x.png"),
                new LlmToolOutputBinary(LlmBinaryKind.Audio, "audio/wav", new byte[] { 1 }, "x.wav"),
                new LlmToolOutputBinary(LlmBinaryKind.Document, "text/plain", "word"u8.ToArray(), "x.txt"),
                new LlmToolOutputResource("https://example.test/r") { Name = "r" }
            ],
            StructuredContent = JsonSerializer.SerializeToElement(new { ok = true }),
            IsError = true
        };
        await Complete(client, Connection(), Request() with
        {
            Messages = [new("user", "see") { Attachments = [image, document] },
            new("assistant", "") { ToolCalls = [new("call", "echo", "{}")] },
            new("user", "") { ToolResults = [new("call", output)] }]
        });
        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement items = sent.RootElement.GetProperty("input")[2].GetProperty("output");
        Assert.Equal(
            ["input_text", "input_text", "input_image", "input_file", "input_text", "input_text"],
            items.EnumerateArray().Select(item => item.GetProperty("type").GetString()));
        Assert.Equal(ToolOutputProjector.ErrorMarker, items[0].GetProperty("text").GetString());
        Assert.Equal("read", items[1].GetProperty("text").GetString());
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(png)}", items[2].GetProperty("image_url").GetString());
        Assert.Equal("x.txt", items[3].GetProperty("filename").GetString());
        Assert.Equal($"data:text/plain;base64,{Convert.ToBase64String("word"u8)}", items[3].GetProperty("file_data").GetString());
        Assert.Equal("[Resource link: r] https://example.test/r", items[4].GetProperty("text").GetString());
        Assert.Equal("{\"ok\":true}", items[5].GetProperty("text").GetString());
        Assert.DoesNotContain("OmittedAttachments", sent.RootElement.GetRawText());

        var providers = new LlmProviderService(new Source(Connection()), [client]);
        LlmToolOutputProjection report = await providers.ProjectToolOutputAsync("codex", "model", output);
        Assert.Equal(
            [LlmToolPartDelivery.Delivered, LlmToolPartDelivery.Delivered, LlmToolPartDelivery.Omitted, LlmToolPartDelivery.Delivered,
                LlmToolPartDelivery.Converted],
            report.Parts.Select(part => part.Delivery));
        Assert.Equal(LlmToolPartOmission.UnsupportedByRoute, report.Parts[2].Omission);
        Assert.Equal(LlmToolPartDelivery.Converted, report.StructuredContent!.Delivery);
        Assert.Equal(LlmToolPartDelivery.Converted, report.ErrorFlag.Delivery);
    }

    [Fact]
    public void ContextEstimateIncludesInstructionsAndSchemasButDoesNotInventOpaqueOrImageCounts()
    {
        using var http = new HttpClient();
        var client = new OpenAiCodexLlmClient(http);
        long baseline = client.EstimateInputTokens(Connection(), Request())!.Value.Tokens;
        Assert.True(baseline > 0);
        long expanded = client.EstimateInputTokens(Connection(), Request() with
        { Instructions = string.Join(' ', Enumerable.Repeat("instruction", 100)) })!.Value.Tokens;
        Assert.True(expanded > baseline);
        Assert.Null(client.EstimateInputTokens(Connection(), Request() with
        {
            Messages = [new("user", "read")
        { Attachments = [new LlmAttachment { Kind = LlmBinaryKind.Document, FileName = "x.txt", Data = "text"u8.ToArray() }] }]
        }));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("http://provider.test")]
    [InlineData("/bin/codex")]
    public void FormerExecutableAndNonHttpsEndpointsRejectWithMigrationGuidance(string endpoint)
    {
        using var http = new HttpClient();
        var error = Assert.Throws<InvalidOperationException>(() => new OpenAiCodexLlmClient(http).GetCapabilities(Connection() with { Endpoint = endpoint }));
        Assert.Contains("HTTPS", error.Message);
    }

    [Fact]
    public async Task InvalidAuthCapabilitiesAndUnsupportedFieldsFailBeforeHttp()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("No HTTP expected"));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection(), Request() with { MaxOutputTokens = 100 }));
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection(), Request() with { Temperature = .5 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(client, Connection() with { Credential = null }, Request()));
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection() with
        { Credential = new LlmCredential { ApiKey = "key" }.Serialize() }, Request()));
        await Assert.ThrowsAsync<Darkspyre.Inference.Common.InferenceException>(() => Complete(client, Connection() with
        { ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides> { ["model"] = new() { ReasoningLevels = new HashSet<LlmReasoningLevel>() } } },
            Request() with { ReasoningLevel = LlmReasoningLevel.High }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ModelsUseDirectOAuthCatalogWithoutInventedLimits()
    {
        using var handler = new Handler(_ => Json("""{"models":[{"slug":"hidden","visibility":"hide"},{"slug":"model","display_name":"Model","context_window":200000},{"id":"unknown"}]}"""));
        using var http = new HttpClient(handler);
        var models = await new OpenAiCodexLlmClient(http).ListModelsAsync(Connection());
        Assert.Equal(["model", "unknown"], models.Select(model => model.Id));
        Assert.Equal(200000, models[0].MaxContextTokens);
        Assert.Null(models[1].MaxContextTokens);
        Assert.Null(models[0].MaxOutputTokens);
        Assert.Contains("/models?client_version=0.155.1", handler.Uri?.AbsoluteUri);
        Assert.Equal("Bearer token", handler.Headers["Authorization"]);
    }

    [Fact]
    public async Task QuotasRemainReadOnlyPartialAndRespectProviderPermission()
    {
        using var handler = new Handler(_ => Json("""
            {"plan_type":"pro","rate_limit":{"allowed":false,"primary_window":{"used_percent":40,"limit_window_seconds":604800,"reset_at":2000000000}},"credits":{"balance":"12.5","has_credits":true},"rate_limit_reset_credits":{"available_count":2}}
            """));
        using var http = new HttpClient(handler);
        var usage = await new OpenAiCodexLlmClient(http).GetAccountUsageAsync(Connection());
        Assert.Equal("https://provider.test/backend-api/wham/usage", handler.Uri?.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(.6, usage.Quotas[0].RemainingFraction);
        Assert.Equal(TimeSpan.FromDays(7), usage.Quotas[0].WindowDuration);
        Assert.Equal(LlmQuotaState.Exhausted, usage.Quotas[0].State);
        Assert.Equal(12.5m, usage.Credits[0].Remaining);
        Assert.Equal(2m, usage.Credits[1].Remaining);
        Assert.Empty(usage.Metrics);
        handler.Response = _ => Json("""{"rate_limit":{"primary_window":{}},"credits":{"balance":"invalid"}}""");
        usage = await new OpenAiCodexLlmClient(http).GetAccountUsageAsync(Connection());
        Assert.Null(usage.Quotas[0].RemainingFraction);
        Assert.Equal(LlmQuotaState.Unknown, usage.Quotas[0].State);
        Assert.Null(usage.Credits[0].Remaining);
    }

    [Theory]
    [InlineData("data: {\"type\":\"error\",\"code\":\"context_length_exceeded\",\"message\":\"overflow token\"}\n\n")]
    [InlineData("data: {\"type\":\"response.failed\",\"response\":{\"status\":\"failed\",\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"overflow token\"}}}\n\n")]
    public async Task OverflowCodesArePreservedAndSecretsRedacted(string events)
    {
        using var handler = new Handler(_ => Sse(events));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Complete(new(http), Connection(), Request()));
        Assert.Equal("context_length_exceeded", error.ProviderCode);
        Assert.DoesNotContain("token", error.Message);
        Assert.DoesNotContain("token", error.SanitizedResponseBody);
    }

    [Fact]
    public async Task TruncatedStreamDoesNotReturnASuccessfulCompletion()
    {
        using var handler = new Handler(_ => Sse("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<LlmProviderException>(() => Complete(new(http), Connection(), Request()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ManagedFacadeRefreshesEachStepAndRejectsConnectionChanges(bool switchAccount, bool disableAgent)
    {
        int posts = 0;
        using var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json(request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                ? "{\"plan_type\":\"pro\"}" : "{\"models\":[{\"slug\":\"model\",\"context_window\":100000}]}")
            : Sse(++posts == 1 ? "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"function_call\",\"call_id\":\"a\",\"name\":\"echo\",\"arguments\":\"{}\"}]}}\n\n" : Completed));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        var source = new Source(Connection());
        var auth = new RefreshAuthenticator();
        var providers = new LlmProviderService(source, [client], [auth]);
        var managed = new ManagedCodexAgentClient(client, providers);
        await using var agents = new LlmAgentProviderService(source, [managed]);
        var options = new LlmAgentSessionOptions
        {
            ModelId = "model",
            Tools = [new() { Name = "echo", InputSchema = JsonSerializer.SerializeToElement(new
            { type = "object", properties = new { }, additionalProperties = false }) }],
            ToolHandler = (_, _) =>
        {
            source.Connection = source.Connection with { Credential = Credential(switchAccount ? "other" : "account") };
            if (disableAgent) { source.Connection = source.Connection with { CapabilityOverrides = new() { SupportsAgentSessions = false } }; }
            return Task.FromResult(new LlmToolOutput());
        }
        };
        Assert.IsNotAssignableFrom<ILlmAgentResumeClient>(managed);
        Assert.IsNotAssignableFrom<ILlmAgentForkClient>(managed);
        await Assert.ThrowsAsync<NotSupportedException>(() => agents.ResumeSessionAsync("configured", "old-thread"));
        await Assert.ThrowsAsync<NotSupportedException>(() => agents.ForkSessionAsync("configured", "old-thread"));
        Assert.Equal(0, handler.Calls);
        await using var session = await agents.StartSessionAsync("configured", options);
        Assert.True((await session.GetCapabilitiesAsync()).SupportsCompactionHandoff);
        var events = new List<LlmAgentEvent>();
        await foreach (var item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("use echo")] })) { events.Add(item); }
        var final = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(switchAccount || disableAgent ? LlmAgentTurnStatus.Failed : LlmAgentTurnStatus.Completed, final.Status);
        Assert.Equal(switchAccount || disableAgent ? 1 : 2, posts);
        Assert.Equal(3, auth.Calls);
        if (switchAccount) { Assert.Contains("connection/account changed", final.Error); }
        else if (disableAgent) { Assert.Contains("disables", final.Error); }
        else
        {
            await providers.GetAccountUsageAsync("configured");
            Assert.Equal(4, auth.Calls);
            Assert.Equal(4, handler.AuthorizationHistory.Distinct().Count());
        }
    }

    [Fact]
    public async Task ReportedTokensTravelWithTheStateAndCountTheEncryptedHistory()
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
                new("user", "") { ToolResults = [new("call", LlmToolOutput.FromText("answer"))] }]
        };

        ContextEstimate estimate = client.EstimateInputTokens(Connection(), followup)!.Value;
        LlmRequestAccounting accounting = await new LlmProviderService(new Source(Connection()), [client], [new RefreshAuthenticator()])
            .MeasureRequestAsync("codex", followup);

        Assert.True(estimate.IncludesProviderReport);
        Assert.InRange(estimate.Tokens, 1281, 1400); // The report, plus the tokenized tool result after it.
        Assert.Equal(estimate.Tokens, accounting.UsedContextTokens);
        Assert.Equal(LlmContextMeasurementSource.Mixed, accounting.Source);
        // Encrypted reasoning without a report stays unknown, as does encrypted state after the last report.
        var unreported = first.ContinuationState with { InputTokens = null, OutputTokens = null };
        Assert.Null(client.EstimateInputTokens(Connection(), followup with
        {
            Messages = [followup.Messages[0], followup.Messages[1] with { ContinuationState = unreported }, followup.Messages[2]]
        }));
        Assert.Null(client.EstimateInputTokens(Connection(), followup with
        {
            Messages = [.. followup.Messages, new("assistant", "") { ToolCalls = first.ToolCalls, ContinuationState = unreported },
                new("user", "") { ToolResults = [new("call", LlmToolOutput.FromText("again"))] }]
        }));
    }

    private const string ReasonedToolCall = """
        data: {"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc","call_id":"call","name":"echo","arguments":"{}"}}

        data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"reason","encrypted_content":"opaque","summary":[{"type":"summary_text","text":"Thinking"}]}}

        data: {"type":"response.completed","response":{"status":"completed","output":[],"id":"r"}}

        """;

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

    [Fact]
    public async Task RejectStillFailsAnotherModelsStateBeforeAnyRequest()
    {
        (Handler handler, LlmProviderService providers, LlmChatRequest followup) = await OtherModelFollowupAsync();
        using (handler)
        {
            int before = handler.Calls;

            await Assert.ThrowsAsync<ArgumentException>(() => providers.CompleteAsync(
                "codex", followup with { UnboundContinuationState = LlmUnboundContinuationState.Reject }));

            // The refreshed connection version lists the Codex catalog once for per-model reasoning levels; no
            // generation request is sent.
            Assert.Equal(before + 1, handler.Calls);
            Assert.Equal(HttpMethod.Get, handler.Method);
        }
    }

    [Fact]
    public async Task MeasuringWithOmitCountsTheRequestThatWouldBeSent()
    {
        (Handler handler, LlmProviderService providers, LlmChatRequest followup) = await OtherModelFollowupAsync();
        using (handler)
        {
            LlmChatRequest stripped = followup with
            {
                UnboundContinuationState = LlmUnboundContinuationState.Reject,
                Messages = [.. followup.Messages.Select(message => message with { ContinuationState = null })]
            };

            LlmRequestAccounting omitted = await providers.MeasureRequestAsync("codex", followup);
            LlmRequestAccounting expected = await providers.MeasureRequestAsync("codex", stripped);

            Assert.NotNull(omitted.UsedContextTokens);
            Assert.Equal(expected.UsedContextTokens, omitted.UsedContextTokens);
        }
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
                new("user", "") { ToolResults = [new("call", LlmToolOutput.FromText("answer"))] }]
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
    private static HttpResponseMessage Sse(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static async Task<LlmChatResult> Complete(OpenAiCodexLlmClient client, LlmConnection connection, LlmChatRequest request) =>
        Assert.Single((await Read(client.StreamAsync(connection, request))).OfType<LlmChatCompletionEvent>()).Result;
    private static async Task<List<LlmStreamEvent>> Read(IAsyncEnumerable<LlmStreamEvent> source)
    {
        var result = new List<LlmStreamEvent>();
        await foreach (var item in source) { result.Add(item); }
        return result;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Response { get; set; } = response;
        public string? Body { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Dictionary<string, string> Headers { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public int Calls { get; private set; }
        public List<string> AuthorizationHistory { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            if (Headers.TryGetValue("Authorization", out var authorization)) { AuthorizationHistory.Add(authorization); }
            return Response(request);
        }
    }
}
