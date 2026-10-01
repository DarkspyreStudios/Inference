using System.Net;

namespace Darkspyre.Inference.Connect.LLM.Azure.Tests;

public sealed class AzureLifetimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellationRetiresTheActiveRequest(bool responses)
    {
        using var handler = new HeldHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = AzureProtocolTests.Client(http, responses);
        using var cancellation = new CancellationTokenSource();
        Task operation = ReadAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handler.Request!.Content!.ReadAsStringAsync());
        Assert.Equal(1, handler.Calls);

        async Task ReadAsync()
        {
            await foreach (var item in client.StreamAsync(AzureProtocolTests.Connection(responses, true), AzureProtocolTests.Request, cancellation.Token)) { }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactProviderCountingIsExplicitlyUnsupportedWithoutGenerationOrDiscovery(bool responses)
    {
        using var handler = new AzureProtocolTests.Handler(responses, false);
        using var http = new HttpClient(handler);
        var connection = AzureProtocolTests.Connection(responses, false);
        await using var service = new LlmProviderService(new Source(connection), [AzureProtocolTests.Client(http, responses)]);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CountInputTokensAsync("id", AzureProtocolTests.Request));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class HeldHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpRequestMessage? Request { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Request = request; Started.SetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }
    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Counting must not write credentials.");
    }
}
