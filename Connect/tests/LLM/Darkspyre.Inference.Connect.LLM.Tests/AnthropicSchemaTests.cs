using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AnthropicSchemaTests
{
    private const string Model = "claude-opus-4-5";
    private const string ClosedSchema = """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""";

    [Theory]
    [InlineData("""{"type":"string","pattern":"^[a-z]+$"}""")]
    [InlineData("""{"type":"string","pattern":"(?:cat|dog){1,3}"}""")]
    [InlineData("""{"type":"string","pattern":"\\\\b"}""")]
    [InlineData("""{"type":"string","format":"date"}""")]
    [InlineData("""{"type":["string","null"],"default":null}""")]
    [InlineData("""{"type":"array","items":{"type":"integer"},"minItems":1}""")]
    [InlineData("""{"enum":["value",1,true,null]}""")]
    [InlineData("""{"allOf":[{"type":"string"},{"enum":["a","b"]}]}""")]
    [InlineData("""{"anyOf":[{"type":"string"},{"type":"integer"}]}""")]
    [InlineData("""{"$defs":{"answer":{"type":"string"}},"$ref":"#/$defs/answer"}""")]
    [InlineData("""{"definitions":{"a/b~c":{"type":"string"}},"$ref":"#/definitions/a~1b~0c"}""")]
    [InlineData("""{"type":"object","properties":{"minimum":{"type":"string"}},"additionalProperties":false}""")]
    public void SupportedSchemasKeepTheirOriginalShape(string json)
    {
        JsonElement schema = JsonElement.Parse(json);
        Assert.True(AnthropicSchemas.Supports(schema));
        Assert.Equal(json, schema.GetRawText());
    }

    [Theory]
    [InlineData("""{"type":"string","minLength":3}""")]
    [InlineData("""{"type":"integer","minimum":3}""")]
    [InlineData("""{"type":"number","multipleOf":2}""")]
    [InlineData("""{"type":"array","items":{"type":"string"},"minItems":2}""")]
    [InlineData("""{"type":"array","items":{"type":"string"},"maxItems":2}""")]
    [InlineData("""{"type":"object","properties":{"answer":{"type":"string"}}}""")]
    [InlineData("""{"type":"object","additionalProperties":true}""")]
    [InlineData("""{"type":"object","additionalProperties":{"type":"string"}}""")]
    [InlineData("""{"type":"string","format":"regex"}""")]
    [InlineData("""{"enum":[{"nested":"object"}]}""")]
    [InlineData("""{"$ref":"https://example.invalid/schema"}""")]
    [InlineData("""{"$ref":"#/$defs/missing"}""")]
    [InlineData("""{"$defs":{"node":{"$ref":"#/$defs/node"}},"$ref":"#/$defs/node"}""")]
    [InlineData("""{"$defs":{"a":{"$ref":"#/$defs/b"},"b":{"$ref":"#/$defs/a"}},"$ref":"#/$defs/a"}""")]
    [InlineData("""{"$defs":{"a":{"type":"string"}},"allOf":[{"$ref":"#/$defs/a"}]}""")]
    [InlineData("""{"type":"string","pattern":"(?=a)a"}""")]
    [InlineData("""{"type":"string","pattern":"(a)\\1"}""")]
    [InlineData("""{"type":"string","pattern":"\\bword\\b"}""")]
    [InlineData("""{"type":"string","pattern":"[invalid"}""")]
    [InlineData("""{"type":"object","properties":{"answer":{"type":"string","minLength":2}},"additionalProperties":false}""")]
    [InlineData("""{"type":"string","future_constraint":true}""")]
    public void UnsupportedSchemasRequireAnExplicitLocalFallback(string json)
    {
        JsonElement schema = JsonElement.Parse(json);
        Assert.False(AnthropicSchemas.Supports(schema));
        var request = new LlmStructuredOutputRequest { Name = "result", Schema = schema };
        var capabilities = new LlmProviderCapabilities { SupportsJsonSchema = true };
        Assert.Throws<NotSupportedException>(() => AnthropicSchemas.Enforcement(request, capabilities));
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly,
            AnthropicSchemas.Enforcement(request with { Fallback = LlmStructuredOutputFallback.LocalValidation }, capabilities));
        Assert.Equal(json, schema.GetRawText());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeFormatMergesWithEffortAndValidatesBothResponseModes(bool streaming)
    {
        using var handler = new Handler(streaming, "{\"answer\":\"ok\"}");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var request = Request(ClosedSchema) with { ReasoningLevel = LlmReasoningLevel.Medium };
        LlmChatResult result = await Complete(client, Connection(streaming), request);
        JsonNode payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Equal("json_schema", (string?)payload["output_config"]!["format"]!["type"]);
        Assert.Equal("medium", (string?)payload["output_config"]!["effort"]);
        Assert.Null(payload["output_format"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ClosedSchema), payload["output_config"]!["format"]!["schema"]));
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, result.StructuredOutput!.Enforcement);
        Assert.True(result.StructuredOutput.IsValid);
        Assert.Equal(ClosedSchema, request.StructuredOutput!.Schema.GetRawText());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalFallbackOmitsNativeFormatAndValidatesEveryOriginalConstraint(bool valid)
    {
        const string schema = """{"type":"object","properties":{"answer":{"type":"string","minLength":5}},"required":["answer"],"additionalProperties":false}""";
        using var handler = new Handler(false, valid ? "{\"answer\":\"long enough\"}" : "{\"answer\":\"no\"}");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var request = Request(schema, LlmStructuredOutputFallback.LocalValidation);
        var result = await Complete(client, Connection(false), request);
        Assert.Null(JsonNode.Parse(Assert.Single(handler.Bodies))!["output_config"]);
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly, result.StructuredOutput!.Enforcement);
        Assert.Equal(valid, result.StructuredOutput.IsValid);
        Assert.Equal(schema, request.StructuredOutput!.Schema.GetRawText());
    }

    [Fact]
    public async Task UnsupportedSchemaAndStrictToolFailBeforeDispatchWithoutChangingStrictness()
    {
        using var handler = new Handler(false, "{}");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection(false), Request("""{"type":"string","minLength":2}""")));
        var tool = new LlmToolDefinition
        {
            Name = "run",
            Strict = true,
            InputSchema = JsonElement.Parse("""{"type":"object","properties":{"name":{"type":"string","minLength":2}},"additionalProperties":false}""")
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection(false), new() { ModelId = Model, Tools = [tool] }));
        Assert.Empty(handler.Bodies);
        Assert.True(tool.Strict);
        Assert.True(tool.InputSchema.GetProperty("properties").GetProperty("name").TryGetProperty("minLength", out _));
        await Complete(client, Connection(false), new() { ModelId = Model, Tools = [tool with { Strict = false }] });
        JsonNode native = JsonNode.Parse(Assert.Single(handler.Bodies))!["tools"]![0]!;
        Assert.Null(native["strict"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(tool.InputSchema.GetRawText()), native["input_schema"]));
    }

    [Fact]
    public async Task NativeOutputAndMixedStrictToolsCoexist()
    {
        using var handler = new Handler(false, "{\"answer\":\"ok\"}");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var tool = new LlmToolDefinition { Name = "run", InputSchema = JsonElement.Parse(ClosedSchema) };
        await Complete(client, Connection(false), Request(ClosedSchema) with
        { Tools = [tool, tool with { Name = "strict_run", Strict = true }] });
        JsonNode payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Null(payload["tools"]![0]!["strict"]);
        Assert.Equal(true, (bool?)payload["tools"]![1]!["strict"]);
        Assert.NotNull(payload["output_config"]!["format"]);
    }

    [Fact]
    public void LongReferenceChainsUseIterativeTraversalAndRejectCycles()
    {
        var definitions = new JsonObject();
        for (int index = 0; index < 5000; index++)
            definitions["node_" + index] = new JsonObject { ["$ref"] = "#/$defs/node_" + (index + 1) };
        definitions["node_5000"] = new JsonObject { ["type"] = "string" };
        var schema = new JsonObject { ["$defs"] = definitions, ["$ref"] = "#/$defs/node_0" };
        Assert.True(AnthropicSchemas.Supports(JsonSerializer.SerializeToElement(schema)));
        definitions["node_5000"] = new JsonObject { ["$ref"] = "#/$defs/node_0" };
        Assert.False(AnthropicSchemas.Supports(JsonSerializer.SerializeToElement(schema)));
    }

    [Fact]
    public async Task CatalogRejectionFencesStrictToolsAndUnknownProfilesPermitExplicitAttempts()
    {
        using var handler = new Handler(false, "{}");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var request = new LlmChatRequest
        {
            ModelId = Model,
            Tools = [new()
        { Name = "run", Strict = true, InputSchema = JsonElement.Parse(ClosedSchema) }]
        };
        LlmConnection unsupported = Connection(false) with
        { Catalog = [new(Model, Model) { CatalogEntry = JsonElement.Parse("""{"capabilities":{"structured_outputs":{"supported":false}}}""") }] };
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, unsupported, request));
        Assert.Empty(handler.Bodies);
        await Complete(client, Connection(false) with { Catalog = null }, request);
        Assert.Equal(true, (bool?)JsonNode.Parse(Assert.Single(handler.Bodies))!["tools"]![0]!["strict"]);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    public async Task StrictToolCountUsesTheNativeLimitWithoutCountingOrdinaryTools(int strictCount)
    {
        using var handler = new Handler(false, "{}");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var tools = Enumerable.Range(0, strictCount + 5).Select(index => new LlmToolDefinition
        { Name = "run_" + index, Strict = index < strictCount, InputSchema = JsonElement.Parse(ClosedSchema) }).ToArray();
        var request = new LlmChatRequest { ModelId = Model, Tools = tools };
        if (strictCount == 21)
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection(false), request));
            Assert.Empty(handler.Bodies);
        }
        else
        {
            await Complete(client, Connection(false), request);
            Assert.Equal(25, JsonNode.Parse(Assert.Single(handler.Bodies))!["tools"]!.AsArray().Count);
        }
    }

    [Fact]
    public void CatalogSupportIsAuthoritativeAndUnknownStrictAttemptsRemainExplicit()
    {
        using var http = new HttpClient();
        var client = new AnthropicMessagesLlmClient(http);
        var supported = client.GetCapabilities(Connection(), Model);
        Assert.True(supported.SupportsJsonSchema);
        Assert.Equal(LlmStrictInputSchemaSupport.Supported, supported.Tools.StrictInputSchema);
        var unsupported = client.GetCapabilities(Connection() with
        {
            Catalog = [new(Model, Model) { CatalogEntry = JsonElement.Parse("""{"capabilities":{"structured_outputs":{"supported":false}}}""") }],
            CapabilityOverrides = new() { SupportsJsonSchema = true }
        }, Model);
        Assert.False(unsupported.SupportsJsonSchema);
        Assert.Equal(LlmStrictInputSchemaSupport.Unsupported, unsupported.Tools.StrictInputSchema);
        var unknown = client.GetCapabilities(Connection() with { Catalog = null }, Model);
        Assert.False(unknown.SupportsJsonSchema);
        Assert.Equal(LlmStrictInputSchemaSupport.Unknown, unknown.Tools.StrictInputSchema);
        Assert.False(client.GetCapabilities(Connection() with { CapabilityOverrides = new() { SupportsJsonSchema = false } }, Model).SupportsJsonSchema);
    }

    [Theory]
    [InlineData("output_format")]
    [InlineData("output_config")]
    public async Task ProviderOptionsCannotBypassTypedOutputPreflight(string key)
    {
        using var handler = new Handler(false, "{}");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, Connection(false), new()
        { ModelId = Model, ProviderOptions = new Dictionary<string, JsonElement> { [key] = JsonElement.Parse("{}") } }));
        Assert.Empty(handler.Bodies);
    }

    private static LlmConnection Connection(bool streaming = true) => new()
    {
        Version = "1",
        Transport = LlmTransport.AnthropicMessages,
        Endpoint = "https://anthropic.invalid",
        Credential = new LlmCredential { ApiKey = "fixture-key" }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming },
        Catalog = [new(Model, Model) { CatalogEntry = JsonElement.Parse("""{"capabilities":{"structured_outputs":{"supported":true},"effort":{"supported":true,"medium":{"supported":true}}}}""") }]
    };

    private static LlmChatRequest Request(string schema, LlmStructuredOutputFallback fallback = LlmStructuredOutputFallback.None) => new()
    {
        ModelId = Model,
        Messages = [new("user", "Return the requested JSON.")],
        StructuredOutput = new() { Name = "result", Schema = JsonElement.Parse(schema), Fallback = fallback }
    };

    private static async Task<LlmChatResult> Complete(AnthropicMessagesLlmClient client, LlmConnection connection, LlmChatRequest request)
    {
        LlmChatResult? result = null;
        await foreach (var item in client.StreamAsync(connection, request))
            if (item is LlmChatCompletionEvent completion) result = completion.Result;
        return Assert.IsType<LlmChatResult>(result);
    }

    private sealed class Handler(bool streaming, string text) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            string body = streaming
                ? "data: " + new JsonObject { ["type"] = "content_block_delta", ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text } }.ToJsonString()
                    + "\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\ndata: {\"type\":\"message_stop\"}\n\n"
                : new JsonObject { ["type"] = "message", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["stop_reason"] = "end_turn" }.ToJsonString();
            return new(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
