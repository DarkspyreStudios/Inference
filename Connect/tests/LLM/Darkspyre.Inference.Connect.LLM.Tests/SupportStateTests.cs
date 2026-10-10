using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
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
        Assert.Equal(InferenceSupport.Unknown, OpenAiModelProfiles.Get("unlisted-model").SupportsPromptCacheOptions);
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
                Content = new StringContent(request.RequestUri!.Host is "generativelanguage.googleapis.com" or "aiplatform.googleapis.com"
                    ? "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"{}\"}]},\"finishReason\":\"STOP\"}]}"
                    : request.RequestUri.Host is "api.x.ai" or "api.openai.com"
                    ? "{\"id\":\"response-fixture\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"{}\"}]}]}"
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
