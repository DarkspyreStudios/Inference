using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedPreflightTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string route in new[] { "responses", "compatible", "anthropic", "gemini", "xai" })
            foreach (int operation in route == "gemini" ? new[] { 0, 1, 2, 3 } : route == "anthropic" ? new[] { 0, 1, 2 } : route is "responses" or "xai" ? new[] { 0, 1, 3 } : new[] { 0, 1 })
                foreach (bool limitOnly in new[] { false, true })
                    yield return [route, operation, limitOnly];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task UnavailableHostedFeaturesRefuseBeforeDirectEffects(string route, int operation, bool limitOnly)
    {
        using var handler = new RawSamplingGuardsTests.Handler(route);
        using var http = new HttpClient(handler);
        ILlmClient client = RawSamplingGuardsTests.Client(http, route);
        var request = RawSamplingGuardsTests.Request(route) with
        { HostedTools = limitOnly ? [] : [new LlmCodeExecutionTool()], MaxHostedTurns = limitOnly ? 1 : null };
        var connection = RawSamplingGuardsTests.Connection(route, operation == 0);
        Assert.Empty(client.GetCapabilities(connection, request.ModelId).HostedTools);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (operation == 2) await ((ILlmProviderTokenCounter)client).CountInputTokensAsync(connection, request);
            else if (operation == 3) ((ILlmContextEstimator)client).EstimateInputTokens(connection, request);
            else await foreach (LlmStreamEvent unused in client.StreamAsync(connection, request)) { }
        });
        Assert.Empty(handler.Bodies);
        Assert.Equal(0, handler.Calls);
        if (!limitOnly) Assert.IsType<LlmCodeExecutionTool>(Assert.Single(request.HostedTools));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FacadeRefusesBeforeRefreshPreparationDiscoveryOrProvider(string route, int operation, bool limitOnly)
    {
        using var handler = new RawSamplingGuardsTests.Handler(route);
        using var http = new HttpClient(handler);
        var connection = RawSamplingGuardsTests.Connection(route, operation == 0) with
        { Credential = new LlmCredential { ApiKey = "fixture", OAuth = route == "gemini" ? null : new() { AccessToken = "fixture" } }.Serialize() };
        var source = new Source(connection);
        var authenticator = new Authenticator(connection.Transport);
        var request = RawSamplingGuardsTests.Request(route) with
        { HostedTools = limitOnly ? [] : [new LlmCodeExecutionTool()], MaxHostedTurns = limitOnly ? 1 : null };
        await using (var service = new LlmProviderService(source, [RawSamplingGuardsTests.Client(http, route)], [authenticator]))
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                if (operation == 2) await service.CountInputTokensAsync("connection", request);
                else if (operation == 3) await service.MeasureRequestAsync("connection", request);
                else await foreach (LlmStreamEvent unused in service.StreamAsync("connection", request)) { }
            });
            Assert.Equal(1, source.Reads);
            Assert.Equal(0, source.Writes);
            Assert.Equal(0, authenticator.Calls);
            Assert.Equal(0, handler.Calls);
            Assert.Empty(handler.Bodies);
            Assert.Equal(connection.Credential, source.Connection.Credential);
        }
        Assert.False(source.Disposed);
        Assert.False(authenticator.Disposed);
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource, IDisposable
    {
        internal LlmConnection Connection { get; } = connection;
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal bool Disposed { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Reads++; return Task.FromResult<LlmConnection?>(Connection); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Writes++; throw new InvalidOperationException("Hosted refusal cannot save a credential."); }
        public void Dispose() => Disposed = true;
    }

    private sealed class Authenticator(LlmTransport transport) : ILlmAuthenticator, IDisposable
    {
        public LlmTransport Transport => transport;
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(credential); }
        public void Dispose() => Disposed = true;
    }
}
