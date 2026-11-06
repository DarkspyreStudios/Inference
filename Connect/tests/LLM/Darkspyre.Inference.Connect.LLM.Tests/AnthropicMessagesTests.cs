using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;
using Handler = Darkspyre.Inference.Connect.LLM.Tests.DirectCodexTests.Handler;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>The Anthropic API-key library: registration, managed turns, hosted web search and key handling.</summary>
public sealed class AnthropicMessagesTests
{
    private const string Key = "sk-ant-test-key-0123456789";
    private const string Model = "claude-test";

    /// <summary>
    /// Discovery lists the Anthropic API-key provider, and its agent client is the shared managed one.
    /// Defect: the provider is not discovered, or the old dedicated agent client is wired.
    /// </summary>
    [Fact]
    public async Task RegistersSharedManagedClient()
    {
        using TestDataFolder folder = new();
        ServiceCollection services = new();
        // The host supplies what every discovered provider needs, as an application does.
        services.AddSingleton<ILlmConnectionSource>(new Source(Connection()));
        services.AddSingleton<ILlmDataFolder>(folder);
        services.AddSingleton<ILlmSelfHost>(new UnusedSelfHost());
        services.AddLlmConnect();
        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Contains(provider.GetRequiredService<LlmProviderCatalog>().SupportedProviders,
            definition => definition.Id == "anthropic-api-key");
        ILlmAgentClient agents = Assert.Single(provider.GetServices<ILlmAgentClient>(),
            client => client.Transport == LlmTransport.AnthropicMessages);
        Assert.IsType<ManagedLlmAgentClient<AnthropicMessagesLlmClient>>(agents);
    }

