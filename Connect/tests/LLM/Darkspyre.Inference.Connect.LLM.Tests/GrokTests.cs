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

/// <summary>The Grok library (DSA-1070): device sign-in, hosted search, agent count, speed tier, provider compaction,
/// tokenizer estimates and continuation of hosted calls.</summary>
public sealed class GrokTests
{
    private const string DeviceCode = """
        {"device_code":"device-1","user_code":"ABCD","verification_uri":"https://accounts.x.ai/device","expires_in":900,"interval":1}
        """;
    private const string Tokens = """{"access_token":"grok-access","token_type":"Bearer","expires_in":3600,"refresh_token":"refresh"}""";

    /// <summary>The device login runs through the shared seam: a pending code keeps polling, slow_down widens the interval
    /// by five seconds, and the approved code is saved only after /v1/me names the account (B2, S1).</summary>
    [Fact]
    public async Task DeviceLoginPollsThroughTheSeamAndSavesTheAccountAfterApproval()
    {
        var polls = new Queue<HttpResponseMessage>([Error("authorization_pending"), Error("slow_down"), Json(Tokens)]);
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/oauth2/device/code" => Json(DeviceCode),
            "/oauth2/token" => polls.Dequeue(),
            "/v1/me" => Json("""{"user_id":"user-1","team_blocked":false}"""),
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

    /// <summary>Setup offers Grok's device sign-in and its API region; the region moves the endpoint, and the check
    /// after approval marks the connection signed in (B1, B2, B3, S1).</summary>
    [Fact]
    public async Task SetupSignsInWithTheDeviceCodeAndRoutesToTheChosenRegion()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/oauth2/device/code" => Json(DeviceCode),
            "/oauth2/token" => Json(Tokens),
            "/v1/me" => Json("""{"user_id":"user-1"}"""),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        using var service = Service(new Source(Connection() with { Credential = null }), handler);
        LlmSettingsManager manager = service.CreateSettingsManager(new Target());
        LlmSettings settings = await manager.OpenAsync(LlmTransport.XaiResponses,
            new(SetupChangeIntent.Create, "grok", null), null, "grok-4.7");

        var region = Assert.IsType<SetupChoiceField>(settings.Steps.SelectMany(step => step.Sections)
            .SelectMany(section => section.Fields).Single(field => field.Description.Id == "xai-region"));
        Assert.Equal(SetupEditState.Applied, region.SetValue(nameof(XaiRegion.UnitedStates)).State);
        SetupActionDescriptor signIn = Assert.Single(settings.Actions, action => action.Purpose == SetupActionPurpose.Authenticate);
        await manager.RunAsync(settings, signIn.Id);
        SetupActionResult approved = await manager.RunAsync(settings,
            Assert.IsType<SetupDeviceAuthorization>(settings.Interaction).Continue.Id);

        Assert.Equal(("authorize", "Sign in with Grok"), (signIn.Id, signIn.DisplayName));
        Assert.Equal("https://us.api.x.ai/v1", settings.CurrentConnection().Endpoint);
        Assert.Equal(SetupActionState.Completed, approved.State);
        Assert.True(settings.Status.Authenticated.Value);
        Assert.Equal(SetupProviderGroup.Subscription, GrokDefinition.Value.Group);
        Assert.NotNull(GrokDefinition.Value.Icon);
    }

    /// <summary>A denied or expired code and a team-blocked account each end sign-in with their message and save nothing.</summary>
    [Theory]
    [InlineData(false, "Grok sign-in was denied or expired. Start sign-in again.")]
    [InlineData(true, "This Grok account is not entitled to API access.")]
    public async Task RefusedSignInSavesNothing(bool teamBlocked, string message)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/oauth2/token" => teamBlocked ? Json(Tokens) : Error("access_denied"),
            "/v1/me" => Json("""{"user_id":"user-1","team_blocked":true}"""),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        var source = new Source(Connection() with { Credential = null });
        using var service = Service(source, handler);

