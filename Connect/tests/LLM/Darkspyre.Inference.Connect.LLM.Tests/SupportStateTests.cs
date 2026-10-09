using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

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
                Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string id, CancellationToken token = default) => Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string id, string credential, string version, CancellationToken token = default) => throw new InvalidOperationException("This fixture does not change credentials.");
    }
}
