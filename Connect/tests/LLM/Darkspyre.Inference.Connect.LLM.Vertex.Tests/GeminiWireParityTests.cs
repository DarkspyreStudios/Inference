using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

/// <summary>The Vertex Gemini route and the Gemini API send the same generateContent body for the same portable request.</summary>
public sealed class GeminiWireParityTests
{
    private const string Reply = """{"candidates":[{"content":{"parts":[{"text":"{\"ok\":true}"}]},"finishReason":"STOP"}]}""";

    [Fact]
    public async Task SamePortableRequestProducesIdenticalBodiesOnBothRoutes()
    {
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
        var vertexHandler = new Capture();
        var vertex = new VertexLlmClient(new HttpClient(vertexHandler), new FixedAuthentication());
        await Drain(vertex.StreamAsync(new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.GoogleVertexAi,
            Endpoint = "https://aiplatform.googleapis.com",
            Vertex = new() { ProjectId = "p", Location = "global" }
        }, request with { ModelId = "google/gemini-3-flash-preview" }));
        var geminiHandler = new Capture();
        var gemini = new GeminiApiLlmClient(new HttpClient(geminiHandler));
        await Drain(gemini.StreamAsync(new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.GoogleGeminiApi,
            Endpoint = GeminiApiLlmClient.DefaultEndpoint,
            Credential = new LlmCredential { ApiKey = "k" }.Serialize()
        }, request with { ModelId = "gemini-3-flash-preview" }));
        JsonNode fromVertex = JsonNode.Parse(Assert.Single(vertexHandler.Bodies))!;
        JsonNode fromGemini = JsonNode.Parse(Assert.Single(geminiHandler.Bodies))!;
        Assert.True(JsonNode.DeepEquals(fromVertex, fromGemini));
        Assert.Equal("MEDIUM", (string?)fromGemini["generationConfig"]?["thinkingConfig"]?["thinkingLevel"]);
        Assert.NotNull(fromGemini["generationConfig"]?["responseJsonSchema"]);
    }

    private static async Task Drain(IAsyncEnumerable<LlmStreamEvent> events)
    {
        await foreach (LlmStreamEvent _ in events) { }
    }

    private sealed class Capture : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + Reply + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class FixedAuthentication : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(new VertexAccess("token", "scope"));
    }
}