    /// <summary>
    /// A managed turn streams thinking, text and one host tool call through the shared opening, runs the tool, and sends
    /// its result in the next request. Defect: the stream or the tool continuation broke in the move.
    /// </summary>
    [Fact]
    public async Task ManagedTurnStreamsTextReasoningAndToolCall()
    {
        int posts = 0;
        using var handler = new Handler(request => request.Method == HttpMethod.Get ? Models(webSearch: false)
            : ++posts == 1
                ? Sse(Start(), Block(0, new JsonObject { ["type"] = "thinking", ["thinking"] = "" }),
                    Delta(0, new JsonObject { ["type"] = "thinking_delta", ["thinking"] = "Considering." }),
                    Delta(0, new JsonObject { ["type"] = "signature_delta", ["signature"] = "signed" }), Stop(0),
                    Block(1, new JsonObject { ["type"] = "text", ["text"] = "" }),
                    Delta(1, new JsonObject { ["type"] = "text_delta", ["text"] = "Reading it." }), Stop(1),
                    Block(2, new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = "read_fact", ["input"] = new JsonObject() }),
                    Delta(2, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"name\":\"a\"}" }), Stop(2),
                    End("tool_use"), Done())
                : Sse(Start(), Block(0, new JsonObject { ["type"] = "text", ["text"] = "" }),
                    Delta(0, new JsonObject { ["type"] = "text_delta", ["text"] = "It is a." }), Stop(0), End("end_turn"), Done()));
        var client = new AnthropicMessagesLlmClient(new HttpClient(handler));
        LlmConnection connection = Connection();
        using var providers = new LlmProviderService(new Source(connection), [client], []);
        var agents = new ManagedLlmAgentClient<AnthropicMessagesLlmClient>(client, providers, new());
        string? toolInput = null;
        await using ILlmAgentSession session = await agents.StartSessionAsync("anthropic", connection, new()
        {
            ModelId = Model,
            Tools = [new LlmAgentToolDefinition { Name = "read_fact", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = (call, _) =>
            {
                toolInput = call.Arguments.GetRawText();
                return Task.FromResult(LlmToolOutput.FromText("fact a"));
            }
        });

        List<LlmAgentEvent> events = await Run(session);

        LlmAgentTurnCompletedEvent completed = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Null(completed.Error);
        Assert.Contains(events.OfType<LlmAgentReasoningDeltaEvent>(), reasoning => reasoning.Delta == "Considering.");
        Assert.Contains(events.OfType<LlmAgentTextDeltaEvent>(), text => text.Delta == "Reading it.");
        Assert.Equal("{\"name\":\"a\"}", toolInput);
        Assert.Equal(LlmAgentCompletionReason.Completed, completed.Reason);
        JsonArray messages = JsonNode.Parse(handler.Bodies[^1])!["messages"]!.AsArray();
        JsonNode result = messages[^1]!["content"]!.AsArray().Single(block => (string?)block!["type"] == "tool_result")!;
        Assert.Equal("toolu_1", (string?)result["tool_use_id"]);
        Assert.Equal("fact a", (string?)result["content"]);
    }

    /// <summary>
    /// On a model whose catalog reports web search, the hosted tool writes the basic entry; a search that pauses
    /// continues in the same step, reports its start, its query and sources, and one citation, and sends the paused
    /// blocks back unchanged; the result keeps every block for the next request. An error result reports its code and no
    /// sources. Defect: wrong wire, the pause ends the turn, an observation or citation is lost, or replay is altered.
    /// </summary>
    [Fact]
    public async Task WebSearchIsMappedObservedAndCited()
    {
        int posts = 0;
        JsonObject call = new() { ["type"] = "server_tool_use", ["id"] = "srvtoolu_1", ["name"] = "web_search", ["input"] = new JsonObject() };
        JsonObject results = new()
        {
            ["type"] = "web_search_tool_result",
            ["tool_use_id"] = "srvtoolu_1",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "web_search_result", ["url"] = "https://docs.example/a", ["title"] = "A",
                ["page_age"] = "1 day", ["encrypted_content"] = "opaque"
            })
        };
        JsonObject citation = new()
        {
            ["type"] = "web_search_result_location", ["url"] = "https://docs.example/a", ["title"] = "A",
            ["encrypted_index"] = "index", ["cited_text"] = "a"
        };
        JsonObject firstStart = Start();
        JsonObject secondStart = Start();
        foreach (JsonObject start in new[] { firstStart, secondStart })
        {
            start["message"]!["usage"]!["cache_read_input_tokens"] = 2;
            start["message"]!["usage"]!["cache_creation_input_tokens"] = 3;
            start["message"]!["usage"]!["server_tool_use"] = new JsonObject { ["web_search_requests"] = 1 };
        }
        JsonObject finish = End("end_turn");
        finish["usage"]!["server_tool_use"] = new JsonObject { ["web_search_requests"] = 2 };
        using var handler = new Handler(_ => ++posts == 1
            ? Sse(firstStart, Block(0, call),
                Delta(0, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"query\":\"what is a\"}" }), Stop(0),
                End("pause_turn"), Done())
            : Sse(secondStart, Block(0, results), Stop(0), Block(1, new JsonObject { ["type"] = "text", ["text"] = "" }),
                Delta(1, new JsonObject { ["type"] = "citations_delta", ["citation"] = citation }),
                Delta(1, new JsonObject { ["type"] = "text_delta", ["text"] = "A is a." }), Stop(1), finish, Done()));
        var client = new AnthropicMessagesLlmClient(new HttpClient(handler));
        LlmConnection connection = Connection() with { Catalog = [Listed(webSearch: true)] };

        List<LlmStreamEvent> events = await Read(client.StreamAsync(connection, Request() with
        { HostedTools = [new LlmWebSearchTool(AllowedDomains: ["docs.example"])] }));

        JsonNode tool = Assert.Single(JsonNode.Parse(handler.Bodies[0])!["tools"]!.AsArray())!;
        Assert.Equal("{\"type\":\"web_search_20250305\",\"name\":\"web_search\",\"allowed_domains\":[\"docs.example\"]}", tool.ToJsonString());
        Assert.Equal("srvtoolu_1", Assert.Single(events.OfType<LlmHostedToolCallStartedEvent>()).CallId);
        LlmHostedToolCall completed = Assert.Single(events.OfType<LlmHostedToolCallCompletedEvent>()).Call;
        Assert.Equal(("srvtoolu_1", LlmHostedToolKind.WebSearch, (string?)null), (completed.CallId, completed.Kind, completed.Status));
        Assert.Equal("what is a", completed.Input!.Value.GetProperty("query").GetString());
        LlmHostedSearchSource source = Assert.Single(Assert.IsType<LlmHostedSearchOutput>(completed.Output).Sources!);
        Assert.Equal(("https://docs.example/a", "A"), (source.Url, source.Title));
        Assert.Equal(new LlmCitation("https://docs.example/a", "A"), Assert.Single(events.OfType<LlmCitationEvent>()).Citation);

        // The paused content went back unchanged as the assistant message of the continued request.
        Assert.Equal(2, handler.Bodies.Count);
        JsonNode paused = JsonNode.Parse(handler.Bodies[1])!["messages"]!.AsArray()[^1]!;
        Assert.Equal("assistant", (string?)paused["role"]);
        Assert.Equal(new JsonArray(Final(call, "what is a")).ToJsonString(), paused["content"]!.ToJsonString());

        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("A is a.", result.Text);
        Assert.Equal(30, result.Usage!.InputTokens); Assert.Equal(10, result.Usage.OutputTokens);
        Assert.Equal(4, result.Usage.CachedInputTokens); Assert.Equal(6, result.Usage.CacheWriteInputTokens);
        Assert.NotNull(result.Usage.HostedToolCalls);
        Assert.Equal(3, result.Usage.HostedToolCalls[LlmHostedToolKind.WebSearch]);
        Assert.Null(result.Usage.ProviderReportedCostUsd);
        Assert.Equal(new LlmCitation("https://docs.example/a", "A"), Assert.Single(result.Citations));
        JsonArray kept = AnthropicContinuation.ReadPayload(result.ContinuationState!.Payload).Content;
        Assert.Equal(["server_tool_use", "web_search_tool_result", "text"], kept.Select(block => (string?)block!["type"]));
        Assert.Equal(results.ToJsonString(), kept[1]!.ToJsonString());
        Assert.Equal(citation.ToJsonString(), kept[2]!["citations"]![0]!.ToJsonString());

        // An error result reports the provider's own code and no sources.
        using var failing = new Handler(_ => Sse(Start(), Block(0, call), Stop(0), Block(1, new JsonObject
        {
            ["type"] = "web_search_tool_result", ["tool_use_id"] = "srvtoolu_1",
            ["content"] = new JsonObject { ["type"] = "web_search_tool_result_error", ["error_code"] = "max_uses_exceeded" }
        }), Stop(1), End("end_turn"), Done()));
        List<LlmStreamEvent> failed = await Read(new AnthropicMessagesLlmClient(new HttpClient(failing)).StreamAsync(connection,
            Request() with { HostedTools = [new LlmWebSearchTool()] }));
        LlmHostedToolCall error = Assert.Single(failed.OfType<LlmHostedToolCallCompletedEvent>()).Call;
        Assert.Equal("max_uses_exceeded", error.Status);
        Assert.Null(error.Output);
    }

    /// <summary>
    /// Search citations cannot use native JSON format: the schema is guidance and the existing local validator checks it.
    /// A request without search keeps native format. Defect: a search/schema request gets a provider400 or its answer is unchecked.
    /// </summary>
    [Fact]
    public async Task WebSearchSchemaUsesLocalValidationAndKeepsNativeFormatWithoutSearch()
    {
        int posts = 0;
        using var handler = new Handler(_ =>
        {
            string answer = ++posts == 1 ? "{\"answer\":7}" : "{\"answer\":\"yes\"}";
            return Sse(Start(), Block(0, new JsonObject { ["type"] = "text", ["text"] = "" }),
                Delta(0, new JsonObject { ["type"] = "text_delta", ["text"] = answer }), Stop(0), End("end_turn"), Done());
        });
        var client = new AnthropicMessagesLlmClient(new HttpClient(handler));
        LlmConnection connection = Connection() with { Catalog = [Listed(webSearch: true)] };
        JsonElement schema = JsonElement.Parse("{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}");
        LlmChatRequest request = Request() with { StructuredOutput = new() { Name = "research_result", Schema = schema }, HostedTools = [new LlmWebSearchTool()] };
        List<LlmStreamEvent> searched = await Read(client.StreamAsync(connection, request));
        JsonNode payload = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Null(payload["output_config"]?["format"]);
        Assert.Contains(schema.GetRawText(), string.Join("\n", payload["system"]!.AsArray().Select(block => (string?)block!["text"])), StringComparison.Ordinal);
        LlmChatResult local = Assert.Single(searched.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly, local.StructuredOutput!.Enforcement);
        Assert.True(local.StructuredOutput.WasEvaluated); Assert.False(local.StructuredOutput.IsValid);
        List<LlmStreamEvent> plain = await Read(client.StreamAsync(connection, request with { HostedTools = [] }));
        JsonNode native = JsonNode.Parse(handler.Bodies[1])!;
        Assert.Equal("json_schema", (string?)native["output_config"]!["format"]!["type"]);
        Assert.Equal(schema.GetRawText(), native["output_config"]!["format"]!["schema"]!.ToJsonString());
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, Assert.Single(plain.OfType<LlmChatCompletionEvent>()).Result.StructuredOutput!.Enforcement);
    }

    /// <summary>
    /// The key is sent only as the x-api-key header, and an authentication failure's text does not hold it, even when the
    /// response echoes it. Defect: the key leaks into a header, an exception or its text.
    /// </summary>
    [Fact]
    public async Task ApiKeyIsSentOnlyAsHeader()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key " + Key + "\"}}",
                Encoding.UTF8, "application/json")
        });
        var client = new AnthropicMessagesLlmClient(new HttpClient(handler));

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => Read(client.StreamAsync(Connection(), Request())));

        Assert.Equal(Key, handler.Headers["x-api-key"]);
        Assert.False(handler.Headers.ContainsKey("Authorization"));
        Assert.DoesNotContain(Key, handler.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, handler.Uri!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, failure.SanitizedResponseBody ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// (a) An idle session on a model without web search measures its context with one count request and reports the
    /// provider's 1234 tokens with no margin and a Mixed source; a model step sends no count. (b) On a model with web
    /// search the request carries it, so no count is sent and the reading is the local estimate of that request.
    /// Defect: the count path is not wired, a partial count passes as the whole request, a server tool reaches the count
    /// route, or every step adds a count.
    /// </summary>
    [Fact]
    public async Task IdleContextStatusUsesProviderCount()
    {
        var counts = new List<string>();
        using var handler = new Handler(request => request.Method == HttpMethod.Get ? Models(webSearch: false)
            : request.RequestUri!.AbsolutePath.EndsWith("/count_tokens", StringComparison.Ordinal)
                ? Count(counts, request)
                : Sse(Start(), Block(0, new JsonObject { ["type"] = "text", ["text"] = "" }),
                    Delta(0, new JsonObject { ["type"] = "text_delta", ["text"] = "done" }), Stop(0), End("end_turn"), Done()));
        var client = new AnthropicMessagesLlmClient(new HttpClient(handler));
        LlmConnection connection = Connection();
        using var providers = new LlmProviderService(new Source(connection), [client], []);
        var agents = new ManagedLlmAgentClient<AnthropicMessagesLlmClient>(client, providers, new());
        await using (ILlmAgentSession session = await agents.StartSessionAsync("anthropic", connection, new()
        { ModelId = Model, HostedTools = [new LlmWebSearchTool()] }))
        {
            LlmAgentContextStatus counted = await session.GetContextStatusAsync();

            Assert.Equal(1234, counted.UsedContextTokens);
            Assert.Equal(LlmContextMeasurementSource.Mixed, counted.Source);
            string body = Assert.Single(counts);
            Assert.DoesNotContain("web_search", body, StringComparison.Ordinal);
            Assert.DoesNotContain("max_tokens", body, StringComparison.Ordinal);

            List<LlmAgentEvent> events = await Run(session);
            Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
            Assert.Single(counts);
        }

        // On a model that offers web search, the request carries the server tool, which the count route refuses.
        using var searching = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/count_tokens", StringComparison.Ordinal)
            ? Count(counts, request) : throw new InvalidOperationException("Measuring must not generate a response."));
        var searchClient = new AnthropicMessagesLlmClient(new HttpClient(searching));
        LlmConnection listed = Connection() with { Catalog = [Listed(webSearch: true)] };
        var recorder = new RecordingEstimator(searchClient);
        await using ManagedAgentSession estimated = await ManagedAgentSession.CreateAsync("anthropic", listed,
            new() { ModelId = Model, HostedTools = [new LlmWebSearchTool()] }, listed.Catalog!, recorder,
            (_, _) => throw new InvalidOperationException("Measuring must not generate a response."),
            model => searchClient.GetCapabilities(listed, model), new LearnedModelContext());

        LlmAgentContextStatus local = await estimated.GetContextStatusAsync();

        Assert.Single(counts);
        Assert.Equal(0, searching.Calls);
        Assert.Contains(recorder.LastRequest!.HostedTools, tool => tool is LlmWebSearchTool);
        ContextEstimate estimate = Assert.NotNull(recorder.LastEstimate);
        Assert.Equal(estimate.Tokens + ManagedAgentPolicy.EstimateMargin, local.UsedContextTokens);
        Assert.NotEqual(LlmContextMeasurementSource.Mixed, local.Source);
    }

    // Records each count request's body and answers it with 1234 input tokens.
    private static HttpResponseMessage Count(List<string> counts, HttpRequestMessage request)
    {
        counts.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"input_tokens\":1234}", Encoding.UTF8, "application/json") };
    }

    /// <summary>The real client behind the session, recording the local estimate the session asked for.</summary>
    private sealed class RecordingEstimator(AnthropicMessagesLlmClient client) : ILlmContextEstimator, ILlmInputTokenCounter
    {
        public LlmChatRequest? LastRequest { get; private set; }
        public ContextEstimate? LastEstimate { get; private set; }
        public string? EstimateDiagnostic => client.EstimateDiagnostic;

        public async ValueTask<ContextEstimate?> EstimateAsync(LlmConnection connection, LlmChatRequest request,
            IReadOnlyDictionary<Common.Resources.InferenceResourceReference, Common.Resources.InferenceResourceDescriptor?> resources,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastEstimate = await client.EstimateAsync(connection, request, resources, cancellationToken);
            return LastEstimate;
        }

        public ValueTask<long?> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request,
            CancellationToken cancellationToken) => client.CountInputTokensAsync(connection, request, cancellationToken);
    }

    internal static LlmConnection Connection() => new()
    {
        Version = "1",
        Transport = LlmTransport.AnthropicMessages,
        Endpoint = "https://api.anthropic.test/v1",
        Credential = new LlmCredential { ApiKey = Key }.Serialize()
    };

    /// <summary>One model's catalog entry, reporting web search or not.</summary>
    internal static LlmModel Listed(bool webSearch) => new(Model, "Claude")
    {
        CatalogEntry = JsonElement.Parse(Entry(webSearch).ToJsonString())
    };

    /// <summary>The Models API page listing the one model.</summary>
    internal static HttpResponseMessage Models(bool webSearch) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject { ["data"] = new JsonArray(Entry(webSearch)), ["has_more"] = false }.ToJsonString(),
            Encoding.UTF8, "application/json")
    };

    private static JsonObject Entry(bool webSearch) => new()
    {
        ["id"] = Model,
        ["display_name"] = "Claude",
        ["capabilities"] = new JsonObject
        { ["server_tools"] = new JsonObject { ["web_search"] = new JsonObject { ["supported"] = webSearch } } }
    };

    private static LlmChatRequest Request() => new() { ModelId = Model, Messages = [new("user", "hello")] };

    // The server tool call block as it ends: its start with the assembled input.
    private static JsonObject Final(JsonObject call, string query)
    {
        JsonObject final = call.DeepClone().AsObject();
        final["input"] = new JsonObject { ["query"] = query };
        return final;
    }

    internal static HttpResponseMessage Sse(params JsonObject[] events) =>
        DirectCodexTests.Sse(string.Concat(events.Select(item => "event: " + (string?)item["type"] + "\ndata: " + item.ToJsonString() + "\n\n")));

    internal static JsonObject Start() => new()
    {
        ["type"] = "message_start",
        ["message"] = new JsonObject
        {
            ["id"] = "msg_1", ["type"] = "message", ["role"] = "assistant", ["model"] = Model, ["content"] = new JsonArray(),
            ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 1 }
        }
    };

    internal static JsonObject Block(int index, JsonObject block) =>
        new() { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = block.DeepClone() };

    internal static JsonObject Delta(int index, JsonObject delta) =>
        new() { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta.DeepClone() };

    internal static JsonObject Stop(int index) => new() { ["type"] = "content_block_stop", ["index"] = index };

    internal static JsonObject End(string reason) => new()
    {
        ["type"] = "message_delta",
        ["delta"] = new JsonObject { ["stop_reason"] = reason },
        ["usage"] = new JsonObject { ["output_tokens"] = 5 }
    };

    internal static JsonObject Done() => new() { ["type"] = "message_stop" };

    private static async Task<List<LlmStreamEvent>> Read(IAsyncEnumerable<LlmStreamEvent> source)
    {
        var result = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in source) result.Add(item);
        return result;
    }

    private static async Task<List<LlmAgentEvent>> Run(ILlmAgentSession session)
    {
        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")] })) events.Add(item);
        return events;
    }

    private sealed class UnusedSelfHost : ILlmSelfHost
    {
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Common.Embedding.IEmbeddingInference> GetEmbeddingInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) => throw new NotSupportedException();
    }

    internal sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
