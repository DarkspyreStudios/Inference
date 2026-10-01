using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class RawSamplingFacadeTests
{
    public static IEnumerable<object[]> Cases() => RawSamplingGuardsTests.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ResolvedRawSamplingFailsBeforeRefreshPreparationCatalogOrClientOperation(
        string route, int operation, bool connectionScope, string field)
    {
        using var handler = new RawSamplingGuardsTests.Handler(route);
        using var http = new HttpClient(handler);
        ILlmClient client = RawSamplingGuardsTests.Client(http, route);
        var options = new Dictionary<string, JsonElement> { [field] = JsonElement.Parse("0.5") };
        LlmConnection connection = RawSamplingGuardsTests.Connection(route, operation == 0) with
        { ProviderOptions = connectionScope ? options : new Dictionary<string, JsonElement>() };
        var credential = LlmCredential.Parse(connection.Credential)!;
        if (credential.OAuth is null)
            connection = connection with { Credential = (credential with { OAuth = new() { AccessToken = "fixture-token" } }).Serialize() };
        string? storedCredential = connection.Credential;
        var source = new Source(connection);
        var authenticator = new Authenticator(connection.Transport);
        var request = RawSamplingGuardsTests.Request(route) with
        { ProviderOptions = connectionScope ? new Dictionary<string, JsonElement>() : options };
        await using (var service = new LlmProviderService(source, [client], [authenticator]))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
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
            Assert.Equal(storedCredential, source.Current.Credential);
            Assert.Same(options, connectionScope ? source.Current.ProviderOptions : request.ProviderOptions);
            Assert.Equal("0.5", Assert.Single(options).Value.GetRawText());
            Assert.Empty(connectionScope ? request.ProviderOptions : source.Current.ProviderOptions);
        }
        Assert.False(source.Disposed);
        Assert.False(authenticator.Disposed);
    }

    [Fact]
    public async Task TypedControlsRetainTheExistingSharedRefreshAndCallerExtension()
    {
        using var handler = new RawSamplingGuardsTests.Handler("compatible");
        using var http = new HttpClient(handler);
        var credential = new LlmCredential { ApiKey = "fixture-key", OAuth = new() { AccessToken = "fixture-token" } };
        var source = new Source(RawSamplingGuardsTests.Connection("compatible", false) with
        { Credential = credential.Serialize(), ProviderOptions = new Dictionary<string, JsonElement> { ["vendor_extension"] = JsonElement.Parse("1") } });
        var authenticator = new Authenticator(source.Current.Transport);
        await using var service = new LlmProviderService(source, [RawSamplingGuardsTests.Client(http, "compatible")], [authenticator]);
        var request = RawSamplingGuardsTests.Request("compatible") with
        { Temperature = .5, TopP = .8, ProviderOptions = new Dictionary<string, JsonElement> { ["vendor_extension"] = JsonElement.Parse("2") } };
        await foreach (LlmStreamEvent unused in service.StreamAsync("connection", request)) { }
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, authenticator.Calls);
        Assert.Equal(0, source.Writes);
        JsonElement payload = JsonElement.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(.5, payload.GetProperty("temperature").GetDouble());
        Assert.Equal(.8, payload.GetProperty("top_p").GetDouble());
        Assert.Equal(2, payload.GetProperty("vendor_extension").GetInt32());
    }

    private sealed class Source(LlmConnection current) : ILlmConnectionSource, IDisposable
    {
        internal LlmConnection Current { get; } = current;
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal bool Disposed { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult<LlmConnection?>(Current);
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            throw new InvalidOperationException("This unchanged credential cannot require a save.");
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class Authenticator(LlmTransport transport) : ILlmAuthenticator, IDisposable
    {
        public LlmTransport Transport => transport;
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(credential);
        }
        public void Dispose() => Disposed = true;
    }
}
