using System.Net;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HttpMappedPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuthParserDoesNotHandOffDocumentWhenActualStreamCleanupRefuses(bool jsonCleanupError)
    {
        Exception failure = jsonCleanupError ? new System.Text.Json.JsonException("controlled OAuth stream cleanup refusal")
            : new IOException("controlled OAuth stream cleanup refusal");
        var stream = new RefusingStream(failure);
        var services = new ServiceCollection();
        services.AddLlmConnect();
        services.AddHttpClient("ClaudeAuthenticator").ConfigurePrimaryHttpMessageHandler(() => new Handler(new StreamContent(stream)));
        ServiceProvider root = services.BuildServiceProvider();
        SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient("ClaudeAuthenticator");
        using HttpResponseMessage response = await client.GetAsync("https://controlled.invalid", HttpCompletionOption.ResponseHeadersRead);
        try
        {
            Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => ParserProbe.ParseAsync(response)));
            Assert.Equal(1, stream.AsyncDisposals);
            Assert.Equal(0, stream.SyncDisposals);
            Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => pool.DisposeAsync().AsTask()));
            Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => pool.DisposeAsync().AsTask()));
            Assert.Equal(1, stream.AsyncDisposals);
            Assert.Equal(0, stream.SyncDisposals);
        }
        finally
        {
            response.Dispose();
            client.Dispose();
            try { await root.DisposeAsync(); }
            catch (Exception error) when (ReferenceEquals(error, failure)) { }
        }
    }

    [Theory]
    [InlineData("embedding")]
    [InlineData("models")]
    [InlineData("account")]
    public async Task FailedMappingIsNotReplacedByCleanupRetirementFence(string route)
    {
        SdkHttpGenerationPool? pool = null;
        Task? retirement = null;
        var content = new RetiringContent("{", () => retirement ??= pool!.DisposeAsync().AsTask(), null);
        var services = new ServiceCollection();
        services.AddLlmConnect();
        services.AddHttpClient(route == "embedding" ? "OpenAiEmbeddingClient" : "OpenAiCompatibleChatLlmClient")
            .ConfigurePrimaryHttpMessageHandler(() => new Handler(content));
        await using ServiceProvider root = services.BuildServiceProvider();
        pool = root.GetRequiredService<SdkHttpGenerationPool>();
        var connection = new LlmConnection
        {
            Version = "controlled",
            Transport = route == "embedding" ? LlmTransport.OpenAiResponses : LlmTransport.OpenAiCompatibleChat,
            Endpoint = route == "account" ? "https://openrouter.ai/api/v1" : "https://controlled.invalid"
        };
        Task pending = route switch
        {
            "models" => root.GetRequiredService<OpenAiCompatibleChatLlmClient>().ListModelsAsync(connection),
            "account" => root.GetRequiredService<OpenAiCompatibleChatLlmClient>().GetAccountUsageAsync(connection),
            _ => root.GetRequiredService<OpenAiEmbeddingClient>().EmbedAsync(connection,
                new LlmEmbeddingRequest { ModelId = "actual", Inputs = ["actual"] })
        };
        if (route == "account")
            await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => pending);
        else
            Assert.IsAssignableFrom<System.Text.Json.JsonException>(
                (await Assert.ThrowsAsync<LlmProviderException>(() => pending)).InnerException);
        await retirement!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, content.Disposals);
    }

    [Fact]
    public async Task FailedBodyAwaitPreservesOriginalErrorWhenResponseCleanupStartsRetirement()
    {
        var failure = new IOException("controlled body read failure");
        SdkHttpGenerationPool? pool = null;
        Task? retirement = null;
        var content = new FailedContent(failure, () => retirement ??= pool!.DisposeAsync().AsTask());
        var services = new ServiceCollection();
        services.AddLlmConnect();
        services.AddHttpClient("OpenAiEmbeddingClient").ConfigurePrimaryHttpMessageHandler(() => new Handler(content));
        await using ServiceProvider root = services.BuildServiceProvider();
        pool = root.GetRequiredService<SdkHttpGenerationPool>();
        var connection = new LlmConnection { Version = "controlled", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://controlled.invalid" };
        HttpRequestException actual = await Assert.ThrowsAsync<HttpRequestException>(() =>
            root.GetRequiredService<OpenAiEmbeddingClient>().EmbedAsync(connection,
                new LlmEmbeddingRequest { ModelId = "actual", Inputs = ["actual"] }));
        Assert.Same(failure, actual.InnerException);
        await retirement!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, content.Disposals);
    }

    [Theory]
    [InlineData("embedding", false)]
    [InlineData("embedding", true)]
    [InlineData("models", false)]
    [InlineData("models", true)]
    [InlineData("account", false)]
    [InlineData("account", true)]
    public async Task ActualAdapterCannotPublishAfterResponseCleanupStartsRetirement(string route, bool failCleanup)
    {
        var failure = new IOException("controlled content cleanup failure");
        SdkHttpGenerationPool? pool = null;
        Task? retirement = null;
        string body = route switch { "models" => "{\"data\":[{\"id\":\"actual\"}]}", "account" => "{\"data\":{\"usage\":2}}", _ => "{\"data\":[{\"index\":0,\"embedding\":[0.5]}]}" };
        var content = new RetiringContent(body, () => retirement ??= pool!.DisposeAsync().AsTask(), failCleanup ? failure : null);
        var services = new ServiceCollection(); services.AddLlmConnect();
        services.AddHttpClient(route == "embedding" ? "OpenAiEmbeddingClient" : "OpenAiCompatibleChatLlmClient").ConfigurePrimaryHttpMessageHandler(() => new Handler(content));
        ServiceProvider root = services.BuildServiceProvider(); pool = root.GetRequiredService<SdkHttpGenerationPool>();
        try
        {
            var connection = new LlmConnection
            {
                Version = "controlled",
                Transport = route == "embedding" ? LlmTransport.OpenAiResponses : LlmTransport.OpenAiCompatibleChat,
                Endpoint = route == "account" ? "https://openrouter.ai/api/v1" : "https://controlled.invalid"
            };
            Task pending = route switch
            {
                "models" => root.GetRequiredService<OpenAiCompatibleChatLlmClient>().ListModelsAsync(connection),
                "account" => root.GetRequiredService<OpenAiCompatibleChatLlmClient>().GetAccountUsageAsync(connection),
                _ => root.GetRequiredService<OpenAiEmbeddingClient>().EmbedAsync(connection, new LlmEmbeddingRequest { ModelId = "actual", Inputs = ["actual"] })
            };
            if (failCleanup)
            {
                Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pending));
                Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => retirement!.WaitAsync(TimeSpan.FromSeconds(5))));
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                await retirement!.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(1, content.Disposals);
        }
        finally
        {
            try { await root.DisposeAsync(); }
            catch (IOException error) when (ReferenceEquals(error, failure)) { }
        }
    }

    private sealed class Handler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
    private sealed class RetiringContent(string body, Action retire, Exception? failure) : ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body))
    {
        public int Disposals { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposals++; retire(); if (failure is not null) throw failure; }
            base.Dispose(disposing);
        }
    }
    private sealed class FailedContent(Exception failure, Action retire) : HttpContent
    {
        public int Disposals { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.FromException(failure);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposals++; retire(); }
            base.Dispose(disposing);
        }
    }
    private sealed class RefusingStream(Exception failure) : MemoryStream("{\"actual\":true}"u8.ToArray())
    {
        public int AsyncDisposals { get; private set; }
        public int SyncDisposals { get; private set; }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215", Justification = "This controlled fixture refuses actual cleanup and detects a forbidden dependent synchronous retry.")]
        public override ValueTask DisposeAsync() { AsyncDisposals++; return ValueTask.FromException(failure); }
        protected override void Dispose(bool disposing) { if (disposing) SyncDisposals++; base.Dispose(disposing); }
    }
    private sealed class ParserProbe(HttpClient client) : OAuthAuthenticatorBase(client)
    {
        public static Task<System.Text.Json.JsonDocument> ParseAsync(HttpResponseMessage response) => ParseJsonAsync(response, default);
        public override LlmTransport Transport => LlmTransport.AnthropicClaudeCli;
        protected override string ClientId => "controlled";
        protected override string TokenEndpoint => "https://controlled.invalid";
        protected override HttpRequestMessage BuildRefreshRequest(LlmOAuthTokens tokens) => throw new NotSupportedException();
        protected override LlmCredential MapTokens(System.Text.Json.JsonElement tokenResponse, LlmOAuthTokens previous) => throw new NotSupportedException();
    }
}
