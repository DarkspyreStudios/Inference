using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class EmbeddingContractTests
{
    private static LlmConnection Connection => new()
    {
        Version = "v1",
        Transport = LlmTransport.OpenAiResponses,
        Endpoint = "https://embedding.invalid/v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "protected-token" } }.Serialize()
    };

    private static LlmEmbeddingRequest Request => new() { ModelId = "manually-entered-model", Inputs = ["one", "two"] };

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task DimensionsUseTypedWireAndUnknownModelRemainsAllowed(int? dimensions)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OpenAiEmbeddingClient(http);
        LlmEmbeddingResult result = await client.EmbedAsync(Connection, Request with { Dimensions = dimensions });
        using JsonDocument sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(dimensions.HasValue, sent.RootElement.TryGetProperty("dimensions", out JsonElement value));
        if (dimensions.HasValue) Assert.Equal(dimensions.Value, value.GetInt32());
        Assert.Equal([1f, 2f], result.Vectors[0]);
        Assert.Equal([3f, 4f], result.Vectors[1]);
        Assert.Null(result.Usage!.InputTokens);
        Assert.Equal(0, result.Usage.TotalTokens);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, null)]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    public async Task RawDimensionsRefuseAtBothScopesBeforeRefreshOrHttp(bool connectionScope, int? dimensions)
    {
        var options = new Dictionary<string, JsonElement> { ["dimensions"] = JsonElement.Parse("2") };
        var connection = connectionScope ? Connection with { ProviderOptions = options } : Connection;
        var request = Request with { Dimensions = dimensions, ProviderOptions = connectionScope ? new Dictionary<string, JsonElement>() : options };
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OpenAiEmbeddingClient(http);
        var auth = new Authenticator();
        await using var facade = new LlmEmbeddingProviderService(new Source(connection), [client], [auth]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.EmbedAsync("connection", request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(connection, request));
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveDimensionsRefuseBeforeRefreshOrHttp(int dimensions)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OpenAiEmbeddingClient(http);
        var auth = new Authenticator();
        await using var facade = new LlmEmbeddingProviderService(new Source(Connection), [client], [auth]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => facade.EmbedAsync("connection", Request with { Dimensions = dimensions }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.EmbedAsync(Connection, Request with { Dimensions = dimensions }));
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("model", true)]
    [InlineData("model", false)]
    [InlineData("input", true)]
    [InlineData("input", false)]
    public async Task ExistingReservedFieldsAlsoRefuseBeforeRefresh(string field, bool connectionScope)
    {
        var options = new Dictionary<string, JsonElement> { [field] = JsonElement.Parse("null") };
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OpenAiEmbeddingClient(http);
        var auth = new Authenticator();
        var connection = connectionScope ? Connection with { ProviderOptions = options } : Connection;
        var request = connectionScope ? Request : Request with { ProviderOptions = options };
        await using var facade = new LlmEmbeddingProviderService(new Source(connection), [client], [auth]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.EmbedAsync("connection", request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(connection, request));
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task UnsupportedDirectTransportAndMissingModelRefuseBeforeHttp()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OpenAiEmbeddingClient(http);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.EmbedAsync(Connection with { Transport = LlmTransport.OpenAiCodex }, Request));
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedAsync(Connection, Request with { ModelId = " " }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RequestAndConnectionSnapshotSurviveMutationAndDisposedJsonDuringRefresh()
    {
        using var document = JsonDocument.Parse("{\"original\":true}");
        var inputs = new List<string> { "one", "two" };
        var options = new Dictionary<string, JsonElement> { ["custom"] = document.RootElement };
        var connectionOptions = new Dictionary<string, JsonElement> { ["connection_custom"] = document.RootElement };
        var headers = new Dictionary<string, string> { ["X-Test"] = "original" };
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var auth = new Authenticator { Block = true };
        await using var facade = new LlmEmbeddingProviderService(new Source(Connection with
        { ProviderOptions = connectionOptions, AdditionalHeaders = headers }), [new OpenAiEmbeddingClient(http)], [auth]);
        Task<LlmEmbeddingResult> pending = facade.EmbedAsync("connection", Request with { Inputs = inputs, ProviderOptions = options, Dimensions = 2 });
        try
        {
            await auth.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            inputs.Clear(); options.Clear(); connectionOptions["dimensions"] = JsonElement.Parse("7"); headers["X-Test"] = "changed";
            document.Dispose();
            auth.Release.TrySetResult();
            LlmEmbeddingResult result = await pending;
            Assert.Equal(2, result.Vectors.Count);
            using JsonDocument sent = JsonDocument.Parse(handler.Body!);
            Assert.Equal("one", sent.RootElement.GetProperty("input")[0].GetString());
            Assert.True(sent.RootElement.GetProperty("custom").GetProperty("original").GetBoolean());
            Assert.True(sent.RootElement.GetProperty("connection_custom").GetProperty("original").GetBoolean());
            Assert.Equal(2, sent.RootElement.GetProperty("dimensions").GetInt32());
            Assert.Equal("original", handler.Header);
        }
        finally
        {
            auth.Release.TrySetResult();
            await pending;
        }
    }

    [Theory]
    [InlineData("[{\"index\":0,\"embedding\":[1,2]},{\"index\":0,\"embedding\":[3,4]}]")]
    [InlineData("[{\"index\":0,\"embedding\":[1,2]},{\"index\":2,\"embedding\":[3,4]}]")]
    [InlineData("[{\"index\":-1,\"embedding\":[1,2]},{\"index\":1,\"embedding\":[3,4]}]")]
    [InlineData("[{\"index\":0,\"embedding\":[1,2]}]")]
    [InlineData("[{\"index\":0,\"embedding\":[1]},{\"index\":1,\"embedding\":[2]}]")]
    [InlineData("[{\"index\":\"0\",\"embedding\":[1,2]},{\"index\":1,\"embedding\":[3,4]}]")]
    [InlineData("[{\"index\":0,\"embedding\":[1,\"secret\"]},{\"index\":1,\"embedding\":[3,4]}]")]
    [InlineData("[{\"index\":0,\"embedding\":[1,1e1000]},{\"index\":1,\"embedding\":[3,4]}]")]
    [InlineData("[null,null]")]
    public async Task InvalidIndexesAndDimensionsAreProtocolFailures(string data)
    {
        using var handler = new Handler { Response = "{\"data\":" + data + "}" };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => new OpenAiEmbeddingClient(http)
            .EmbedAsync(Connection, Request with { Dimensions = 2 }));
        Assert.Equal("invalid_embedding_response", error.ProviderCode);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnedMetadataPreservesDimensionsOptionsVectorsAndUnknownUsage()
    {
        var original = Request with { Dimensions = 2, ProviderOptions = new Dictionary<string, JsonElement> { ["custom"] = JsonElement.Parse("{\"x\":true}") } };
        var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(original, ConnectLlmJson.LlmEmbeddingRequest), ConnectLlmJson.LlmEmbeddingRequest)!;
        Assert.Equal(2, request.Dimensions);
        Assert.Equal(original.Inputs, request.Inputs);
        Assert.True(request.ProviderOptions["custom"].GetProperty("x").GetBoolean());
        var usage = new LlmEmbeddingUsage(null, 0);
        Assert.Equal(usage, JsonSerializer.Deserialize(JsonSerializer.Serialize(usage, ConnectLlmJson.LlmEmbeddingUsage), ConnectLlmJson.LlmEmbeddingUsage));
        var result = new LlmEmbeddingResult { Vectors = [new float[] { 0, 1 }], Usage = usage, Diagnostics = new(200, null, null) };
        var roundtrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(result, ConnectLlmJson.LlmEmbeddingResult), ConnectLlmJson.LlmEmbeddingResult)!;
        Assert.Equal(result.Vectors[0], roundtrip.Vectors[0]);
        Assert.Equal(usage, roundtrip.Usage);
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Authenticator : ILlmAuthenticator
    {
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        internal int Calls { get; private set; }
        internal bool Block { get; init; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            return credential;
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal string Response { get; init; } = """{"data":[{"index":1,"embedding":[3,4]},{"index":0,"embedding":[1,2]}],"usage":{"prompt_tokens":-1,"total_tokens":0}}""";
        internal int Calls { get; private set; }
        internal string? Body { get; private set; }
        internal string? Header { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Header = request.Headers.TryGetValues("X-Test", out var headers) ? headers.Single() : null;
            return new(HttpStatusCode.OK) { Content = new StringContent(Response) };
        }
    }
}
