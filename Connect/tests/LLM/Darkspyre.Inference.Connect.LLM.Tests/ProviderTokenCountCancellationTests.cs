using Darkspyre.Inference.Common;
using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderTokenCountCancellationTests
{
    [Theory]
    [InlineData("anthropic")]
    [InlineData("vertex-claude")]
    [InlineData("gemini-api")]
    [InlineData("vertex-gemini")]
    public async Task ExactCountingUsesTheSamePromptProjectionAsGeneration(string route)
    {
        bool claude = route is "anthropic" or "vertex-claude";
        bool vertex = route.StartsWith("vertex-", StringComparison.Ordinal);
        var handler = new CanonicalHandler(claude);
        using var http = new HttpClient(handler);
        ILlmClient client = route switch
        {
            "anthropic" => new AnthropicMessagesLlmClient(http),
            "gemini-api" => new GeminiApiLlmClient(http),
            _ => new VertexLlmClient(http, new VertexAuthenticationBoundary())
        };
        string model = claude ? "claude-sonnet-4-6" : "gemini-3-flash-preview";
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = client.Transport,
            Endpoint = vertex ? "https://aiplatform.googleapis.com"
                : claude ? "https://api.anthropic.com/v1" : GeminiApiLlmClient.DefaultEndpoint,
            Vertex = vertex ? new()
            {
                ProjectId = "project",
                Location = "global",
                AuthenticationMode = LlmGoogleAuthenticationMode.ApplicationDefault
            } : null,
            Credential = vertex ? null : new LlmCredential { ApiKey = "test-key" }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = InferenceSupport.Unsupported }
        };
        var request = new LlmChatRequest
        {
            ModelId = vertex ? (claude ? "anthropic/" : "google/") + model : model,
            Instructions = "Use the reference.",
            ReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel.Low,
            MaxOutputTokens = 512,
            ToolChoice = new() { Mode = LlmToolChoiceMode.Auto },
            Tools = [new LlmToolDefinition { Name = "echo", Description = "Echo text.",
                InputSchema = JsonElement.Parse("""{"type":"object","properties":{"text":{"type":"string"}}}""") }],
            Messages = [new("user", "Read the reference") { Attachments = [new LlmAttachment
                { Kind = LlmBinaryKind.Document, FileName = "reference.txt", MediaType = "text/plain", Data = "reference text"u8.ToArray() }] }]
        };
        LlmProviderInputTokenCount count = await Assert.IsAssignableFrom<ILlmProviderTokenCounter>(client)
            .CountInputTokensAsync(connection, request);
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);

        Assert.Equal(123, count.InputTokens);
        Assert.Equal(request.ModelId, count.ModelId);
        Assert.Equal("answer", Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result.Text);
        Assert.Equal(2, handler.Requests.Count);
        JsonElement counted = handler.Requests[0].Payload;
        if (route == "gemini-api") counted = counted.GetProperty("generateContentRequest");
        JsonElement generated = handler.Requests[1].Payload;
        string[] fields = claude ? ["messages", "system", "tools", "tool_choice", "thinking", "output_config"]
            : ["contents", "systemInstruction", "tools", "generationConfig"];
        foreach (string field in fields)
            Assert.True(JsonElement.DeepEquals(generated.GetProperty(field), counted.GetProperty(field)), field);
        if (claude)
        {
            JsonElement document = Assert.Single(counted.GetProperty("messages")[0].GetProperty("content").EnumerateArray(),
                part => part.GetProperty("type").GetString() == "document");
            Assert.Equal("reference text", document.GetProperty("source").GetProperty("data").GetString());
            Assert.Equal(model, counted.GetProperty("model").GetString());
            Assert.False(counted.TryGetProperty("max_tokens", out _));
            Assert.False(counted.TryGetProperty("stream", out _));
            Assert.False(counted.TryGetProperty("anthropic_version", out _));
        }
        else
        {
            JsonElement document = Assert.Single(counted.GetProperty("contents")[0].GetProperty("parts").EnumerateArray(),
                part => part.TryGetProperty("inlineData", out _)).GetProperty("inlineData");
            Assert.Equal("reference text"u8.ToArray(), Convert.FromBase64String(document.GetProperty("data").GetString()!));
            if (vertex) Assert.False(counted.TryGetProperty("toolConfig", out _));
            else Assert.True(JsonElement.DeepEquals(generated.GetProperty("toolConfig"), counted.GetProperty("toolConfig")));
        }
        Assert.EndsWith(claude ? vertex ? "/models/count-tokens:rawPredict" : "/messages/count_tokens"
            : "/models/" + model + ":countTokens", handler.Requests[0].Endpoint.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRetainsTheActualProviderAccountingFailure(bool tokenize)
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new LlmProviderException("The provider rejected accounting.",
            LlmTransport.OpenAiResponses, HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(3));
        await using var service = new LlmProviderService(new Source(), [new FailingAccountingClient(cancellation, failure)]);

        OperationCanceledException canceled = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            tokenize ? service.TokenizeAsync("connection", "model", "text", cancellation.Token)
                : service.CountInputTokensAsync("connection", new() { ModelId = "model" }, cancellation.Token));

        Assert.Same(failure, canceled.InnerException);
        Assert.True(canceled.CancellationToken.IsCancellationRequested);
    }

    private sealed class VertexAuthenticationBoundary : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(new VertexAccess("test-token", "test-scope"));
    }

    private sealed class CanonicalHandler(bool claude) : HttpMessageHandler
    {
        public List<(Uri Endpoint, JsonElement Payload)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            bool count = Requests.Count == 0;
            Requests.Add((request.RequestUri!, JsonElement.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))));
            string body = count ? claude ? "{\"input_tokens\":123}" : "{\"totalTokens\":123}" : claude
                ? "{\"type\":\"message\",\"id\":\"reply-1\",\"content\":[{\"type\":\"text\",\"text\":\"answer\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":123,\"output_tokens\":1}}"
                : "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"answer\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":123,\"candidatesTokenCount\":1,\"totalTokenCount\":124}}";
            return new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
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

    private sealed class FailingAccountingClient(CancellationTokenSource cancellation, Exception failure)
        : ILlmClient, ILlmProviderTokenCounter, ILlmTextTokenizerClient
    {
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public LlmProviderDefinition ProviderDefinition => OpenAiApiDefinition.Value;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new();

        public Task<LlmProviderInputTokenCount> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromException<LlmProviderInputTokenCount>(failure);
        }

        public Task<LlmTokenization> TokenizeAsync(LlmConnection connection, string modelId, string text,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromException<LlmTokenization>(failure);
        }

        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
