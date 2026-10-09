using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AnthropicAdmissionTests
{
    [Theory]
    [InlineData("anthropic/claude-opus-4-6", LlmReasoningLevel.ExtraHigh, "high", false)]
    [InlineData("anthropic/claude-sonnet-5-5", LlmReasoningLevel.None, "low", false)]
    [InlineData("anthropic/claude-unfamiliar", LlmReasoningLevel.Maximum, "max", false)]
    [InlineData("anthropic/claude-unfamiliar", LlmReasoningLevel.Maximum, "low", true)]
    public async Task VertexClaudeMapsEffortBeforeOuterAdmissionAndMessagesProjection(
        string model, LlmReasoningLevel effort, string expected, bool narrow)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new VertexLlmClient(http, new VertexAuthenticationBoundary());
        var connection = new LlmConnection
        {
            Version = "1", Transport = LlmTransport.GoogleVertexAi, Endpoint = "https://aiplatform.googleapis.com",
            Vertex = new() { ProjectId = "project", Location = "global", AuthenticationMode = LlmGoogleAuthenticationMode.ApplicationDefault },
            CapabilityOverrides = new()
            {
                SupportsStreaming = InferenceSupport.Unsupported,
                ReasoningLevels = narrow ? LlmReasoningEffort.Levels(LlmReasoningLevel.Low) : null
            }
        };
        var request = new LlmChatRequest
        { ModelId = model, Messages = [new("user", "answer")], ReasoningLevel = effort };

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(expected, sent.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("vertex-2023-10-16", sent.RootElement.GetProperty("anthropic_version").GetString());
        Assert.False(sent.RootElement.TryGetProperty("model", out _));
        Assert.Equal(effort, request.ReasoningLevel);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("claude-opus-4-6", LlmReasoningLevel.ExtraHigh, "adaptive", "high", null)]
    [InlineData("claude-opus-4-5", LlmReasoningLevel.Maximum, null, "high", null)]
    [InlineData("claude-haiku-4-5", LlmReasoningLevel.High, "enabled", null, 16384)]
    [InlineData("claude-haiku-4-5", LlmReasoningLevel.Minimal, null, null, null)]
    [InlineData("claude-sonnet-5-5", LlmReasoningLevel.None, "adaptive", "low", null)]
    [InlineData("unfamiliar-claude-model", LlmReasoningLevel.High, null, "high", null)]
    [InlineData("unfamiliar-claude-model", LlmReasoningLevel.None, "disabled", null, null)]
    public async Task MessagesAdapterMapsEffortIntoItsNativeFormWithoutChangingCallerIntent(
        string model, LlmReasoningLevel effort, string? thinkingType, string? effortWord, int? budget)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var request = new LlmChatRequest
        { ModelId = model, Messages = [new("user", "answer")], ReasoningLevel = effort, MaxOutputTokens = 123 };

        await foreach (LlmStreamEvent _ in client.StreamAsync(Connection(), request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement payload = sent.RootElement;
        Assert.Equal(model, payload.GetProperty("model").GetString());
        Assert.Equal(thinkingType is not null, payload.TryGetProperty("thinking", out JsonElement thinking));
        if (thinkingType is not null) Assert.Equal(thinkingType, thinking.GetProperty("type").GetString());
        bool hasEffort = payload.TryGetProperty("output_config", out JsonElement config)
            && config.TryGetProperty("effort", out _);
        Assert.Equal(effortWord is not null, hasEffort);
        if (effortWord is not null) Assert.Equal(effortWord, config.GetProperty("effort").GetString());
        if (budget is not null) Assert.Equal(budget, thinking.GetProperty("budget_tokens").GetInt32());
        Assert.Equal(123 + (budget ?? 0), payload.GetProperty("max_tokens").GetInt32());
        Assert.Equal(effort, request.ReasoningLevel);
        Assert.Equal(123, request.MaxOutputTokens);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MessagesCatalogSeparatesMissingEffortFactsFromAnExplicitNoReasoningFact(bool negative)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        LlmConnection connection = Connection() with
        {
            CapabilityOverrides = Connection().CapabilityOverrides! with
            { ReasoningLevels = negative ? LlmReasoningEffort.Levels(LlmReasoningLevel.Low) : null },
            Catalog = [new("unfamiliar-claude-model", "Model")
            { CatalogEntry = JsonElement.Parse(negative
                ? """{"capabilities":{"thinking":{"supported":false},"effort":{"supported":false}}}"""
                : """{"id":"unfamiliar-claude-model"}""") }]
        };
        var request = new LlmChatRequest
        { ModelId = "unfamiliar-claude-model", Messages = [new("user", "answer")], ReasoningLevel = LlmReasoningLevel.Maximum };
        Assert.Equal(negative ? InferenceSupport.Unsupported : InferenceSupport.Unknown,
            client.GetCapabilities(connection, request.ModelId).SupportsReasoning);

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        bool hasEffort = sent.RootElement.TryGetProperty("output_config", out JsonElement config)
            && config.TryGetProperty("effort", out _);
        Assert.Equal(!negative, hasEffort);
        if (!negative) Assert.Equal("max", config.GetProperty("effort").GetString());
        Assert.False(sent.RootElement.TryGetProperty("thinking", out _));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MessagesReasoningUsesTheEffectiveCallerNativeChoices()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        LlmConnection connection = Connection() with
        { CapabilityOverrides = Connection().CapabilityOverrides! with { ReasoningLevels = LlmReasoningEffort.Levels(LlmReasoningLevel.Low) } };

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, new()
        { ModelId = "claude-opus-5-5", Messages = [new("user", "answer")], ReasoningLevel = LlmReasoningLevel.Maximum })) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal("low", sent.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void UnknownAnnotationsDoNotDisableKnownConstraintValidation()
    {
        var output = new LlmStructuredOutputRequest
        {
            Name = "answer",
            Schema = JsonElement.Parse("""{"type":"object","properties":{"value":{"type":"integer","$comment":"retained"}},"required":["value"],"additionalProperties":false}""")
        };

        LlmStructuredOutputValidation result = StructuredOutput.Validate("""{"value":"wrong-type"}""",
            output, LlmStructuredOutputEnforcement.ProviderJsonSchema);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task UnevaluableSchemaRetainsTheProviderAnswerAndReportsTheEvaluatorFailure()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        JsonElement schema = JsonElement.Parse("""
            {"type":"object","properties":{"value":{"type":"string","x-provider-note":{"opaque":true}}},"required":["value"],"additionalProperties":false}
            """);
        var request = new LlmChatRequest
        {
            ModelId = "unfamiliar-claude-model", Messages = [new("user", "answer")],
            StructuredOutput = new() { Name = "answer", Schema = schema }
        };
        var events = new List<LlmStreamEvent>();

        await foreach (LlmStreamEvent item in client.StreamAsync(Connection(), request)) events.Add(item);

        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal("""{"value":"answer"}""", result.Text);
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        LlmStructuredOutputValidation validation = Assert.IsType<LlmStructuredOutputValidation>(result.StructuredOutput);
        Assert.False(validation.WasEvaluated);
        Assert.False(validation.IsValid);
        Assert.Contains("could not be evaluated", Assert.Single(validation.Errors), StringComparison.Ordinal);
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, validation.Enforcement);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.True(JsonElement.DeepEquals(schema,
            sent.RootElement.GetProperty("output_config").GetProperty("format").GetProperty("schema")));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FirstPartyDeprecatedSamplingDefaultsReachTheProviderWithDefaultThinking()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var request = new LlmChatRequest
        { ModelId = "claude-opus-5-5", Messages = [new("user", "answer")], Temperature = 1, TopP = 0.99 };
        Assert.Equal(InferenceSupport.Unknown, client.GetCapabilities(Connection(), request.ModelId).SupportsTemperature);

        await foreach (LlmStreamEvent _ in client.StreamAsync(Connection(), request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(1, sent.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.99, sent.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal("adaptive", sent.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(0.7, null, null, false)]
    [InlineData(null, 0.98, null, false)]
    [InlineData(null, null, 1, false)]
    [InlineData(1.0, null, null, true)]
    public async Task KnownSamplingRestrictionsAndExplicitCallerDenialStillRejectBeforeSending(
        double? temperature, double? topP, int? topK, bool callerDenial)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        LlmConnection connection = Connection();
        if (callerDenial) connection = connection with
        { CapabilityOverrides = connection.CapabilityOverrides! with { SupportsTemperature = InferenceSupport.Unsupported } };
        var request = new LlmChatRequest
        { ModelId = "claude-opus-5-5", Messages = [new("user", "answer")], Temperature = temperature, TopP = topP, TopK = topK };

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        { await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { } });

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void UnqualifiedCodeExecutionDefinitionCanReplayItsProgrammaticCall()
    {
        var payload = JsonNode.Parse("""
            {"tools":[{"type":"code_execution_unqualified","name":"code_execution"}],"container":"retained-container",
             "messages":[{"role":"assistant","content":[
               {"type":"server_tool_use","id":"run","name":"code_execution"},
               {"type":"tool_use","id":"call","name":"echo","caller":{"type":"code_execution_20260120","tool_id":"run"}}]},
               {"role":"user","content":[{"type":"tool_result","tool_use_id":"call","content":"answer"}]}]}
            """)!.AsObject();

        AnthropicToolContinuation.Validate(payload);

        Assert.Equal("code_execution_unqualified", (string?)payload["tools"]![0]!["type"]);
        Assert.Equal("retained-container", (string?)payload["container"]);
    }

    [Theory]
    [InlineData("code_execution_unqualified", "code_execution", "allowed_callers", "[\"code_execution_20260120\"]")]
    [InlineData("tool_search_unqualified", "tool_search_tool_bm25", "defer_loading", "true")]
    [InlineData("unfamiliar_server_revision", "unfamiliar_server", null, null)]
    public async Task UnqualifiedServerDescriptorsAndExistingClientMetadataReachTheProvider(
        string type, string name, string? metadataName, string? metadataValue)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var native = new System.Text.Json.Nodes.JsonObject { ["type"] = type, ["name"] = name, ["opaque"] = "retained" };
        var options = new System.Text.Json.Nodes.JsonObject
        {
            ["server_tools"] = new System.Text.Json.Nodes.JsonArray(native),
            ["client_tools"] = metadataName is null ? new System.Text.Json.Nodes.JsonObject()
                : new System.Text.Json.Nodes.JsonObject
                { ["echo"] = new System.Text.Json.Nodes.JsonObject { [metadataName] = System.Text.Json.Nodes.JsonNode.Parse(metadataValue!) } }
        };
        var request = new LlmChatRequest
        {
            ModelId = "unfamiliar-claude-model", Messages = [new("user", "answer")],
            Tools = [new LlmToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("""{"type":"object"}""") }],
            ProviderOptions = new Dictionary<string, JsonElement> { ["anthropic_tools"] = JsonElement.Parse(options.ToJsonString()) }
        };

        await foreach (LlmStreamEvent _ in client.StreamAsync(Connection(), request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement tools = sent.RootElement.GetProperty("tools");
        Assert.True(JsonElement.DeepEquals(JsonElement.Parse(native.ToJsonString()), tools[1]));
        if (metadataName is not null)
            Assert.True(JsonElement.DeepEquals(JsonElement.Parse(metadataValue!), tools[0].GetProperty(metadataName)));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task KnownClientExecutedNativeToolCannotUseTheServerLane()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var request = new LlmChatRequest
        {
            ModelId = "unfamiliar-claude-model", Messages = [new("user", "answer")],
            ProviderOptions = new Dictionary<string, JsonElement>
            { ["anthropic_tools"] = JsonElement.Parse("""{"server_tools":[{"type":"bash_20250124","name":"bash"}]}""") }
        };

        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        { await foreach (LlmStreamEvent _ in client.StreamAsync(Connection(), request)) { } });

        Assert.Contains("caller mapping", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownSchemaAnnotationsReachNativeOutputAndStrictToolsUnchanged(bool strictTool)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        JsonObject original = JsonNode.Parse("""
            {"type":"object","properties":{"value":{"type":"string","$comment":"retained","examples":["answer"]}},"required":["value"],"additionalProperties":false}
            """)!.AsObject();
        if (strictTool) original["properties"]!["value"]!["x-provider-note"] = new JsonObject { ["opaque"] = true };
        JsonElement schema = JsonElement.Parse(original.ToJsonString());
        var request = new LlmChatRequest
        {
            ModelId = "unfamiliar-claude-model",
            Messages = [new("user", "answer")],
            Tools = strictTool ? [new LlmToolDefinition { Name = "echo", Strict = true, InputSchema = schema }] : [],
            StructuredOutput = strictTool ? null : new() { Name = "answer", Schema = schema }
        };
        LlmConnection connection = Connection();
        Assert.Equal(InferenceSupport.Unknown, client.GetCapabilities(connection, request.ModelId).SupportsJsonSchema);

        await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }

        using var sent = JsonDocument.Parse(handler.Body!);
        JsonElement emitted = strictTool ? sent.RootElement.GetProperty("tools")[0].GetProperty("input_schema")
            : sent.RootElement.GetProperty("output_config").GetProperty("format").GetProperty("schema");
        Assert.True(JsonElement.DeepEquals(schema, emitted));
        if (strictTool) Assert.True(sent.RootElement.GetProperty("tools")[0].GetProperty("strict").GetBoolean());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DocumentedUnsupportedStrictConstraintStillRejectsBeforeSending()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        var request = new LlmChatRequest
        {
            ModelId = "unfamiliar-claude-model", Messages = [new("user", "answer")],
            Tools = [new LlmToolDefinition
            {
                Name = "echo", Strict = true,
                InputSchema = JsonElement.Parse("""{"type":"object","properties":{"value":{"type":"number","minimum":1}},"additionalProperties":false}""")
            }]
        };

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        { await foreach (LlmStreamEvent _ in client.StreamAsync(Connection(), request)) { } });

        Assert.Equal(0, handler.Calls);
    }

    private static LlmConnection Connection() => new()
    {
        Version = "1", Transport = LlmTransport.AnthropicMessages, Endpoint = "https://anthropic.example.test/v1",
        Credential = new LlmCredential { ApiKey = "test-key" }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = InferenceSupport.Unsupported }
    };

    private sealed class VertexAuthenticationBoundary : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(new VertexAccess("test-token", "test-scope"));
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal string? Body { get; private set; }
        internal int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"response","type":"message","role":"assistant","model":"unfamiliar-claude-model","content":[{"type":"text","text":"{\"value\":\"answer\"}"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
                    """, Encoding.UTF8, "application/json")
            };
        }
    }
}
