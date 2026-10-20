using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Handler = Darkspyre.Inference.Connect.LLM.Tests.DirectCodexTests.Handler;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>Grok subscription sign-in, proxy protocol and managed-session recovery.</summary>
public sealed class GrokTests
{
    private const string DeviceCode = """
        {"device_code":"device-1","user_code":"ABCD","verification_uri":"https://accounts.x.ai/device","expires_in":900,"interval":1}
        """;
    private const string Tokens = """{"access_token":"grok-access","token_type":"Bearer","expires_in":3600,"refresh_token":"refresh"}""";

    /// <summary>The device login runs through the shared seam: a pending code keeps polling, slow_down widens the interval
    /// by five seconds, and the approved code is saved only after /v1/user names the account (B2, S1).</summary>
    [Fact]
    public async Task DeviceLoginPollsThroughTheSeamAndSavesTheAccountAfterApproval()
    {
        var polls = new Queue<HttpResponseMessage>([Error("authorization_pending"), Error("slow_down"), Json(Tokens)]);
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/oauth2/device/code" => Json(DeviceCode),
            "/oauth2/token" => polls.Dequeue(),
            "/v1/user" => Json("""{"user_id":"user-1"}"""),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        var source = new Source(Connection() with { Credential = null });
        using var service = Service(source, handler);

        LlmDeviceCode code = await service.StartDeviceLoginAsync(LlmTransport.XaiResponses);
        LlmAuthPendingException pending = await Assert.ThrowsAsync<LlmAuthPendingException>(() =>
            service.CompleteDeviceLoginAsync(LlmTransport.XaiResponses, "grok", code.DeviceAuthId, code.UserCode, "1"));
        LlmAuthPendingException slow = await Assert.ThrowsAsync<LlmAuthPendingException>(() =>
            service.CompleteDeviceLoginAsync(LlmTransport.XaiResponses, "grok", code.DeviceAuthId, code.UserCode, "1"));
        Assert.Equal(0, source.Saves);
        await service.CompleteDeviceLoginAsync(LlmTransport.XaiResponses, "grok", code.DeviceAuthId, code.UserCode, "1");

        Assert.Equal(("ABCD", 1), (code.UserCode, code.IntervalSeconds));
        Assert.Equal(1, code.AfterPending(pending).IntervalSeconds);
        Assert.Equal(6, code.AfterPending(slow).IntervalSeconds);
        Assert.Equal(1, source.Saves);
        Assert.Equal(("grok-access", "user-1"), (source.Saved!.OAuth!.AccessToken, source.Saved.OAuth.AccountId));
    }

    /// <summary>Setup offers Grok's device sign-in and no region; the connection uses the subscription proxy, and the check
    /// after approval marks the connection signed in (B1, B2, S1).</summary>
    [Fact]
    public async Task SetupSignsInWithTheDeviceCodeAndUsesTheSubscriptionProxy()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/oauth2/device/code" => Json(DeviceCode),
            "/oauth2/token" => Json(Tokens),
            "/v1/user" => Json("""{"user_id":"user-1"}"""),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        using var service = Service(new Source(Connection() with { Credential = null }), handler);
        LlmSettingsManager manager = service.CreateSettingsManager(new Target());
        LlmSettings settings = await manager.OpenAsync(LlmTransport.XaiResponses,
            new(SetupChangeIntent.Create, "grok", null), null, "grok-4.7");

        SetupActionDescriptor signIn = Assert.Single(settings.Actions, action => action.Purpose == SetupActionPurpose.Authenticate);
        await manager.RunAsync(settings, signIn.Id);
        SetupActionResult approved = await manager.RunAsync(settings,
            Assert.IsType<SetupDeviceAuthorization>(settings.Interaction).Continue.Id);

