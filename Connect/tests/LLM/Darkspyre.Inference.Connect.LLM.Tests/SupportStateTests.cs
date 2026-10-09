using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.LLM.Vertex;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class SupportStateTests
{
    [Theory]
    [InlineData(InferenceSupport.Unknown)]
    [InlineData(InferenceSupport.Supported)]
    [InlineData(InferenceSupport.Unsupported)]
    public void SupportEvidenceRoundTripsThroughOwnedMetadata(InferenceSupport support)
    {
        var common = new LlmCapabilities
        {
            SupportsJsonSchema = support,
            Tools = new() { SupportsParallelCalls = support },
            PromptCache = new() { Automatic = support, AcceptsHints = support }
        };
        string commonJson = JsonSerializer.Serialize(common, InferenceJson.LlmCapabilities);
        using var document = JsonDocument.Parse(commonJson);
        Assert.Equal(support.ToString(), document.RootElement.GetProperty("SupportsJsonSchema").GetString());
        var restoredCommon = JsonSerializer.Deserialize(commonJson, InferenceJson.LlmCapabilities)!;
        Assert.Equal(support, restoredCommon.SupportsJsonSchema);
        Assert.Equal(support, restoredCommon.Tools.SupportsParallelCalls);
        Assert.Equal(support, restoredCommon.PromptCache.Automatic);
        Assert.Equal(support, restoredCommon.PromptCache.AcceptsHints);

        var connection = Connection() with { CapabilityOverrides = new() { SupportsJsonSchema = support } };
        string connectionJson = JsonSerializer.Serialize(connection, ConnectLlmJson.Connection);
        Assert.Equal(support, JsonSerializer.Deserialize(connectionJson, ConnectLlmJson.Connection)!.CapabilityOverrides!.SupportsJsonSchema);

        var files = new LlmFileCapabilities { Upload = support, PublicUrls = support };
        string filesJson = JsonSerializer.Serialize(files, ConnectLlmJson.LlmFileCapabilities);
        var restoredFiles = JsonSerializer.Deserialize(filesJson, ConnectLlmJson.LlmFileCapabilities)!;
        Assert.Equal(support, restoredFiles.Upload);
        Assert.Equal(support, restoredFiles.PublicUrls);
        var skills = new XaiSkillCapabilities { ResourceOperations = support, GenerationAttachment = support };
        string skillsJson = JsonSerializer.Serialize(skills, ConnectLlmJson.XaiSkillCapabilities);
        Assert.Equal(support, JsonSerializer.Deserialize(skillsJson, ConnectLlmJson.XaiSkillCapabilities)!.GenerationAttachment);
    }

    [Fact]
    public void NullInheritsButExplicitUnknownAndUnsupportedRemainDistinct()
    {
        var capabilities = new LlmProviderCapabilities { SupportsJsonSchema = InferenceSupport.Supported };
        Assert.Equal(InferenceSupport.Supported, capabilities.Apply(new()).SupportsJsonSchema);
        Assert.Equal(InferenceSupport.Unknown, capabilities.Apply(new() { SupportsJsonSchema = InferenceSupport.Unknown }).SupportsJsonSchema);
        Assert.Equal(InferenceSupport.Unsupported, capabilities.Apply(new() { SupportsJsonSchema = InferenceSupport.Unsupported }).SupportsJsonSchema);
        Assert.Equal(InferenceSupport.Unknown, new LlmProviderCapabilities().Apply(null).SupportsJsonSchema);
    }

    [Fact]
    public void MissingModelFactsDoNotBecomeNegativeEvidence()
    {
        Assert.Equal(InferenceSupport.Unknown, XaiModelProfiles.Get(Connection(), "unlisted-model").AllowsSampling);
        Assert.Equal(InferenceSupport.Unknown, XaiModelProfiles.Get(Connection(), "unlisted-model").AcceptsImages);
        Assert.Equal(InferenceSupport.Unknown, AnthropicModelProfiles.Resolve("unlisted-model", null).AllowsSampling);
        Assert.Equal(InferenceSupport.Unknown, OpenAiModelProfiles.Get("unlisted-model").SupportsPromptCacheOptions);
    }

    [Fact]
    public void KnownAnthropicSamplingRetainsAnExplicitUnknownOverride()
    {
        using var http = new HttpClient(new SchemaHandler());
        var client = new AnthropicMessagesLlmClient(http);
        var connection = Connection() with
        {
            Transport = LlmTransport.AnthropicMessages,
            Endpoint = "https://api.anthropic.com/v1",
            CapabilityOverrides = new() { SupportsTemperature = InferenceSupport.Unknown }
        };
        Assert.Equal(InferenceSupport.Unknown, client.GetCapabilities(connection, "claude-sonnet-4-5").SupportsTemperature);
    }

    [Theory]
    [InlineData(null, InferenceSupport.Unknown)]
    [InlineData("{}", InferenceSupport.Unsupported)]
    [InlineData("{\"topK\":40}", InferenceSupport.Supported)]
    public async Task GeminiMissingCatalogPermitsTopKAndUnreviewedSchemaWithoutChangingEither(
        string? entry, InferenceSupport expected)
    {
        var handler = new SchemaHandler();
        using var http = new HttpClient(handler);
        var client = new GeminiApiLlmClient(http);
        const string model = "gemini-unlisted";
        var connection = Connection() with
        {
            Transport = LlmTransport.GoogleGeminiApi,
            Endpoint = GeminiApiLlmClient.DefaultEndpoint,
            Catalog = entry is null ? null : [new(model, model) { CatalogEntry = JsonElement.Parse(entry) }],
            CapabilityOverrides = new() { SupportsStreaming = InferenceSupport.Unsupported }
        };
        Assert.Equal(expected, client.GetCapabilities(connection, model).SupportsTopK);
        Assert.Equal(InferenceSupport.Unknown, client.GetCapabilities(connection, model).SupportsJsonSchema);
        var request = new LlmChatRequest
        {
            ModelId = model, Messages = [new("user", "Return an object.")], TopK = 7,
            StructuredOutput = new() { Name = "answer", Schema = JsonElement.Parse("{\"type\":\"object\"}") }
        };
        if (expected == InferenceSupport.Unsupported)
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await foreach (var _ in client.StreamAsync(connection, request)) { }
            });
            Assert.Null(handler.Payload);
            return;
        }
        await foreach (var _ in client.StreamAsync(connection, request)) { }
        using var sent = JsonDocument.Parse(handler.Payload!);
        JsonElement config = sent.RootElement.GetProperty("generationConfig");
        Assert.Equal(7, config.GetProperty("topK").GetInt32());
        Assert.Equal(request.StructuredOutput.Schema.GetRawText(),
            config.GetProperty("responseFormat").GetProperty("text").GetProperty("schema").GetRawText());
    }

    [Theory]
    [InlineData(LlmTransport.GoogleGeminiApi, null)]
    [InlineData(LlmTransport.GoogleGeminiApi, InferenceSupport.Unknown)]
    [InlineData(LlmTransport.GoogleVertexAi, null)]
    [InlineData(LlmTransport.GoogleVertexAi, InferenceSupport.Unknown)]
    [InlineData(LlmTransport.AppleFoundationModels, null)]
    [InlineData(LlmTransport.AppleFoundationModels, InferenceSupport.Unknown)]
    public void CapabilityLookupDoesNotMistakeUnknownForEnablingAMissingOperation(
        LlmTransport transport, InferenceSupport? jsonObjectOverride)
    {
        using var http = new HttpClient(new SchemaHandler());
        var connection = Connection() with
        {
            Transport = transport,
            Endpoint = transport switch
            {
                LlmTransport.GoogleGeminiApi => GeminiApiLlmClient.DefaultEndpoint,
                LlmTransport.GoogleVertexAi => VertexEndpoints.BaseEndpoint,
                _ => "apple-fixture"
            },
            Credential = transport == LlmTransport.AppleFoundationModels ? null : Connection().Credential,
            Vertex = transport == LlmTransport.GoogleVertexAi ? new() { AuthenticationMode = LlmGoogleAuthenticationMode.ApiKey } : null,
            CapabilityOverrides = new() { SupportsJsonObject = jsonObjectOverride }
        };
        LlmProviderCapabilities capabilities = transport switch
        {
            LlmTransport.GoogleGeminiApi => new GeminiApiLlmClient(http).GetCapabilities(connection),
            LlmTransport.GoogleVertexAi => VertexLlmClient.Capabilities(connection, "google/gemini-3-flash-preview"),
            _ => new AppleFoundationModelsClient(http, new AppleProcessRunner()).GetCapabilities(connection)
        };
        Assert.Equal(jsonObjectOverride ?? InferenceSupport.Unsupported, capabilities.SupportsJsonObject);
    }

    [Theory]
    [InlineData(LlmOperationSupport.Unknown)]
    [InlineData(LlmOperationSupport.Supported)]
    [InlineData(LlmOperationSupport.Unsupported)]
    public void BackgroundWorkAdmissionDistinguishesUnknownFromUnsupported(LlmOperationSupport support)
    {
        var capabilities = NativeAgentContract.AgentCore with { ResourceObservation = support };
        var options = new LlmAgentSessionOptions
        {
            ModelId = "default",
            Runtime = new()
            {
                WorkingDirectory = Environment.CurrentDirectory,
                Policy = new LlmAgentHostRuntimePolicy { AllowRuntimeBackgroundWork = true }
            }
        };
        if (support == LlmOperationSupport.Unsupported)
        {
            Assert.Throws<NotSupportedException>(() => NativeAgentContract.ValidateStart(options, capabilities));
            return;
        }
        Assert.Same(options.Runtime, NativeAgentContract.ValidateStart(options, capabilities));
        Assert.Equal(support, capabilities.ResourceObservation);
    }

    [Theory]
    [InlineData(LlmToolChoiceMode.Auto, true)]
    [InlineData(LlmToolChoiceMode.Auto, false)]
    [InlineData(LlmToolChoiceMode.Required, true)]
    public async Task NonRequiredToolControlsReachTheExistingWriterWithoutToolDefinitions(
        LlmToolChoiceMode mode, bool parallel)
    {
        var handler = new SchemaHandler();
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        var connection = Connection() with { CapabilityOverrides = new() { SupportsStreaming = InferenceSupport.Unsupported } };
        var request = new LlmChatRequest
        {
            ModelId = "unlisted-model", Messages = [new("user", "Hello")],
            ToolChoice = new() { Mode = mode }, AllowParallelToolCalls = parallel
        };
        if (mode == LlmToolChoiceMode.Required)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                await foreach (var _ in client.StreamAsync(connection, request)) { }
            });
            Assert.Null(handler.Payload);
            return;
        }
        await foreach (var _ in client.StreamAsync(connection, request)) { }
        using var sent = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("auto", sent.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal(parallel, sent.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(sent.RootElement.TryGetProperty("tools", out _));
    }

    [Theory]
    [InlineData(InferenceSupport.Unknown)]
    [InlineData(InferenceSupport.Unsupported)]
    public async Task SchemaAdmissionDistinguishesAnAttemptFromAnExplicitRestriction(InferenceSupport support)
    {
        var handler = new SchemaHandler();
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        var connection = Connection() with
        {
            CapabilityOverrides = new()
            {
                SupportsStreaming = InferenceSupport.Unsupported,
                SupportsJsonSchema = support,
                SupportsJsonObject = InferenceSupport.Unsupported
            }
        };
        var request = new LlmChatRequest
        {
            ModelId = "unlisted-model",
            Messages = [new("user", "Return the value.")],
            StructuredOutput = new() { Name = "answer", Schema = JsonElement.Parse("{\"type\":\"object\"}") }
        };
        if (support == InferenceSupport.Unsupported)
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await foreach (var _ in client.StreamAsync(connection, request)) { }
            });
            Assert.Null(handler.Payload);
            return;
        }

        var events = new List<LlmStreamEvent>();
        await foreach (var item in client.StreamAsync(connection, request)) events.Add(item);
        Assert.Equal("{}", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        using var sent = JsonDocument.Parse(handler.Payload!);
        JsonElement format = sent.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal(request.StructuredOutput.Schema.GetRawText(), format.GetProperty("json_schema").GetProperty("schema").GetRawText());
    }

    [Fact]
    public async Task CommonBindingPreservesUnknownSupport()
    {
        using var http = new HttpClient(new SchemaHandler());
        var connection = Connection() with { CapabilityOverrides = new() { SupportsJsonSchema = InferenceSupport.Unknown } };
        await using var service = new LlmProviderService(new Source(connection), [new OpenAiCompatibleChatLlmClient(http)]);
        var portable = await service.Bind("connection").GetCapabilitiesAsync();
        Assert.Equal(InferenceSupport.Unknown, portable.SupportsJsonSchema);
    }

    private static LlmConnection Connection() => new()
    {
        Version = "1",
        Transport = LlmTransport.OpenAiCompatibleChat,
        Endpoint = "https://compatible.example.test/v1",
        Credential = new LlmCredential { ApiKey = "fixture-key" }.Serialize()
    };

    private sealed class SchemaHandler : HttpMessageHandler
    {
        public string? Payload { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Payload = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.Host == "generativelanguage.googleapis.com"
                    ? "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"{}\"}]},\"finishReason\":\"STOP\"}]}"
                    : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string id, CancellationToken token = default) => Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string id, string credential, string version, CancellationToken token = default) => throw new InvalidOperationException("This fixture does not change credentials.");
    }
}
