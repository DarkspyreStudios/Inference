using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexTokenCountingTests
{
    private const string Claude = "anthropic/claude-sonnet-4-6";
    private const string Gemini = "google/gemini-3-flash-preview";
    private const string Schema = """{"type":"object","properties":{"answer":{"type":"string"}},"additionalProperties":false}""";

    [Theory]
    [InlineData(Claude)]
    [InlineData(Gemini)]
    public async Task CountUsesTheGenerationProjectionWithProviderSpecificRouting(string model)
    {
        using var handler = new Handler(model == Claude);
        using var http = new HttpClient(handler);
        var auth = new Authentication();
        var client = new VertexLlmClient(http, auth);
        var request = new LlmChatRequest
        {
            ModelId = model,
            Instructions = "Count this instruction too.",
            MaxOutputTokens = 100,
            Messages = [new("user", "Return JSON")],
            Tools = [new() { Name = "run", InputSchema = JsonElement.Parse(Schema) }],
            ToolChoice = new() { Mode = LlmToolChoiceMode.Auto },
            StructuredOutput = new() { Name = "result", Schema = JsonElement.Parse(Schema) },
            ReasoningLevel = LlmReasoningLevel.Low
        };
        LlmConnection connection = Connection with { CapabilityOverrides = new() { SupportsStreaming = false, SupportsJsonSchema = true } };
        var count = await client.CountInputTokensAsync(connection, request);
        Assert.Equal(model, count.ModelId);
        Assert.Equal(12, count.InputTokens);
        Assert.Single(handler.Bodies);
        Assert.Equal("Bearer protected-token", handler.Authorization[0]);
        Assert.DoesNotContain("protected-token", handler.Bodies[0], StringComparison.Ordinal);
        Assert.EndsWith(model == Claude ? "/publishers/anthropic/models/count-tokens:rawPredict"
            : "/publishers/google/models/gemini-3-flash-preview:countTokens", handler.Uris[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("aiplatform.googleapis.com", handler.Uris[0].Host);
        await foreach (var _ in client.StreamAsync(connection, request)) { }
        JsonObject generation = JsonNode.Parse(handler.Bodies[1])!.AsObject();
        JsonObject counted = JsonNode.Parse(handler.Bodies[0])!.AsObject();
        if (model == Claude)
        {
            Assert.Equal("claude-sonnet-4-6", (string?)counted["model"]);
            counted.Remove("model");
            generation.Remove("max_tokens");
            generation.Remove("stream");
            generation.Remove("anthropic_version");
            Assert.Null(count.BillableCharacters);
        }
        else
        {
            Assert.Equal(40, count.BillableCharacters);
            Assert.Equal(new LlmTokenCountDetail(LlmTokenModality.Text, 12), Assert.Single(count.InputTokenDetails));
            generation.Remove("toolConfig");
            Assert.Null(counted["generateContentRequest"]);
        }
        Assert.True(JsonNode.DeepEquals(generation, counted));
        Assert.Equal(2, auth.Calls);
    }

    [Theory]
    [InlineData("global", "aiplatform.googleapis.com")]
    [InlineData("asia-southeast1", "asia-southeast1-aiplatform.googleapis.com")]
    public async Task ClaudeUsesItsSelectedSupportedLocationWithoutRerouting(string location, string host)
    {
        using var handler = new Handler(true);
        using var http = new HttpClient(handler);
        var client = new VertexLlmClient(http, new Authentication());
        await client.CountInputTokensAsync(Connection with { Vertex = Connection.Vertex! with { Location = location } },
            new() { ModelId = Claude, Messages = [new("user", "hi")] });
        Assert.Equal(host, Assert.Single(handler.Uris).Host);
        Assert.Contains("/locations/" + location + "/", handler.Uris[0].AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("us-east5")]
    [InlineData("europe-west1")]
    public async Task UnsupportedCountLocationFailsBeforeAuthenticationAndHttp(string location)
    {
        using var handler = new Handler(true);
        using var http = new HttpClient(handler);
        var auth = new Authentication();
        var client = new VertexLlmClient(http, auth);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CountInputTokensAsync(
            Connection with { Vertex = Connection.Vertex! with { Location = location } }, new() { ModelId = Claude }));
        Assert.Empty(handler.Uris);
        Assert.Equal(0, auth.Calls);
    }

    [Theory]
    [InlineData(Claude)]
    [InlineData(Gemini)]
    public async Task InvalidControlsFailBeforeAuthenticationAndCounting(string model)
    {
        using var handler = new Handler(model == Claude);
        using var http = new HttpClient(handler);
        var auth = new Authentication();
        var client = new VertexLlmClient(http, auth);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CountInputTokensAsync(Connection,
            new()
            {
                ModelId = model,
                ProviderOptions = new Dictionary<string, JsonElement>
                { [model == Claude ? "foreign" : AnthropicTools.OptionName] = JsonElement.Parse("true") }
            }));
        Assert.Empty(handler.Bodies);
        Assert.Equal(0, auth.Calls);
    }

    [Fact]
    public async Task APreparedIdentityCannotChangeBetweenResolutionAndCountDispatch()
    {
        using var handler = new Handler(false);
        using var http = new HttpClient(handler);
        var client = new VertexLlmClient(http, new Authentication());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CountInputTokensAsync(
            Connection with { ResolvedContinuationScope = "other-identity" }, new() { ModelId = Gemini }));
        Assert.Empty(handler.Bodies);
    }

    private static LlmConnection Connection => new()
    {
        Version = "1",
        Transport = LlmTransport.GoogleVertexAi,
        Endpoint = VertexEndpoints.BaseEndpoint,
        Vertex = new() { ProjectId = "test-project", Location = "global" },
        CapabilityOverrides = new() { SupportsStreaming = false }
    };

    private sealed class Authentication : IVertexAuthentication
    {
        internal int Calls { get; private set; }
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new VertexAccess("protected-token", "identity")); }
    }

    private sealed class Handler(bool claude) : HttpMessageHandler
    {
        internal List<Uri> Uris { get; } = [];
        internal List<string> Bodies { get; } = [];
        internal List<string?> Authorization { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            Authorization.Add(request.Headers.Authorization?.ToString());
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            bool counting = request.RequestUri!.AbsolutePath.Contains("count", StringComparison.Ordinal);
            string body = counting ? claude ? """{"input_tokens":12}"""
                : """{"totalTokens":12,"totalBillableCharacters":40,"promptTokensDetails":[{"modality":"TEXT","tokenCount":12}]}"""
                : claude ? """{"type":"message","content":[{"type":"text","text":"{\"answer\":\"ok\"}"}],"stop_reason":"end_turn"}"""
                : """{"candidates":[{"content":{"parts":[{"text":"{\"answer\":\"ok\"}"}]},"finishReason":"STOP"}]}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
