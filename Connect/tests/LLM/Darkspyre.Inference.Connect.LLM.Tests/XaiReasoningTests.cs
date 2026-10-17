using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>Grok maps a requested reasoning level to the nearest level the model offers, from catalog facts first and
/// the model-family profile second (DSA-1070 B8).</summary>
public sealed class XaiReasoningTests
{
    [Theory]
    [InlineData("grok-4.7", LlmReasoningLevel.Maximum, "xhigh")]
    [InlineData("grok-4.7", LlmReasoningLevel.None, "low")]
    [InlineData("grok-4.5", LlmReasoningLevel.ExtraHigh, "high")]
    public async Task KnownModelMapsDesiredEffortInsideTheGrokWriter(string model, LlmReasoningLevel desired, string expected)
    {
        JsonElement payload = await ProjectAsync(Connection(), Request(model, desired));
        Assert.Equal(expected, Effort(payload));
        Assert.Equal(model, payload.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(LlmReasoningLevel.Maximum, "max")]
    [InlineData(LlmReasoningLevel.None, "none")]
    public async Task MissingModelFactsPermitTheRequestedNativeSpelling(LlmReasoningLevel desired, string expected)
    {
        LlmConnection connection = Connection();
        using var http = new HttpClient();
        Assert.Equal(InferenceSupport.Unknown, new GrokResponsesLlmClient(http)
            .GetCapabilities(connection, "grok-unlisted").SupportsReasoning);
        JsonElement payload = await ProjectAsync(connection, Request("grok-unlisted", desired));
        Assert.Equal(expected, Effort(payload));
    }

    [Fact]
    public async Task EmptyCatalogEffortsRemainUnknownAndPermitTheRequestedSpelling()
    {
        LlmConnection connection = Connection() with { Catalog = Catalog() };
        JsonElement payload = await ProjectAsync(connection, Request("grok-unlisted", LlmReasoningLevel.Maximum));
        Assert.Equal("max", Effort(payload));
        using var http = new HttpClient();
        Assert.Equal(InferenceSupport.Unknown, new GrokResponsesLlmClient(http)
            .GetCapabilities(connection, "grok-unlisted").SupportsReasoning);
    }

    [Fact]
    public async Task CatalogNoneAndHighMapMediumDownToNoneWithoutLosingObservations()
    {
        LlmConnection connection = Connection() with { Catalog = Catalog("none", "high") };
        JsonElement payload = await ProjectAsync(connection, Request("grok-unlisted", LlmReasoningLevel.Medium));
        Assert.Equal("none", Effort(payload));
        using var http = new HttpClient();
        Assert.True(new GrokResponsesLlmClient(http).GetCapabilities(connection, "grok-unlisted")
            .ReasoningLevels.SetEquals([LlmReasoningLevel.None, LlmReasoningLevel.High]));
    }

    [Fact]
    public async Task CatalogMinimalAndMaximumAreNativeFactsNotFilteredVocabulary()
    {
        LlmConnection connection = Connection() with { Catalog = Catalog("minimal", "max") };
        JsonElement payload = await ProjectAsync(connection, Request("grok-unlisted", LlmReasoningLevel.High));
        Assert.Equal("minimal", Effort(payload));
        using var http = new HttpClient();
        Assert.True(new GrokResponsesLlmClient(http).GetCapabilities(connection, "grok-unlisted")
            .ReasoningLevels.SetEquals([LlmReasoningLevel.Minimal, LlmReasoningLevel.Maximum]));
    }

    [Fact]
    public async Task ExplicitReasoningOverrideSuppliesMappingChoicesWithoutChangingTheCaller()
    {
        LlmConnection connection = Connection() with
        {
            CapabilityOverrides = new()
            {
                SupportsStreaming = InferenceSupport.Unsupported,
                ReasoningLevels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.Low, LlmReasoningLevel.High }
            }
        };
        LlmChatRequest request = Request("grok-4.7", LlmReasoningLevel.Maximum);
        JsonElement payload = await ProjectAsync(connection, request);
        Assert.Equal("high", Effort(payload));
        Assert.Equal(LlmReasoningLevel.Maximum, request.ReasoningLevel);
    }

    [Fact]
    public async Task NullEffortLeavesTheKnownModelDefaultUnspecified()
    {
        JsonElement payload = await ProjectAsync(Connection(), Request("grok-4.7", null));
        Assert.Null(Effort(payload));
    }

    private static LlmChatRequest Request(string model, LlmReasoningLevel? effort) => new()
    { ModelId = model, ReasoningLevel = effort, Messages = [new("user", "Hello")] };

    private static LlmConnection Connection() => new()
    {
        Version = "1", Transport = LlmTransport.XaiResponses, Endpoint = "https://api.x.ai/v1", Xai = new(),
        Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize(),
        CapabilityOverrides = new() { SupportsStreaming = InferenceSupport.Unsupported }
    };

    private static IReadOnlyList<LlmModel> Catalog(params string[] words) =>
    [new("grok-unlisted", "Unlisted")
    {
        CatalogEntry = JsonElement.Parse(new JsonObject
        {
            ["capabilities"] = new JsonObject
            { ["reasoning_effort"] = new JsonArray(words.Select(word => (JsonNode?)JsonValue.Create(word)).ToArray()) }
        }.ToJsonString())
    }];

    private static string? Effort(JsonElement payload) =>
        payload.TryGetProperty("reasoning", out JsonElement reasoning)
            && reasoning.TryGetProperty("effort", out JsonElement effort) ? effort.GetString() : null;

    private static async Task<JsonElement> ProjectAsync(LlmConnection connection, LlmChatRequest request)
    {
        LlmReasoningLevel? desired = request.ReasoningLevel;
        using var boundary = new Boundary(request.ModelId);
        using var http = new HttpClient(boundary);
        var client = new GrokResponsesLlmClient(http);
        IReadOnlySet<LlmReasoningLevel> before = client.GetCapabilities(connection, request.ModelId).ReasoningLevels;
        LlmChatResult? result = null;
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request))
            if (item is LlmChatCompletionEvent completion) result = completion.Result;
        Assert.Equal("answer", result!.Text);
        Assert.Equal(desired, request.ReasoningLevel);
        Assert.True(before.SetEquals(client.GetCapabilities(connection, request.ModelId).ReasoningLevels));
        Assert.Equal(1, boundary.Sends);
        return boundary.Body;
    }

    private sealed class Boundary(string model) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public JsonElement Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.x.ai/v1/responses", request.RequestUri!.AbsoluteUri);
            Body = JsonElement.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            string response = new JsonObject
            {
                ["id"] = "response-1", ["model"] = model, ["status"] = "completed",
                ["output"] = new JsonArray(new JsonObject
                {
                    ["type"] = "message", ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "answer" })
                })
            }.ToJsonString();
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }
}
