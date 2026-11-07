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

    internal const string Completed = """
        data: {"type":"response.output_text.delta","delta":"Hello"}

        data: {"type":"response.completed","response":{"id":"response","status":"completed","output":[{"type":"message","id":"msg","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"Hello"}]}],"usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}}

        """;

    private const string ReasonedToolCall = """
        data: {"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc","call_id":"call","name":"echo","arguments":"{}"}}

        data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"reason","encrypted_content":"opaque","summary":[{"type":"summary_text","text":"Thinking"}]}}

        data: {"type":"response.completed","response":{"status":"completed","output":[],"id":"r"}}

        """;

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

    /// <summary>The validated Codex shape returns observed citations and replays the call, action, sources and text.</summary>
    [Fact]
    public async Task WebSearchIsMappedAndReplayedWithCitations()
    {
        const string searchReply = """
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"web_search_call","id":"ws_probe","status":"completed","action":{"type":"search","queries":["site.nasa.gov Artemis program NASA official"],"query":"site.nasa.gov Artemis program NASA official","sources":[{"type":"url","url":"https://www.nasa.gov/humans-in-space/artemis/"}]}}}

            data: {"type":"response.output_text.delta","delta":"NASA Artemis"}

            data: {"type":"response.output_item.done","output_index":1,"item":{"type":"message","id":"msg_probe","role":"assistant","content":[{"type":"output_text","text":"NASA Artemis","annotations":[{"type":"url_citation","url":"https://www.nasa.gov/humans-in-space/artemis/","title":"NASA Artemis","start_index":0,"end_index":12}]}]}}

            data: {"type":"response.completed","response":{"id":"response_probe","status":"completed","output":[]}}

            """;
        using var handler = new Handler(_ => Sse(searchReply));
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        LlmConnection connection = Connection();
        Assert.Contains(LlmHostedToolKind.WebSearch, client.GetCapabilities(connection, "gpt-6.1-sol").HostedTools);
        LlmChatResult first = await Complete(client, connection, Request() with
        {
            ModelId = "gpt-6.1-sol", HostedTools = [new LlmWebSearchTool()],
        });
        Assert.Equal(LlmCompletionStatus.Completed, first.Status);
        LlmCitation citation = Assert.Single(first.Citations);
        Assert.Equal("https://www.nasa.gov/humans-in-space/artemis/", citation.Url);
        Assert.Equal("NASA Artemis", citation.Title);
        Assert.Equal("0.159.3", handler.Headers["version"]);
        using (JsonDocument sent = JsonDocument.Parse(handler.Body!))
        {
            Assert.Equal("web_search", Assert.Single(sent.RootElement.GetProperty("tools").EnumerateArray()).GetProperty("type").GetString());
            Assert.Contains(sent.RootElement.GetProperty("include").EnumerateArray(), entry => entry.GetString() == "web_search_call.action.sources");
        }
        Assert.NotNull(first.ContinuationState);
        handler.Response = _ => Sse(Completed);
        _ = await Complete(client, connection, Request() with
        {
            ModelId = "gpt-6.1-sol", HostedTools = [new LlmWebSearchTool()],
            Messages = [new("user", "Find NASA Artemis"),
                new("assistant", first.Text) { ContinuationState = first.ContinuationState }, new("user", "What was the source?")],
        });
        using JsonDocument replay = JsonDocument.Parse(handler.Body!);
        JsonElement[] input = [.. replay.RootElement.GetProperty("input").EnumerateArray()];
        JsonElement call = Assert.Single(input, item => item.GetProperty("type").GetString() == "web_search_call");
        Assert.Equal("ws_probe", call.GetProperty("id").GetString());
        Assert.Equal("completed", call.GetProperty("status").GetString());
        Assert.Equal("search", call.GetProperty("action").GetProperty("type").GetString());
        Assert.Equal("site.nasa.gov Artemis program NASA official", call.GetProperty("action").GetProperty("queries")[0].GetString());
        JsonElement answer = Assert.Single(input, item => item.GetProperty("type").GetString() == "message" &&
            item.GetProperty("role").GetString() == "assistant");
        Assert.Equal(citation.Url, call.GetProperty("action").GetProperty("sources")[0].GetProperty("url").GetString());
        Assert.Equal(first.Text, answer.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(2, handler.Calls);
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

    /// <summary>An OpenAI API connection at <paramref name="endpoint"/> with no key.</summary>
    internal static LlmConnection OpenAiConnection(string endpoint) =>
        new() { Version = "1", Transport = LlmTransport.OpenAiResponses, Endpoint = endpoint };

    private static async Task<List<LlmAgentEvent>> RunAsync(ManagedAgentSession session, LlmAgentTurnRequest turn)
    {
        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(turn)) events.Add(item);
        return events;
    }

    private static async Task<List<LlmAgentEvent>> RunAsync(ManagedAgentSession session, string text)
    {
        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent(text)] }))
            events.Add(item);
        return events;
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
