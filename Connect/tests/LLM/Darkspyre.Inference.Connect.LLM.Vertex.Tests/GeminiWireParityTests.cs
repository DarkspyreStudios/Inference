using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

/// <summary>The Vertex Gemini route and the Gemini API send the same generateContent body for the same portable request.</summary>
public sealed class GeminiWireParityTests
{
    private const string Reply = """{"candidates":[{"content":{"parts":[{"text":"{\"ok\":true}"}]},"finishReason":"STOP"}]}""";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SamePortableRequestProducesIdenticalBodiesOnBothRoutes(bool stream)
    {
        LlmCapabilityOverrides? overrides = stream ? null : new() { SupportsStreaming = false };
        var request = new LlmChatRequest
        {
            ModelId = "unused",
            Instructions = "Be brief.",
            Messages = [new("system", "Extra rule."), new("user", "Run pwd")],
            Tools =
            [
                new()
                {
                    Name = "run",
                    Description = "Run a command",
                    InputSchema = JsonElement.Parse("""{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}""")
                }
            ],
            ToolChoice = new() { Mode = LlmToolChoiceMode.Specific, Name = "run" },
            MaxOutputTokens = 512,
            Temperature = 0.5,
            ReasoningLevel = LlmReasoningLevel.Medium,
            StructuredOutput = new() { Name = "result", Schema = JsonElement.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}}}""") }
        };
        var vertexHandler = new Capture(stream);
        var vertex = new VertexLlmClient(new HttpClient(vertexHandler), new FixedAuthentication());
        await Drain(vertex.StreamAsync(new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.GoogleVertexAi,
            Endpoint = "https://aiplatform.googleapis.com",
            Vertex = new() { ProjectId = "p", Location = "global" },
            CapabilityOverrides = overrides
        }, request with { ModelId = "google/gemini-3-flash-preview" }));
        var geminiHandler = new Capture(stream);
        var gemini = new GeminiApiLlmClient(new HttpClient(geminiHandler));
        await Drain(gemini.StreamAsync(new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.GoogleGeminiApi,
            Endpoint = GeminiApiLlmClient.DefaultEndpoint,
            Credential = new LlmCredential { ApiKey = "k" }.Serialize(),
            CapabilityOverrides = overrides
        }, request with { ModelId = "gemini-3-flash-preview" }));
        string operation = stream ? ":streamGenerateContent" : ":generateContent";
        Assert.EndsWith(operation, Assert.Single(vertexHandler.Uris).AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith(operation, Assert.Single(geminiHandler.Uris).AbsolutePath, StringComparison.Ordinal);
        JsonNode fromVertex = JsonNode.Parse(Assert.Single(vertexHandler.Bodies))!;
        JsonNode fromGemini = JsonNode.Parse(Assert.Single(geminiHandler.Bodies))!;
        Assert.True(JsonNode.DeepEquals(fromVertex, fromGemini));
        Assert.Equal("MEDIUM", (string?)fromGemini["generationConfig"]?["thinkingConfig"]?["thinkingLevel"]);
        Assert.NotNull(fromGemini["generationConfig"]?["responseJsonSchema"]);
    }

    [Fact]
    public void BothRoutesOfferTheSharedGeminiReasoningLevels()
    {
        var vertex = new VertexLlmClient(new HttpClient(new Capture(stream: true)), new FixedAuthentication());
        var gemini = new GeminiApiLlmClient(new HttpClient(new Capture(stream: true)));

        IReadOnlySet<LlmReasoningLevel> fromVertex = vertex.GetCapabilities(new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.GoogleVertexAi,
            Endpoint = "https://aiplatform.googleapis.com",
            Vertex = new() { ProjectId = "p", Location = "global" }
        }, "google/gemini-3-flash-preview").ReasoningLevels;
        IReadOnlySet<LlmReasoningLevel> fromGemini = gemini.GetCapabilities(new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.GoogleGeminiApi,
            Endpoint = GeminiApiLlmClient.DefaultEndpoint,
            Credential = new LlmCredential { ApiKey = "k" }.Serialize()
        }, "gemini-3-flash-preview").ReasoningLevels;

        Assert.Equal(fromGemini.Order(), fromVertex.Order());
        Assert.Equal(GeminiReasoning.Get("gemini-3-flash-preview", catalogThinking: null).Levels.Order(), fromVertex.Order());
    }

    private static async Task Drain(IAsyncEnumerable<LlmStreamEvent> events)
    {
        await foreach (LlmStreamEvent _ in events) { }
    }

    private sealed class Capture(bool stream) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<Uri> Uris { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK)
            {
                Content = stream
                    ? new StringContent("data: " + Reply + "\n\n", Encoding.UTF8, "text/event-stream")
                    : new StringContent(Reply, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FixedAuthentication : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(new VertexAccess("token", "scope"));
    }
}
