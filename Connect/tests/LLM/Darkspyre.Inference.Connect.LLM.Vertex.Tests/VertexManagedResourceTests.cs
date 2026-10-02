using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexManagedResourceTests
{
    [Theory]
    [InlineData("selection")]
    [InlineData("selection-version")]
    [InlineData("workspace-resource")]
    [InlineData("native-file")]
    [InlineData("tool-resource")]
    public async Task UnsupportedContextRefusesBeforeCredentialOrDiscoveryEffects(string variant)
    {
        using var handler = new ForbiddenHttp();
        using var http = new HttpClient(handler);
        var authentication = new ForbiddenAuthentication();
        var source = new ForbiddenSource();
        var client = new VertexLlmClient(http, authentication);
        await using var providers = new LlmProviderService(source, [client]);
        var agent = new ManagedVertexAgentClient(client, providers);
        LlmAgentContext context = variant switch
        {
            "selection" => new() { SelectedResources = [new("workspace", "original")] },
            "selection-version" => new() { ResourceSelectionVersion = new("workspace", "session", 9) },
            "tool-resource" => new()
            {
                Entries =
                [
                    new("call", "turn", new("assistant", "") { ToolCalls = [new LlmToolCall("actual", "create", "{}")] }),
                    new("result", "turn", new("user", "")
                    {
                        ToolResults = [new LlmToolResult("actual", new()
                        { Parts = [new LlmToolOutputSessionResource(new("workspace", "generated"))] })]
                    })
                ]
            },
            _ => new()
            {
                Entries = [new("entry", "turn", new("user", "inspect")
                {
                    Attachments = [variant == "native-file" ? new LlmFileReference(new("native"))
                        : new LlmResourceAttachment(new("workspace", "original"))]
                })]
            }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => agent.StartSessionAsync("selected", Connection,
            new() { ModelId = "google/gemini-3-flash-preview", Context = context }));
        Assert.Equal(0, authentication.Calls);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullOptionsOrCallerCancellationRefusesBeforeCredentialResolution(bool canceled)
    {
        using var handler = new ForbiddenHttp();
        using var http = new HttpClient(handler);
        var authentication = new ForbiddenAuthentication();
        var source = new ForbiddenSource();
        var client = new VertexLlmClient(http, authentication);
        await using var providers = new LlmProviderService(source, [client]);
        var agent = new ManagedVertexAgentClient(client, providers);
        if (canceled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.StartSessionAsync("selected", Connection,
                new() { ModelId = "google/gemini-3-flash-preview" }, new CancellationToken(true)));
        else
            await Assert.ThrowsAsync<ArgumentNullException>(() => agent.StartSessionAsync("selected", Connection, null!));
        Assert.Equal(0, authentication.Calls);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, handler.Calls);
    }

    private static LlmConnection Connection => new()
    {
        Version = "1",
        Transport = LlmTransport.GoogleVertexAi,
        Endpoint = "https://aiplatform.googleapis.com",
        Vertex = new() { ProjectId = "project", Location = "global" }
    };

    private sealed class ForbiddenAuthentication : IVertexAuthentication
    {
        public int Calls { get; private set; }
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Unexpected credential resolution."); }
    }

    private sealed class ForbiddenSource : ILlmConnectionSource
    {
        public int Calls { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Unexpected catalog connection resolution."); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Unexpected credential write."); }
    }

    private sealed class ForbiddenHttp : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Unexpected HTTP discovery."); }
    }
}
