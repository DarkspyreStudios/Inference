using System.Net;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HttpMappedPublicationTests
{
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
}
