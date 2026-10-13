using Darkspyre.Inference.Common;
using Microsoft.Extensions.DependencyInjection;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class SelfHostEmbeddingTests
{
    [Theory]
    [InlineData("headers", "HTTP headers")]
    [InlineData("connection", "capability overrides")]
    [InlineData("model", "capability overrides")]
    public async Task UnsupportedConnectionOptionsRefuseBeforeEmbeddingEngineAcquisition(string option, string message)
    {
        var connection = new LlmConnection { Version = "1", Transport = LlmTransport.SelfHost, Endpoint = "engine" };
        connection = option switch
        {
            "headers" => connection with { AdditionalHeaders = new Dictionary<string, string> { ["X-Custom"] = "value" } },
            "connection" => connection with { CapabilityOverrides = new() },
            "model" => connection with { ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides> { ["model"] = new() } },
            _ => throw new ArgumentException("Unknown fixture option.", nameof(option))
        };
        await using var service = new LlmEmbeddingProviderService(new Source(connection), [new SelfHostEmbeddingClient(new Host())]);

        NotSupportedException refusal = await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.EmbedAsync("connection", new() { ModelId = "model", Inputs = [new TextEmbeddingInput("reference")] }));

        Assert.Contains(message, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectPreservesTypedInputsAndEngineProvenance()
    {
        var engine = new Embeddings();
        var services = new ServiceCollection();
        services.AddLlmConnect();
        services.AddLlmConnectSelfHost();
        services.AddSingleton<ILlmConnectionSource>(new Source(new() { Version = "1", Transport = LlmTransport.SelfHost, Endpoint = "engine" }));
        services.AddSingleton<ILlmSelfHost>(new Host(engine));
        await using var provider = services.BuildServiceProvider();
        var request = new LlmEmbeddingRequest
        {
            ModelId = "model", Kind = EmbeddingInputKind.Query, Truncate = true, Dimensions = 2,
            Inputs = [new TextEmbeddingInput("christmas", "title"), new MediaEmbeddingInput(new("image/png", new byte[] { 1, 2, 3 }))]
        };
        byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, ConnectLlmJson.LlmEmbeddingRequest);
        request = System.Text.Json.JsonSerializer.Deserialize(bytes, ConnectLlmJson.LlmEmbeddingRequest)!;
        LlmEmbeddingResult result = await provider.GetRequiredService<LlmEmbeddingProviderService>().EmbedAsync("connection", request);
        Assert.Equal(request.Inputs, engine.Request!.Inputs);
        Assert.Equal(EmbeddingInputKind.Query, engine.Request.Kind);
        Assert.True(engine.Request.Truncate);
        Assert.Equal(2, engine.Request.Dimensions);
        Assert.Equal(new float[] { 1, 0 }, result.Vectors[0]);
        Assert.Equal(new float[] { 0, 1 }, result.Vectors[1]);
        Assert.Equal("actual", result.ModelId);
        Assert.Equal("fingerprint", result.ModelFingerprint);
        Assert.Equal(new LlmEmbeddingUsage(7, 7), result.Usage);
        Assert.Equal([1], result.TruncatedInputs);
    }

    [Fact]
    public async Task TextOnlyRemoteProviderRejectsImagesBeforeHttp()
    {
        using var http = new HttpClient(new RefuseHttp());
        await using var service = new LlmEmbeddingProviderService(new Source(new()
        { Version = "1", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://example.invalid" }), [new OpenAiEmbeddingClient(http)]);
        var error = await Assert.ThrowsAsync<InferenceException>(() => service.EmbedAsync("connection", new()
        { ModelId = "model", Inputs = [new MediaEmbeddingInput(new("image/png", new byte[] { 1 }))] }));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
    }

    private sealed class RefuseHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("No request may reach HTTP.");
    }

    private sealed class Embeddings : IEmbeddingInference
    {
        public EmbeddingRequest? Request { get; private set; }
        public InferenceImplementationDescriptor Descriptor { get; } = new("engine", "Engine", InferenceImplementationKind.Tensor, InferenceTask.Embedding);
        public Task<EmbeddingCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new EmbeddingResult
            {
                Vectors = [new float[] { 1, 0 }, new float[] { 0, 1 }], Dimensions = 2, ModelFingerprint = "fingerprint",
                TruncatedInputs = [1], Usage = new(7), Provenance = new() { ImplementationId = "engine", ModelId = "actual" }
            });
        }
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(connection);

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("This refusal must not change credentials.");
    }

    private sealed class Host(IEmbeddingInference? embeddings = null) : ILlmSelfHost
    {
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This refusal must not enumerate engines.");

        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This refusal must not acquire a chat engine.");

        public Task<IEmbeddingInference> GetEmbeddingInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            embeddings is not null ? Task.FromResult(embeddings) : throw new InvalidOperationException("Unsupported connection options must refuse before engine acquisition.");

        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) =>
            throw new InvalidOperationException("This refusal must not inspect a chat engine.");
    }
}
