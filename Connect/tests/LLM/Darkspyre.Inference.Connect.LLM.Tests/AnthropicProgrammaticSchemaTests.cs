using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AnthropicProgrammaticSchemaTests
{
    private const string Recursive = """{"type":"object","properties":{"next":{"$ref":"#"}}}""";
    private const string Programmatic = """
        {"server_tools":[{"type":"code_execution_20260120","name":"code_execution"}],
         "client_tools":{"run":{"allowed_callers":["code_execution_20260120"]}}}
        """;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RecursiveProgrammaticSchemaFailsBeforeGenerationOrCountDispatch(int operation)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection(operation == 0);
        var request = Request(Recursive);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(client, connection, request, operation));
        Assert.DoesNotContain(Recursive, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Bodies);
    }

    public static TheoryData<string> Cycles => new()
    {
        Recursive,
        """{"$ref":""}""",
        """{"$ref":"#","properties":{}}""",
        """{"$defs":{"a":{"$ref":"#/$defs/b"},"b":{"$ref":"#/$defs/a"}}}""",
        """{"$anchor":"root","properties":{"child":{"$ref":"#root"}}}""",
        """{"$dynamicAnchor":"root","properties":{"child":{"$ref":"#root"}}}""",
        """{"$defs":{"a":{"$anchor":"a","$ref":"#b"},"b":{"$anchor":"b","$ref":"#a"}}}""",
        """{"$defs":{"a/b~c":{"$ref":"#/$defs/a~1b~0c"}}}""",
        """{"$defs":{"a+b":{"$ref":"#/$defs/a+b"}}}""",
        """{"$defs":{"a b":{"$ref":"#%2F$defs%2Fa%20b"}}}""",
        """{"$defs":{"a%b":{"$ref":"#/$defs/a%25b"}}}""",
        """{"$defs":{"\u96ea":{"$ref":"#/$defs/%E9%9B%AA"}}}""",
        """{"properties":{"":{"$ref":"#/properties/"}}}""",
        """{"prefixItems":[{"$ref":"#/prefixItems/0"}]}""",
        """{"$id":"https://schema.invalid/root","properties":{"next":{"$ref":"https://schema.invalid/root#"}}}""",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema#","$id":"https://schema.invalid/root#","properties":{"next":{"$ref":"#"}}}""",
        """{"$id":"https://schema.invalid/root","$defs":{"child":{"$id":"child","properties":{"next":{"$ref":"#"}}}}}""",
        """{"$id":"https://schema.invalid/root","$defs":{"child":{"$id":"child","properties":{"next":{"$ref":"https://schema.invalid/root#/$defs/child"}}}}}""",
        """{"$id":"https://schema.invalid/root","$defs":{"a":{"$id":"a","$ref":"b"},"b":{"$id":"b","$ref":"a"}}}"""
    };

    [Theory]
    [MemberData(nameof(Cycles))]
    public void LocalReferencesDetectOnlyActualCycles(string schema)
    {
        var request = Request(schema);
        Assert.Throws<NotSupportedException>(() => AnthropicTools.Resolve(Connection(false), request));
        Assert.Equal(schema, Assert.IsType<LlmToolDefinition>(request.Tools[0]).InputSchema.GetRawText());
        Assert.Equal(Programmatic, request.ProviderOptions[AnthropicTools.OptionName].GetRawText());
    }

    public static TheoryData<string> Preserved => new()
    {
        """{"type":"object","$defs":{"a":{"type":"string"}},"properties":{"first":{"$ref":"#/$defs/a"},"second":{"$ref":"#/$defs/a"}}}""",
        """{"$defs":{"unused":{"$ref":"#"}},"properties":{"value":true},"additionalProperties":false}""",
        """{"$ref":"https://external.invalid/schema#/$defs/a"}""",
        """{"$ref":"https://external.invalid/schema#anchor"}""",
        """{"$ref":"#/$defs/missing"}""",
        """{"$ref":"#missing"}""",
        """{"$ref":5}""",
        """{"$ref":"#/~2"}""",
        """{"$schema":"https://schema.invalid/custom","$ref":"#"}""",
        """{"$schema":false,"$ref":"#"}""",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema##","$ref":"#"}""",
        """{"$id":5,"$ref":"#"}""",
        """{"$id":"https://schema.invalid/root#bad","$ref":"#"}""",
        """{"$id":"https://schema.invalid/root","$defs":{"a":{"$id":"child","$ref":"#"},"b":{"$id":"child","$ref":"#"}},"$ref":"child"}""",
        """{"$defs":{"a":{"$anchor":"shared","$ref":"#shared"},"b":{"$anchor":"shared","$ref":"#shared"}}}""",
        """{"$anchor":"1bad","$ref":"#1bad"}""",
        """{"$anchor":5,"$ref":"#bad"}""",
        """{"$ref":"#","$ref":"#"}""",
        """{"$defs":{"duplicate":{"$ref":"#"},"duplicate":{"$ref":"#"}},"$ref":"#/$defs/duplicate"}""",
        """{"default":{"$ref":"#"},"const":{"$ref":"#"},"enum":[{"$ref":"#"}],"examples":[{"$ref":"#"}],"unknown":{"$ref":"#"}}""",
        """{"default":{"$ref":"#/default"},"$ref":"#/default"}""",
        """{"$defs":{"a b":{"type":"string"},"a+b":{"$ref":"#/$defs/a%20b"}}}""",
        """{"prefixItems":[{"$ref":"#/prefixItems/-"}]}""",
        """{"prefixItems":[true,false],"properties":{"boolean":false}}""",
        """{"$dynamicAnchor":"root","properties":{"next":{"$dynamicRef":"#root"}}}""",
        """{"$defs":{"a":{"$id":"urn:example:a","type":"string"}},"$ref":"urn:example:a"}""",
        """{"$id":"https://schema.invalid/root","$defs":{"a":{"$id":"child","$anchor":"value","type":"string"}},"properties":{"first":{"$ref":"child#value"},"second":{"$ref":"#/$defs/a"}}}"""
    };

    [Theory]
    [MemberData(nameof(Preserved))]
    public void AcyclicOpaqueUnresolvedAndAmbiguousSchemasRemainProviderOwned(string schema)
    {
        var request = Request(schema);
        AnthropicTools.Resolve(Connection(false), request);
        Assert.Equal(schema, Assert.IsType<LlmToolDefinition>(request.Tools[0]).InputSchema.GetRawText());
        Assert.Equal(Programmatic, request.ProviderOptions[AnthropicTools.OptionName].GetRawText());
    }

    [Theory]
    [InlineData("default")]
    [InlineData("const")]
    [InlineData("enum")]
    [InlineData("examples")]
    [InlineData("provider_extension")]
    public void PointersIntoOpaqueDataDoNotPromoteItToASchema(string keyword)
    {
        string pointer = "#/" + keyword + (keyword is "enum" or "examples" ? "/0" : "");
        var fake = new JsonObject { ["$ref"] = pointer };
        var schema = new JsonObject { [keyword] = keyword is "enum" or "examples" ? new JsonArray(fake) : fake, ["$ref"] = pointer };
        AnthropicProgrammaticSchemas.Validate(JsonElement.Parse(schema.ToJsonString()));
    }

    [Fact]
    public void SharedBranchingGraphDoesNotExpandExponentially()
    {
        var definitions = new JsonObject { ["leaf"] = false };
        string previous = "leaf";
        for (int index = 0; index < 100; index++)
        {
            definitions["n" + index] = new JsonObject
            {
                ["properties"] = new JsonObject
                { ["left"] = new JsonObject { ["$ref"] = "#/$defs/" + previous }, ["right"] = new JsonObject { ["$ref"] = "#/$defs/" + previous } }
            };
            previous = "n" + index;
        }
        AnthropicProgrammaticSchemas.Validate(JsonElement.Parse(new JsonObject
        { ["$defs"] = definitions, ["$ref"] = "#/$defs/" + previous }.ToJsonString()));
    }

    [Theory]
    [InlineData("properties", "map")]
    [InlineData("patternProperties", "map")]
    [InlineData("dependentSchemas", "map")]
    [InlineData("dependencies", "map")]
    [InlineData("$defs", "map")]
    [InlineData("definitions", "map")]
    [InlineData("allOf", "array")]
    [InlineData("anyOf", "array")]
    [InlineData("oneOf", "array")]
    [InlineData("prefixItems", "array")]
    [InlineData("additionalProperties", "single")]
    [InlineData("items", "single")]
    [InlineData("contains", "single")]
    [InlineData("propertyNames", "single")]
    [InlineData("not", "single")]
    [InlineData("if", "single")]
    [InlineData("then", "single")]
    [InlineData("else", "single")]
    [InlineData("unevaluatedProperties", "single")]
    [InlineData("unevaluatedItems", "single")]
    [InlineData("contentSchema", "single")]
    public void DocumentedSchemaLocationsParticipateWithoutAKeywordAllowlist(string keyword, string shape)
    {
        string pointer = "#/" + keyword + (shape == "map" ? "/child" : shape == "array" ? "/0" : "");
        var child = new JsonObject { ["$ref"] = pointer };
        JsonNode value = shape switch
        { "map" => new JsonObject { ["child"] = child }, "array" => new JsonArray(child), _ => child };
        var schema = new JsonObject { [keyword] = value, ["provider_extension"] = new JsonObject { ["$ref"] = "#" } };
        Assert.Throws<NotSupportedException>(() => AnthropicProgrammaticSchemas.Validate(JsonElement.Parse(schema.ToJsonString())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongFlatReferencePathsAreIterativeAndDoNotExpandSharedGraphs(bool cyclic)
    {
        var definitions = new JsonObject();
        for (int index = 0; index < 5000; index++)
            definitions["n" + index] = index == 4999 && !cyclic ? new JsonObject { ["type"] = "string" }
                : new JsonObject { ["$ref"] = "#/$defs/n" + (index == 4999 ? 0 : index + 1) };
        var schema = new JsonObject
        {
            ["$defs"] = definitions,
            ["properties"] = new JsonObject
            { ["first"] = new JsonObject { ["$ref"] = "#/$defs/n0" }, ["second"] = new JsonObject { ["$ref"] = "#/$defs/n0" } }
        };
        JsonElement input = JsonElement.Parse(schema.ToJsonString());
        if (cyclic) Assert.Throws<NotSupportedException>(() => AnthropicProgrammaticSchemas.Validate(input));
        else AnthropicProgrammaticSchemas.Validate(input);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MixedDirectRecursiveAndProgrammaticAcyclicSchemasPreserveWireBytes(int operation)
    {
        const string acyclic = """{ "type": "object", "$defs": {"a+b":true}, "properties": {"value":{"$ref":"#/$defs/a+b"}}, "default":{"$ref":"#"} }""";
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection(operation == 0);
        var request = Request(acyclic) with { Tools = [Request(acyclic).Tools[0], Assert.IsType<LlmToolDefinition>(Request(Recursive).Tools[0]) with { Name = "direct" }] };
        await Invoke(client, connection, request with { ProviderOptions = new Dictionary<string, JsonElement>() }, operation);
        await Invoke(client, connection, request, operation);
        var baseline = JsonElement.Parse(handler.Bodies[0]).GetProperty("tools");
        var programmatic = JsonElement.Parse(handler.Bodies[1]).GetProperty("tools");
        for (int index = 0; index < 2; index++)
            Assert.Equal(baseline[index].GetProperty("input_schema").GetRawText(), programmatic[index].GetProperty("input_schema").GetRawText());
        Assert.Equal(acyclic, Assert.IsType<LlmToolDefinition>(request.Tools[0]).InputSchema.GetRawText());
        Assert.Equal(Recursive, Assert.IsType<LlmToolDefinition>(request.Tools[1]).InputSchema.GetRawText());
        Assert.Equal(Programmatic, request.ProviderOptions[AnthropicTools.OptionName].GetRawText());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task RequestClearingOrReplacingConnectionMetadataKeepsRecursiveToolsDirectOnly(int operation, bool explicitDirect)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var request = Request(Recursive) with
        {
            ProviderOptions = new Dictionary<string, JsonElement>
            { [AnthropicTools.OptionName] = JsonElement.Parse(explicitDirect ? """{"client_tools":{"run":{"allowed_callers":["direct"]}}}""" : "{}") }
        };
        await Invoke(new AnthropicMessagesLlmClient(http), Connection(operation == 0) with
        { ProviderOptions = Request(Recursive).ProviderOptions }, request, operation);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExternalUnresolvedReferencesRemainUnchangedOnActualGenerationAndCount(int operation)
    {
        const string schema = """{"type":"object","properties":{"external":{"$ref":"https://external.invalid/schema#anchor"}},"examples":[{"$ref":"#"}]}""";
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var request = Request(schema);
        var connection = Connection(operation == 0);
        await Invoke(client, connection, request with { ProviderOptions = new Dictionary<string, JsonElement>() }, operation);
        await Invoke(client, connection, request, operation);
        Assert.Equal(JsonElement.Parse(handler.Bodies[0]).GetProperty("tools")[0].GetProperty("input_schema").GetRawText(),
            JsonElement.Parse(handler.Bodies[1]).GetProperty("tools")[0].GetProperty("input_schema").GetRawText());
        Assert.Equal(schema, Assert.IsType<LlmToolDefinition>(request.Tools[0]).InputSchema.GetRawText());
    }

    private static LlmConnection Connection(bool streaming) => new()
    {
        Version = "1",
        Transport = LlmTransport.AnthropicMessages,
        Endpoint = "https://api.anthropic.invalid",
        Credential = new LlmCredential { ApiKey = "fixture-key" }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };

    private static LlmChatRequest Request(string schema) => new()
    {
        ModelId = "claude-sonnet-4-6",
        ReasoningLevel = LlmReasoningLevel.None,
        Messages = [new("user", "hello")],
        Tools = [new LlmToolDefinition() { Name = "run", InputSchema = JsonElement.Parse(schema) }],
        ProviderOptions = new Dictionary<string, JsonElement>
        { [AnthropicTools.OptionName] = JsonElement.Parse(Programmatic) }
    };

    private static async Task Invoke(AnthropicMessagesLlmClient client, LlmConnection connection,
        LlmChatRequest request, int operation)
    {
        if (operation == 2) { await client.CountInputTokensAsync(connection, request); return; }
        await foreach (LlmStreamEvent unused in client.StreamAsync(connection, request)) { }
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