        Assert.Equal(("authorize", "Sign in with Grok"), (signIn.Id, signIn.DisplayName));
        Assert.DoesNotContain(settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields),
            field => field.Description.Id == "xai-region");
        Assert.Equal("https://cli-chat-proxy.grok.com/v1", settings.CurrentConnection().Endpoint);
        Assert.Equal(SetupActionState.Completed, approved.State);
        Assert.True(settings.Status.Authenticated.Value);
        Assert.Equal(SetupProviderGroup.Subscription, GrokDefinition.Value.Group);
        Assert.NotNull(GrokDefinition.Value.Icon);
    }

    /// <summary>A denied code ends sign-in and saves nothing.</summary>
    [Fact]
    public async Task RefusedSignInSavesNothing()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/oauth2/token" => Error("access_denied"),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        var source = new Source(Connection() with { Credential = null });
        using var service = Service(source, handler);

        Exception refusal = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.CompleteDeviceLoginAsync(LlmTransport.XaiResponses, "grok", "device-1", "ABCD", "1"));

        Assert.Equal("Grok sign-in was denied or expired. Start sign-in again.", refusal.Message);
        Assert.IsType<LlmProviderException>(refusal);
        Assert.Equal(0, source.Saves);
    }

    /// <summary>The request goes to the subscription proxy with the headers its auth middleware reads.</summary>
    [Fact]
    public async Task RequestsGoToTheSubscriptionProxyWithItsAuthHeaders()
    {
        using var handler = new Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new GrokResponsesLlmClient(new HttpClient(handler), TimeProvider.System);

        await Complete(client, Connection(), Request("grok-4.7"));

        Assert.Equal("https://cli-chat-proxy.grok.com/v1/responses", handler.Uri!.AbsoluteUri);
        Assert.Equal(("Bearer test-token", "xai-grok-cli", "authenticate-response"),
            (handler.Headers["Authorization"], handler.Headers["X-XAI-Token-Auth"], handler.Headers["x-authenticateresponse"]));
    }

    /// <summary>The model list is the proxy's GET models: each row's context_window is the model's window and its
    /// reasoning_efforts are its levels. The proxy serves no compaction route, so the host compacts (B4, B10).</summary>
    [Fact]
    public async Task ModelListReadsTheProxyCatalogAndTheHostCompacts()
    {
        using var handler = new Handler(_ => Json("""
            {"data":[{"id":"grok-4.7","name":"Grok 4.7","owned_by":"xAI","context_window":256000,
              "reasoning_efforts":[{"value":"xhigh","default":false},{"value":"high","default":true},{"value":"low","default":false}]}]}
            """));
        var client = new GrokResponsesLlmClient(new HttpClient(handler), TimeProvider.System);

        LlmModel model = Assert.Single(await client.ListModelsAsync(Connection()));

        Assert.Equal("https://cli-chat-proxy.grok.com/v1/models", handler.Uri!.AbsoluteUri);
        Assert.Equal(("grok-4.7", "Grok 4.7", 256000L), (model.Id, model.DisplayName, model.MaxContextTokens));
        Assert.True(model.Capabilities!.ReasoningLevels.SetEquals([LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.High, LlmReasoningLevel.Low]));
        Assert.Equal(LlmCompactionSide.Host, model.Capabilities.Agent.CompactionSide);
    }

    /// <summary>A Grok reply holding reasoning keeps it in its continuation state, and the next request replays it verbatim
    /// and in order.</summary>
    [Fact]
    public async Task ReplyWithReasoningRoundTrips()
    {
        const string output = """
            [{"type":"reasoning","id":"r","summary":[],"encrypted_content":"thought"},
             {"type":"message","id":"m","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Found.","annotations":[]}]}]
            """;
        using var handler = new Handler(_ => DirectCodexTests.Sse("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp\",\"status\":\"completed\",\"output\":"
            + output.ReplaceLineEndings("") + ",\"usage\":{\"input_tokens\":5,\"output_tokens\":2,\"total_tokens\":7}}}\n\n"));
        var client = new GrokResponsesLlmClient(new HttpClient(handler), TimeProvider.System);
        LlmConnection connection = Connection();

        LlmChatResult reply = await Complete(client, connection, Request("grok-4.7"));
        await Complete(client, connection, Request("grok-4.7") with
        {
            Messages = [new("user", "hello"), new("assistant", reply.Text) { ContinuationState = reply.ContinuationState }, new("user", "more")]
        });

        JsonArray replayed = (JsonArray)JsonNode.Parse(handler.Bodies[1])!["input"]!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(output), new JsonArray([.. replayed.Skip(1).Take(2).Select(item => item!.DeepClone())])),
            replayed.ToJsonString());
    }

    /// <summary>Account usage is the proxy's billing route, sent with the account's x-userid. The period becomes one quota
    /// window and the cent amounts dollar balances; proto3's omitted zeros read as 0% and $0, and a reply without a
    /// period fails naming the field, not its values.</summary>
    [Fact]
    public async Task AccountUsageMapsTheBillingReply()
    {
        var replies = new Queue<string>([
            """
            {"config":{"creditUsagePercent":100,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"2026-10-19T00:00:00Z","end":"2026-10-26T00:00:00Z"},
              "prepaidBalance":{"val":1250},"onDemandCap":{"val":5000},"onDemandUsed":{"val":355}},"onDemandEnabled":true}
            """,
            """{"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_MONTHLY","start":"2026-10-01T00:00:00Z","end":"2026-11-01T00:00:00Z"},"prepaidBalance":{}}}""",
            """{"config":{"creditUsagePercent":42}}"""]);
        using var handler = new Handler(_ => Json(replies.Dequeue()));
        var client = new GrokResponsesLlmClient(new HttpClient(handler), TimeProvider.System);

        LlmAccountUsageSnapshot full = await client.GetAccountUsageAsync(Connection());
        Assert.Equal("https://cli-chat-proxy.grok.com/v1/billing?format=credits", handler.Uri!.AbsoluteUri);
        Assert.Equal(("Bearer test-token", "account"), (handler.Headers["Authorization"], handler.Headers["x-userid"]));
        LlmAccountUsageSnapshot zeros = await client.GetAccountUsageAsync(Connection());
        LlmProviderException unrecognized = await Assert.ThrowsAsync<LlmProviderException>(() => client.GetAccountUsageAsync(Connection()));

        LlmQuotaWindow weekly = Assert.Single(full.Quotas);
        Assert.Equal(("Weekly limit", 1d, LlmQuotaState.Exhausted, TimeSpan.FromDays(7), new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero)),
            (weekly.DisplayName, weekly.UsedFraction, weekly.State, weekly.WindowDuration, weekly.ResetsAt));
        Assert.Equal([("prepaid", (decimal?)12.50m, "usd"), ("on-demand", 46.45m, "usd")],
            full.Credits.Select(credit => (credit.Id, credit.Remaining, credit.Unit)));
        LlmQuotaWindow monthly = Assert.Single(zeros.Quotas);
        Assert.Equal(("Monthly limit", 0d, LlmQuotaState.Available), (monthly.DisplayName, monthly.UsedFraction, monthly.State));
        Assert.Equal([("prepaid", (decimal?)0m)], zeros.Credits.Select(credit => (credit.Id, credit.Remaining)));
        Assert.Equal("Grok account usage returned an unrecognized reply: config.currentPeriod is missing.", unrecognized.Message);
        Assert.Null(unrecognized.SanitizedResponseBody);
    }

    /// <summary>Sign-in uses only the sign-in host and proxy; a team-block field does not refuse the account.</summary>
    [Fact]
    public async Task SignInSavesTheAccountTheProxyNames()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://auth.x.ai/oauth2/token" => Json(Tokens),
            "https://cli-chat-proxy.grok.com/v1/user" => Json("""{"userId":"proxy-account","teamBlockedReasons":["api"]}"""),
            _ => throw new InvalidOperationException("Unexpected authorization route.")
        });
        var source = new Source(Connection() with { Credential = null });
        using var service = Service(source, handler);
        await service.CompleteDeviceLoginAsync(LlmTransport.XaiResponses, "grok", "device-1", "ABCD", "1");
        Assert.Equal("proxy-account", source.Saved!.OAuth!.AccountId);
        Assert.Equal("xai-grok-cli", handler.Headers["X-XAI-Token-Auth"]);
    }

    /// <summary>Either account-id spelling is accepted; a missing id fails without saving.</summary>
    [Theory]
    [InlineData("user_id")]
    [InlineData("userId")]
    [InlineData("missing")]
    public async Task AccountLookupAcceptsEitherSpelling(string field)
    {
        using var handler = new Handler(request => request.RequestUri!.Host == "auth.x.ai" ? Json(Tokens)
            : Json(new JsonObject { [field] = "proxy-account" }.ToJsonString()));
        var source = new Source(Connection() with { Credential = null });
        using var service = Service(source, handler);
        Task signIn = service.CompleteDeviceLoginAsync(LlmTransport.XaiResponses, "grok", "device-1", "ABCD", "1");
        if (field == "missing")
        {
            Assert.Equal("xai_authorization_protocol_error", (await Assert.ThrowsAsync<LlmProviderException>(() => signIn)).ProviderCode);
            Assert.Equal(0, source.Saves);
        }
        else
        {
            await signIn;
            Assert.Equal("proxy-account", source.Saved!.OAuth!.AccountId);
        }
    }

    /// <summary>Only visible Responses rows enter the catalog, with account headers on the lookup.</summary>
    [Fact]
    public async Task ModelListDropsHiddenAndOtherProtocolRowsAndSendsTheAccount()
    {
        using var handler = new Handler(_ => Json("""
            {"data":[{"id":"hidden","hidden":true},{"id":"other","api_backend":"chat_completions"},
              {"id":"responses","api_backend":"responses"},{"id":"unspecified"}]}
            """));
        var client = new GrokResponsesLlmClient(new HttpClient(handler), TimeProvider.System);
        LlmConnection connection = Connection() with
        {
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token", AccountId = "account", Email = "account@example.test" } }.Serialize()
        };
        Assert.Equal(["responses", "unspecified"], (await client.ListModelsAsync(connection)).Select(model => model.Id));
        Assert.Equal(("account", "account@example.test"), (handler.Headers["x-userid"], handler.Headers["x-email"]));
    }

    /// <summary>Both model-row spellings select identical protocol and capability facts.</summary>
    [Fact]
    public async Task ModelRowsAcceptEitherSpelling()
    {
        using var handler = new Handler(_ => Json("""
            {"data":[{"id":"snake","api_backend":"responses","stream_tool_calls":true,"inference_idle_timeout_secs":2,"supports_backend_search":true},
              {"id":"camel","apiBackend":"responses","streamToolCalls":true,"inferenceIdleTimeoutSecs":2,"supportsBackendSearch":true},
              {"id":"excluded","apiBackend":"chat_completions"}]}
            """));
        var client = new GrokResponsesLlmClient(new HttpClient(handler), TimeProvider.System);
        LlmConnection connection = Connection() with { Catalog = await client.ListModelsAsync(Connection()) };
        Assert.Equal(2, connection.Catalog.Count);
        XaiModelProfile snake = XaiModelProfiles.Get(connection, "snake");
        XaiModelProfile camel = XaiModelProfiles.Get(connection, "camel");
        Assert.Equal((true, TimeSpan.FromSeconds(10), true), (snake.StreamsToolCalls, snake.IdleTimeout, snake.SearchesOnBackend));
        Assert.Equal((snake.StreamsToolCalls, snake.IdleTimeout, snake.SearchesOnBackend), (camel.StreamsToolCalls, camel.IdleTimeout, camel.SearchesOnBackend));
    }

    /// <summary>Argument streaming is sent and offered only when the model row enables it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToolArgumentsStreamOnlyWhereTheModelRowSaysSo(bool streams)
    {
        using var handler = new Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new GrokResponsesLlmClient(new HttpClient(handler), TimeProvider.System);
        LlmConnection connection = Listed(new JsonObject { ["stream_tool_calls"] = streams });
        await Complete(client, connection, Request("grok-4.7"));
        Assert.Equal(streams, JsonNode.Parse(handler.Body!)!.AsObject().ContainsKey("stream_tool_calls"));
        Assert.Equal(streams ? InferenceSupport.Supported : InferenceSupport.Unsupported,
            client.GetCapabilities(connection, "grok-4.7").Tools.SupportsArgumentStreaming);
    }

    /// <summary>A managed opening keeps function and image tools and adds only model-offered search, with no tier
    /// or no_inline_citations (D18).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GrokSessionSendsTheCliHostedToolsAndNoTier(bool searches)
    {
        using var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json(new JsonObject
            {
                ["data"] = new JsonArray(new JsonObject
                { ["id"] = "grok-4.7", ["context_window"] = 256000, ["supports_backend_search"] = searches })
            }.ToJsonString())
            : DirectCodexTests.Sse(DirectCodexTests.Completed));
        LlmConnection connection = Connection();
        var client = new GrokResponsesLlmClient(new HttpClient(handler), TimeProvider.System);
        using var providers = new LlmProviderService(new Source(connection), [client], []);
        var agents = new ManagedLlmAgentClient<GrokResponsesLlmClient>(client, providers, new());
        await using ILlmAgentSession session = await agents.StartSessionAsync("grok", connection, new()
        {
            ModelId = "grok-4.7",
            HostedTools = [new LlmImageGenerationTool()],
            CallerServices = new() { Resources = new ResourcePreparationTests.Resources(new("workspace", "session", 0, []), []) },
            Tools = [new LlmAgentToolDefinition { Name = "read_fact", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }]
        });
        LlmAgentTurnCompletedEvent completion = Assert.Single((await Run(session)).OfType<LlmAgentTurnCompletedEvent>());
        Assert.Null(completion.Error);
        Assert.Equal(LlmAgentCompletionReason.Completed, completion.Reason);
        JsonObject payload = JsonNode.Parse(handler.Body!)!.AsObject();
        string[] expectedTools = searches ? ["function", "function", "image_generation", "web_search", "x_search"] : ["function", "function", "image_generation"];
        Assert.Equal(["read_fact", "update_plan"], payload["tools"]!.AsArray().Where(tool => (string?)tool!["type"] == "function")
            .Select(tool => (string?)tool!["name"]));
        Assert.Equal(expectedTools, payload["tools"]!.AsArray().Select(tool => (string?)tool!["type"]));
        Assert.False(payload.ContainsKey("service_tier"));
        Assert.Equal(["reasoning.encrypted_content"], payload["include"]!.AsArray().Select(item => (string?)item));
        Assert.DoesNotContain("no_inline_citations", payload.ToJsonString());
        Assert.Empty(new GrokResponsesLlmClient(new HttpClient(handler), TimeProvider.System).GetCapabilities(connection).ServiceTiers);
    }

    /// <summary>Only domain filters and X date ranges reach the proxy; unsupported hosted options refuse.</summary>
    [Fact]
    public async Task UnsupportedSearchOptionsAreRefused()
    {
        using var handler = new Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new GrokResponsesLlmClient(new HttpClient(handler), TimeProvider.System);
        LlmConnection connection = Listed(new JsonObject { ["supports_backend_search"] = true });
        await Complete(client, connection, Request("grok-4.7") with
        { HostedTools = [new LlmWebSearchTool { AllowedDomains = ["one.test", "two.test"] }] });
        Assert.Equal(["one.test", "two.test"], JsonNode.Parse(handler.Body!)!["tools"]![0]!["filters"]!["allowed_domains"]!.AsArray().Select(item => (string?)item));
        foreach ((LlmHostedTool tool, string message) in new (LlmHostedTool, string)[]
        {
            (new LlmSocialSearchTool { AllowedHandles = ["handle"] }, "Grok X search accepts only a date range."),
            (new LlmWebSearchTool { IncludeImageSearch = true }, "Grok web search accepts only allowed or excluded domains."),
            (new LlmCodeExecutionTool(), "This route does not implement code execution.")
        })
        {
            Exception error = await Assert.ThrowsAnyAsync<Exception>(() => Complete(client, connection,
                Request("grok-4.7") with { HostedTools = [tool] }));
            Assert.StartsWith(message, error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>A broken search-capable step replays before a hosted call starts, and never afterwards.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedSearchStepResendsOnlyBeforeASearchStarts(bool started)
    {
        int sends = 0;
        string prefix = started
            ? "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"web_search_call\",\"id\":\"search\",\"status\":\"in_progress\"}}\n\n"
            : "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n";
        using var handler = new Handler(_ => DirectCodexTests.Sse(++sends == 1
            ? prefix + "data: {\"type\":\"error\",\"error\":{\"code\":\"overloaded\",\"message\":\"busy\"}}\n\n" : DirectCodexTests.Completed));
        await using var session = await Session(handler, Listed(new JsonObject { ["supports_backend_search"] = true }));
        List<LlmAgentEvent> events = await Run(session);
        LlmAgentTurnCompletedEvent completion = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(started ? 1 : 2, sends);
        if (started) Assert.Contains("Provider-side work may already have run", completion.Error);
        else
        {
            Assert.Equal(LlmAgentCompletionReason.Completed, completion.Reason);
            Assert.Single(events.OfType<LlmAgentMessageCompletedEvent>());
            Assert.DoesNotContain((await session.CaptureContextAsync()).Entries, entry => entry.Message.Content == "partial");
            Assert.NotEqual(Assert.Single(events.OfType<LlmAgentTextDeltaEvent>(), item => item.Delta == "partial").ItemId,
                Assert.Single(events.OfType<LlmAgentMessageCompletedEvent>()).ItemId);
        }
    }

    /// <summary>Search billing includes internal turns and must not teach the local context estimator.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SearchStepTeachesNoContextCorrection(bool hosted)
    {
        string usage = hosted ? ",\"num_server_side_tools_used\":2" : "";
        using var handler = new Handler(_ => DirectCodexTests.Sse("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":20000,\"output_tokens\":2" + usage + "}}}\n\n"));
        var learned = new LearnedModelContext();
        await using var session = await Session(handler, Listed(new JsonObject()), learned);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single((await Run(session)).OfType<LlmAgentTurnCompletedEvent>()).Reason);
        if (hosted) Assert.Empty(learned.Read("grok"));
        else Assert.Contains(learned.Read("grok"), fact => fact.Name == "Framing tokens");
    }

    /// <summary>The spending-limit refusal ends the turn at once with a marker-free entitlement message.</summary>
    [Fact]
    public async Task OutOfCreditsEndsWithoutRetryAndWithoutTheMarker()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.TooManyRequests)
        { Content = new StringContent("""{"code":429,"error":"Limit reached [WKE=subscription:spending-limit]."}""") });
        await using var session = await Session(handler, Listed(new JsonObject()));
        LlmAgentTurnCompletedEvent completion = Assert.Single((await Run(session)).OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(1, handler.Calls);
        Assert.Equal("InferenceException: Grok requires an eligible subscription or available credits: Limit reached", completion.Error);
        Assert.DoesNotContain("[WKE=", completion.Error);
    }

    /// <summary>The transient ladder, send cap, server hint and explicit no-retry header match the CLI.</summary>
    [Fact]
    public async Task TransientFailuresFollowTheCliSchedule()
    {
        var counts = new Dictionary<LlmStepRetryKind, int>();
        var failure = new LlmProviderException("busy", LlmTransport.XaiResponses, HttpStatusCode.ServiceUnavailable);
        foreach (int seconds in new[] { 2, 4, 8, 16, 30 })
        {
            LlmStepRetry retry = XaiStepRetryPolicy.Decide(failure, new(counts, false, false), () => 0.5)!;
            Assert.Equal(TimeSpan.FromSeconds(seconds), retry.Delay);
            counts[LlmStepRetryKind.Transient] = counts.GetValueOrDefault(LlmStepRetryKind.Transient) + 1;
        }
        counts[LlmStepRetryKind.Transient] = 14;
        Assert.Null(XaiStepRetryPolicy.Decide(failure, new(counts, false, false), () => 0.5));
        Assert.Null(XaiStepRetryPolicy.Decide(new LlmProviderException("tls", LlmTransport.XaiResponses, (HttpStatusCode)525), new(new Dictionary<LlmStepRetryKind, int>(), false, false), () => 0.5));
        var hinted = new LlmProviderException("busy", LlmTransport.XaiResponses, HttpStatusCode.InternalServerError, retryAfter: TimeSpan.FromSeconds(70));
        Assert.Equal(TimeSpan.FromSeconds(30), XaiStepRetryPolicy.Decide(hinted, new(new Dictionary<LlmStepRetryKind, int>(), false, false), () => 0.5)!.Delay);
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{"code":"bad","error":"request refused"}""") };
        response.Headers.TryAddWithoutValidation("x-should-retry", "FALSE");
        LlmProviderException refused = await Assert.ThrowsAsync<LlmProviderException>(() => XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, null, [], CancellationToken.None));
        Assert.False(refused.ShouldRetry);
        Assert.Null(XaiStepRetryPolicy.Decide(refused, new(new Dictionary<LlmStepRetryKind, int>(), false, false), () => 0.5));
    }

    /// <summary>A rate limit gets one capped retry only when no earlier retry occurred.</summary>
    [Fact]
    public void RateLimitRetriesOnceAndOnlyFirst()
    {
        var failure = new LlmProviderException("rate limited", LlmTransport.XaiResponses, HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(200));
        Assert.Equal(TimeSpan.FromSeconds(120), XaiStepRetryPolicy.Decide(failure, new(new Dictionary<LlmStepRetryKind, int>(), false, false), () => 0.5)!.Delay);
        Assert.Null(XaiStepRetryPolicy.Decide(failure, new(new Dictionary<LlmStepRetryKind, int> { [LlmStepRetryKind.Transient] = 1 }, false, false), () => 0.5));
        Assert.Null(XaiStepRetryPolicy.Decide(failure, new(new Dictionary<LlmStepRetryKind, int> { [LlmStepRetryKind.RateLimited] = 1 }, false, false), () => 0.5));
    }

    /// <summary>Two sessions rejected on the same credential share one serialized renewal and both resend the saved token (D19).</summary>
    [Fact]
    public async Task UnauthorizedRenewsOnceAcrossSessionsAndResends()
    {
        var bothRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int rejected = 0, refreshes = 0, renewedSends = 0;
        using var handler = new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "auth.x.ai")
            {
                Interlocked.Increment(ref refreshes);
                refreshing.TrySetResult();
                await allowRefresh.Task.WaitAsync(token);
                return Json(Tokens);
            }
            if (request.Method == HttpMethod.Get) return Json("""{"data":[{"id":"grok-4.7","context_window":256000}]}""");
            if (request.Headers.Authorization!.Parameter == "test-token")
            {
                if (Interlocked.Increment(ref rejected) == 2) bothRejected.TrySetResult();
                await bothRejected.Task.WaitAsync(token);
                return new(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            }
            Assert.Equal("grok-access", request.Headers.Authorization.Parameter);
            Interlocked.Increment(ref renewedSends);
            return DirectCodexTests.Sse(DirectCodexTests.Completed);
        });
        LlmConnection connection = RenewableConnection(60);
        var source = new Source(connection);
        using var providers = new LlmProviderService(source,
            [new GrokResponsesLlmClient(new HttpClient(handler), TimeProvider.System)], [new GrokAuthenticator(new HttpClient(handler))]);
        await using var first = await Session(handler, connection, providers: providers);
        await using var second = await Session(handler, connection, providers: providers);
        Task<List<LlmAgentEvent>> a = Run(first), b = Run(second);
        await bothRejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        allowRefresh.SetResult();
        List<LlmAgentEvent>[] turns = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(turns, events => Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason));
        Assert.Equal((2, 1, 2, 1), (rejected, refreshes, renewedSends, source.Saves));
        Assert.Equal("grok-access", source.Saved!.OAuth!.AccessToken);
    }

    /// <summary>An invalid refresh grant ends the turn after its first send with the sign-in message.</summary>
    [Fact]
    public async Task RenewalRefusalEndsTheTurnWithSignIn()
    {
        int sends = 0;
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "auth.x.ai") return Error("invalid_grant");
            if (request.Method == HttpMethod.Get) return Json("""{"data":[]}""");
            sends++;
            return new(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
        });
        LlmConnection connection = RenewableConnection(60);
        using var providers = Service(new Source(connection), handler);
        await using var session = await Session(handler, connection, providers: providers);
        Assert.Equal("InferenceException: Grok authorization is no longer valid; sign in again.", Assert.Single((await Run(session)).OfType<LlmAgentTurnCompletedEvent>()).Error);
        Assert.Equal(1, sends);
    }

    /// <summary>Four minutes of remaining life renews before sending; six minutes does not.</summary>
    [Theory]
    [InlineData(4, 1)]
    [InlineData(6, 0)]
    public async Task RenewalStartsFiveMinutesBeforeExpiry(int minutes, int refreshes)
    {
        int calls = 0;
        using var handler = new Handler(request => request.RequestUri!.Host == "auth.x.ai" ? Refresh()
            : request.Method == HttpMethod.Get ? Json("""{"data":[]}""") : DirectCodexTests.Sse(DirectCodexTests.Completed));
        HttpResponseMessage Refresh() { calls++; return Json(Tokens); }
        var source = new Source(RenewableConnection(minutes));
        using var providers = Service(source, handler);
        await providers.CompleteAsync("grok", Request("grok-4.7"));
        Assert.Equal(refreshes, calls);
        Assert.Equal(refreshes, source.Saves);
        Assert.Equal(refreshes == 0 ? "Bearer test-token" : "Bearer grok-access", handler.Headers["Authorization"]);
    }

    /// <summary>A fired model idle deadline ends the stalled send and the policy resends it successfully.</summary>
    [Fact]
    public async Task StalledStreamTimesOutAndResends()
    {
        var clock = new StreamClock();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;
        using var handler = new Handler(_ => ++sends == 1
            ? new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(reading)) }
            : DirectCodexTests.Sse(DirectCodexTests.Completed));
        await using var session = await Session(handler, Listed(new JsonObject { ["inference_idle_timeout_secs"] = 10 }), time: clock);
        Task<List<LlmAgentEvent>> run = Run(session);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Timer!.Due);
        clock.Timer.Fire();
        List<LlmAgentEvent> events = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((2, LlmAgentCompletionReason.Completed), (sends, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason));
        Assert.Equal("The model stopped responding after 10s.", Assert.Single(events.OfType<LlmAgentResponseRecoveryEvent>()).Error);
    }

    /// <summary>A loop resamples with only the CLI reminder, and the third report ends the turn.</summary>
    [Fact]
    public async Task RepetitionLoopResamplesWithTheReminderThenEnds()
    {
        int sends = 0;
        using var handler = new Handler(_ => ++sends == 2
            ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
            : DirectCodexTests.Sse("data: {\"type\":\"response.output_text.delta\",\"delta\":\"failed-attempt-text\"}\n\ndata: {\"type\":\"response.doom_loop_check\",\"doom_loop_check\":{\"triggers\":[\"tail_repetition:4@thinking\"]}}\n\n"));
        await using var session = await Session(handler, Listed(new JsonObject()));
        List<LlmAgentEvent> events = await Run(session);
        Assert.Equal(4, handler.Calls);
        Assert.Equal(("1024", "64"), (handler.Headers["x-grok-doom-loop-check"], handler.Headers["x-grok-exact-repetition-check"]));
        for (int index = 1; index < handler.Bodies.Count; index++)
        {
            string body = handler.Bodies[index];
            string input = ResponsesLlmClientBase.DecodedText(JsonNode.Parse(body)!["input"]);
            if (index == 2) Assert.DoesNotContain(XaiRepetitionCheck.Reminder, input);
            else Assert.Contains(XaiRepetitionCheck.Reminder, input);
            Assert.DoesNotContain("failed-attempt-text", body);
            Assert.Equal(index == 2 ? 1 : 2, JsonNode.Parse(body)!["input"]!.AsArray().Count);
        }
        Assert.Equal("Grok kept repeating itself; the turn stopped.", Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Error);
        Assert.DoesNotContain((await session.CaptureContextAsync()).Entries, entry => entry.Message.Content == "failed-attempt-text");
    }

    private static LlmConnection RenewableConnection(int minutes) => Listed(new JsonObject()) with
    {
        Credential = new LlmCredential
        {
            OAuth = new()
            {
                AccessToken = "test-token",
                RefreshToken = "refresh",
                AccountId = "account",
                ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds()
            }
        }.Serialize()
    };

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class StreamClock : TimeProvider
    {
        internal ManualTimer? Timer { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new(callback, state, dueTime);
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        internal TimeSpan Due { get; private set; } = due;
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; return true; }
        internal void Fire() => callback(state);
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StalledStream(TaskCompletionSource reading) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static LlmConnection Listed(JsonObject facts) => Connection() with
    {
        Catalog = [new("grok-4.7", "Grok") { MaxContextTokens = 256000, CatalogEntry = JsonElement.Parse(facts.ToJsonString()) }]
    };

    private static Task<ManagedAgentSession> Session(HttpMessageHandler handler, LlmConnection connection,
        LearnedModelContext? learned = null, LlmAgentSessionOptions? options = null, TimeProvider? time = null,
        LlmProviderService? providers = null)
    {
        var client = new GrokResponsesLlmClient(new HttpClient(handler), time ?? TimeProvider.System);
        return ManagedAgentSession.CreateAsync("grok", connection, options ?? new() { ModelId = "grok-4.7" },
            connection.Catalog ?? [], client,
            (request, token) => providers is null ? client.StreamAsync(connection, request, token) : providers.StreamAgentAsync("grok", request, ProviderContinuationStateCodec.Scope(connection), token),
            model => client.GetCapabilities(connection, model), learned ?? new(), retryPolicy: client, sessionHostedTools: client);
    }

    private static async Task<List<LlmAgentEvent>> Run(ILlmAgentSession session)
    {
        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")] })) events.Add(item);
        return events;
    }

    internal static LlmConnection Connection(XaiConnectionOptions? options = null)
    {
        options ??= new();
        return new()
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = XaiRequestHeaders.SubscriptionEndpoint,
            Xai = options,
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token", AccountId = "account" } }.Serialize()
        };
    }

    private static LlmChatRequest Request(string model) => new() { ModelId = model, Messages = [new("user", "hello")] };

    private static LlmProviderService Service(Source source, Handler handler) =>
        new(source, [new GrokResponsesLlmClient(new HttpClient(handler), TimeProvider.System)], [new GrokAuthenticator(new HttpClient(handler))]);

    private static async Task<LlmChatResult> Complete(ResponsesLlmClientBase client, LlmConnection connection, LlmChatRequest request)
    {
        LlmChatResult? result = null;
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request))
            if (item is LlmChatCompletionEvent completion) result = completion.Result;
        return result!;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(string code) => new(HttpStatusCode.BadRequest)
    { Content = new StringContent("{\"error\":\"" + code + "\"}", Encoding.UTF8, "application/json") };

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public int Saves { get; private set; }
        public LlmCredential? Saved { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(connection.Version, expectedVersion);
            Saves++;
            Saved = LlmCredential.Parse(credentialJson);
            connection = connection with { Credential = credentialJson, Version = Guid.NewGuid().ToString() };
            return Task.CompletedTask;
        }
    }

    private sealed class Target : ISetupCommitTarget<LlmSetupChanges>
    {
        public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
