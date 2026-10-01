using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class SharedOpenAiProtocolHookTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RoutingAndHeaderHooksRetainTheRequestModel(bool responses, bool streaming)
    {
        using var handler = new Handler(responses, streaming);
        using var http = new HttpClient(handler);
        LlmClientBase client = responses ? new Responses(http) : new Chat(http);
        LlmConnection connection = new()
        {
            Version = "v1",
            Transport = client.Transport,
            Endpoint = "https://original.test/v1",
            Credential = new LlmCredential { ApiKey = "original" }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = streaming }
        };
        await foreach (var item in client.StreamAsync(connection, new()
        { ModelId = "deployment-alias", Messages = [new("user", "question")], MaxOutputTokens = 17 })) { }

        Assert.Equal("https://owned.test/operation", handler.Uri!.AbsoluteUri);
        Assert.Equal("selected", handler.Authorization);
        Assert.Equal("deployment-alias", handler.Body!["model"]!.GetValue<string>());
        Assert.Equal(streaming, handler.Body["stream"]!.GetValue<bool>());
        Assert.Equal(17, handler.Body[responses ? "max_output_tokens" : "max_completion_tokens"]!.GetValue<int>());
        Assert.True(responses ? ((Responses)client).Validated : ((Chat)client).Validated);
    }

    private sealed class Chat(HttpClient http) : ChatCompletionsLlmClientBase(http)
    {
        public override LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;
        public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiCompatible;
        public bool Validated { get; private set; }
        protected override void ValidateModelSampling(LlmConnection connection, LlmChatRequest request) => Validated = true;
        protected override Uri BuildRequestUri(LlmConnection connection, LlmChatRequest request) => new("https://owned.test/operation");
        protected override LlmChatCompletionsOutputLimitField OutputLimitField(LlmConnection connection) => LlmChatCompletionsOutputLimitField.MaxCompletionTokens;
        protected override void ApplyRequestHeaders(HttpRequestMessage message, LlmConnection connection, LlmChatRequest request) =>
            message.Headers.Add("Authorization", "selected");
    }

    private sealed class Responses(HttpClient http) : ResponsesLlmClientBase(http)
    {
        public override LlmTransport Transport => LlmTransport.OpenAiResponses;
        public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;
        protected override LlmProviderCapabilities DefaultCapabilities => new OpenAiResponsesLlmClient(Http).GetCapabilities(new()
        { Version = "v1", Transport = Transport, Endpoint = "https://original.test/v1" });
        protected override bool OffersNonStreamingRequests => true;
        public bool Validated { get; private set; }
        protected override void ValidateModelSampling(LlmConnection connection, LlmChatRequest request) => Validated = true;
        protected override Uri BuildRequestUri(LlmConnection connection, LlmChatRequest request) => new("https://owned.test/operation");
        protected override void ApplyRequestHeaders(HttpRequestMessage message, LlmConnection connection, LlmChatRequest request) =>
            message.Headers.Add("Authorization", "selected");
    }

    private sealed class Handler(bool responses, bool streaming) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public JsonObject? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.GetValues("Authorization").Single();
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            string response = responses
                ? "{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"output\":[]}}"
                : "{\"id\":\"r\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"delta\":{\"content\":\"ok\"}}]}";
            if (!streaming && responses) response = "{\"id\":\"r\",\"status\":\"completed\",\"output\":[]}";
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(streaming ? $"data: {response}\n\ndata: [DONE]\n\n" : response,
                    Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            };
        }
    }
}
