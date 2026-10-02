using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexExplicitCacheTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task CacheSelectionRefusesBeforeAuthentication(bool claude, int operation) => await Refuse(claude, operation, true);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task ProviderControlsRefuseBeforeAuthentication(bool claude, int operation) => await Refuse(claude, operation, false);

    private static async Task Refuse(bool claude, int operation, bool cache)
    {
        using var http = new HttpClient(new Handler());
        var authentication = new Authentication();
        var client = new VertexLlmClient(http, authentication);
        var connection = new LlmConnection
        {
            Transport = LlmTransport.GoogleVertexAi,
            Version = "1",
            Endpoint = "https://aiplatform.googleapis.com",
            Vertex = new() { ProjectId = "project", Location = "global" },
            CapabilityOverrides = new() { SupportsStreaming = operation == 0 }
        };
        string model = claude ? "anthropic/claude-sonnet-4-6" : "google/gemini-2.5-flash";
        var request = JsonSerializer.Deserialize("{\"ModelId\":\"" + model + "\",\"Messages\":[{\"Role\":\"user\",\"Content\":\"hello\"}],\"ExplicitCache\":{\"ResourceName\":\"cachedContents/test\",\"ModelId\":\"" + model + "\",\"ScopeId\":\"scope\"}}", ConnectLlmJson.ChatRequest)!;
        if (!cache) request = request with { ExplicitCache = null, ProviderControls = new XaiMultiAgentControls(4) };
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (operation == 2) await client.CountInputTokensAsync(connection, request);
            else if (operation == 3) client.EstimateInputTokens(connection, request);
            else await foreach (var unused in client.StreamAsync(connection, request)) { }
        });
        Assert.Equal(0, authentication.Calls);
    }

    private sealed class Authentication : IVertexAuthentication
    {
        internal int Calls { get; private set; }
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Cache refusal must precede authentication."); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Cache refusal must precede HTTP.");
    }
}
