using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AnthropicNativeToolsTests
{
    private const string Model = "claude-sonnet-4-6";
    private const string Schema = """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""";
    private const string Mixed = """
        {"server_tools":[{"type":"code_execution_20260120","name":"code_execution"},
                         {"type":"web_search_20250305","name":"web_search","max_uses":3,"allowed_domains":["example.invalid"]}],
         "client_tools":{"run":{"allowed_callers":["code_execution_20260120"],"input_examples":[{"value":"fixture"}]}}}
        """;
    private static LlmToolDefinition Tool => new() { Name = "run", Description = "Typed description", InputSchema = JsonElement.Parse(Schema) };
    private static LlmChatRequest Request => new() { ModelId = Model, Messages = [new("user", "hello")], ReasoningLevel = LlmReasoningLevel.None };
    private static LlmConnection Connection(bool streaming = false) => new()
    {
        Version = "1",
        Transport = LlmTransport.AnthropicMessages,
        Endpoint = "https://api.anthropic.invalid",
        Credential = new LlmCredential { ApiKey = "fixture-key" }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };
    private static Dictionary<string, JsonElement> Options(string json) => new(StringComparer.Ordinal)
    { [AnthropicTools.OptionName] = JsonElement.Parse(json) };

    [Theory]
    [InlineData("web_search_20250305", "web_search")]
    [InlineData("web_search_20260209", "web_search")]
    [InlineData("web_search_20260318", "web_search")]
    [InlineData("web_fetch_20250910", "web_fetch")]
    [InlineData("web_fetch_20260209", "web_fetch")]
    [InlineData("web_fetch_20260309", "web_fetch")]
    [InlineData("web_fetch_20260318", "web_fetch")]
    [InlineData("code_execution_20250522", "code_execution")]
    [InlineData("code_execution_20250825", "code_execution")]
    [InlineData("code_execution_20260120", "code_execution")]
    [InlineData("code_execution_20260521", "code_execution")]
    [InlineData("tool_search_tool_bm25_20251119", "tool_search_tool_bm25")]
    [InlineData("tool_search_tool_regex_20251119", "tool_search_tool_regex")]
    [InlineData("tool_search_tool_bm25", "tool_search_tool_bm25")]
    [InlineData("tool_search_tool_regex", "tool_search_tool_regex")]
    [InlineData("advisor_20260301", "advisor")]
    public void DocumentedServerIdentitiesRetainTheirProviderConfiguration(string type, string name)
    {
        var native = new JsonObject { ["type"] = type, ["name"] = name, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } };
        var options = new JsonObject { ["server_tools"] = new JsonArray(native.DeepClone()) };
        var request = Request with { ProviderOptions = Options(options.ToJsonString()) };
        JsonNode merged = Assert.Single(AnthropicTools.Resolve(Connection(), request).Build([]))!;
        Assert.True(JsonNode.DeepEquals(native, merged));
        Assert.True(JsonNode.DeepEquals(options, JsonNode.Parse(request.ProviderOptions[AnthropicTools.OptionName].GetRawText())));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedToolsUseOneCanonicalGenerationAndCountPayloadWithoutMutation(bool streaming)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection(streaming) with { ProviderOptions = Options(Mixed) };
        var request = Request with { Tools = [Tool, Tool with { Name = "strict_run", Strict = true }], AllowParallelToolCalls = true };
        await Complete(client, connection, request);
        LlmProviderInputTokenCount count = await client.CountInputTokensAsync(connection, request);
        Assert.Equal(37, count.InputTokens);
        var payload = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Null(payload[AnthropicTools.OptionName]);
        Assert.Null(payload["tools"]![0]!["strict"]);
        Assert.Equal("Typed description", (string?)payload["tools"]![0]!["description"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Schema), payload["tools"]![0]!["input_schema"]));
        Assert.Equal("code_execution_20260120", (string?)payload["tools"]![0]!["allowed_callers"]![0]);
        Assert.Equal("fixture", (string?)payload["tools"]![0]!["input_examples"]![0]!["value"]);
        Assert.Equal(true, (bool?)payload["tools"]![1]!["strict"]);
        Assert.Equal("code_execution", (string?)payload["tools"]![2]!["name"]);
        Assert.Equal(3, (int?)payload["tools"]![3]!["max_uses"]);
        Assert.Equal("example.invalid", (string?)payload["tools"]![3]!["allowed_domains"]![0]);
        Assert.Equal(false, (bool?)payload["tool_choice"]!["disable_parallel_tool_use"]);
        Assert.True(JsonNode.DeepEquals(payload["tools"], JsonNode.Parse(handler.Bodies[1])!["tools"]));
        Assert.Equal(Mixed, connection.ProviderOptions[AnthropicTools.OptionName].GetRawText());
        Assert.Empty(request.ProviderOptions);
        Assert.Equal(Schema, request.Tools[0].InputSchema.GetRawText());
    }

    [Theory]
    [InlineData(LlmToolChoiceMode.Auto)]
    [InlineData(LlmToolChoiceMode.None)]
    [InlineData(LlmToolChoiceMode.Required)]
    [InlineData(LlmToolChoiceMode.Specific)]
    public async Task NativeOnlyRequestsUseTypedChoiceAndParallelControls(LlmToolChoiceMode mode)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Complete(new AnthropicMessagesLlmClient(http), Connection(), Request with
        {
            ProviderOptions = Options("""{"server_tools":[{"type":"web_search_20250305","name":"web_search"}]}"""),
            ToolChoice = new() { Mode = mode, Name = mode == LlmToolChoiceMode.Specific ? "web_search" : null },
            AllowParallelToolCalls = false
        });
        var choice = JsonNode.Parse(Assert.Single(handler.Bodies))!["tool_choice"]!;
        Assert.Equal(mode switch { LlmToolChoiceMode.Required => "any", LlmToolChoiceMode.Specific => "tool", _ => mode.ToString().ToLowerInvariant() },
            (string?)choice["type"]);
        Assert.Equal(true, (bool?)choice["disable_parallel_tool_use"]);
        Assert.Equal(mode == LlmToolChoiceMode.Specific ? "web_search" : null, (string?)choice["name"]);
    }

    [Fact]
    public async Task RequestOptionsReplaceTheWholeConnectionObjectAndEmptyClearsIt()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection() with { ProviderOptions = Options(Mixed) };
        await Complete(client, connection, Request with { ProviderOptions = Options("{}") });
        Assert.Null(JsonNode.Parse(handler.Bodies[0])!["tools"]);
        const string replacement = """{"server_tools":[{"type":"web_fetch_20250910","name":"web_fetch"}]}""";
        await Complete(client, connection, Request with { ProviderOptions = Options(replacement) });
        Assert.Equal("web_fetch", (string?)JsonNode.Parse(handler.Bodies[1])!["tools"]![0]!["name"]);
        Assert.Single(JsonNode.Parse(handler.Bodies[1])!["tools"]!.AsArray());
        Assert.Equal(Mixed, connection.ProviderOptions[AnthropicTools.OptionName].GetRawText());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"unknown":true}""")]
    [InlineData("""{"server_tools":null}""")]
    [InlineData("""{"server_tools":[null]}""")]
    [InlineData("""{"server_tools":[{"type":"web_search_20250305","name":"run"}]}""")]
    [InlineData("""{"server_tools":[{"type":"web_search_20250305","name":"web_search","name":"web_search"}]}""")]
    [InlineData("""{"server_tools":[{"type":"web_search_20250305","name":"web_search","input_schema":{}}]}""")]
    [InlineData("""{"server_tools":[{"type":"web_search_20250305","name":"web_search","strict":false}]}""")]
    [InlineData("""{"server_tools":[{"type":"web_search_20250305","name":"web_search"},{"type":"web_search_20260209","name":"web_search"}]}""")]
    [InlineData("""{"server_tools":[{"type":"bash_20250124","name":"bash"}]}""")]
    [InlineData("""{"server_tools":[{"type":"custom","name":"raw_client","input_schema":{}}]}""")]
    [InlineData("""{"server_tools":[{"type":"web_search_20260209","name":"web_search","allowed_callers":null}]}""")]
    [InlineData("""{"server_tools":[{"type":"web_search_20260209","name":"web_search","allowed_callers":["unknown"]}]}""")]
    [InlineData("""{"server_tools":[{"type":"web_search_20260209","name":"web_search","allowed_callers":["direct","direct"]}]}""")]
    [InlineData("""{"client_tools":{"missing":{"allowed_callers":["direct"]}}}""")]
    [InlineData("""{"client_tools":{"run":{"input_schema":{}}}}""")]
    [InlineData("""{"client_tools":{"run":{"strict":true}}}""")]
    [InlineData("""{"client_tools":{"run":{"allowed_callers":[]}}}""")]
    [InlineData("""{"client_tools":{"run":{"allowed_callers":["direct","direct"]}}}""")]
    [InlineData("""{"client_tools":{"run":{"allowed_callers":["unknown"]}}}""")]
    [InlineData("""{"client_tools":{"run":{"allowed_callers":[1]}}}""")]
    [InlineData("""{"client_tools":{"run":{"allowed_callers":["code_execution_20260120"]}}}""")]
    [InlineData("""{"client_tools":{"run":{"defer_loading":true}}}""")]
    [InlineData("""{"client_tools":{"run":{"defer_loading":"true"}}}""")]
    [InlineData("""{"client_tools":{"run":{"input_examples":["bad"]}}}""")]
    [InlineData("""{"client_tools":{"run":{},"run":{}}}""")]
    [InlineData("""{"client_tools":{},"client_tools":{}}""")]
    public async Task MalformedOrForgedConfigurationFailsBeforeDispatch(string options)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        Exception? error = await Record.ExceptionAsync(() => Complete(new AnthropicMessagesLlmClient(http), Connection(),
            Request with { Tools = [Tool], ProviderOptions = Options(options) }));
        Assert.True(error is ArgumentException or NotSupportedException, error?.ToString());
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData("tools", true)]
    [InlineData("tools", false)]
    [InlineData("tool_choice", true)]
    [InlineData("tool_choice", false)]
    public async Task RawToolFieldsCannotBypassTypedControls(string name, bool connectionLevel)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var options = new Dictionary<string, JsonElement> { [name] = JsonElement.Parse("[]") };
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(new AnthropicMessagesLlmClient(http),
            connectionLevel ? Connection() with { ProviderOptions = options } : Connection(),
            connectionLevel ? Request : Request with { ProviderOptions = options }));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData(true, null, false)]
    [InlineData(false, false, false)]
    [InlineData(false, null, true)]
    public async Task ProgrammaticClientsRejectStrictParallelDisableAndForcedIndirectChoice(bool strict, bool? parallel, bool forced)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(new AnthropicMessagesLlmClient(http), Connection(), Request with
        {
            Tools = [Tool with { Strict = strict }],
            ProviderOptions = Options(Mixed),
            AllowParallelToolCalls = parallel,
            ToolChoice = forced ? new() { Mode = LlmToolChoiceMode.Specific, Name = "run" } : null
        }));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task NativeToolsCannotCollideWithClientsOrWeakenStrictBudgets()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var options = Options("""{"server_tools":[{"type":"web_search_20250305","name":"web_search"}]}""");
        await Assert.ThrowsAsync<ArgumentException>(() => Complete(client, Connection(), Request with
        { Tools = [Tool with { Name = "web_search" }], ProviderOptions = options }));
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection(), Request with
        { Tools = Enumerable.Range(0, 21).Select(index => Tool with { Name = "strict_" + index, Strict = true }).ToArray(), ProviderOptions = options }));
        await Assert.ThrowsAsync<ArgumentException>(() => Complete(client, Connection(), Request with
        { ProviderOptions = options, ToolChoice = new() { Mode = LlmToolChoiceMode.Specific, Name = "missing" } }));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task DeferredClientsAndDirectProgrammaticChoicesRetainTheirMetadata()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        const string options = """
            {"server_tools":[{"type":"code_execution_20260521","name":"code_execution"},
                             {"type":"tool_search_tool_bm25_20251119","name":"tool_search_tool_bm25"}],
             "client_tools":{"run":{"allowed_callers":["direct","code_execution_20260120"],"defer_loading":true}}}
            """;
        await Complete(new AnthropicMessagesLlmClient(http), Connection(), Request with
        { Tools = [Tool], ProviderOptions = Options(options), ToolChoice = new() { Mode = LlmToolChoiceMode.Specific, Name = "run" } });
        JsonNode payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Equal(true, (bool?)payload["tools"]![0]!["defer_loading"]);
        Assert.Equal(2, payload["tools"]![0]!["allowed_callers"]!.AsArray().Count);
        Assert.Equal("run", (string?)payload["tool_choice"]!["name"]);
    }

    private static async Task Complete(AnthropicMessagesLlmClient client, LlmConnection connection, LlmChatRequest request)
    {
        await foreach (var unused in client.StreamAsync(connection, request)) { }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            if (request.RequestUri!.AbsolutePath.EndsWith("count_tokens", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"input_tokens":37}""", Encoding.UTF8, "application/json") };
            bool streaming = JsonElement.Parse(body).GetProperty("stream").GetBoolean();
            string response = streaming
                ? "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n"
                : """{"id":"m","type":"message","content":[],"stop_reason":"end_turn"}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
