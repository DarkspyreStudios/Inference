using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

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
            ReasoningEffort = "high",
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
        Assert.Equal("thread", sent.RootElement.GetProperty("prompt_cache_key").GetString());
        Assert.Single(sent.RootElement.GetProperty("input").EnumerateArray());
        Assert.False(sent.RootElement.TryGetProperty("previous_response_id", out _));
        Assert.Equal("Hello", result.Text);
        Assert.Equal(7, result.Usage?.TotalTokens);
        Assert.Contains("final_answer", result.ResponseState?.Payload);
        Assert.DoesNotContain("token", result.ResponseState?.Payload);
        Assert.Equal(LlmSetupAction.AuthorizeDeviceCode, client.ProviderDefinition.SetupSteps[0].Action);
        Assert.True(client.GetCapabilities(Connection()).SupportsToolUse);
        Assert.Equal(LlmAgentContextOwnership.Host, client.GetCapabilities(Connection()).Agent.ContextOwnership);
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
        var result = Assert.Single(stream.OfType<LlmCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.RequiresAction, result.Status);
        Assert.Equal("call", Assert.Single(result.ToolCalls).Id);
        var state = ProviderResponseStateCodec.Decode(result.ResponseState!, Connection(), "model");
        Assert.Equal("reasoning", state[0]!["type"]!.GetValue<string>());
        Assert.Equal("function_call", state[1]!["type"]!.GetValue<string>());
        handler.Response = _ => Sse(Completed);
        await Complete(client, Connection(), Request() with
        {
            Messages = [new("user", "first"),
            new("assistant", result.Text) { ToolCalls = result.ToolCalls, ResponseState = result.ResponseState },
            new("user", "") { ToolResults = [new("call", "answer")] }]
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
        var followup = Request() with { Messages = [new("assistant", result.Text) { ResponseState = result.ResponseState }, new("user", "next")] };
        await Complete(client, Connection(), followup);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(2, sent.RootElement.GetProperty("input").GetArrayLength());
        int before = handler.Calls;
        await Assert.ThrowsAsync<ArgumentException>(() => Complete(client, Connection(), followup with { ModelId = "other" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Complete(client, Connection() with { Credential = Credential("other") }, followup));
        Assert.Equal(before, handler.Calls);
    }

    [Fact]
    public async Task ImagesDocumentsAndToolAttachmentsUseAdapterOwnedWireBlocks()
    {
        using var handler = new Handler(_ => Sse(Completed));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        var image = new LlmAttachment
        {
            Kind = LlmAttachmentKind.Image,
            FileName = "x.png",
            Data = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jG9sAAAAASUVORK5CYII=")
        };
        var document = new LlmAttachment { Kind = LlmAttachmentKind.Document, FileName = "x.txt", Data = "word"u8.ToArray() };
        await Complete(client, Connection(), Request() with
        {
            Messages = [new("user", "see") { Attachments = [image, document] },
            new("assistant", "") { ToolCalls = [new("call", "echo", "{}")] },
            new("user", "") { ToolResults = [new("call", "read") { Attachments = [image, document] }] }]
        });
        using var sent = JsonDocument.Parse(handler.Body!);
        string payload = sent.RootElement.GetRawText();
        Assert.Contains("data:image/png;base64,", payload);
        Assert.Contains(Convert.ToBase64String("word"u8), payload);
        Assert.Contains("function_call_output", payload);
        Assert.DoesNotContain("OmittedAttachments", payload);
    }

    [Fact]
    public void ContextEstimateIncludesInstructionsAndSchemasButDoesNotInventOpaqueOrImageCounts()
    {
        using var http = new HttpClient();
        var client = new OpenAiCodexLlmClient(http);
        long baseline = Assert.IsType<long>(client.EstimateInputTokens(Connection(), Request()));
        Assert.True(baseline > 0);
        long expanded = Assert.IsType<long>(client.EstimateInputTokens(Connection(), Request() with
        { Instructions = string.Join(' ', Enumerable.Repeat("instruction", 100)) }));
        Assert.True(expanded > baseline);
        Assert.Null(client.EstimateInputTokens(Connection(), Request() with
        {
            Messages = [new("user", "read")
        { Attachments = [new() { Kind = LlmAttachmentKind.Document, FileName = "x.txt", Data = "text"u8.ToArray() }] }]
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
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection() with
        { ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides> { ["model"] = new() { SupportsReasoningEffort = false } } },
            Request() with { ReasoningEffort = "high" }));
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
        var agents = new LlmAgentProviderService(source, [managed]);
        var options = new LlmAgentSessionOptions
        {
            ModelId = "model",
            WorkingDirectory = Environment.CurrentDirectory,
            Tools = [new() { Name = "echo", InputSchema = JsonSerializer.SerializeToElement(new
            { type = "object", properties = new { }, additionalProperties = false }) }],
            Handlers = new()
            {
                ToolHandler = (_, _) =>
            {
                source.Connection = source.Connection with { Credential = Credential(switchAccount ? "other" : "account") };
                if (disableAgent) { source.Connection = source.Connection with { CapabilityOverrides = new() { SupportsAgentSessions = false } }; }
                return ValueTask.FromResult(new LlmAgentToolResult());
            }
            }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => managed.StartSessionAsync(Connection(), options));
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
        Assert.Single((await Read(client.StreamAsync(connection, request))).OfType<LlmCompletionEvent>()).Result;
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
