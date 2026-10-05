using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class EmbeddingModelIdentityTests
{
    [Theory]
    [InlineData("actual-model")]
    [InlineData(null)]
    public async Task EmbeddingResultsRetainOnlyTheReportedModel(string? reportedModel)
    {
        using var http = new HttpClient(new Handler(reportedModel));
        await using var service = new LlmEmbeddingProviderService(new Source(), [new OpenAiEmbeddingClient(http)]);

        LlmEmbeddingResult result = await service.EmbedAsync("connection", new()
        {
            ModelId = "requested-alias",
            Inputs = ["reference"]
        });
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(result, ConnectLlmJson.LlmEmbeddingResult);
        LlmEmbeddingResult restored = JsonSerializer.Deserialize(json, ConnectLlmJson.LlmEmbeddingResult)!;

        Assert.Equal(reportedModel, result.ModelId);
        Assert.Equal(reportedModel, restored.ModelId);
        float[] vector = Assert.Single(restored.Vectors);
        Assert.Equal(2, vector.Length);
        Assert.Equal(0.25f, vector[0]);
        Assert.Equal(0.75f, vector[1]);
        Assert.Equal(new LlmEmbeddingUsage(3, 3), restored.Usage);
    }

    [Fact]
    public async Task ProtectedModelMetadataCannotBecomePortableIdentity()
    {
        using var http = new HttpClient(new Handler("model-test-key"));
        await using var service = new LlmEmbeddingProviderService(new Source(), [new OpenAiEmbeddingClient(http)]);

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            service.EmbedAsync("connection", new() { ModelId = "requested-alias", Inputs = ["reference"] }));

        Assert.Equal("invalid_embedding_response", failure.ProviderCode);
        Assert.DoesNotContain("test-key", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", failure.SanitizedResponseBody!, StringComparison.Ordinal);
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new()
            {
                Version = "1",
                Transport = LlmTransport.OpenAiResponses,
                Endpoint = "https://provider.example.test/v1",
                Credential = new LlmCredential { ApiKey = "test-key" }.Serialize()
            });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Handler(string? model) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = new JsonObject
            {
                ["data"] = new JsonArray(new JsonObject { ["index"] = 0, ["embedding"] = new JsonArray(0.25, 0.75) }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 3, ["total_tokens"] = 3 }
            };
            if (model is not null) body["model"] = model;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) });
        }
    }
}
