using System.Net;
using System.Text;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFoundationReconciliationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrokDoesNotInterpretUnqualifiedHeadersAsOpenAiAccountEvidence(bool stream)
    {
        using var handler = new QuotaHandler(stream);
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1", Transport = LlmTransport.XaiResponses, Endpoint = "https://api.x.ai/v1",
            Credential = new LlmCredential { OAuth = new() { AccessToken = "controlled", AccountId = "account" } }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = stream }
        };
        var request = new LlmChatRequest { ModelId = "grok-4.7", Messages = [new("user", "question")] };
        int observations = 0;
        int completions = 0;
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request))
        {
            if (item is AccountUsageObservedEvent) observations++;
            if (item is LlmChatCompletionEvent) completions++;
        }
        Assert.Equal(0, observations);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void TransportAllocationKeepsEveryExistingIdentityAndAllThreeNewIdentities()
    {
        (LlmTransport Transport, int Id)[] expected = [(LlmTransport.OpenAiResponses, 0), (LlmTransport.OpenAiCompatibleChat, 1),
            (LlmTransport.AnthropicMessages, 2), (LlmTransport.OpenAiCodex, 3), (LlmTransport.AwsBedrockConverse, 4),
            (LlmTransport.GoogleAntigravityCli, 6), (LlmTransport.AnthropicClaudeCli, 7), (LlmTransport.AwsBedrockAgentCore, 8),
            (LlmTransport.SelfHost, 9), (LlmTransport.AppleFoundationModels, 10), (LlmTransport.GoogleVertexAi, 11),
            (LlmTransport.GoogleGeminiApi, 12), (LlmTransport.XaiResponses, 13),
            (LlmTransport.AzureOpenAiResponses, 14), (LlmTransport.AzureOpenAiChat, 15)];
        foreach ((LlmTransport transport, int id) in expected) Assert.Equal(id, (int)transport);
        Assert.False(Enum.IsDefined((LlmTransport)5));
    }

    private sealed class QuotaHandler(bool stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string result = "{\"status\":\"completed\",\"output\":[]}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream ? "data: {\"type\":\"response.completed\",\"response\":" + result + "}\n\n" : result,
                    Encoding.UTF8, stream ? "text/event-stream" : "application/json")
            };
            response.Headers.Add("x-ratelimit-limit-requests", "100");
            response.Headers.Add("x-ratelimit-remaining-requests", "90");
            response.Headers.Add("x-ratelimit-reset-requests", "1s");
            return Task.FromResult(response);
        }
    }
}
