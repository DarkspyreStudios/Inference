using Darkspyre.Inference.Common.Embedding;
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
            Inputs = [new TextEmbeddingInput("reference")]
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

    [Theory]
    [InlineData("test-key")]
    [InlineData("default-value")]
    public async Task ProtectedModelMetadataCannotBecomePortableIdentity(string protectedValue)
    {
        using var http = new HttpClient(new Handler($"model-{protectedValue}"));
        http.DefaultRequestHeaders.Add("X-Default", "default-value");
        await using var service = new LlmEmbeddingProviderService(new Source(), [new OpenAiEmbeddingClient(http)]);

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            service.EmbedAsync("connection", new() { ModelId = "requested-alias", Inputs = [new TextEmbeddingInput("reference")] }));

        Assert.Equal("invalid_embedding_response", failure.ProviderCode);
        Assert.DoesNotContain("test-key", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", failure.SanitizedResponseBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("default-value", failure.ToString() + failure.SanitizedResponseBody, StringComparison.Ordinal);
        Assert.Contains("Embedding model metadata cannot be published safely.", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddingDiagnosticsRedactEffectiveRequestSecrets(bool malformed)
    {
        const string invalidBody = "not-json test-key header-value default-value";
        using var handler = new Handler("actual-model", malformed ? invalidBody : null, diagnosticEchoes: true);
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("X-Default", "default-value");
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.OpenAiResponses,
            Endpoint = "https://provider.example.test/v1",
            Credential = new LlmCredential { ApiKey = "test-key" }.Serialize(),
            AdditionalHeaders = new Dictionary<string, string> { ["X-Trace"] = "header-value" }
        };
        var client = new OpenAiEmbeddingClient(http);
        var request = new LlmEmbeddingRequest { ModelId = "requested-alias", Inputs = [new TextEmbeddingInput("reference")] };
        string diagnostics;
        if (malformed)
        {
            LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => client.EmbedAsync(connection, request));
            Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
            Assert.Equal("invalid_embedding_response", failure.ProviderCode);
            Assert.Equal("request-[REDACTED]-[REDACTED]-[REDACTED]-1", failure.RequestId);
            JsonException parser = Assert.IsType<JsonException>(failure.InnerException);
            JsonException original = Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(invalidBody));
            Assert.Null(parser.Path);
            Assert.Equal(original.LineNumber, parser.LineNumber);
            Assert.Equal(original.BytePositionInLine, parser.BytePositionInLine);
            diagnostics = failure.ToString() + failure.SanitizedResponseBody;
        }
        else
        {
            LlmEmbeddingResult result = await client.EmbedAsync(connection, request);
            Assert.Equal("actual-model", result.ModelId);
            float[] vector = Assert.Single(result.Vectors);
            Assert.Equal(2, vector.Length);
            Assert.Equal(0.25f, vector[0]);
            Assert.Equal(0.75f, vector[1]);
            Assert.Equal(new LlmEmbeddingUsage(3, 3), result.Usage);
            Assert.Equal("request-[REDACTED]-[REDACTED]-[REDACTED]-1", result.Diagnostics!.RequestId);
            diagnostics = result.Diagnostics.ToString();
        }
        foreach (string secret in new[] { "test-key", "header-value", "default-value" })
            Assert.DoesNotContain(secret, diagnostics, StringComparison.Ordinal);
        Assert.Equal("header-value", handler.RequestTrace);
        Assert.Equal("default-value", handler.RequestDefaultTrace);
        Assert.Equal(1, handler.Calls);
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

    private sealed class Handler(string? model, string? rawBody = null, bool diagnosticEchoes = false) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string? RequestTrace { get; private set; }
        internal string? RequestDefaultTrace { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestTrace = request.Headers.TryGetValues("X-Trace", out var trace) ? trace.Single() : null;
            RequestDefaultTrace = request.Headers.TryGetValues("X-Default", out var defaults) ? defaults.Single() : null;
            var body = new JsonObject
            {
                ["object"] = "list",
                ["data"] = new JsonArray(new JsonObject { ["object"] = "embedding", ["index"] = 0, ["embedding"] = new JsonArray(0.25, 0.75) }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 3, ["total_tokens"] = 3 }
            };
            if (model is not null) body["model"] = model;
            if (diagnosticEchoes) body["trace"] = "test-key header-value default-value";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(rawBody ?? body.ToJsonString()) };
            if (diagnosticEchoes) response.Headers.Add("x-request-id", "request-test-key-header-value-default-value-1");
            return Task.FromResult(response);
        }
    }
}
