using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AgentLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task FacadeRetiresStartedResumedAndForkedSessionsButNotBorrowedDependencies()
    {
        using var source = new Source();
        using var client = new Client();
        await using var facade = new LlmAgentProviderService(source, [client]);
        ILlmAgentSession[] sessions =
        [
            await facade.StartSessionAsync("connection", new() { ModelId = "model" }),
            await facade.ResumeSessionAsync("connection", "resume"),
            await facade.ForkSessionAsync("connection", "fork", "last-turn")
        ];

        await facade.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(["start", "resume", "fork"], client.Paths);
        Assert.All(client.Sessions, session => Assert.Equal(1, session.Disposals));
        foreach (ILlmAgentSession session in sessions)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetCapabilitiesAsync());
            await session.DisposeAsync();
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.StartSessionAsync("connection", new() { ModelId = "model" }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.ResumeSessionAsync("connection", "thread"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.ForkSessionAsync("connection", "thread"));
        Assert.Equal(3, source.Reads);
        Assert.False(source.Disposed);
        Assert.False(client.Disposed);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("resume")]
    [InlineData("fork")]
    public async Task SessionCreatedAfterRetirementIsDisposedBeforeItsOpeningOperationLeaves(string path)
    {
        using var source = new Source();
        using var client = new Client { BlockFromCall = 1, IgnoreOpenCancellation = true };
        await using var facade = new LlmAgentProviderService(source, [client]);
        Task<ILlmAgentSession> opening = OpenAsync(facade, path);
        try
        {
            await client.Started.Task.WaitAsync(Bound);
            Task retirement = facade.DisposeAsync().AsTask();
            await client.Stopped.Task.WaitAsync(Bound);
            Assert.False(retirement.IsCompleted);
            client.Release.TrySetResult();

            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
            Assert.Equal(1, Assert.Single(client.Sessions).Disposals);
        }
        finally { client.Release.TrySetResult(); }
    }

    [Fact]
    public async Task ExistingSessionRetiresWhileAnotherSessionIsStillOpening()
    {
        using var source = new Source();
        using var client = new Client { BlockFromCall = 2, IgnoreOpenCancellation = true };
        await using var facade = new LlmAgentProviderService(source, [client]);
        ILlmAgentSession session = await facade.StartSessionAsync("connection", new() { ModelId = "model" });
        Session inner = Assert.Single(client.Sessions);
        inner.BlockedMethod = "run";
        Task running = InvokeAsync(session, "run");
        Task<ILlmAgentSession> opening = facade.ResumeSessionAsync("connection", "resume");
        try
        {
            await inner.Started.Task.WaitAsync(Bound);
            await client.Started.Task.WaitAsync(Bound);
            Task retirement = facade.DisposeAsync().AsTask();
            await inner.Stopped.Task.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Bound));
            Assert.False(retirement.IsCompleted);
            client.Release.TrySetResult();

            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
            Assert.All(client.Sessions, value => Assert.Equal(1, value.Disposals));
        }
        finally
        {
            inner.Release.TrySetResult();
            client.Release.TrySetResult();
        }
    }

    [Theory]
    [InlineData("capabilities")]
    [InlineData("measure")]
    [InlineData("status")]
    [InlineData("capture")]
    [InlineData("replace")]
    [InlineData("continue")]
    [InlineData("run")]
    [InlineData("steer")]
    [InlineData("interrupt")]
    [InlineData("observe")]
    [InlineData("resources")]
    [InlineData("read")]
    [InlineData("stop")]
    public async Task EverySessionOperationUsesItsOwnerTokenAndIsFencedAfterRetirement(string method)
    {
        var inner = new Session { BlockedMethod = method };
        await using var session = new OwnedLlmAgentSession(inner, _ => { });
        Task operation = InvokeAsync(session, method);
        await inner.Started.Task.WaitAsync(Bound);

        await session.DisposeAsync().AsTask().WaitAsync(Bound);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(inner.OperationToken.IsCancellationRequested);
        Assert.Equal(1, inner.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => InvokeAsync(session, method));
        Assert.Single(inner.Calls);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("observe")]
    [InlineData("capture")]
    public async Task RetirementWaitsForIgnoringSessionWorkAndRejectsLateOutput(string method)
    {
        var inner = new Session { BlockedMethod = method, IgnoreCancellation = true };
        await using var session = new OwnedLlmAgentSession(inner, _ => { });
        Task operation = InvokeAsync(session, method);
        try
        {
            await inner.Started.Task.WaitAsync(Bound);
            Task retirement = session.DisposeAsync().AsTask();
            await inner.Stopped.Task.WaitAsync(Bound);
            Assert.Same(retirement, session.DisposeAsync().AsTask());
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, inner.Disposals);
            inner.Release.TrySetResult();

            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(1, inner.Disposals);
        }
        finally { inner.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData("run")]
    [InlineData("continue")]
    [InlineData("observe")]
    public async Task EnumerableAcquiredBeforeRetirementCannotStartAfterward(string method)
    {
        var inner = new Session();
        await using var session = new OwnedLlmAgentSession(inner, _ => { });
        IAsyncEnumerable<LlmAgentEvent> stream = Stream(session, method);

        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => ReadAsync(stream));
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task CallerCancellationDoesNotRetireTheSessionOrAnotherOperation()
    {
        var inner = new Session { BlockedMethod = "capture" };
        await using var session = new OwnedLlmAgentSession(inner, _ => { });
        using var caller = new CancellationTokenSource();
        Task abandoned = session.CaptureContextAsync(cancellationToken: caller.Token);
        Task remaining = session.CaptureContextAsync();
        try
        {
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
            Assert.False(remaining.IsCompleted);
            Assert.Equal(0, inner.Disposals);
            inner.Release.TrySetResult();

            await remaining.WaitAsync(Bound);
            Assert.Same(inner.Capabilities, await session.GetCapabilitiesAsync());
        }
        finally { inner.Release.TrySetResult(); }
    }

    [Fact]
    public async Task OwnerWaitsForCallerStartedDisposal()
    {
        using var source = new Source();
        using var client = new Client();
        await using var facade = new LlmAgentProviderService(source, [client]);
        ILlmAgentSession session = await facade.StartSessionAsync("connection", new() { ModelId = "model" });
        Session inner = Assert.Single(client.Sessions);
        inner.BlockDisposal = true;
        Task caller = session.DisposeAsync().AsTask();
        try
        {
            await inner.DisposalStarted.Task.WaitAsync(Bound);
            Task owner = facade.DisposeAsync().AsTask();
            Assert.False(owner.IsCompleted);
            Assert.Same(caller, session.DisposeAsync().AsTask());
            inner.DisposeRelease.TrySetResult();

            await owner.WaitAsync(Bound);
            await caller.WaitAsync(Bound);
            Assert.Equal(1, inner.Disposals);
        }
        finally { inner.DisposeRelease.TrySetResult(); }
    }

    [Fact]
    public async Task RetiringOneSessionLeavesItsSiblingAndFacadeUsable()
    {
        using var source = new Source();
        using var client = new Client();
        await using var facade = new LlmAgentProviderService(source, [client]);
        ILlmAgentSession first = await facade.StartSessionAsync("connection", new() { ModelId = "model" });
        ILlmAgentSession second = await facade.StartSessionAsync("connection", new() { ModelId = "model" });

        await first.DisposeAsync();

        Assert.NotNull(await second.GetCapabilitiesAsync());
        await facade.StartSessionAsync("connection", new() { ModelId = "model" });
        Assert.Equal([1, 0, 0], client.Sessions.Select(session => session.Disposals));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("resume")]
    [InlineData("fork")]
    public async Task CallerCancelledLateOpeningRetiresItsSessionWithoutRetiringTheFacade(string path)
    {
        using var source = new Source();
        using var client = new Client { BlockFromCall = 1, IgnoreOpenCancellation = true };
        await using var facade = new LlmAgentProviderService(source, [client]);
        using var caller = new CancellationTokenSource();
        Task<ILlmAgentSession> opening = path switch
        {
            "start" => facade.StartSessionAsync("connection", new() { ModelId = "model" }, caller.Token),
            "resume" => facade.ResumeSessionAsync("connection", "thread", cancellationToken: caller.Token),
            _ => facade.ForkSessionAsync("connection", "thread", cancellationToken: caller.Token)
        };
        try
        {
            await client.Started.Task.WaitAsync(Bound);
            await caller.CancelAsync();
            client.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(Bound));
            Assert.Equal(1, Assert.Single(client.Sessions).Disposals);

            ILlmAgentSession next = await facade.StartSessionAsync("connection", new() { ModelId = "model" });
            Assert.NotNull(await next.GetCapabilitiesAsync());
        }
        finally { client.Release.TrySetResult(); }
    }

    [Fact]
    public async Task CancellationCallbackFailureStillDrainsAndDisposesTheUnderlyingSession()
    {
        using var source = new Source();
        using var client = new Client();
        var facade = new LlmAgentProviderService(source, [client]);
        ILlmAgentSession session = await facade.StartSessionAsync("connection", new() { ModelId = "model" });
        Session inner = Assert.Single(client.Sessions);
        inner.BlockedMethod = "capture";
        inner.IgnoreCancellation = true;
        Task operation = session.CaptureContextAsync();
        try
        {
            await inner.Started.Task.WaitAsync(Bound);
            using CancellationTokenRegistration registration = inner.OperationToken.Register(() =>
                throw new InvalidOperationException("controlled cancellation callback failure"));
            Task retirement = session.DisposeAsync().AsTask();
            await inner.Stopped.Task.WaitAsync(Bound);
            Assert.False(retirement.IsCompleted);
            inner.Release.TrySetResult();

            await Assert.ThrowsAsync<AggregateException>(() => retirement.WaitAsync(Bound));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(1, inner.Disposals);
            await Assert.ThrowsAsync<AggregateException>(() => facade.DisposeAsync().AsTask().WaitAsync(Bound));
        }
        finally { inner.Release.TrySetResult(); }
    }

    [Fact]
    public async Task FailedSessionRetirementDoesNotSkipOtherSessionsAndRemainsVisibleToTheOwner()
    {
        using var source = new Source();
        using var client = new Client();
        var facade = new LlmAgentProviderService(source, [client]);
        ILlmAgentSession failed = await facade.StartSessionAsync("connection", new() { ModelId = "model" });
        await facade.StartSessionAsync("connection", new() { ModelId = "model" });
        client.Sessions.First().FailDisposal = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.DisposeAsync().AsTask());

        Task owner = facade.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.WaitAsync(Bound));
        Assert.Same(owner, facade.DisposeAsync().AsTask());
        Assert.All(client.Sessions, session => Assert.Equal(1, session.Disposals));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.StartSessionAsync("connection", new() { ModelId = "model" }));
    }

    [Fact]
    public async Task AsyncScopeRetiresItsReturnedSessionAndKeepsDependenciesBorrowed()
    {
        using var source = new Source();
        using var client = new Client();
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(source);
        services.AddSingleton<ILlmAgentClient>(client);
        services.AddScoped<LlmAgentProviderService>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        AsyncServiceScope scope = provider.CreateAsyncScope();
        ILlmAgentSession session = await scope.ServiceProvider.GetRequiredService<LlmAgentProviderService>()
            .StartSessionAsync("connection", new() { ModelId = "model" });

        await scope.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(1, Assert.Single(client.Sessions).Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetCapabilitiesAsync());
        Assert.False(source.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task OwnerWrapperPreservesMetadataResultsAndRequestArguments()
    {
        var inner = new Session();
        await using var session = new OwnedLlmAgentSession(inner, _ => { });
        var context = new LlmAgentContext { Revision = 41 };
        var turn = new LlmAgentTurnRequest { Input = [new LlmAgentTextContent("input")] };
        var capture = new LlmAgentContextCaptureOptions { IncludeAttachmentData = true };

        Assert.Equal(inner.ThreadId, session.ThreadId);
        Assert.Equal(inner.SessionId, session.SessionId);
        Assert.Same(inner.Capabilities, await session.GetCapabilitiesAsync());
        Assert.Same(inner.Accounting, await session.MeasureContextAsync(context));
        Assert.Same(inner.Status, await session.GetContextStatusAsync(turn));
        Assert.Same(inner.Context, await session.CaptureContextAsync(capture));
        await session.ReplaceContextAsync(context, 41);
        Assert.Same(inner.Output, await session.ReadResourceOutputAsync("resource", "cursor"));
        Assert.Equal([context], inner.Arguments["measure"]);
        Assert.Equal([turn], inner.Arguments["status"]);
        Assert.Equal([capture], inner.Arguments["capture"]);
        Assert.Equal([context, 41L], inner.Arguments["replace"]);
        Assert.Equal(["resource", "cursor"], inner.Arguments["read"]);
    }

    private static Task<ILlmAgentSession> OpenAsync(LlmAgentProviderService facade, string path) => path switch
    {
        "start" => facade.StartSessionAsync("connection", new() { ModelId = "model" }),
        "resume" => facade.ResumeSessionAsync("connection", "thread"),
        "fork" => facade.ForkSessionAsync("connection", "thread"),
        _ => throw new ArgumentOutOfRangeException(nameof(path))
    };

    private static Task InvokeAsync(ILlmAgentSession session, string method, CancellationToken token = default) => method switch
    {
        "capabilities" => session.GetCapabilitiesAsync(token),
        "measure" => session.MeasureContextAsync(new(), token),
        "status" => session.GetContextStatusAsync(cancellationToken: token),
        "capture" => session.CaptureContextAsync(cancellationToken: token),
        "replace" => session.ReplaceContextAsync(new(), 0, token),
        "steer" => session.SteerAsync("turn", [], token),
        "interrupt" => session.InterruptAsync("turn", token),
        "resources" => session.GetResourcesAsync(token),
        "read" => session.ReadResourceOutputAsync("resource", "cursor", token),
        "stop" => session.StopResourceAsync("resource", token),
        _ => ReadAsync(Stream(session, method, token))
    };

    private static IAsyncEnumerable<LlmAgentEvent> Stream(ILlmAgentSession session, string method, CancellationToken token = default) => method switch
    {
        "run" => session.RunTurnAsync(new() { Input = [] }, token),
        "continue" => session.ContinueTurnAsync(new("turn", "pause", 0, new()), token),
        "observe" => session.ObserveAsync(token),
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    private static async Task ReadAsync(IAsyncEnumerable<LlmAgentEvent> stream)
    {
        await foreach (LlmAgentEvent _ in stream) { }
    }

    private sealed class Source : ILlmConnectionSource, IDisposable
    {
        internal int Reads { get; private set; }
        internal bool Disposed { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult<LlmConnection?>(new() { Version = "v1", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://provider.test/v1" });
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }

    private sealed class Client : ILlmAgentResumeClient, ILlmAgentForkClient, IDisposable
    {
        private int _calls;
        internal int BlockFromCall { get; init; } = int.MaxValue;
        internal bool IgnoreOpenCancellation { get; init; }
        internal bool Disposed { get; private set; }
        internal ConcurrentQueue<string> Paths { get; } = new();
        internal ConcurrentQueue<Session> Sessions { get; } = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LlmTransport Transport => LlmTransport.OpenAiResponses;

        public Task<ILlmAgentSession> StartSessionAsync(string connectionId, LlmConnection connection,
            LlmAgentSessionOptions options, CancellationToken cancellationToken = default) => OpenAsync("start", cancellationToken);
        public Task<ILlmAgentSession> ResumeSessionAsync(string connectionId, LlmConnection connection,
            string threadId, LlmAgentResumeOptions? options = null, CancellationToken cancellationToken = default) => OpenAsync("resume", cancellationToken);
        public Task<ILlmAgentSession> ForkSessionAsync(string connectionId, LlmConnection connection,
            string threadId, string? lastTurnId = null, LlmAgentResumeOptions? options = null,
            CancellationToken cancellationToken = default) => OpenAsync("fork", cancellationToken);

        private async Task<ILlmAgentSession> OpenAsync(string path, CancellationToken token)
        {
            Paths.Enqueue(path);
            if (Interlocked.Increment(ref _calls) >= BlockFromCall)
            {
                using CancellationTokenRegistration registration = token.Register(() => Stopped.TrySetResult());
                Started.TrySetResult();
                try
                {
                    if (IgnoreOpenCancellation) await Release.Task;
                    else await Release.Task.WaitAsync(token);
                }
                finally
                {
                    if (token.IsCancellationRequested) Stopped.TrySetResult();
                }
            }
            var session = new Session();
            Sessions.Enqueue(session);
            return session;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class Session : ILlmAgentSession
    {
        internal string? BlockedMethod { get; set; }
        internal bool IgnoreCancellation { get; set; }
        internal bool BlockDisposal { get; set; }
        internal bool FailDisposal { get; set; }
        internal int Disposals { get; private set; }
        internal CancellationToken OperationToken { get; private set; }
        internal ConcurrentQueue<string> Calls { get; } = new();
        internal ConcurrentDictionary<string, object?[]> Arguments { get; } = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LlmAgentCapabilities Capabilities { get; } = new();
        internal LlmRequestAccounting Accounting { get; } = new() { ModelId = "model" };
        internal LlmAgentContextStatus Status { get; } = new() { ModelId = "model" };
        internal LlmAgentContext Context { get; } = new();
        internal LlmAgentResourceOutput Output { get; } = new(LlmAgentResourceOutputStatus.Data, new byte[] { 1 }, "next", "first", false);
        public string ThreadId => "thread";
        public string SessionId => "session";

        public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => CallAsync("capabilities", Capabilities, cancellationToken);
        public Task<LlmRequestAccounting> MeasureContextAsync(LlmAgentContext proposedContext, CancellationToken cancellationToken = default) => CallAsync("measure", Accounting, cancellationToken, proposedContext);
        public Task<LlmAgentContextStatus> GetContextStatusAsync(LlmAgentTurnRequest? prospectiveTurn = null, CancellationToken cancellationToken = default) => CallAsync("status", Status, cancellationToken, prospectiveTurn);
        public Task<LlmAgentContext> CaptureContextAsync(LlmAgentContextCaptureOptions? options = null, CancellationToken cancellationToken = default) => CallAsync("capture", Context, cancellationToken, options);
        public Task ReplaceContextAsync(LlmAgentContext context, long expectedRevision, CancellationToken cancellationToken = default) => CallAsync("replace", true, cancellationToken, context, expectedRevision);
        public Task SteerAsync(string turnId, IReadOnlyList<LlmAgentContent> input, CancellationToken cancellationToken = default) => CallAsync("steer", true, cancellationToken, turnId, input);
        public Task InterruptAsync(string turnId, CancellationToken cancellationToken = default) => CallAsync("interrupt", true, cancellationToken, turnId);
        public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default) => CallAsync<IReadOnlyList<LlmAgentResource>>("resources", [], cancellationToken);
        public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(string resourceId, string? cursor, CancellationToken cancellationToken = default) => CallAsync("read", Output, cancellationToken, resourceId, cursor);
        public Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId, CancellationToken cancellationToken = default) => CallAsync("stop", LlmAgentResourceStopStatus.Requested, cancellationToken, resourceId);
        public IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(LlmAgentTurnRequest request, CancellationToken cancellationToken = default) => EventsAsync("run", cancellationToken, request);
        public IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(LlmAgentTurnContinuation continuation, CancellationToken cancellationToken = default) => EventsAsync("continue", cancellationToken, continuation);
        public IAsyncEnumerable<LlmAgentEvent> ObserveAsync(CancellationToken cancellationToken = default) => EventsAsync("observe", cancellationToken);

        private async Task<T> CallAsync<T>(string method, T result, CancellationToken token, params object?[] arguments)
        {
            Calls.Enqueue(method);
            Arguments[method] = arguments;
            if (method == BlockedMethod)
            {
                OperationToken = token;
                using CancellationTokenRegistration registration = token.Register(() => Stopped.TrySetResult());
                Started.TrySetResult();
                try
                {
                    if (IgnoreCancellation) await Release.Task;
                    else await Release.Task.WaitAsync(token);
                }
                finally
                {
                    // WaitAsync can leave before CancelAsync invokes the registered callback.
                    if (token.IsCancellationRequested) Stopped.TrySetResult();
                }
            }
            return result;
        }

        private async IAsyncEnumerable<LlmAgentEvent> EventsAsync(string method,
            [EnumeratorCancellation] CancellationToken token, params object?[] arguments)
        {
            await CallAsync(method, true, token, arguments);
            yield return new LlmAgentTurnStartedEvent(ThreadId, "turn");
        }

        public async ValueTask DisposeAsync()
        {
            Disposals++;
            DisposalStarted.TrySetResult();
            if (BlockDisposal) await DisposeRelease.Task;
            if (FailDisposal) throw new InvalidOperationException("controlled session retirement failure");
        }
    }
}
