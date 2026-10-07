using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Connect.Image.Providers;
using Darkspyre.Inference.Connect.LLM;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed class XaiImagineImageTransportTests
{
    [Fact]
    public async Task ImageFailurePreservesSanitizedRequestIdentityWithoutExposingTheBody()
    {
        const string token = "image-test-credential";
        using var handler = new RejectedImageHandler(token);
        using var factory = new Factory(handler);
        var services = new ServiceCollection();
        services.AddLlmConnect(options => options.CreationMode = InferenceHttpCreationMode.BorrowHostFactory);
        services.AddSingleton<IHttpClientFactory>(factory);
        services.AddSingleton<ILlmConnectionSource>(new Source(token));
        await using ServiceProvider container = services.BuildServiceProvider();
        await using AsyncServiceScope scope = container.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<LlmProviderService>();
        var binding = new XaiFamilyBinding(new("selected"),
            scope.ServiceProvider.GetRequiredService<LlmConnectionAccess>(), facade);
        using var transport = new XaiImagineImageTransport();
        var connection = new ImageConnection
        {
            Transport = ImageTransport.XaiImagine,
            Endpoint = "https://api.x.ai/v1",
            Xai = binding.Selection
        };
        PreparedImageTransportRequest prepared = transport.Prepare(new ImageTransportPreparationRequest
        {
            Workflow = new()
            {
                Id = "selected-model",
                Label = "Selected model",
                Version = "1",
                Transport = ImageTransport.XaiImagine,
                TransportConfiguration = JsonElement.Parse("""{"model":"selected-model"}""")
            },
            PositivePrompt = "A caller-selected image",
            CacheKey = string.Empty
        });

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            transport.SubmitAsync(connection, prepared, binding));

        Assert.Equal("image-request-[REDACTED]", failure.RequestId);
        Assert.Equal("rate_limit_[REDACTED]_[REDACTED]", failure.ProviderCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(17), failure.RetryAfter);
        Assert.DoesNotContain(token, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private generated prompt", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Null(failure.SanitizedResponseBody);
        LlmProviderException projected = Assert.IsType<LlmProviderException>(XaiImagineImageTransport.SafeFailure(failure));
        Assert.Equal(failure.ProviderCode, projected.ProviderCode);
        Assert.Equal(failure.RequestId, projected.RequestId);
        Assert.Equal(1, handler.Sends);
    }

    private sealed class Source(string token) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new()
            {
                Version = "1",
                Transport = LlmTransport.XaiResponses,
                Endpoint = "https://api.x.ai/v1",
                Xai = new(),
                Credential = new LlmCredential
                {
                    OAuth = new()
                    {
                        AccessToken = token,
                        AccountId = "test-account",
                        ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                    }
                }.Serialize()
            });
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("The supplied credential is already fresh.");
    }

    private sealed class RejectedImageHandler(string token) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            Assert.Equal("/v1/images/generations", request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"code\":\"rate_limit_" + token
                    + "_provider-secret\",\"message\":\"private generated prompt and image\",\"refresh_token\":\"provider-secret\"}}")
            };
            response.Headers.Add("x-request-id", "image-request-" + token);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return Task.FromResult(response);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly List<HttpClient> _clients = [];
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            _clients.Add(client);
            return client;
        }
        public void Dispose()
        {
            foreach (HttpClient client in _clients) client.Dispose();
        }
    }
}