        Exception refusal = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.CompleteDeviceLoginAsync(LlmTransport.XaiResponses, "grok", "device-1", "ABCD", "1"));

        Assert.Equal(message, refusal.Message);
        Assert.IsType(teamBlocked ? typeof(InferenceException) : typeof(LlmProviderException), refusal);
        Assert.Equal(0, source.Saves);
    }

    /// <summary>Grok writes its own hosted-tool shapes and X search, adds tool search for a deferred MCP server, always asks
    /// for no inline citations and sends the hosted-turn cap (Tech Design 5.3).</summary>
    [Fact]
    public async Task GrokPayloadWritesItsHostedShapesAndXSearch()
    {
        using var handler = new Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new GrokResponsesLlmClient(new HttpClient(handler));

        await Complete(client, Connection(), Request("grok-4.7") with
        {
            MaxHostedTurns = 3,
            HostedTools =
            [
                new LlmWebSearchTool(AllowedDomains: ["example.com"]),
                new LlmSocialSearchTool { AllowedHandles = ["xai"], FromDate = new(2026, 10, 1), ToDate = new(2026, 10, 16), IncludeImageUnderstanding = true },
                new LlmCodeExecutionTool(),
                new LlmDocumentSearchTool(["collection-1"]),
                new LlmRemoteMcpTool("https://mcp.example.test", "tools", ServerDescription: "Lookup", DeferLoading: true)
            ]
        });

        JsonNode sent = JsonNode.Parse(handler.Body!)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            [
              {"type":"web_search","enable_image_understanding":false,"enable_image_search":false,"allowed_domains":["example.com"]},
              {"type":"x_search","enable_image_understanding":true,"enable_video_understanding":false,"allowed_x_handles":["xai"],
               "from_date":"2026-10-01","to_date":"2026-10-16"},
              {"type":"code_interpreter"},
              {"type":"file_search","vector_store_ids":["collection-1"]},
              {"type":"mcp","server_label":"tools","server_url":"https://mcp.example.test","server_description":"Lookup","defer_loading":true},
              {"type":"tool_search"}
            ]
            """), sent["tools"]), sent["tools"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            ["reasoning.encrypted_content","web_search_call.action.sources","file_search_call.results","code_interpreter_call.outputs","no_inline_citations"]
            """), sent["include"]), sent["include"]!.ToJsonString());
        Assert.Equal(3, (int)sent["max_turns"]!);
        Assert.Equal("https://api.x.ai/v1/responses", handler.Uri!.AbsoluteUri);
    }

    /// <summary>On a multi-agent model 4 agents run under effort low and 16 under high; a turn that names none takes the
    /// saved count, and with neither the provider default runs (B15, S5).</summary>
    [Theory]
    [InlineData(4, null, "low")]
    [InlineData(16, null, "high")]
    [InlineData(null, 16, "high")]
    [InlineData(null, null, null)]
    public async Task AgentCountMapsToReasoningEffort(int? count, int? saved, string? effort)
    {
        using var handler = new Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new GrokResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = Connection(new() { MultiAgent = saved is null ? null : new XaiMultiAgentControls(saved) });

        await Complete(client, connection, Request("grok-4.20-multi-agent") with { AgentCount = count, ReasoningLevel = LlmReasoningLevel.Maximum });

        JsonNode sent = JsonNode.Parse(handler.Body!)!;
        Assert.Equal(effort, (string?)sent["reasoning"]?["effort"]);
        Assert.False(sent.AsObject().ContainsKey("agent_count"));
    }

    /// <summary>A multi-agent model offers 4 and 16 agents and no reasoning levels; any other model offers no count, so the
    /// normalizer drops a requested count and the turn still runs (B15, S5 step 2).</summary>
    [Fact]
    public void OnlyMultiAgentModelsOfferAgentCounts()
    {
        var client = new GrokResponsesLlmClient(new HttpClient());
        LlmProviderCapabilities multi = client.GetCapabilities(Connection(), "grok-4.20-multi-agent");
        LlmProviderCapabilities single = client.GetCapabilities(Connection(), "grok-4.7");

        Assert.Equal([4, 16], multi.AgentCounts);
        Assert.Empty(multi.ReasoningLevels);
        Assert.Empty(single.AgentCounts);
        Assert.Equal(16, LlmRequestNormalizer.Normalize(Connection(), Request("grok-4.20-multi-agent") with { AgentCount = 12 }, multi).AgentCount);
        Assert.Null(LlmRequestNormalizer.Normalize(Connection(), Request("grok-4.7") with { AgentCount = 16 }, single).AgentCount);
    }

    /// <summary>The fast tier is sent as priority; a turn that names no tier takes the saved tier (B16, S5).</summary>
    [Theory]
    [InlineData(LlmServiceTier.Fast, XaiServiceTier.Default, "priority")]
    [InlineData(null, XaiServiceTier.Priority, "priority")]
    [InlineData(LlmServiceTier.Standard, XaiServiceTier.Priority, "default")]
    [InlineData(null, XaiServiceTier.Default, "default")]
    public async Task FastTierSendsPriority(LlmServiceTier? tier, XaiServiceTier saved, string expected)
    {
        using var handler = new Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new GrokResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = Connection(new() { ServiceTier = saved });
        LlmProviderCapabilities capabilities = client.GetCapabilities(connection, "grok-4.7");

        await Complete(client, connection, LlmRequestNormalizer.Normalize(connection, Request("grok-4.7") with { ServiceTier = tier }, capabilities));

        Assert.Equal(expected, (string?)JsonNode.Parse(handler.Body!)!["service_tier"]);
        Assert.Equal([LlmServiceTier.Standard, LlmServiceTier.Fast], capabilities.ServiceTiers.Order());
    }

    /// <summary>Grok compacts through responses/compact: the instructions lead the input, the compacted context is one
    /// entry whose continuation state replays the compaction item, and the feature list reports the provider side. A
    /// failed call throws, so the session falls back to the host summary (B10, B11, S3).</summary>
    [Fact]
    public async Task ProviderCompactionReplacesTheContextWithTheCompactionItem()
    {
        bool fail = false;
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/responses/compact", StringComparison.Ordinal)
            ? fail ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") }
                : Json("""{"object":"response.compaction","id":"c","model":"grok-4.7","output":[{"type":"compaction","encrypted_content":"sealed"}]}""")
            : DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new GrokResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = Connection();

        LlmAgentContext compacted = await client.CompactAsync(connection,
            Request("grok-4.7") with { Instructions = "Be brief." }, CancellationToken.None);
        JsonNode compactBody = JsonNode.Parse(handler.Body!)!;
        LlmChatMessage summary = Assert.Single(compacted.Entries).Message;
        await Complete(client, connection, Request("grok-4.7") with { Messages = [summary, new("user", "next")] });
        JsonNode replay = JsonNode.Parse(handler.Body!)!;
        fail = true;

        Assert.Equal("grok-4.7", (string?)compactBody["model"]);
        Assert.Equal(("system", "Be brief."), ((string?)compactBody["input"]![0]!["role"], (string?)compactBody["input"]![0]!["content"]));
        Assert.Equal(("compaction", "sealed"), ((string?)replay["input"]![0]!["type"], (string?)replay["input"]![0]!["encrypted_content"]));
        Assert.Equal(LlmCompactionSide.Provider, client.GetCapabilities(connection, "grok-4.7").Agent.CompactionSide);
        Assert.True(client.CompactsProviderSide(connection, "grok-4.7"));
        await Assert.ThrowsAsync<LlmProviderException>(() => client.CompactAsync(connection, Request("grok-4.7"), CancellationToken.None));
    }

    /// <summary>The text after the last reported reply is counted by Grok's tokenizer; a failed tokenizer call keeps the
    /// local count of that text (B12).</summary>
    [Fact]
    public async Task TokenizerCountsTheUnreportedTextAndFallsBackToTheLocalCount()
    {
        bool fail = false;
        using var handler = new Handler(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
            : Json("""{"token_ids":[{"token_id":1,"string_token":"a","token_bytes":[97]},{"token_id":2,"string_token":"b","token_bytes":[98]},{"token_id":3,"string_token":"c","token_bytes":[99]}]}"""));
        var client = new GrokResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = Connection();
        LlmContinuationState reported = ProviderContinuationStateCodec.Encode(connection, "grok-4.7", new JsonArray(new JsonObject
        {
            ["type"] = "message", ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "earlier answer" })
        })) with { InputTokens = 40, OutputTokens = 2 };
        LlmChatRequest request = Request("grok-4.7") with
        {
            Messages = [new("user", "earlier question"), new("assistant", "earlier answer") { ContinuationState = reported },
                new("user", "the new question that the tokenizer counts")]
        };

        ContextEstimate counted = (await client.EstimateAsync(connection, request, ResourceSizes.None, CancellationToken.None))!.Value;
        string sent = (string)JsonNode.Parse(handler.Body!)!["text"]!;
        fail = true;
        ContextEstimate local = (await client.EstimateAsync(connection, request, ResourceSizes.None, CancellationToken.None))!.Value;

        Assert.Equal("the new question that the tokenizer counts\n", sent);
        Assert.Equal(local.Tokens - LocalTokenCount.Count(sent) + 3, counted.Tokens);
        Assert.False(counted.IncludesProviderReport);
    }

    /// <summary>A Grok reply holding reasoning, hosted calls and Grok's custom X-search call keeps them in its continuation
    /// state, and the next request replays them verbatim and in order.</summary>
    [Fact]
    public async Task ReplyWithHostedCallsRoundTrips()
    {
        const string output = """
            [{"type":"reasoning","id":"r","summary":[],"encrypted_content":"thought"},
             {"type":"x_search_call","id":"xs","status":"completed","action":{"query":"grok"}},
             {"type":"custom_tool_call","id":"ct","call_id":"xs_2","name":"x_keyword_search","input":"grok","status":"completed"},
             {"type":"web_search_call","id":"ws","status":"completed","action":{"type":"search","sources":[{"type":"url","url":"https://a.test"}]}},
             {"type":"message","id":"m","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Found.","annotations":[{"type":"url_citation","url":"https://a.test"}]}]}]
            """;
        using var handler = new Handler(_ => DirectCodexTests.Sse("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp\",\"status\":\"completed\",\"output\":"
            + output.ReplaceLineEndings("") + ",\"usage\":{\"input_tokens\":5,\"output_tokens\":2,\"total_tokens\":7}}}\n\n"));
        var client = new GrokResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = Connection();

        LlmChatResult reply = await Complete(client, connection, Request("grok-4.7"));
        await Complete(client, connection, Request("grok-4.7") with
        {
            Messages = [new("user", "hello"), new("assistant", reply.Text) { ContinuationState = reply.ContinuationState }, new("user", "more")]
        });

        JsonArray replayed = (JsonArray)JsonNode.Parse(handler.Bodies[1])!["input"]!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(output), new JsonArray([.. replayed.Skip(1).Take(5).Select(item => item!.DeepClone())])),
            replayed.ToJsonString());
        Assert.Equal("https://a.test", Assert.Single(reply.Citations).Url);
    }

    /// <summary>The OpenAI API and Grok write each remote MCP server's live authorization on its mcp entry before the
    /// payload is sent, asking a shared authority once (one shared implementation).</summary>
    [Theory]
    [InlineData(LlmTransport.OpenAiResponses)]
    [InlineData(LlmTransport.XaiResponses)]
    public async Task RemoteMcpAuthorizationIsWrittenOnTheMcpEntry(LlmTransport transport)
    {
        using var handler = new Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        ResponsesLlmClientBase client = transport == LlmTransport.XaiResponses
            ? new GrokResponsesLlmClient(new HttpClient(handler)) : new OpenAiResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = transport == LlmTransport.XaiResponses
            ? Connection() : DirectCodexTests.OpenAiConnection(OpenAiResponsesLlmClient.DefaultEndpoint);
        var authority = new Authority();

        await Complete(client, connection, Request(transport == LlmTransport.XaiResponses ? "grok-4.7" : "gpt-5.5") with
        {
            HostedTools =
            [
                new LlmRemoteMcpTool("https://one.example.test", "one", Authorization: authority),
                new LlmRemoteMcpTool("https://two.example.test", "two", Authorization: authority),
                new LlmRemoteMcpTool("https://three.example.test", "three")
            ]
        });

        JsonNode[] servers = [.. ((JsonArray)JsonNode.Parse(handler.Body!)!["tools"]!).Select(tool => tool!)];
        Assert.Equal(["secret-token", "secret-token", null], servers.Select(server => (string?)server["authorization"]));
        Assert.Equal(1, authority.Calls);
    }

    internal static LlmConnection Connection(XaiConnectionOptions? options = null)
    {
        options ??= new();
        return new()
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = options.GetEndpoint(),
            Xai = options,
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token", AccountId = "account" } }.Serialize()
        };
    }

    private static LlmChatRequest Request(string model) => new() { ModelId = model, Messages = [new("user", "hello")] };

    private static LlmProviderService Service(Source source, Handler handler) =>
        new(source, [new GrokResponsesLlmClient(new HttpClient(handler))], [new GrokAuthenticator(new HttpClient(handler))]);

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
            Saves++;
            Saved = LlmCredential.Parse(credentialJson);
            return Task.CompletedTask;
        }
    }

    private sealed class Target : ISetupCommitTarget<LlmSetupChanges>
    {
        public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Authority : IProviderAuthorization
    {
        public int Calls { get; private set; }
        public ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new ProviderAuthorizationHeader("Bearer", "secret-token"));
        }
    }
}
