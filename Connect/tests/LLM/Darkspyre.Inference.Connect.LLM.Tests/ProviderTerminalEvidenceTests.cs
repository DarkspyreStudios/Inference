using Darkspyre.Inference.Common;
using System.Net;
using System.Text;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderTerminalEvidenceTests
{
    [Fact]
    public async Task ResponsesProtocolFailuresRedactEffectiveRequestSecrets()
    {
        const string error = "{\"type\":\"error\",\"error\":{\"code\":\"rate_limit_test-token_header-value_default-value_provider-secret\",\"message\":\"Failure test-token header-value default-value provider-secret\",\"refresh_token\":\"provider-secret\"}}";
        using var handler = new Handler($"data: {error}\n\n", "request-test-token-header-value-default-value-1");
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("X-Default", "default-value");
        var client = new OpenAiCodexLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.OpenAiCodex,
            Endpoint = "https://provider.example.test/v1",
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token", AccountId = "test-account" } }.Serialize(),
            AdditionalHeaders = new Dictionary<string, string> { ["X-Trace"] = "header-value" },
            CapabilityOverrides = new() { SupportsStreaming = InferenceSupport.Supported }
        };

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection,
                new() { ModelId = "selected-model", Messages = [new("user", "hello")] })) { }
        });

        Assert.Equal("request-[REDACTED]-[REDACTED]-[REDACTED]-1", failure.RequestId);
        Assert.Equal("rate_limit_[REDACTED]_[REDACTED]_[REDACTED]_[REDACTED]", failure.ProviderCode);
        foreach (string secret in new[] { "test-token", "header-value", "default-value", "provider-secret" })
            Assert.DoesNotContain(secret, failure.ToString() + failure.SanitizedResponseBody, StringComparison.Ordinal);
    }

    private sealed class Handler(string body, string requestId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            };
            response.Headers.Add("x-request-id", requestId);
            return Task.FromResult(response);
        }
    }
}
