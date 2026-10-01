using System.Net;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexHostedPreflightTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool claude in new[] { false, true })
            foreach (int operation in new[] { 0, 1, 2, 3 })
                foreach (bool limitOnly in new[] { false, true }) yield return [claude, operation, limitOnly];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task HostedRequestsRefuseBeforeVertexAuthenticationOrHttp(bool claude, int operation, bool limitOnly)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
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
        var request = new LlmChatRequest
        {
            ModelId = claude ? "anthropic/claude-sonnet-4-6" : "google/gemini-2.5-flash",
            ReasoningLevel = claude ? LlmReasoningLevel.None : null,
            Messages = [new("user", "hello")],
            HostedTools = limitOnly ? [] : [new LlmWebSearchTool()],
            MaxHostedTurns = limitOnly ? 1 : null
        };
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (operation == 2) await client.CountInputTokensAsync(connection, request);
            else if (operation == 3) client.EstimateInputTokens(connection, request);
            else await foreach (var unused in client.StreamAsync(connection, request)) { }
        });
        Assert.Equal(0, authentication.Calls);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class Authentication : IVertexAuthentication
    {
        internal int Calls { get; private set; }
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Hosted preflight must precede authentication."); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); }
    }
}
