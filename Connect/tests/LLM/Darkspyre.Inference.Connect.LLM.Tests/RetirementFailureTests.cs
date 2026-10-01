using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class RetirementFailureTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task EmbeddingRetirementPreservesIndependentOwnerFailuresAfterBothDrain(int failures, bool releaseRefreshFirst)
    {
        using var source = new EmbeddingSource((failures & 1) != 0);
        using var auth = new BlockingRefresh(source, (failures & 2) != 0);
        using var client = new EmbeddingClient();
        var facade = new LlmEmbeddingProviderService(source, [client], [auth]);
        Task request = facade.EmbedAsync("connection", new() { ModelId = "model", Inputs = ["input"] });
        Task? retirement = null;
        try
        {
            await auth.Entered.Task.WaitAsync(Bound);
            retirement = facade.DisposeAsync().AsTask();
            await auth.OuterCancellationEntered.Task.WaitAsync(Bound);
            await auth.CredentialCancellationEntered.Task.WaitAsync(Bound);
            Assert.True(source.FacadeToken.IsCancellationRequested);
            Assert.True(auth.CredentialToken.IsCancellationRequested);
            Assert.NotEqual(source.FacadeToken, auth.CredentialToken);
            Assert.False(retirement.IsCompleted);
            Assert.False(request.IsCompleted);
            Assert.Same(retirement, facade.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.EmbedAsync("connection",
                new() { ModelId = "model", Inputs = ["input"] }));
            if (releaseRefreshFirst)
            {
                auth.ReleaseRefresh.TrySetResult();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Bound));
                Assert.False(retirement.IsCompleted);
                auth.ReleaseOuterCancellation.Set();
            }
            else
            {
                auth.ReleaseOuterCancellation.Set();
                await source.CancellationEntered.Task.WaitAsync(Bound);
                Assert.False(request.IsCompleted);
                Assert.False(retirement.IsCompleted);
                auth.ReleaseRefresh.TrySetResult();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Bound));
            var expected = new List<Exception>();
            if ((failures & 1) != 0) expected.Add(source.Failure);
            if ((failures & 2) != 0) expected.Add(auth.Failure);
            await AssertSharedFailureAsync(facade, retirement, expected, callbackFailure: true);
            Assert.Equal(1, source.Reads);
            Assert.Equal(0, source.Saves);
            Assert.Equal(1, auth.Calls);
            Assert.Equal(0, client.Calls);
            Assert.False(source.Disposed);
            Assert.False(auth.Disposed);
            Assert.False(client.Disposed);
        }
        finally
        {
            auth.ReleaseOuterCancellation.Set();
            auth.ReleaseRefresh.TrySetResult();
            try { await DrainAsync(request); }
            finally { await DrainAsync(retirement ?? facade.DisposeAsync().AsTask()); }
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task AgentRetirementPreservesAllSessionAndOpeningFailuresAfterEveryCleanup(int sessionFailures, bool opening)
    {
        using var source = new AgentSource();
        using var client = new AgentClient(sessionFailures);
        var facade = new LlmAgentProviderService(source, [client]);
        ILlmAgentSession first = await facade.StartSessionAsync("first", new() { ModelId = "model" });
        ILlmAgentSession second = await facade.StartSessionAsync("second", new() { ModelId = "model" });
        Task? late = opening ? facade.StartSessionAsync("opening", new() { ModelId = "model" }) : null;
        Task? retirement = null;
        try
        {
            if (opening) await source.Entered.Task.WaitAsync(Bound);
            retirement = facade.DisposeAsync().AsTask();
            foreach (FailingSession session in client.Sessions) await session.Entered.Task.WaitAsync(Bound);
            if (opening) await source.Canceled.Task.WaitAsync(Bound);
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, facade.DisposeAsync().AsTask());
            Assert.All(client.Sessions, session => Assert.Equal(1, session.Disposals));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.StartSessionAsync("first", new() { ModelId = "model" }));
            client.Sessions[0].Release.TrySetResult();
            await client.Sessions[0].Exited.Task.WaitAsync(Bound);
            Assert.False(retirement.IsCompleted);
            client.Sessions[1].Release.TrySetResult();
            await client.Sessions[1].Exited.Task.WaitAsync(Bound);
            if (opening)
            {
                Assert.False(retirement.IsCompleted);
                Assert.False(late!.IsCompleted);
                source.Release.TrySetResult();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late.WaitAsync(Bound));
            }
            Exception[] expected =
            [
                .. client.Sessions.Where(session => session.Fail).Select(session => session.Failure),
                .. opening ? new Exception[] { source.Failure } : []
            ];
            await AssertSharedFailureAsync(facade, retirement, expected, callbackFailure: opening);
            Assert.All(client.Sessions, session => Assert.Equal(1, session.Disposals));
            foreach (ILlmAgentSession session in new[] { first, second })
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetCapabilitiesAsync());
                await DrainAsync(session.DisposeAsync().AsTask());
            }
            Assert.Equal(opening ? 3 : 2, source.Reads);
            Assert.Equal(2, client.Calls);
            Assert.False(source.Disposed);
            Assert.False(client.Disposed);
        }
        finally
        {
            source.Release.TrySetResult();
            foreach (FailingSession session in client.Sessions) session.Release.TrySetResult();
            try { if (late is not null) await DrainAsync(late); }
            finally
            {
                try { await DrainAsync(retirement ?? facade.DisposeAsync().AsTask()); }
                finally
                {
                    try { await DrainAsync(first.DisposeAsync().AsTask()); }
                    finally { await DrainAsync(second.DisposeAsync().AsTask()); }
                }
            }
        }
    }

    private static async Task AssertSharedFailureAsync(IAsyncDisposable owner, Task retirement,
        IReadOnlyList<Exception> expected, bool callbackFailure)
    {
        if (expected.Count == 0)
        {
            await retirement.WaitAsync(Bound);
            Assert.Same(retirement, owner.DisposeAsync().AsTask());
            await owner.DisposeAsync();
            return;
        }
        Exception failure = (await Record.ExceptionAsync(() => retirement.WaitAsync(Bound)))!;
        Assert.NotNull(failure);
        if (expected.Count > 1 || callbackFailure)
        {
            AggregateException aggregate = Assert.IsType<AggregateException>(failure);
            Assert.Equal(expected.Count, aggregate.Flatten().InnerExceptions.Count);
            foreach (Exception error in expected) Assert.Contains(error, aggregate.Flatten().InnerExceptions);
        }
        else { Assert.Same(Assert.Single(expected), failure); }
        Assert.Same(retirement, owner.DisposeAsync().AsTask());
        Assert.Same(failure, await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask()));
    }

    private static async Task DrainAsync(Task operation)
    {
        try { await operation.WaitAsync(Bound); }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }
        catch (IOException) { }
    }

    private sealed class EmbeddingSource(bool fail) : ILlmConnectionSource, IDisposable
    {
        private CancellationTokenRegistration _cancellation;
        internal TaskCompletionSource CancellationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IOException Failure { get; } = new("Controlled outer facade cancellation failure.");
        internal CancellationToken FacadeToken { get; private set; }
        internal int Reads { get; private set; }
        internal int Saves { get; private set; }
        internal bool Disposed { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Reads++;
            FacadeToken = cancellationToken;
            _cancellation = cancellationToken.Register(() =>
            {
                CancellationEntered.TrySetResult();
                if (fail) throw Failure;
            });
            return Task.FromResult<LlmConnection?>(new()
            {
                Version = "v1", Transport = LlmTransport.OpenAiCompatibleChat, Endpoint = "https://provider.invalid",
                Credential = new LlmCredential { OAuth = new() { AccessToken = "controlled-token", ExpiresAtUnixSeconds = 0 } }.Serialize()
            });
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) { Saves++; throw new NotSupportedException(); }
        public void Dispose() { Disposed = true; _cancellation.Dispose(); }
    }

    private sealed class BlockingRefresh(EmbeddingSource source, bool fail) : ILlmAuthenticator, IDisposable
    {
        private CancellationTokenRegistration _outer;
        private CancellationTokenRegistration _credential;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource OuterCancellationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CredentialCancellationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim ReleaseOuterCancellation { get; } = new();
        internal IOException Failure { get; } = new("Controlled credential owner cancellation failure.");
        internal CancellationToken CredentialToken { get; private set; }
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;
        public async Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        {
            Calls++;
            CredentialToken = cancellationToken;
            // Hold outer propagation so the credential owner's independent cancellation fires first.
            _outer = source.FacadeToken.Register(() =>
            {
                OuterCancellationEntered.TrySetResult();
                if (!ReleaseOuterCancellation.Wait(Bound)) throw new TimeoutException("The outer callback was not released.");
            });
            _credential = cancellationToken.Register(() =>
            {
                CredentialCancellationEntered.TrySetResult();
                if (fail) throw Failure;
            });
            Entered.TrySetResult();
            await ReleaseRefresh.Task;
            return credential;
        }
        public void Dispose()
        {
            Disposed = true;
            _outer.Dispose();
            _credential.Dispose();
            ReleaseOuterCancellation.Dispose();
        }
    }

    private sealed class EmbeddingClient : ILlmEmbeddingClient, IDisposable
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public bool Supports(LlmTransport transport) => transport == LlmTransport.OpenAiCompatibleChat;
        public Task<LlmEmbeddingResult> EmbedAsync(LlmConnection connection, LlmEmbeddingRequest request,
            CancellationToken cancellationToken = default) { Calls++; throw new NotSupportedException(); }
        public void Dispose() => Disposed = true;
    }

    private sealed class AgentSource : ILlmConnectionSource, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IOException Failure { get; } = new("Controlled opening cancellation failure.");
        internal int Reads { get; private set; }
        internal bool Disposed { get; private set; }
        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (connectionId == "opening")
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                {
                    Canceled.TrySetResult();
                    throw Failure;
                });
                Entered.TrySetResult();
                await Release.Task;
            }
            return new() { Version = "v1", Transport = LlmTransport.OpenAiCompatibleChat, Endpoint = "https://provider.invalid" };
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }

    private sealed class AgentClient(int failures) : ILlmAgentClient, IDisposable
    {
        internal List<FailingSession> Sessions { get; } = [];
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;
        public Task<ILlmAgentSession> StartSessionAsync(string connectionId, LlmConnection connection,
            LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            var session = new FailingSession(connectionId, Calls <= failures);
            Sessions.Add(session);
            return Task.FromResult<ILlmAgentSession>(session);
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class FailingSession(string id, bool fail) : ILlmAgentSession
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IOException Failure { get; } = new($"Controlled {id} session cleanup failure.");
        internal bool Fail => fail;
        internal int Disposals { get; private set; }
        public string ThreadId => id;
        public string SessionId => id;
        public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LlmAgentCapabilities());
        public Task<LlmRequestAccounting> MeasureContextAsync(LlmAgentContext proposedContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentContextStatus> GetContextStatusAsync(LlmAgentTurnRequest? prospectiveTurn = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentContext> CaptureContextAsync(LlmAgentContextCaptureOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplaceContextAsync(LlmAgentContext context, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(LlmAgentTurnContinuation continuation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(LlmAgentTurnRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SteerAsync(string turnId, IReadOnlyList<LlmAgentContent> input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task InterruptAsync(string turnId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<LlmAgentEvent> ObserveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(string resourceId, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            Entered.TrySetResult();
            try
            {
                await Release.Task;
                if (Fail) throw Failure;
            }
            finally { Exited.TrySetResult(); }
        }
    }
}
