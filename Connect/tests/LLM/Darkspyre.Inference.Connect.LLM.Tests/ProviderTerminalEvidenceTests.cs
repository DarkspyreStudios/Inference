using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.LLM.Vertex;
using Darkspyre.Inference.Connect.Resources;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderTerminalEvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrokRequestsNoInlineCitationsThroughThePublishedIncludeList(bool streaming)
    {
        const string terminal = """
            {"id":"response-1","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}
            """;
        string body = streaming ? "data: {\"type\":\"response.completed\",\"response\":" + terminal + "}\n\n" : terminal;
        using var handler = new Handler(LlmTransport.XaiResponses, streaming, body);
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = streaming }
        };
        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, new()
        {
            ModelId = "grok-4.20-0309-non-reasoning",
            Messages = [new("user", "Search the selected domain.")],
            MaxOutputTokens = 64,
            MaxHostedTurns = 2,
            HostedTools = [new LlmWebSearchTool(AllowedDomains: ["docs.x.ai"])]
        })) { }

        using JsonDocument sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(64, sent.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(2, sent.RootElement.GetProperty("max_turns").GetInt32());
        string?[] includes = sent.RootElement.GetProperty("include").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("reasoning.encrypted_content", includes);
        Assert.Contains("web_search_call.action.sources", includes);
        Assert.Contains("no_inline_citations", includes);
        Assert.False(sent.RootElement.TryGetProperty("no_inline_citations", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrokNativeXSearchItemsProduceHostedObservationsAndRetainOpaqueReplay(bool streaming)
    {
        const string search = """{"type":"x_search_call","id":"x-call","status":"failed"}""";
        string terminal = """{"id":"response-1","status":"completed","output":[""" + search
            + """,{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}""";
        string body = streaming
            ? "data: {\"type\":\"response.output_item.added\",\"item\":" + search + "}\n\n"
                + "data: {\"type\":\"response.completed\",\"response\":" + terminal + "}\n\n"
            : terminal;
        using var handler = new Handler(LlmTransport.XaiResponses, streaming, body);
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = streaming }
        };
        var request = new LlmChatRequest
        {
            ModelId = "selected-model",
            Messages = [new("user", "Search X")],
            HostedTools = [new XaiXSearchTool()]
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);

        LlmHostedToolCall call = Assert.Single(events.OfType<LlmHostedToolCallCompletedEvent>()).Call;
        Assert.Equal("x-call", call.CallId);
        Assert.Equal(LlmHostedToolKind.SocialSearch, call.Kind);
        Assert.Equal("x_search", call.Name);
        Assert.Equal("failed", call.Status);
        Assert.Null(call.Input);
        Assert.Null(call.Output);
        Assert.Empty(events.OfType<LlmHostedToolCallProgressEvent>());
        Assert.Empty(events.OfType<LlmToolCallCompletedEvent>());
        Assert.Equal(streaming ? 1 : 0, events.OfType<LlmHostedToolCallStartedEvent>().Count());
        if (streaming)
        {
            LlmHostedToolCallStartedEvent started = Assert.Single(events.OfType<LlmHostedToolCallStartedEvent>());
            Assert.Equal((call.CallId, call.Kind, call.Name), (started.CallId, started.Kind, started.Name));
        }
        LlmChatResult result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("answer", result.Text);

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request with
        {
            Messages = [new("user", "Search X"), new("assistant", result.Text) { ContinuationState = result.ContinuationState },
                new("user", "Continue")]
        })) { }
        using JsonDocument sent = JsonDocument.Parse(handler.RequestBody!);
        JsonElement replay = Assert.Single(sent.RootElement.GetProperty("input").EnumerateArray(),
            item => item.TryGetProperty("type", out JsonElement type) && type.GetString() == "x_search_call");
        Assert.Equal(search, replay.GetRawText());
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("grok-4.20-0309-non-reasoning")]
    [InlineData("grok-4.20-non-reasoning")]
    public async Task GrokNonreasoningCatalogIdentityRetainsItsSamplingProfile(string modelId)
    {
        const string body = """
            {"id":"response-1","model":"grok-4.20-0309-non-reasoning","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}
            """;
        using var handler = new Handler(LlmTransport.XaiResponses, streaming: false, body);
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = false },
            Catalog = [new("grok-4.20-0309-non-reasoning", "Grok 4.20 (Non-Reasoning)")
            {
                CatalogEntry = JsonElement.Parse("""{"aliases":["grok-4.20-non-reasoning"],"capabilities":{"reasoning_effort":[]}}""")
            }]
        };

        LlmProviderCapabilities capabilities = client.GetCapabilities(connection, modelId);
        Assert.True(capabilities.SupportsTemperature);
        Assert.True(capabilities.SupportsTopP);
        Assert.Empty(capabilities.ReasoningLevels);

        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, new()
        {
            ModelId = modelId,
            Messages = [new("user", "hello")],
            Temperature = 0.25,
            TopP = 0.9
        })) events.Add(item);

        Assert.Equal(LlmCompletionStatus.Completed, Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Status);
        using JsonDocument sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(modelId, sent.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.25, sent.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, sent.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false, 20)]
    [InlineData(true, 20)]
    [InlineData(false, 21)]
    [InlineData(true, 21)]
    public async Task GrokXSearchUsesThePublishedHandleLimitWithoutTruncatingFilters(bool excluded, int count)
    {
        const string body = """
            {"id":"response-1","model":"grok-4.7","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}
            """;
        using var handler = new Handler(LlmTransport.XaiResponses, streaming: false, body);
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = false }
        };
        string[] handles = Enumerable.Range(0, count).Select(index => $"handle-{index}").ToArray();
        var request = new LlmChatRequest
        {
            ModelId = "grok-4.7",
            Messages = [new("user", "Search X")],
            HostedTools = [excluded ? new XaiXSearchTool(ExcludedHandles: handles) : new XaiXSearchTool(AllowedHandles: handles)]
        };
        if (count > 20)
        {
            Assert.Throws<ArgumentException>(() => client.PreflightResourceRequest(connection, request));
            await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
            });
            Assert.Equal(0, handler.Calls);
            return;
        }

        client.PreflightResourceRequest(connection, request);
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);
        Assert.Equal(LlmCompletionStatus.Completed, Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Status);
        Assert.Equal(1, handler.Calls);
        using JsonDocument sent = JsonDocument.Parse(handler.RequestBody!);
        JsonElement tool = Assert.Single(sent.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("x_search", tool.GetProperty("type").GetString());
        Assert.Equal(handles, tool.GetProperty(excluded ? "excluded_x_handles" : "allowed_x_handles")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.False(tool.TryGetProperty(excluded ? "allowed_x_handles" : "excluded_x_handles", out _));
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"enum\":[]}}}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"anyOf\":[]}}}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":true}}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":false}}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"array\",\"minContains\":1}}}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"array\",\"maxContains\":1}}}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":17}}}", true)]
    [InlineData("{\"type\":\"object\",\"required\":[\"value\",\"value\"]}", true)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"enum\":17}}}", true)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"minimum\":\"one\"}}}", true)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"minLength\":1e-100}}}", true)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"maxItems\":1.0000000000000000000000000001}}}", true)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"format\":17}}}", true)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"multipleOf\":0}}}", true)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"uniqueItems\":17}}}", true)]
    [InlineData("{\"type\":\"object\",\"$vocabulary\":{\"https://schemas.example.test/custom\":17}}", true)]
    [InlineData("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"dependencies\":17}", true)]
    [InlineData("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"dependencies\":{\"value\":{\"type\":17}}}", true)]
    [InlineData("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"additionalItems\":17}", false)]
    [InlineData("{\"$schema\":\"https://schemas.example.test/custom-dialect\",\"type\":\"object\",\"properties\":17}", false)]
    [InlineData("""{"type":"object","properties":{"value":{"type":"string","pattern":"\\p{L}"}}}""", false)]
    [InlineData("""{"type":"object","properties":{"value":{"type":"string","pattern":"(?i:a)"}}}""", false)]
    [InlineData("""{"type":"object","properties":{"value":{"type":"string","pattern":"(?:x"}}}""", true)]
    [InlineData("""{"type":"object","properties":{"value":{"type":"string","pattern":"x)"}}}""", true)]
    public async Task GrokSchemaAdmissionDistinguishesMalformedValuesFromNativeRejections(string schema, bool malformed)
    {
        const string body = """
            {"id":"response-1","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"{\"value\":\"x\"}"}]}]}
            """;
        var handler = new Handler(LlmTransport.XaiResponses, streaming: false, body);
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = false, SupportsJsonSchema = true }
        };
        var output = new LlmStructuredOutputRequest { Name = "answer", Schema = JsonElement.Parse(schema) };
        var tool = new LlmToolDefinition { Name = "echo", Strict = true, InputSchema = output.Schema };
        var request = new LlmChatRequest { ModelId = "selected-model", Messages = [new("user", "hello")] };
        await Assert.ThrowsAsync(malformed ? typeof(ArgumentException) : typeof(NotSupportedException), async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request with { StructuredOutput = output })) { }
        });
        await Assert.ThrowsAsync(malformed ? typeof(ArgumentException) : typeof(NotSupportedException), async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request with { Tools = [tool] })) { }
        });
        if (malformed)
            await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request with
                {
                    StructuredOutput = output with { Fallback = LlmStructuredOutputFallback.LocalValidation }
                })) { }
            });
        Assert.Equal(0, handler.Calls);
        bool localBoolean = !malformed && output.Schema.TryGetProperty("properties", out JsonElement properties)
            && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("value", out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        if (localBoolean)
        {
            var events = new List<LlmStreamEvent>();
            await foreach (LlmStreamEvent item in client.StreamAsync(connection, request with
            {
                StructuredOutput = output with { Fallback = LlmStructuredOutputFallback.LocalValidation }
            })) events.Add(item);
            LlmStructuredOutputValidation validation = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.StructuredOutput!;
            Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly, validation.Enforcement);
            Assert.Equal(output.Schema.GetProperty("properties").GetProperty("value").GetBoolean(), validation.IsValid);
            using var local = JsonDocument.Parse(handler.RequestBody!);
            Assert.False(local.RootElement.TryGetProperty("text", out _));
        }
        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request with { Tools = [tool with { Strict = false }] })) { }
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        JsonElement sentTool = Assert.Single(sent.RootElement.GetProperty("tools").EnumerateArray());
        Assert.False(sentTool.GetProperty("strict").GetBoolean());
        Assert.Equal(schema, sentTool.GetProperty("parameters").GetRawText());
        Assert.Equal(localBoolean ? 2 : 1, handler.Calls);
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"additionalProperties\":false}", true)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":[\"string\",\"null\"],\"enum\":[\"x\",null],\"const\":\"x\"}},\"default\":{\"type\":17,\"anyOf\":[]},\"examples\":[{\"minContains\":\"one\"}]}", true)]
    [InlineData("{\"type\":\"object\",\"title\":\"Answer\",\"description\":\"A value\",\"$comment\":\"Annotation\",\"readOnly\":true,\"writeOnly\":false,\"deprecated\":false,\"properties\":{\"value\":{\"type\":\"string\",\"format\":\"email\"}}}", false)]
    [InlineData("""{"type":"object","properties":{"value":{"type":"string","pattern":"^x[.]?$"}}}""", true)]
    [InlineData("""{"type":"object","properties":{"value":{"type":"string","pattern":"^x\\.?$"}}}""", true)]
    [InlineData("""{"type":"object","properties":{"value":{"type":"string","pattern":"^[(?=]?x$"}}}""", true)]
    [InlineData("""{"type":"object","properties":{"value":{"type":"string","pattern":"^(?:x)$"}}}""", true)]
    [InlineData("""{"type":"object","properties":{"value":{"type":"string","pattern":"^[()]?x\\)?$"}}}""", true)]
    public async Task GrokSchemaAdmissionPreservesBooleanClosureAndLiteralPayloads(string schema, bool validOutput)
    {
        await CheckReferenceAdmission(JsonElement.Parse(schema), guaranteed: true, validOutput);
    }

    [Fact]
    public void OriginalSchemaValidationKeepsRepeatedIdsRequestLocal()
    {
        var first = new LlmStructuredOutputRequest
        {
            Name = "answer",
            Schema = JsonElement.Parse("""
                {"$id":"https://schemas.example.test/repeated-validation","type":"string","maxLength":1}
                """)
        };
        var second = first with
        {
            Schema = JsonElement.Parse("""
                {"$id":"https://schemas.example.test/repeated-validation","type":"string","maxLength":2}
                """)
        };
        const LlmStructuredOutputEnforcement mode = LlmStructuredOutputEnforcement.LocalValidationOnly;
        Assert.False(StructuredOutput.Validate("\"xx\"", first, mode).IsValid);
        Assert.True(StructuredOutput.Validate("\"xx\"", second, mode).IsValid);
        Assert.False(StructuredOutput.Validate("\"xx\"", first, mode).IsValid);
    }

    [Theory]
    [InlineData("{\"type\":\"string\"}", false)]
    [InlineData("{\"properties\":{\"value\":{\"type\":\"string\"}}}", false)]
    [InlineData("{\"type\":[\"object\",\"null\"]}", false)]
    [InlineData("{\"anyOf\":[{\"type\":\"object\"},{\"type\":\"string\"}]}", false)]
    [InlineData("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"$ref\":\"#/definitions/input\",\"definitions\":{\"input\":{\"type\":\"string\"}}}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}}}", true)]
    [InlineData("{\"$ref\":\"#/$defs/input\",\"$defs\":{\"input\":{\"type\":\"object\"}}}", true)]
    [InlineData("{\"anyOf\":[{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}},{\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"integer\"}}}]}", true)]
    public async Task GrokStrictToolsRequireProvenObjectRootsWithoutChangingNonStrictTools(string schema, bool guaranteed)
    {
        const string body = """
            {"id":"response-1","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hello"}]}]}
            """;
        var handler = new Handler(LlmTransport.XaiResponses, streaming: false, body);
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = false }
        };
        var tool = new LlmToolDefinition { Name = "echo", Strict = true, InputSchema = JsonElement.Parse(schema) };
        var request = new LlmChatRequest { ModelId = "selected-model", Messages = [new("user", "hello")], Tools = [tool] };
        if (!guaranteed)
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
            });
            Assert.Equal(0, handler.Calls);
        }
        else
        {
            var events = new List<LlmStreamEvent>();
            await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);
            Assert.Equal("hello", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
            using var sent = JsonDocument.Parse(handler.RequestBody!);
            JsonElement sentTool = Assert.Single(sent.RootElement.GetProperty("tools").EnumerateArray());
            Assert.True(sentTool.GetProperty("strict").GetBoolean());
            Assert.Equal(schema, sentTool.GetProperty("parameters").GetRawText());
        }
        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request with { Tools = [tool with { Strict = false }] })) { }
        using var nonStrict = JsonDocument.Parse(handler.RequestBody!);
        JsonElement unchanged = Assert.Single(nonStrict.RootElement.GetProperty("tools").EnumerateArray());
        Assert.False(unchanged.GetProperty("strict").GetBoolean());
        Assert.Equal(schema, unchanged.GetProperty("parameters").GetRawText());
        Assert.Equal(guaranteed ? 2 : 1, handler.Calls);
    }

    [Theory]
    [InlineData(4096, 1, false)]
    [InlineData(1, 4096, true)]
    public async Task GrokReferenceGuaranteeUsesTheEmbeddedResourceNotTheOuterPointer(int innerBound, int outerBound, bool guaranteed)
    {
        JsonNode schema = JsonNode.Parse("""
            {"$id":"https://schemas.example.test/root","type":"object","$defs":{"item":{"type":"string"},"nested":{"$id":"nested","$defs":{"item":{"type":"string"}},"$ref":"#/$defs/item"}},"properties":{"value":{"$ref":"#/$defs/nested"}}}
            """)!;
        schema["$defs"]!["item"]!["maxLength"] = outerBound;
        schema["$defs"]!["nested"]!["$defs"]!["item"]!["maxLength"] = innerBound;
        await CheckReferenceAdmission(JsonElement.Parse(schema.ToJsonString()), guaranteed);
    }

    [Fact]
    public async Task GrokReferenceGuaranteeResolvesDeclaredAnchorsAndSharesTheSameTarget()
    {
        JsonElement schema = JsonElement.Parse("""
            {"$id":"https://schemas.example.test/root","type":"object","$defs":{"item":{"$anchor":"content","type":"string","maxLength":1}},"properties":{"value":{"$ref":"#content"},"other":{"$ref":"https://schemas.example.test/root#/$defs/item"}}}
            """);
        await CheckReferenceAdmission(schema, guaranteed: true);
    }

    [Theory]
    [InlineData("http://json-schema.org/draft-07/schema#", true)]
    [InlineData("https://json-schema.org/draft/2020-12/schema", false)]
    public async Task GrokReferenceGuaranteeUsesTheDeclaredDraftSiblingRules(string dialect, bool guaranteed)
    {
        JsonNode schema = JsonNode.Parse("""
            {"$ref":"#/definitions/object","maxProperties":4096,"definitions":{"object":{"type":"object","properties":{"value":{"type":"string"}}}}}
            """)!;
        schema["$schema"] = dialect;
        await CheckReferenceAdmission(JsonElement.Parse(schema.ToJsonString()), guaranteed);
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"default\":{\"type\":\"string\",\"maxLength\":1},\"properties\":{\"value\":{\"$ref\":\"#/default\"}}}")]
    [InlineData("{\"type\":\"object\",\"$defs\":{\"node\":{\"$id\":\"nested\",\"$ref\":\"#\"}},\"properties\":{\"value\":{\"$ref\":\"#/$defs/node\"}}}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"$ref\":\"https://schemas.other.test/external\"}}}")]
    [InlineData("{\"type\":\"object\",\"$defs\":{\"item\":{\"type\":\"string\",\"maxLength\":1}},\"properties\":{\"value\":{\"$ref\":\"https://json-schema.invalid/root#/$defs/item\"}}}")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":true,\"properties\":{\"value\":{\"$ref\":\"#/additionalProperties\"}}}")]
    public async Task GrokReferenceGuaranteeRefusesLiteralTargetsCyclesAndUnresolvedResources(string schema)
    {
        await CheckReferenceAdmission(JsonElement.Parse(schema), guaranteed: false);
    }

    private static async Task CheckReferenceAdmission(JsonElement schema, bool guaranteed, bool validOutput = true)
    {
        const string body = """
            {"id":"response-1","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"{\"value\":\"x\"}"}]}]}
            """;
        var handler = new Handler(LlmTransport.XaiResponses, streaming: false, body);
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = false, SupportsJsonSchema = true }
        };
        var request = new LlmChatRequest
        {
            ModelId = "selected-model",
            Messages = [new("user", "hello")],
            StructuredOutput = new() { Name = "answer", Schema = schema }
        };
        if (!guaranteed)
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
            });
            Assert.Equal(0, handler.Calls);
            return;
        }
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);
        LlmStructuredOutputValidation validation = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.StructuredOutput!;
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, validation.Enforcement);
        Assert.Equal(validOutput, validation.IsValid);
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(schema.GetRawText(), sent.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema").GetRawText());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{\"oneOf\":[{\"type\":\"number\"},{\"type\":\"integer\"}]}", false, "1")]
    [InlineData("{\"allOf\":[{\"minimum\":2},{\"maximum\":4}]}", false, "1")]
    [InlineData("{\"not\":{\"const\":1}}", false, "1")]
    [InlineData("{\"if\":{\"const\":1},\"then\":{\"const\":2}}", false, "1")]
    [InlineData("{\"format\":\"custom-format\"}", true, "1")]
    [InlineData("{\"multipleOf\":2}", false, "1")]
    [InlineData("{\"uniqueItems\":true}", true, "1")]
    [InlineData("{\"customAssertion\":false}", true, "1")]
    [InlineData("{\"$vocabulary\":{\"https://schemas.example.test/custom\":false}}", true, "1")]
    [InlineData("{\"prefixItems\":17}", true, "1")]
    [InlineData("{\"dependentSchemas\":17}", true, "1")]
    [InlineData("{\"unevaluatedProperties\":17}", true, "1")]
    [InlineData("{\"$anchor\":17}", true, "1")]
    [InlineData("{\"dependencies\":{\"requiredForValue\":[\"missing\"],\"value\":{\"multipleOf\":2}}}", true, "1")]
    [InlineData("""{"type":"string","pattern":"^.$"}""", false, "\"\\n\"")]
    [InlineData("""{"type":"string","pattern":"^(a)\\1$"}""", true, "\"aa\"")]
    [InlineData("""{"type":"string","pattern":"(?=a)a"}""", true, "\"a\"")]
    [InlineData("""{"type":"string","pattern":"\\ba\\b"}""", true, "\"a\"")]
    public async Task GrokUnprovenGuaranteesRequireExplicitOriginalSchemaValidation(string constraint, bool validOutput, string outputValue)
    {
        JsonElement schema = JsonElement.Parse("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"type\":\"object\",\"properties\":{\"value\":" + constraint + "}}");
        JsonNode body = JsonNode.Parse("""
            {"id":"response-1","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"{\"value\":1}"}]}]}
            """)!;
        body["output"]![0]!["content"]![0]!["text"] = new JsonObject { ["value"] = JsonNode.Parse(outputValue) }.ToJsonString();
        var handler = new Handler(LlmTransport.XaiResponses, streaming: false, body.ToJsonString());
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = false, SupportsJsonSchema = true }
        };
        var request = new LlmChatRequest { ModelId = "selected-model", Messages = [new("user", "hello")] };
        var tool = new LlmToolDefinition { Name = "echo", Strict = true, InputSchema = schema };

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request with { Tools = [tool] })) { }
        });
        var output = new LlmStructuredOutputRequest { Name = "answer", Schema = schema };
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request with { StructuredOutput = output })) { }
        });
        Assert.Equal(0, handler.Calls);

        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request with
        {
            StructuredOutput = output with { Fallback = LlmStructuredOutputFallback.LocalValidation }
        })) events.Add(item);
        LlmStructuredOutputValidation validation = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.StructuredOutput!;
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly, validation.Enforcement);
        Assert.Equal(validOutput, validation.IsValid);
        Assert.Equal(schema.GetRawText(), output.Schema.GetRawText());
        using (var sent = JsonDocument.Parse(handler.RequestBody!)) Assert.False(sent.RootElement.TryGetProperty("text", out _));
        Assert.Equal(1, handler.Calls);

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request with { Tools = [tool with { Strict = false }] })) { }
        using var nonStrict = JsonDocument.Parse(handler.RequestBody!);
        JsonElement sentTool = Assert.Single(nonStrict.RootElement.GetProperty("tools").EnumerateArray());
        Assert.False(sentTool.GetProperty("strict").GetBoolean());
        Assert.Equal(schema.GetRawText(), sentTool.GetProperty("parameters").GetRawText());
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrokSimpleTypeUnionRequiresUnreferencedDisjointBranches(bool referenced)
    {
        JsonElement schema = JsonElement.Parse(referenced ? """
            {"$schema":"http://json-schema.org/draft-07/schema#","type":"object","definitions":{"value":{}},"properties":{"value":{"oneOf":[{"$ref":"#/definitions/value","type":"string"},{"$ref":"#/definitions/value","type":"integer"}]}}}
            """ : """
            {"type":"object","properties":{"value":{"oneOf":[{"type":["string","null"]},{"type":"integer"}]}}}
            """);
        const string body = """
            {"id":"response-1","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"{\"value\":1}"}]}]}
            """;
        var handler = new Handler(LlmTransport.XaiResponses, streaming: false, body);
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = false, SupportsJsonSchema = true }
        };
        var request = new LlmChatRequest
        {
            ModelId = "selected-model",
            Messages = [new("user", "hello")],
            StructuredOutput = new() { Name = "answer", Schema = schema }
        };
        if (referenced)
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
            });
            Assert.Equal(0, handler.Calls);
            return;
        }
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);
        LlmStructuredOutputValidation validation = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.StructuredOutput!;
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, validation.Enforcement);
        Assert.True(validation.IsValid);
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(schema.GetRawText(), sent.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema").GetRawText());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("access_denied")]
    [InlineData("expired_token")]
    public async Task GrokDeviceAuthorizationTerminationKeepsSafeRequestAndRetryFacts(string code)
    {
        var retryAfter = TimeSpan.FromSeconds(17);
        var handler = new Handler(LlmTransport.XaiResponses, streaming: false,
            "{\"error\":\"" + code + "\",\"error_description\":\"Ended device-secret user-secret\",\"refresh_token\":\"provider-secret\"}",
            HttpStatusCode.BadRequest, retryAfter, "request-device-secret-1");
        using var http = new HttpClient(handler);
        var authenticator = new XaiAuthenticator(http);

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            authenticator.CompleteAsync("device-secret", "user-secret"));

        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.Equal(code, failure.ProviderCode);
        Assert.Equal("request-[REDACTED]-1", failure.RequestId);
        Assert.Equal(retryAfter, failure.RetryAfter);
        Assert.Contains("start sign-in again", failure.Message, StringComparison.Ordinal);
        Assert.Contains(code, failure.SanitizedResponseBody!, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", failure.SanitizedResponseBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("device-secret", failure.ToString() + failure.SanitizedResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("user-secret", failure.ToString() + failure.SanitizedResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret", failure.ToString() + failure.SanitizedResponseBody, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", handler.RequestBody!, StringComparison.Ordinal);
        Assert.Contains("device_code=device-secret", handler.RequestBody!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "reasoning.text", "text")]
    [InlineData(false, "reasoning.summary", "summary")]
    [InlineData(true, "reasoning.text", "text")]
    [InlineData(true, "reasoning.summary", "summary")]
    public async Task CompatibleReasoningReplayPreservesCompleteBlocksAndAssemblesStreamingChunks(
        bool streaming, string type, string field)
    {
        var first = new JsonObject
        {
            ["type"] = type,
            [field] = "first",
            ["id"] = "block-0",
            ["format"] = "anthropic-claude-v1",
            ["index"] = 0
        };
        var second = new JsonObject
        {
            ["type"] = type,
            [field] = "second",
            ["id"] = streaming ? "block-0" : "block-1",
            ["format"] = "anthropic-claude-v1",
            ["index"] = streaming ? 0 : 1
        };
        if (field == "text")
        {
            first["signature"] = "signature-0";
            second["signature"] = streaming ? "signature-0" : "signature-1";
        }
        string body = streaming
            ? "data: {\"choices\":[{\"delta\":{\"reasoning_details\":[" + first.ToJsonString() + "]}}]}\n\n"
                + "data: {\"choices\":[{\"delta\":{\"reasoning_details\":[" + second.ToJsonString() + "]},\"finish_reason\":\"stop\"}]}\n\n"
                + "data: [DONE]\n\n"
            : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning_details\":["
                + first.ToJsonString() + "," + second.ToJsonString() + "]},\"finish_reason\":\"stop\"}]}";
        var handler = new Handler(LlmTransport.OpenAiCompatibleChat, streaming, body);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.OpenAiCompatibleChat,
            Endpoint = "https://openrouter.ai/api/v1",
            Credential = new LlmCredential { ApiKey = "test-key" }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = streaming }
        };
        var request = new LlmChatRequest
        {
            ModelId = "selected-model",
            Messages = [new("user", "hello")],
            Tools = [new LlmToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }]
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        LlmChatResult restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(result, ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;
        Assert.Equal(LlmCompletionStatus.Completed, restored.Status);
        Assert.NotNull(restored.ContinuationState);
        Assert.Equal("firstsecond", string.Concat(events.OfType<LlmReasoningDeltaEvent>().Select(item => item.Delta)));
        Assert.All(events.OfType<LlmReasoningDeltaEvent>(), item => Assert.Equal(field == "summary", item.IsSummary));

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request with
        {
            Messages = [new("user", "hello"),
                new("assistant", restored.Text) { ContinuationState = restored.ContinuationState },
                new("user", "continue")]
        })) { }

        var expected = new JsonArray();
        if (streaming) first[field] = "firstsecond";
        expected.Add(item: first);
        if (!streaming) expected.Add(item: second);
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        JsonElement replay = sent.RootElement.GetProperty("messages")[1].GetProperty("reasoning_details");
        Assert.Equal(expected.ToJsonString(), replay.GetRawText());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GrokFlatModelAccessFailureKeepsItsCodeSeparateFromTheRedactedMessage()
    {
        const string body = """
            {"code":"not-found","error":"The model requested-model does not exist or your team test-token does not have access to it."}
            """;
        using var handler = new Handler(LlmTransport.XaiResponses, streaming: true, body,
            HttpStatusCode.NotFound, requestId: "request-1");
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://us.api.x.ai/v1",
            Xai = new() { Region = XaiRegion.UnitedStates },
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize()
        };

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, new()
            {
                ModelId = "requested-model",
                Messages = [new("user", "hello")]
            })) { }
        });

        Assert.Equal("not-found", failure.ProviderCode);
        Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
        Assert.Equal("request-1", failure.RequestId);
        Assert.Contains("does not have access", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GrokBatchFailureKeepsSafeRequestIdInTheProviderErrorAndOperationReceipt()
    {
        var retryAfter = TimeSpan.FromSeconds(17);
        var handler = new Handler(LlmTransport.XaiResponses, streaming: false,
            "{\"error\":{\"code\":\"rate_limit_test-token_provider-secret\",\"message\":\"Rate limit test-token\",\"refresh_token\":\"provider-secret\"}}",
            HttpStatusCode.TooManyRequests, retryAfter, "request-test-token-1");
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var source = new ResourcePreparationTests.Source(new()
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize()
        });
        await using var access = new LlmConnectionAccess(source, [], [client]);
        await using var core = new LlmProviderService(source, [client], [], connectionAccess: access);
        await using var batches = new LlmResourceProviderService(access, core);

        LlmResourceOperationException operation = await Assert.ThrowsAsync<LlmResourceOperationException>(() =>
            batches.CreateAsync("connection", "batch"));
        LlmProviderException failure = Assert.IsType<LlmProviderException>(operation.InnerException);

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal("rate_limit_[REDACTED]_[REDACTED]", failure.ProviderCode);
        Assert.Equal("request-[REDACTED]-1", failure.RequestId);
        Assert.Equal(retryAfter, failure.RetryAfter);
        Assert.Equal(failure.RequestId, operation.Observation.RequestId);
        Assert.Equal(429, operation.Observation.HttpStatusCode);
        Assert.True(operation.Observation.BatchMutationDispatched);
        Assert.True(operation.Observation.ResponseObserved);
        Assert.Null(operation.Observation.ObservedBatch);
        Assert.Null(failure.SanitizedResponseBody);
        Assert.DoesNotContain("test-token", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GrokFileFailureKeepsSafeRequestAndRetryFactsWithoutRetryingOrDisposingBorrowedContent()
    {
        var retryAfter = TimeSpan.FromSeconds(17);
        var handler = new Handler(LlmTransport.XaiResponses, streaming: false,
            "{\"error\":{\"code\":\"rate_limit_test-token_provider-secret\",\"message\":\"Rate limit test-token\",\"refresh_token\":\"provider-secret\"}}",
            HttpStatusCode.TooManyRequests, retryAfter, "request-test-token-1");
        using var http = new HttpClient(handler);
        await using var files = new XaiFileClient(http);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("reference text"), writable: false);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize()
        };

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            files.UploadAsync(connection, source, "reference.txt", null, () => { }, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal("rate_limit_[REDACTED]_[REDACTED]", failure.ProviderCode);
        Assert.Equal("request-[REDACTED]-1", failure.RequestId);
        Assert.Equal(retryAfter, failure.RetryAfter);
        Assert.DoesNotContain("test-token", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData("download")]
    [InlineData("configured-download")]
    [InlineData("configured-metadata")]
    public async Task GrokFileReadFailuresRedactTheEffectiveRequestHeaders(string route)
    {
        var retryAfter = TimeSpan.FromSeconds(17);
        var handler = new Handler(LlmTransport.XaiResponses, streaming: false,
            "{\"error\":{\"code\":\"rate_limit_test-token_header-value_provider-secret\",\"refresh_token\":\"provider-secret\"}}",
            HttpStatusCode.TooManyRequests, retryAfter, "request-test-token-header-value-1", HttpMethod.Get);
        using var http = new HttpClient(handler);
        await using var files = new XaiFileClient(http);
        using var destination = new MemoryStream();
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            AdditionalHeaders = route == "download" ? new Dictionary<string, string> { ["X-Trace"] = "header-value" } : []
        };
        void Configure(HttpRequestMessage request) => request.Headers.Add("X-Trace", "header-value");

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            if (route == "download")
                await files.DownloadAsync(connection, "file-1", destination, null, () => { }, CancellationToken.None);
            else if (route == "configured-download")
                await files.OpenDownloadAsync(connection, "file-1", Timeout.InfiniteTimeSpan, () => { }, CancellationToken.None, Configure);
            else
                await files.GetAsync(connection, "file-1", () => { }, CancellationToken.None, configureHeaders: Configure);
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal("rate_limit_[REDACTED]_[REDACTED]_[REDACTED]", failure.ProviderCode);
        Assert.Equal("request-[REDACTED]-[REDACTED]-1", failure.RequestId);
        Assert.Equal(retryAfter, failure.RetryAfter);
        Assert.DoesNotContain("test-token", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("header-value", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal("header-value", handler.RequestTrace);
        Assert.Equal(1, handler.Calls);
        Assert.True(destination.CanWrite);
        Assert.Equal(0, destination.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrokSearchAndEmbeddingFailuresKeepSafeProviderCodes(bool embedding)
    {
        var retryAfter = TimeSpan.FromSeconds(17);
        var handler = new Handler(LlmTransport.XaiResponses, streaming: false,
            "{\"error\":{\"code\":\"rate_limit_test-token_header-value_provider-secret\",\"refresh_token\":\"provider-secret\"}}",
            HttpStatusCode.TooManyRequests, retryAfter, "request-test-token-header-value-1");
        using var http = new HttpClient(handler);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
            AdditionalHeaders = new Dictionary<string, string> { ["X-Trace"] = "header-value" },
            Catalog = []
        };

        LlmProviderException failure = embedding
            ? await Assert.ThrowsAsync<LlmProviderException>(() => new XaiEmbeddingClient(http).EmbedAsync(connection,
                new() { ModelId = "requested-model", Inputs = ["reference"] }))
            : await Assert.ThrowsAsync<LlmProviderException>(() => XaiDocumentSearch.SearchAsync(http, connection,
                new("reference", ["collection-1"]), () => { }, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal("rate_limit_[REDACTED]_[REDACTED]_[REDACTED]", failure.ProviderCode);
        Assert.Equal("request-[REDACTED]-[REDACTED]-1", failure.RequestId);
        Assert.Equal(retryAfter, failure.RetryAfter);
        Assert.Null(failure.SanitizedResponseBody);
        Assert.DoesNotContain("test-token", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("header-value", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(LlmTransport.GoogleGeminiApi, true, HttpStatusCode.OK)]
    [InlineData(LlmTransport.GoogleGeminiApi, false, HttpStatusCode.OK)]
    [InlineData(LlmTransport.GoogleVertexAi, true, HttpStatusCode.OK)]
    [InlineData(LlmTransport.GoogleVertexAi, false, HttpStatusCode.OK)]
    [InlineData(LlmTransport.GoogleVertexAi, true, HttpStatusCode.TooManyRequests)]
    [InlineData(LlmTransport.GoogleVertexAi, false, HttpStatusCode.TooManyRequests)]
    public async Task GoogleGenerationErrorsKeepObservedStatusAndRetryWithoutExposingProviderBodies(
        LlmTransport transport, bool streaming, HttpStatusCode statusCode)
    {
        const string overflow = """
            {"error":{"code":400,"status":"INVALID_ARGUMENT","message":"Input token count exceeds the maximum number of tokens. test-key reflected-value","refresh_token":"unowned-secret"}}
            """;
        const string rateLimit = """
            {"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"The quota was reached. test-key reflected-value","refresh_token":"unowned-secret"}}
            """;
        string error = statusCode == HttpStatusCode.OK ? overflow : rateLimit;
        string body = streaming && statusCode == HttpStatusCode.OK
            ? "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"partial\"}]}}]}\n\n"
                + "data: " + error + "\n\n"
            : error;
        var retryAfter = TimeSpan.FromSeconds(2);
        var handler = new Handler(transport, streaming, body, statusCode, retryAfter);
        using var http = new HttpClient(handler);
        await using var authentication = new VertexAuthentication();
        ILlmClient client = transport == LlmTransport.GoogleGeminiApi
            ? new GeminiApiLlmClient(http) : new VertexLlmClient(http, authentication);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = transport,
            Endpoint = transport == LlmTransport.GoogleGeminiApi ? GeminiApiLlmClient.DefaultEndpoint : VertexEndpoints.BaseEndpoint,
            Vertex = transport == LlmTransport.GoogleVertexAi ? new() { AuthenticationMode = LlmGoogleAuthenticationMode.ApiKey } : null,
            Credential = new LlmCredential { ApiKey = "test-key" }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = streaming }
        };
        var events = new List<LlmStreamEvent>();
        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            await foreach (LlmStreamEvent item in client.StreamAsync(connection, new()
            {
                ModelId = transport == LlmTransport.GoogleGeminiApi ? "gemini-3-flash-preview" : "google/gemini-3-flash-preview",
                Messages = [new("user", "hello")]
            })) events.Add(item);
        });

        Assert.Equal(statusCode, failure.StatusCode);
        Assert.Equal(retryAfter, failure.RetryAfter);
        Assert.Equal(transport, failure.Transport);
        Assert.Equal(statusCode == HttpStatusCode.OK, failure.IsContextOverflow);
        Assert.Null(failure.ProviderCode);
        Assert.Null(failure.RequestId);
        Assert.Null(failure.SanitizedResponseBody);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("test-key", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("reflected-value", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unowned-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(events, item => item is LlmChatCompletionEvent);
        Assert.Equal(streaming && statusCode == HttpStatusCode.OK ? 1 : 0, events.Count);
        if (events.Count == 1) Assert.Equal("partial", Assert.IsType<LlmTextDeltaEvent>(events[0]).Delta);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(LlmTransport.GoogleGeminiApi)]
    [InlineData(LlmTransport.GoogleVertexAi)]
    public async Task GeminiTerminalUsageReplacesEarlierCountsWithoutCarryingStaleSubsets(LlmTransport transport)
    {
        const string body = """
            data: {"candidates":[{"content":{"parts":[{"text":"hel"}]}}],"usageMetadata":{"promptTokenCount":5,"toolUsePromptTokenCount":3,"candidatesTokenCount":2,"thoughtsTokenCount":4,"cachedContentTokenCount":1,"totalTokenCount":14}}

            data: {"candidates":[{"content":{"parts":[{"text":"lo"}]}}]}

            data: {"candidates":[{"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":6,"candidatesTokenCount":3,"totalTokenCount":9}}
            """;
        using var http = new HttpClient(new Handler(transport, streaming: true, body + "\n\n"));
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = transport,
            Endpoint = "https://provider.example.test/v1",
            ResolvedContinuationScope = "test-scope"
        };
        var request = new LlmChatRequest { ModelId = "gemini-2.5-flash", Messages = [new("user", "hello")] };
        var codec = new GeminiContinuationCodec("test-gemini", "Gemini");
        var route = new GeminiRoute(transport, "Gemini", "gemini_",
            (response, token) => ProviderHttp.ThrowIfUnsuccessfulAsync(response, transport, null, "generation", token), codec.Encode);
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in GeminiStream.ReadAsync(http, new(HttpMethod.Post, connection.Endpoint), route,
            connection, request, null, stream: true, usageDeltas: true, reasoningText: false, CancellationToken.None))
            events.Add(item);

        LlmUsage[] observations = events.OfType<LlmUsageDeltaEvent>().Select(item => item.Cumulative).ToArray();
        Assert.Equal(2, observations.Length);
        Assert.Equal(new LlmUsage(8, 6, 14) { CachedInputTokens = 1, ReasoningOutputTokens = 4 }, observations[0]);
        var corrected = new LlmUsage(6, 3, 9);
        Assert.Equal(corrected, observations[1]);
        LlmChatResult terminal = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        LlmChatResult restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(terminal, ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;
        Assert.Equal(LlmCompletionStatus.Completed, restored.Status);
        Assert.Equal("hello", restored.Text);
        Assert.Equal(corrected, restored.Usage);
        Assert.NotNull(restored.ContinuationState);
    }

    [Theory]
    [InlineData(LlmTransport.OpenAiResponses, true)]
    [InlineData(LlmTransport.OpenAiResponses, false)]
    [InlineData(LlmTransport.OpenAiCompatibleChat, true)]
    [InlineData(LlmTransport.OpenAiCompatibleChat, false)]
    [InlineData(LlmTransport.AnthropicMessages, true)]
    [InlineData(LlmTransport.AnthropicMessages, false)]
    [InlineData(LlmTransport.GoogleGeminiApi, true)]
    [InlineData(LlmTransport.GoogleGeminiApi, false)]
    [InlineData(LlmTransport.GoogleVertexAi, true)]
    [InlineData(LlmTransport.GoogleVertexAi, false)]
    [InlineData(LlmTransport.OpenAiCodex, true)]
    [InlineData(LlmTransport.XaiResponses, true)]
    [InlineData(LlmTransport.XaiResponses, false)]
    public async Task UnknownTerminalToolCallsRemainEvidenceNotSuccessfulContinuation(LlmTransport transport, bool streaming)
    {
        using var http = new HttpClient(new Handler(transport, streaming));
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = transport,
            Endpoint = transport == LlmTransport.XaiResponses ? "https://api.x.ai/v1" : "https://provider.example.test/v1",
            Xai = transport == LlmTransport.XaiResponses ? new() : null,
            Credential = new LlmCredential
            {
                ApiKey = transport is LlmTransport.OpenAiCodex or LlmTransport.XaiResponses ? null : "test-key",
                OAuth = transport is LlmTransport.OpenAiCodex or LlmTransport.XaiResponses
                    ? new() { AccessToken = "test-token", AccountId = "test-account" } : null
            }.Serialize(),
            ResolvedContinuationScope = transport is LlmTransport.GoogleGeminiApi or LlmTransport.GoogleVertexAi ? "test-scope" : null,
            CapabilityOverrides = new() { SupportsStreaming = streaming }
        };
        var request = new LlmChatRequest
        {
            ModelId = transport == LlmTransport.AnthropicMessages ? "claude-sonnet-4-6" : "selected-model",
            MaxOutputTokens = transport == LlmTransport.OpenAiCodex ? null : 16,
            Messages = [new("user", "hello")],
            Tools = [new LlmToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\",\"additionalProperties\":false}") }]
        };
        IAsyncEnumerable<LlmStreamEvent> source;
        if (transport is LlmTransport.GoogleGeminiApi or LlmTransport.GoogleVertexAi)
        {
            var codec = new GeminiContinuationCodec("test-gemini", "Gemini");
            var route = new GeminiRoute(transport, "Gemini", "gemini_",
                (response, token) => ProviderHttp.ThrowIfUnsuccessfulAsync(response, transport, null, "generation", token), codec.Encode);
            source = GeminiStream.ReadAsync(http, new(HttpMethod.Post, connection.Endpoint), route, connection,
                request, null, streaming, usageDeltas: true, reasoningText: false, CancellationToken.None);
        }
        else
        {
            ILlmClient client = transport switch
            {
                LlmTransport.OpenAiResponses => new OpenAiResponsesLlmClient(http),
                LlmTransport.OpenAiCodex => new OpenAiCodexLlmClient(http),
                LlmTransport.XaiResponses => new XaiResponsesLlmClient(http),
                LlmTransport.OpenAiCompatibleChat => new OpenAiCompatibleChatLlmClient(http),
                LlmTransport.AnthropicMessages => new AnthropicMessagesLlmClient(http),
                _ => throw new InvalidOperationException("Unexpected test route.")
            };
            source = client.StreamAsync(connection, request);
        }
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in source) events.Add(item);
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.IsType<LlmChatCompletionEvent>(events[^1]);
        LlmChatResult restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(result, ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;

        Assert.Equal(LlmCompletionStatus.Unknown, restored.Status);
        Assert.Equal("UNKNOWN_STOP", restored.FinishReason);
        Assert.Equal("partial", restored.Text);
        Assert.Equal("reported-model", restored.ModelId);
        Assert.Equal(new LlmUsage(5, 2, 7), restored.Usage);
        Assert.Null(restored.ContinuationState);
        LlmToolCall call = Assert.IsType<LlmToolCall>(Assert.Single(restored.ToolCalls));
        Assert.Equal("call-1", call.Id);
        Assert.Equal("echo", call.Name);
        Assert.Equal("{}", call.ArgumentsJson);
        Assert.Equal(call.Id, Assert.Single(events.OfType<LlmToolCallStartedEvent>()).ToolCallId);
        Assert.Equal(call.Id, Assert.Single(events.OfType<LlmToolCallCompletedEvent>()).ToolCall.Id);
    }

    [Fact]
    public async Task GrokEmptyTerminalOutputDoesNotResurrectProvisionalCallsOrText()
    {
        const string body = """
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"echo","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","output_index":0,"delta":"{}"}

            data: {"type":"response.output_text.delta","delta":"provisional"}

            data: {"type":"response.output_item.done","output_index":1,"item":{"type":"message","id":"message-1","content":[{"type":"output_text","text":"provisional","annotations":[{"type":"url_citation","url":"https://source.example.test/provisional"}]}]}}

            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"echo","arguments":"{}"}}

            data: {"type":"response.completed","response":{"id":"response-1","model":"reported-model","status":"completed","output":[],"usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}}
            """;
        using var http = new HttpClient(new Handler(LlmTransport.XaiResponses, streaming: true, body + "\n\n"));
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token", AccountId = "test-account" } }.Serialize()
        };
        var request = new LlmChatRequest
        {
            ModelId = "selected-model",
            Messages = [new("user", "hello")],
            Tools = [new LlmToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\",\"additionalProperties\":false}") }]
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);

        Assert.Equal("call-1", Assert.Single(events.OfType<LlmToolCallStartedEvent>()).ToolCallId);
        Assert.Single(events.OfType<LlmToolCallArgumentsDeltaEvent>());
        Assert.Single(events.OfType<LlmTextDeltaEvent>());
        Assert.Single(events.OfType<LlmCitationEvent>());
        Assert.Empty(events.OfType<LlmToolCallCompletedEvent>());
        LlmChatResult terminal = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        LlmChatResult restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(terminal, ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;
        Assert.Equal(LlmCompletionStatus.Completed, restored.Status);
        Assert.Equal("reported-model", restored.ModelId);
        Assert.Equal(new LlmUsage(5, 2, 7), restored.Usage);
        Assert.Empty(restored.Text);
        Assert.Empty(restored.ToolCalls);
        Assert.Empty(restored.Citations);
        Assert.Null(restored.ContinuationState);
    }

    [Fact]
    public async Task GrokTerminalCitationsReplaceProvisionalValuesWithoutRepeatingObservedPositions()
    {
        const string body = """
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"message-1","content":[{"type":"output_text","text":"answer","annotations":[{"type":"url_citation","url":"https://source.example.test/provisional"}]}]}}

            data: {"type":"response.completed","response":{"id":"response-1","model":"reported-model","status":"completed","output":[{"type":"message","id":"message-1","role":"assistant","content":[{"type":"output_text","text":"answer","annotations":[{"type":"url_citation","url":"https://source.example.test/final"}]}]}]}}
            """;
        using var http = new HttpClient(new Handler(LlmTransport.XaiResponses, streaming: true, body + "\n\n"));
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize()
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection,
            new() { ModelId = "selected-model", Messages = [new("user", "hello")] })) events.Add(item);

        Assert.Single(events.OfType<LlmCitationEvent>());
        LlmChatResult terminal = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        LlmChatResult restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(terminal, ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;
        Assert.Equal(LlmCompletionStatus.Completed, restored.Status);
        Assert.Equal("answer", restored.Text);
        Assert.Equal("https://source.example.test/final", Assert.Single(restored.Citations).Url);
    }

    private sealed class Handler(LlmTransport transport, bool streaming, string? scriptedBody = null,
        HttpStatusCode statusCode = HttpStatusCode.OK, TimeSpan? retryAfter = null, string? requestId = null,
        HttpMethod? expectedMethod = null) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string? RequestBody { get; private set; }
        internal string? RequestTrace { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(expectedMethod ?? HttpMethod.Post, request.Method);
            RequestTrace = request.Headers.TryGetValues("X-Trace", out IEnumerable<string>? values) ? Assert.Single(values) : null;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            string body = scriptedBody ?? Body(transport, streaming);
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, streaming && statusCode == HttpStatusCode.OK ? "text/event-stream" : "application/json")
            };
            if (retryAfter is { } delay) response.Headers.RetryAfter = new(delay);
            if (requestId is not null) response.Headers.Add("x-request-id", requestId);
            return response;
        }
    }

    private static string Body(LlmTransport transport, bool streaming)
    {
        const string responses = """
            {"id":"response-1","model":"reported-model","status":"UNKNOWN_STOP","output":[{"type":"message","id":"message-1","role":"assistant","content":[{"type":"output_text","text":"partial"}]},{"type":"function_call","id":"fc-1","call_id":"call-1","name":"echo","arguments":"{}"}],"usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}
            """;
        const string chat = """
            {"id":"response-1","model":"reported-model","choices":[{"index":0,"message":{"role":"assistant","content":"partial","tool_calls":[{"id":"call-1","type":"function","function":{"name":"echo","arguments":"{}"}}]},"finish_reason":"UNKNOWN_STOP"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}
            """;
        const string anthropic = """
            {"type":"message","id":"response-1","model":"reported-model","content":[{"type":"text","text":"partial"},{"type":"tool_use","id":"call-1","name":"echo","input":{}}],"stop_reason":"UNKNOWN_STOP","usage":{"input_tokens":5,"output_tokens":2}}
            """;
        const string gemini = """
            {"responseId":"response-1","modelVersion":"reported-model","candidates":[{"finishReason":"UNKNOWN_STOP","content":{"parts":[{"text":"partial"},{"functionCall":{"id":"call-1","name":"echo","args":{}}}]}}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":2,"totalTokenCount":7}}
            """;
        if (!streaming) return transport switch
        {
            LlmTransport.OpenAiResponses or LlmTransport.XaiResponses => responses,
            LlmTransport.OpenAiCompatibleChat => chat,
            LlmTransport.AnthropicMessages => anthropic,
            LlmTransport.GoogleGeminiApi or LlmTransport.GoogleVertexAi => gemini,
            _ => throw new InvalidOperationException("Unexpected complete test route.")
        };
        return transport switch
        {
            LlmTransport.OpenAiResponses or LlmTransport.OpenAiCodex or LlmTransport.XaiResponses =>
                "data: {\"type\":\"response.completed\",\"response\":" + responses + "}\n\n",
            LlmTransport.GoogleGeminiApi or LlmTransport.GoogleVertexAi => "data: " + gemini + "\n\n",
            LlmTransport.OpenAiCompatibleChat => """
                data: {"id":"response-1","model":"reported-model","choices":[{"index":0,"delta":{"content":"partial","tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"echo","arguments":"{}"}}]},"finish_reason":"UNKNOWN_STOP"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}

                data: [DONE]
                """ + "\n\n",
            LlmTransport.AnthropicMessages => """
                data: {"type":"message_start","message":{"id":"response-1","model":"reported-model","usage":{"input_tokens":5,"output_tokens":0}}}

                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":"partial"}}

                data: {"type":"content_block_stop","index":0}

                data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"call-1","name":"echo","input":{}}}

                data: {"type":"content_block_stop","index":1}

                data: {"type":"message_delta","delta":{"stop_reason":"UNKNOWN_STOP"},"usage":{"output_tokens":2}}

                data: {"type":"message_stop"}
                """ + "\n\n",
            _ => throw new InvalidOperationException("Unexpected streamed test route.")
        };
    }
}
