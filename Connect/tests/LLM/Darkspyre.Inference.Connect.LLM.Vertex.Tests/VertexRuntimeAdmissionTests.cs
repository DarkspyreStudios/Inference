using Darkspyre.Inference.Connect.LLM.Tests;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexRuntimeAdmissionTests
{
    [Theory]
    [InlineData("acp")]
    [InlineData("copilot")]
    [InlineData("launch")]
    [InlineData("copilot-token")]
    [InlineData("runtime-token")]
    public async Task AllDirectRoutesRefuseBeforeGoogleCredentialOrHttpEffects(string input)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var authentication = new Authentication();
        var client = new VertexLlmClient(http, authentication);
        LlmConnection connection = RuntimeInputAdmissionTests.Foreign(input, new()
        {
            Version = "v1", Transport = LlmTransport.GoogleVertexAi, Endpoint = VertexEndpoints.BaseEndpoint,
            Vertex = new() { ProjectId = "project", Location = "global" }
        });
        var request = new LlmChatRequest { ModelId = "google/gemini-3-flash-preview", Messages = [new("user", "input")] };
        Assert.Throws<NotSupportedException>(() => client.GetCapabilities(connection));
        Assert.Throws<NotSupportedException>(() => client.EstimateInputTokens(connection, request));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.PrepareAsync(connection, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CountInputTokensAsync(connection, request));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.ListModelsAsync(connection));
        await Assert.ThrowsAsync<NotSupportedException>(async () => { await foreach (var unused in client.StreamAsync(connection, request)) { } });
        Assert.Equal(0, authentication.Calls);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class Authentication : IVertexAuthentication
    {
        public int Calls;
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Controlled credential effect was reached."); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Controlled HTTP effect was reached."); }
    }
}
