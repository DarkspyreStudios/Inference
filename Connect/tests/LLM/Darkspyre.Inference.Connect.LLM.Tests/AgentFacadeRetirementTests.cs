namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AgentFacadeRetirementTests
{
    [Fact]
    public async Task ALateSessionCleanupFailureIsReportedByFacadeRetirement()
    {
        var failure = new InvalidOperationException("Session cleanup failed.");
        var session = new FailingSession(failure);
        var client = new PendingClient();
        var service = new LlmAgentProviderService(new Source(), [client]);
        Task<ILlmAgentSession> opening = service.StartSessionAsync("connection", new() { ModelId = "model" });
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task retirement = service.DisposeAsync().AsTask();
        client.Session.SetResult(session);
        OperationCanceledException canceled = await Assert.ThrowsAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, canceled.InnerException);
        Assert.True(canceled.CancellationToken.IsCancellationRequested);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => retirement.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisposeAsync().AsTask()));
        Assert.Equal(1, session.Disposals);
    }

    [Fact]
    public async Task AHandedSessionCleanupFailureIsNotCountedTwice()
    {
        var failure = new InvalidOperationException("Session cleanup failed.");
        var session = new FailingSession(failure);
        var client = new PendingClient();
        client.Session.SetResult(session);
        var service = new LlmAgentProviderService(new Source(), [client]);
        _ = await service.StartSessionAsync("connection", new() { ModelId = "model" });

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisposeAsync().AsTask()));
        Assert.Equal(1, session.Disposals);
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new()
            {
                Version = "1",
                Transport = LlmTransport.OpenAiCodex,
                Endpoint = "https://provider.example.test/codex"
            });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PendingClient : ILlmAgentClient
    {
        public LlmTransport Transport => LlmTransport.OpenAiCodex;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ILlmAgentSession> Session { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ILlmAgentSession> StartSessionAsync(string connectionId, LlmConnection connection,
            LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
        {
            Entered.SetResult();
            return Session.Task;
        }
    }

    private sealed class FailingSession(Exception failure) : ILlmAgentSession
    {
        public string ThreadId => "thread";
        public string SessionId => "session";
        public int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.FromException(failure);
        }

        public Task<LlmRequestAccounting> MeasureContextAsync(LlmAgentContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentContextStatus> GetContextStatusAsync(LlmAgentTurnRequest? turn = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentContext> CaptureContextAsync(LlmAgentContextCaptureOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplaceContextAsync(LlmAgentContext context, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentContext> CompactWithProviderAsync(LlmProviderCompactionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(LlmAgentTurnContinuation continuation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(LlmAgentTurnRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SteerAsync(string turnId, IReadOnlyList<LlmAgentContent> input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task InterruptAsync(string turnId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<LlmAgentEvent> ObserveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(string resourceId, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
