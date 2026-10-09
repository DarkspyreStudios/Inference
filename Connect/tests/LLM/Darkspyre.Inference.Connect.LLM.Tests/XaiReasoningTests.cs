using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.Resources;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiReasoningTests
{
    [Theory]
    [InlineData(false, "grok-4.7", LlmReasoningLevel.Maximum, "xhigh")]
    [InlineData(false, "grok-4.7", LlmReasoningLevel.None, "low")]
    [InlineData(false, "grok-4.5", LlmReasoningLevel.ExtraHigh, "high")]
    [InlineData(true, "grok-4.7", LlmReasoningLevel.Maximum, "xhigh")]
    [InlineData(true, "grok-4.7", LlmReasoningLevel.Minimal, "low")]
    [InlineData(true, "grok-4.5", LlmReasoningLevel.Maximum, "high")]
    public async Task KnownModelMapsDesiredEffortInsideTheGrokWriter(bool deferred, string model,
        LlmReasoningLevel desired, string expected)
    {
        JsonElement payload = await ProjectAsync(Connection(), Request(model, desired), deferred);
        Assert.Equal(expected, Effort(payload, deferred));
        Assert.Equal(model, payload.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(false, LlmReasoningLevel.Maximum, "max")]
    [InlineData(false, LlmReasoningLevel.None, "none")]
    [InlineData(true, LlmReasoningLevel.Maximum, "max")]
    [InlineData(true, LlmReasoningLevel.None, "none")]
    public async Task MissingModelFactsPermitTheRequestedNativeSpelling(bool deferred,
        LlmReasoningLevel desired, string expected)
    {
        LlmConnection connection = Connection();
        using var http = new HttpClient();
        Assert.Equal(InferenceSupport.Unknown, new XaiResponsesLlmClient(http)
            .GetCapabilities(connection, "grok-unlisted").SupportsReasoning);
        JsonElement payload = await ProjectAsync(connection, Request("grok-unlisted", desired), deferred);
        Assert.Equal(expected, Effort(payload, deferred));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyCatalogEffortsRemainUnknownAndPermitTheRequestedSpelling(bool deferred)
    {
        LlmConnection connection = Connection() with { Catalog = Catalog() };
        JsonElement payload = await ProjectAsync(connection, Request("grok-unlisted", LlmReasoningLevel.Maximum), deferred);
        Assert.Equal("max", Effort(payload, deferred));
        using var http = new HttpClient();
        Assert.Equal(InferenceSupport.Unknown, new XaiResponsesLlmClient(http)
            .GetCapabilities(connection, "grok-unlisted").SupportsReasoning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogNoneAndHighMapMediumDownToNoneWithoutLosingObservations(bool deferred)
    {
        LlmConnection connection = Connection() with { Catalog = Catalog("none", "high") };
        JsonElement payload = await ProjectAsync(connection, Request("grok-unlisted", LlmReasoningLevel.Medium), deferred);
        Assert.Equal("none", Effort(payload, deferred));
        using var http = new HttpClient();
        Assert.True(new XaiResponsesLlmClient(http).GetCapabilities(connection, "grok-unlisted")
            .ReasoningLevels.SetEquals([LlmReasoningLevel.None, LlmReasoningLevel.High]));
    }

    [Fact]
    public async Task CatalogMinimalAndMaximumAreNativeFactsNotFilteredVocabulary()
    {
        LlmConnection connection = Connection() with { Catalog = Catalog("minimal", "max") };
        JsonElement payload = await ProjectAsync(connection, Request("grok-unlisted", LlmReasoningLevel.High), false);
        Assert.Equal("minimal", Effort(payload, false));
        using var http = new HttpClient();
        Assert.True(new XaiResponsesLlmClient(http).GetCapabilities(connection, "grok-unlisted")
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
        JsonElement payload = await ProjectAsync(connection, request, false);
        Assert.Equal("high", Effort(payload, false));
        Assert.Equal(LlmReasoningLevel.Maximum, request.ReasoningLevel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullEffortLeavesTheKnownModelDefaultUnspecified(bool deferred)
    {
        JsonElement payload = await ProjectAsync(Connection(), Request("grok-4.7", null), deferred);
        Assert.Null(Effort(payload, deferred));
    }

    [Fact]
    public void BatchProjectionRetainsDesiredEffortUntilItsNativePayload()
    {
        using var http = new HttpClient();
        var client = new XaiResponsesLlmClient(http);
        LlmConnection connection = Connection();
        LlmChatRequest request = Request("grok-4.7", LlmReasoningLevel.Maximum);
        LlmChatRequest projected = client.ProjectBatchRequest(connection, request);
        JsonObject payload = client.BuildBatchPayload(connection, projected, null);
        Assert.Equal(LlmReasoningLevel.Maximum, projected.ReasoningLevel);
        Assert.Equal("xhigh", payload["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, 4, "low")]
    [InlineData(true, 16, "high")]
    public async Task MultiAgentCountKeepsItsSeparateNativeMeaning(bool deferred, int count, string expected)
    {
        LlmChatRequest request = Request("grok-4.20-multi-agent", null) with
        { ProviderControls = new XaiMultiAgentControls(count) };
        JsonElement payload = await ProjectAsync(Connection(), request, deferred);
        Assert.Equal(expected, Effort(payload, deferred));
        Assert.False(payload.TryGetProperty("agent_count", out _));
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

    private static string? Effort(JsonElement payload, bool deferred) => deferred
        ? payload.TryGetProperty("reasoning_effort", out JsonElement word) ? word.GetString() : null
        : payload.TryGetProperty("reasoning", out JsonElement reasoning)
            && reasoning.TryGetProperty("effort", out JsonElement effort) ? effort.GetString() : null;

    private static async Task<JsonElement> ProjectAsync(LlmConnection connection, LlmChatRequest request, bool deferred)
    {
        LlmReasoningLevel? desired = request.ReasoningLevel;
        using var boundary = new Boundary(deferred, request.ModelId);
        using var http = new HttpClient(boundary);
        var client = new XaiResponsesLlmClient(http);
        IReadOnlySet<LlmReasoningLevel> before = client.GetCapabilities(connection, request.ModelId).ReasoningLevels;
        if (deferred)
        {
            var source = new Source(connection);
            await using var access = new LlmConnectionAccess(source, [], [client]);
            await using var core = new LlmProviderService(source, [client], [], connectionAccess: access);
            await using var resources = new LlmResourceProviderService(access, core);
            Assert.Equal("request-1", (await resources.SubmitDeferredAsync("connection", request)).RequestId);
        }
        else
        {
            LlmChatResult? result = null;
            await foreach (LlmStreamEvent item in client.StreamAsync(connection, request))
                if (item is LlmChatCompletionEvent completion) result = completion.Result;
            Assert.Equal("answer", result!.Text);
        }
        Assert.Equal(desired, request.ReasoningLevel);
        Assert.True(before.SetEquals(client.GetCapabilities(connection, request.ModelId).ReasoningLevels));
        Assert.Equal(1, boundary.Sends);
        return boundary.Body;
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Boundary(bool deferred, string model) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public JsonElement Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.x.ai/v1/" + (deferred ? "chat/completions" : "responses"), request.RequestUri!.AbsoluteUri);
            Body = JsonElement.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            string response = deferred ? "{\"request_id\":\"request-1\"}" : new JsonObject
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
