using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Azure.Tests;

public sealed class AzureProtocolTests
{
    [Theory]
    [InlineData(true, true, "https://resource.openai.azure.com")]
    [InlineData(true, false, "https://resource.services.ai.azure.com/")]
    [InlineData(false, true, "https://resource.services.ai.azure.com")]
    [InlineData(false, false, "https://resource.openai.azure.com/")]
    public async Task BothProtocolsPreserveDeploymentIdentityAndUseOneApiKey(bool responses, bool streaming, string endpoint)
    {
        using var handler = new Handler(responses, streaming);
        using var http = new HttpClient(handler);
        var client = Client(http, responses);
        LlmConnection connection = Connection(responses, streaming) with { Endpoint = endpoint };
        LlmChatResult result = await Complete(client, connection, Request);
        Assert.Equal(endpoint.TrimEnd('/') + "/openai/v1/" + (responses ? "responses" : "chat/completions"), handler.Uri!.AbsoluteUri);
        Assert.Equal("protected-key", handler.Key);
        Assert.Null(handler.Authorization);
        Assert.Equal("deployment-alias", handler.Body!["model"]!.GetValue<string>());
        Assert.Equal(streaming, handler.Body["stream"]!.GetValue<bool>());
        Assert.Equal(19, handler.Body[responses ? "max_output_tokens" : "max_completion_tokens"]!.GetValue<int>());
        Assert.Equal("ok", result.Text);
        Assert.Equal(11, result.Usage!.InputTokens);
        Assert.Equal(3, result.Usage.OutputTokens);
        Assert.Equal(1, handler.Calls);
        Assert.False(client.GetCapabilities(connection).SupportsModelListing);
        Assert.Empty(await client.ListModelsAsync(connection));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConflictingRequestModelIsRejectedWithoutReplacingIt(bool responses)
    {
        using var handler = new Handler(responses, false);
        using var http = new HttpClient(handler);
        var client = Client(http, responses);
        await Assert.ThrowsAsync<ArgumentException>(() => Complete(client, Connection(responses, false), Request with { ModelId = "gpt-4o" }));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonAuthenticationCustomHeadersRemainSupported(bool responses)
    {
        using var handler = new Handler(responses, false);
        using var http = new HttpClient(handler);
        LlmConnection connection = Connection(responses, false) with
        {
            AdditionalHeaders = new Dictionary<string, string> { ["X-Application"] = "application" },
            Credential = new LlmCredential { ApiKey = "protected-key", SecretHeaders = new Dictionary<string, string> { ["X-Protected"] = "protected" } }.Serialize()
        };
        await Complete(Client(http, responses), connection, Request);
        Assert.Equal("application", handler.Application);
        Assert.Equal("protected", handler.Protected);
        Assert.Equal("protected-key", handler.Key);
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task AzureChatHonorsExplicitOutputLimitAndConversationCacheOptIn()
    {
        using var handler = new Handler(false, false);
        using var http = new HttpClient(handler);
        LlmConnection connection = Connection(false, false) with
        { ChatCompletions = new() { OutputLimitField = LlmChatCompletionsOutputLimitField.MaxTokens, UsePromptCacheKey = true } };
        await Complete(Client(http, false), connection, Request with { ConversationKey = "conversation" });
        Assert.Equal(19, handler.Body!["max_tokens"]!.GetValue<int>());
        Assert.Null(handler.Body["max_completion_tokens"]);
        Assert.Equal("conversation", handler.Body["prompt_cache_key"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownProfilesHaveNoInventedModelEvidenceAndOverridesKeepPrecedence(bool responses)
    {
        using var http = new HttpClient(new Handler(responses, false));
        var client = Client(http, responses);
        LlmConnection connection = Connection(responses, false) with { Azure = new() { DeploymentId = "deployment-alias", ModelProfileId = "unknown-model" } };
        LlmProviderCapabilities unknown = client.GetCapabilities(connection, "deployment-alias");
        Assert.Empty(unknown.ReasoningLevels);
        Assert.False(unknown.SupportsReasoningText);
        Assert.False(unknown.SupportsJsonSchema);
        Assert.False(unknown.SupportsPromptImages);
        Assert.False(unknown.SupportsPromptDocuments);
        Assert.False(unknown.SupportsTemperature);
        Assert.False(unknown.SupportsTopP);
        Assert.False(unknown.SupportsSeed);
        Assert.False(unknown.SupportsPresencePenalty);
        Assert.False(unknown.SupportsFrequencyPenalty);
        Assert.Equal(0, unknown.MaxStopSequences);
        Assert.Equal(LlmStrictInputSchemaSupport.Unknown, unknown.Tools.StrictInputSchema);
        LlmProviderCapabilities overridden = client.GetCapabilities(connection with
        {
            CapabilityOverrides = new() { SupportsPromptImages = true, SupportsJsonSchema = true, SupportsReasoningText = true },
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
            { ["deployment-alias"] = new() { SupportsPromptImages = false, SupportsJsonSchema = false } }
        }, "deployment-alias");
        Assert.False(overridden.SupportsPromptImages);
        Assert.False(overridden.SupportsJsonSchema);
        Assert.True(overridden.SupportsReasoningText);
        Assert.Throws<NotSupportedException>(() => client.GetCapabilities(connection with { CapabilityOverrides = new() { SupportsModelListing = true } }));
        Assert.Throws<NotSupportedException>(() => client.GetCapabilities(connection with { CapabilityOverrides = new() { SupportsAccountUsage = true } }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownDeploymentRemainsManuallyUsableWithoutInventedOutputLimit(bool responses)
    {
        using var handler = new Handler(responses, false);
        using var http = new HttpClient(handler);
        LlmConnection connection = Connection(responses, false) with { Azure = new() { DeploymentId = "deployment-alias" } };
        await Complete(Client(http, responses), connection, Request with { MaxOutputTokens = 120_000 });
        Assert.Equal(120_000, handler.Body![responses ? "max_output_tokens" : "max_completion_tokens"]!.GetValue<int>());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ProfileSamplingValidationUsesHintWithoutChangingWireModel(bool responses, bool streaming)
    {
        using var handler = new Handler(responses, streaming);
        using var http = new HttpClient(handler);
        var client = Client(http, responses);
        LlmConnection connection = Connection(responses, streaming) with { Azure = new() { DeploymentId = "deployment-alias", ModelProfileId = "gpt-5.5" } };
        Assert.Contains(LlmReasoningLevel.None, client.GetCapabilities(connection, "deployment-alias").ReasoningLevels);
        await Assert.ThrowsAsync<NotSupportedException>(() => Complete(client, connection, Request with { Temperature = .5 }));
        Assert.Equal(0, handler.Calls);
        await Complete(client, connection, Request with { Temperature = .5, ReasoningLevel = LlmReasoningLevel.None });
        Assert.Equal("deployment-alias", handler.Body!["model"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ExplicitMediaAndSchemaPathsReuseTheCanonicalBuilders(bool responses, bool streaming)
    {
        using var handler = new Handler(responses, streaming);
        using var http = new HttpClient(handler);
        LlmConnection connection = Connection(responses, streaming) with
        {
            CapabilityOverrides = new() { SupportsStreaming = streaming, SupportsPromptImages = true, SupportsJsonSchema = true }
        };
        LlmChatRequest request = Request with
        {
            Messages = [new("user", "question") { Attachments = [new() { Kind = LlmBinaryKind.Image, Data = new byte[] { 1 }, MediaType = "image/png", FileName = "x.png" }] }],
            StructuredOutput = new() { Name = "answer", Schema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false, properties = new { } }) }
        };
        await Complete(Client(http, responses), connection, request);
        Assert.Equal("deployment-alias", handler.Body!["model"]!.GetValue<string>());
        Assert.Contains("data:image/png;base64,AQ==", handler.Body.ToJsonString(), StringComparison.Ordinal);
        Assert.NotNull(responses ? handler.Body["text"]!["format"] : handler.Body["response_format"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedOpaqueStateReplaysOnlyWithinAzureScope(bool responses)
    {
        using var handler = new Handler(responses, false, reasoning: true);
        using var http = new HttpClient(handler);
        var client = Client(http, responses);
        LlmConnection connection = Connection(responses, false);
        LlmChatResult first = await Complete(client, connection, Request);
        Assert.NotNull(first.ContinuationState);
        Assert.Equal(ProviderContinuationStateCodec.ImplementationId(connection.Transport), first.ContinuationState.ImplementationId);
        LlmChatRequest next = Request with
        {
            Messages = [new("assistant", first.Text) { ContinuationState = first.ContinuationState }, new("user", "next")],
            Tools = [new() { Name = "lookup", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }]
        };
        await Complete(client, connection, next);
        Assert.Contains(responses ? "encrypted_content" : "reasoning_content", handler.Body!.ToJsonString(), StringComparison.Ordinal);
        foreach (LlmConnection changed in new[]
        {
            connection with { Endpoint = "https://other.openai.azure.com" },
            connection with { Credential = new LlmCredential { ApiKey = "different" }.Serialize() },
            connection with { Azure = connection.Azure! with { ModelProfileId = "gpt-4.1" } }
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Complete(client, changed, next));
        }
        Assert.Equal(2, handler.Calls);
        var publicClient = responses ? (ILlmClient)new OpenAiResponsesLlmClient(http) : new OpenAiCompatibleChatLlmClient(http);
        LlmConnection publicConnection = connection with
        { Transport = responses ? LlmTransport.OpenAiResponses : LlmTransport.OpenAiCompatibleChat, Azure = null };
        await Assert.ThrowsAsync<ArgumentException>(() => Complete(publicClient, publicConnection, next));
        Assert.Equal(2, handler.Calls);
    }

    internal static LlmClientBase Client(HttpClient http, bool responses) => responses ? new AzureResponsesLlmClient(http) : new AzureChatLlmClient(http);
    internal static readonly LlmChatRequest Request = new() { ModelId = "deployment-alias", Messages = [new("user", "question")], MaxOutputTokens = 19 };
    internal static LlmConnection Connection(bool responses, bool streaming) => new()
    {
        Version = "v1",
        Transport = responses ? LlmTransport.AzureOpenAiResponses : LlmTransport.AzureOpenAiChat,
        Endpoint = "https://resource.openai.azure.com",
        Azure = new() { DeploymentId = "deployment-alias", ModelProfileId = "gpt-4o" },
        Credential = new LlmCredential { ApiKey = "protected-key" }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = streaming }
    };
    internal static async Task<LlmChatResult> Complete(ILlmClient client, LlmConnection connection, LlmChatRequest request)
    {
        LlmChatResult? result = null;
        await foreach (var item in client.StreamAsync(connection, request)) if (item is LlmChatCompletionEvent completed) result = completed.Result;
        return result ?? throw new InvalidOperationException("Missing completion.");
    }

    internal sealed class Handler(bool responses, bool streaming, bool reasoning = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Key { get; private set; }
        public string? Authorization { get; private set; }
        public string? Application { get; private set; }
        public string? Protected { get; private set; }
        public JsonObject? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Uri = request.RequestUri;
            Key = request.Headers.TryGetValues("api-key", out var keys) ? keys.Single() : null;
            Authorization = request.Headers.Authorization?.ToString();
            Application = request.Headers.TryGetValues("X-Application", out var applications) ? applications.Single() : null;
            Protected = request.Headers.TryGetValues("X-Protected", out var protectedValues) ? protectedValues.Single() : null;
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            string output = responses
                ? "{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":11,\"output_tokens\":3},\"output\":["
                    + (reasoning ? "{\"type\":\"reasoning\",\"id\":\"reason\",\"summary\":[],\"encrypted_content\":\"opaque\"}," : "")
                    + "{\"type\":\"message\",\"id\":\"m\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}]}"
                : "{\"id\":\"r\",\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":3},\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\""
                    + (reasoning ? ",\"reasoning_content\":\"opaque\"" : "") + "},\"delta\":{\"content\":\"ok\"}}]}";
            string body = !streaming ? output : responses
                ? "data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\ndata: {\"type\":\"response.completed\",\"response\":" + output + "}\n\n"
                : "data: " + output + "\n\ndata: [DONE]\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }
}
