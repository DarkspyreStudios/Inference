using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

/// <summary>The Vertex Gemini route and the Gemini API send the same generateContent body for the same portable request.</summary>
public sealed class GeminiWireParityTests
{
    [Fact]
    public async Task SamePortableRequestProducesIdenticalBodiesOnBothRoutes()
    {
        var tool = new LlmToolDefinition
        {
            Name = "run",
            Description = "Run a command",
            InputSchema = JsonElement.Parse("""{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}""")
        };
        var request = new LlmChatRequest
        {
            ModelId = "unused",
            Instructions = "Be brief.",
            Messages = [new("system", "Extra rule."), new("user", "Run pwd")],
            Tools = [tool],
            ToolChoice = new() { Mode = LlmToolChoiceMode.Specific, Name = "run" },
            MaxOutputTokens = 512,
            Temperature = 0.5,
            ReasoningLevel = LlmReasoningLevel.Medium,
            StructuredOutput = new() { Name = "result", Schema = JsonElement.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}}}""") }
        };
        var vertex = await new VertexLlmClient(new HttpClient(), new FixedAuthentication()).PrepareAsync(new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.GoogleVertexAi,
            Endpoint = "https://aiplatform.googleapis.com",
            Vertex = new() { ProjectId = "p", Location = "global" }
        }, default);
        var gemini = await new GeminiApiLlmClient(new HttpClient()).PrepareAsync(new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.GoogleGeminiApi,
            Endpoint = GeminiApiLlmClient.DefaultEndpoint,
            Credential = new LlmCredential { ApiKey = "k" }.Serialize()
        }, default);
        Func<LlmToolOutput, ProjectedToolOutput> project = output => ToolOutputProjector.Project(output, RichContentRoutes.TextOnly, RichContentRoutes.TextOnly);
        JsonObject fromVertex = VertexGeminiRequest.Build(vertex, request, LlmStructuredOutputEnforcement.ProviderJsonSchema, project);
        JsonObject fromGemini = GeminiRequest.Build(gemini, request, LlmStructuredOutputEnforcement.ProviderJsonSchema, project);
        Assert.True(JsonNode.DeepEquals(fromVertex, fromGemini));
        Assert.Equal("MEDIUM", (string?)fromGemini["generationConfig"]?["thinkingConfig"]?["thinkingLevel"]);
    }

    private sealed class FixedAuthentication : IVertexAuthentication
    {
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(new VertexAccess("token", "scope"));
    }
}
