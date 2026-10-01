using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Amazon.BedrockAgentCore;
using Amazon.BedrockAgentCore.Model;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockAgentCoreSessionLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static List<WeakReference>? _foreignRoots;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetirementCancelsAndDrainsHeldSdkOpeningOrRead(bool read, bool ignore)
    {
        var invoker = new HeldInvoker(read, ignore);
        ILlmAgentSession session = await SessionAsync(invoker);
        var events = new List<LlmAgentEvent>();
        Task turn = ReadAsync(session.RunTurnAsync(Turn), events);
        await invoker.Started.Task.WaitAsync(Bound);
        Task retirement = session.DisposeAsync().AsTask();
        TrackRoots(session, invoker, turn, retirement);
        try
        {
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, session.DisposeAsync().AsTask());
            await invoker.Stopped.Task.WaitAsync(Bound);
            Assert.True(invoker.Token.IsCancellationRequested);
            Assert.False(invoker.Disposed);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetCapabilitiesAsync());
            invoker.Release.TrySetResult();
            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(Bound));
            Assert.True(invoker.Disposed);
            // Project inside the foreign caller; permanent assertions must not inspect its generic enumerable metadata.
            bool lateText = events.Any(item => item is LlmAgentTextDeltaEvent { Delta: "late" });
            int terminalEvents = events.Count(item => item is LlmAgentTurnCompletedEvent);
            Assert.False(lateText);
            Assert.Equal(0, terminalEvents);
            Assert.Equal(1, invoker.Calls);
        }
        finally
        {
            invoker.Release.TrySetResult();
            await Record.ExceptionAsync(() => turn.WaitAsync(Bound));
            await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementCancelsAndDrainsHeldHostToolWithoutLateReplyOrAnotherInvocation(bool ignore)
    {
        var started = Completion();
        var stopped = Completion();
        var release = Completion();
        CancellationToken toolToken = default;
        var invoker = new ToolInvoker();
        async Task<LlmToolOutput> Tool(LlmAgentToolCall call, CancellationToken token)
        {
            toolToken = token;
            using CancellationTokenRegistration registration = token.Register(() => stopped.TrySetResult());
            started.TrySetResult();
            try { await release.Task.WaitAsync(ignore ? CancellationToken.None : token); }
            finally { await release.Task; }
            return LlmToolOutput.FromText("late tool result");
        }
        ILlmAgentSession session = await SessionAsync(invoker, Tool);
        var events = new List<LlmAgentEvent>();
        Task turn = ReadAsync(session.RunTurnAsync(Turn), events);
        await started.Task.WaitAsync(Bound);
        Task retirement = session.DisposeAsync().AsTask();
        TrackRoots(session, invoker, turn, retirement);
        try
        {
            Assert.False(retirement.IsCompleted);
            await stopped.Task.WaitAsync(Bound);
            Assert.True(toolToken.IsCancellationRequested);
            release.TrySetResult();
            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(Bound));
            Assert.Equal(1, invoker.Calls);
            Assert.Single(events.OfType<LlmAgentToolEvent>());
            Assert.Null(Assert.Single(events.OfType<LlmAgentToolEvent>()).Output);
            int terminalEvents = events.Count(item => item is LlmAgentTurnCompletedEvent);
            Assert.Equal(0, terminalEvents);
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => turn.WaitAsync(Bound));
            await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APausedIteratorMustExitBeforeRetirementAndCannotResumeEffects(bool resume)
    {
        var invoker = new ToolInvoker();
        ILlmAgentSession session = await SessionAsync(invoker);
        IAsyncEnumerator<LlmAgentEvent> reader = session.RunTurnAsync(Turn).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Task retirement = session.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            if (resume) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
            await reader.DisposeAsync();
            await retirement.WaitAsync(Bound);
            Assert.Equal(0, invoker.Calls);
        }
        finally
        {
            await Record.ExceptionAsync(async () => await reader.DisposeAsync());
            await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
        }
    }

    [Fact]
    public async Task RetirementClosesObservationWhileAnIgnoringTurnRemainsOwned()
    {
        var invoker = new HeldInvoker(false, true);
        ILlmAgentSession session = await SessionAsync(invoker);
        var events = new List<LlmAgentEvent>();
        Task turn = ReadAsync(session.RunTurnAsync(Turn), events);
        await invoker.Started.Task.WaitAsync(Bound);
        await using IAsyncEnumerator<LlmAgentEvent> observer = session.ObserveAsync().GetAsyncEnumerator();
        Task<bool> observing = observer.MoveNextAsync().AsTask();
        Task retirement = session.DisposeAsync().AsTask();
        try
        {
            Assert.False(await observing.WaitAsync(Bound));
            Assert.False(retirement.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetResourcesAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ReadResourceOutputAsync("resource", null));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StopResourceAsync("resource"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.MeasureContextAsync(new()));
            // Unsupported operations retain their declared behavior, including after disposal.
            await Assert.ThrowsAsync<NotSupportedException>(() => session.CaptureContextAsync());
            invoker.Release.TrySetResult();
            await retirement.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(Bound));
            Assert.Same(retirement, session.DisposeAsync().AsTask());
            Assert.False(invoker.OwnerDisposed);
        }
        finally
        {
            invoker.Release.TrySetResult();
            await Record.ExceptionAsync(() => turn.WaitAsync(Bound));
            await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
        }
    }

    [Fact]
    public async Task CallerCancellationEndsOnlyItsTurnAndLeavesTheSessionAndBorrowedInvokerUsable()
    {
        var invoker = new HeldInvoker(true, false);
        ILlmAgentSession session = await SessionAsync(invoker);
        using var caller = new CancellationTokenSource();
        Task turn = ReadAsync(session.RunTurnAsync(Turn, caller.Token), []);
        await invoker.Started.Task.WaitAsync(Bound);
        try
        {
            await caller.CancelAsync();
            await invoker.Stopped.Task.WaitAsync(Bound);
            invoker.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(Bound));
            Assert.NotNull(await session.GetCapabilitiesAsync());
            var next = new List<LlmAgentEvent>();
            await ReadAsync(session.RunTurnAsync(Turn), next);
            Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(next.OfType<LlmAgentTurnCompletedEvent>()).Reason);
            Assert.Equal(2, invoker.Calls);
            Assert.False(invoker.Token.IsCancellationRequested);
        }
        finally
        {
            invoker.Release.TrySetResult();
            await Record.ExceptionAsync(() => turn.WaitAsync(Bound));
            await session.DisposeAsync();
        }
        Assert.False(invoker.OwnerDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWhilePausedOnAToolEventFencesTheNextHostOrSdkEffect(bool completed)
    {
        var invoker = new ToolInvoker();
        int toolCalls = 0;
        ILlmAgentSession session = await SessionAsync(invoker, (_, _) =>
        { toolCalls++; return Task.FromResult(LlmToolOutput.FromText("answer")); });
        IAsyncEnumerator<LlmAgentEvent> reader = session.RunTurnAsync(Turn).GetAsyncEnumerator();
        while (await reader.MoveNextAsync())
            if (reader.Current is LlmAgentToolEvent item && (item.Output is not null) == completed) break;
        Assert.IsType<LlmAgentToolEvent>(reader.Current);
        Task retirement = session.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
            await reader.DisposeAsync();
            await retirement.WaitAsync(Bound);
            Assert.Equal(completed ? 1 : 0, toolCalls);
            Assert.Equal(1, invoker.Calls);
        }
        finally
        {
            await Record.ExceptionAsync(async () => await reader.DisposeAsync());
            await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
        }
    }

    [Fact]
    public async Task RetirementSharesCancellationCallbackFailureAfterTheOperationAndFeedDrain()
    {
        var invoker = new HeldInvoker(false, true, throwsOnCancel: true);
        ILlmAgentSession session = await SessionAsync(invoker);
        Task turn = ReadAsync(session.RunTurnAsync(Turn), []);
        await invoker.Started.Task.WaitAsync(Bound);
        await using IAsyncEnumerator<LlmAgentEvent> observer = session.ObserveAsync().GetAsyncEnumerator();
        Task<bool> observing = observer.MoveNextAsync().AsTask();
        Task retirement = session.DisposeAsync().AsTask();
        try
        {
            await invoker.Stopped.Task.WaitAsync(Bound);
            Assert.False(await observing.WaitAsync(Bound));
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, session.DisposeAsync().AsTask());
            invoker.Release.TrySetResult();
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => retirement.WaitAsync(Bound));
            Assert.Contains(failure.Flatten().InnerExceptions, error => error.Message == "controlled cancellation callback failure");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(Bound));
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => session.DisposeAsync().AsTask()));
            Assert.False(invoker.OwnerDisposed);
        }
        finally
        {
            invoker.Release.TrySetResult();
            await Record.ExceptionAsync(() => turn.WaitAsync(Bound));
            await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalSdkOrReaderFailureRemainsVisibleAndDoesNotStrandTheTurnLease(bool cleanup)
    {
        var invoker = new HeldInvoker(false, true, throwsOnRelease: !cleanup, throwsOnCleanup: cleanup);
        ILlmAgentSession session = await SessionAsync(invoker);
        Task turn = ReadAsync(session.RunTurnAsync(Turn), []);
        await invoker.Started.Task.WaitAsync(Bound);
        invoker.Release.TrySetResult();
        IOException error = await Assert.ThrowsAsync<IOException>(() => turn.WaitAsync(Bound));
        Assert.Equal(cleanup ? "controlled reader disposal failure" : "controlled SDK failure", error.Message);
        await session.DisposeAsync().AsTask().WaitAsync(Bound);
        Assert.False(invoker.OwnerDisposed);
    }

    [Fact]
    public async Task StreamsCreatedBeforeRetirementRejectTheirFirstReadWithoutInvokingSdk()
    {
        var invoker = new ToolInvoker();
        ILlmAgentSession session = await SessionAsync(invoker);
        IAsyncEnumerable<LlmAgentEvent> turn = session.RunTurnAsync(Turn);
        IAsyncEnumerable<LlmAgentEvent> feed = session.ObserveAsync();
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ReadAsync(turn, []));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ReadAsync(feed, []));
        Assert.Equal(0, invoker.Calls);
    }

    [Theory]
    [InlineData("sdk")]
    [InlineData("tools")]
    [InlineData("paused")]
    [InlineData("observation")]
    [InlineData("caller")]
    [InlineData("failure")]
    [InlineData("unstarted")]
    public void ActualForeignSessionSdkToolAndObservationCallersReleaseTheirCollectibleGeneration(string caller)
    {
        (WeakReference generation, WeakReference[] roots) = ExerciseGeneration(caller);
        for (int attempt = 0; generation.IsAlive && attempt < 30; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.All(roots, root => Assert.False(root.IsAlive, "A settled session, invoker, turn, retirement or owner CTS remains rooted."));
        Assert.False(generation.IsAlive, "Settled direct AgentCore callers retain their actual Bedrock/Connect/Common generation.");
    }

    private static async Task<WeakReference[]> ForeignCallerAsync(string caller)
    {
        _foreignRoots = [];
        AssemblyLoadContext? generation = AssemblyLoadContext.GetLoadContext(typeof(BedrockAgentCoreSession).Assembly);
        Assert.True(generation!.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmProviderService).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmAttachment).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(BedrockAgentCoreSessionLifetimeTests).Assembly));
        var fixture = new BedrockAgentCoreSessionLifetimeTests();
        switch (caller)
        {
            case "sdk":
                await fixture.RetirementCancelsAndDrainsHeldSdkOpeningOrRead(false, false);
                await fixture.RetirementCancelsAndDrainsHeldSdkOpeningOrRead(true, true);
                break;
            case "tools":
                await fixture.RetirementCancelsAndDrainsHeldHostToolWithoutLateReplyOrAnotherInvocation(false);
                await fixture.RetirementCancelsAndDrainsHeldHostToolWithoutLateReplyOrAnotherInvocation(true);
                break;
            case "paused":
                await fixture.APausedIteratorMustExitBeforeRetirementAndCannotResumeEffects(false);
                await fixture.APausedIteratorMustExitBeforeRetirementAndCannotResumeEffects(true);
                await fixture.CancellationWhilePausedOnAToolEventFencesTheNextHostOrSdkEffect(false);
                await fixture.CancellationWhilePausedOnAToolEventFencesTheNextHostOrSdkEffect(true);
                break;
            case "observation":
                await fixture.RetirementClosesObservationWhileAnIgnoringTurnRemainsOwned();
                break;
            case "caller":
                await fixture.CallerCancellationEndsOnlyItsTurnAndLeavesTheSessionAndBorrowedInvokerUsable();
                break;
            case "failure":
                await fixture.RetirementSharesCancellationCallbackFailureAfterTheOperationAndFeedDrain();
                await fixture.TerminalSdkOrReaderFailureRemainsVisibleAndDoesNotStrandTheTurnLease(false);
                await fixture.TerminalSdkOrReaderFailureRemainsVisibleAndDoesNotStrandTheTurnLease(true);
                break;
            case "unstarted":
                await fixture.StreamsCreatedBeforeRetirementRejectTheirFirstReadWithoutInvokingSdk();
                break;
            default: throw new ArgumentException("Unknown controlled caller.", nameof(caller));
        }
        WeakReference[] roots = _foreignRoots.ToArray();
        _foreignRoots = null;
        return roots;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Context, WeakReference[] Roots) ExerciseGeneration(string selectedCaller)
    {
        string directory = Path.GetDirectoryName(typeof(BedrockAgentCoreSessionLifetimeTests).Assembly.Location)!;
        var generation = new Generation(directory);
        var weak = new WeakReference(generation);
        WeakReference[] roots;
        try
        {
            Assembly bedrock = generation.LoadFromAssemblyPath(typeof(BedrockAgentCoreSession).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(LlmProviderService).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(LlmAttachment).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(BedrockAgentCoreSessionLifetimeTests).Assembly.Location);
            foreach (Assembly assembly in new[] { bedrock, connect, common, fixture }) Assert.Same(generation, AssemblyLoadContext.GetLoadContext(assembly));
            Type caller = fixture.GetType(typeof(BedrockAgentCoreSessionLifetimeTests).FullName!, true)!;
            var task = (Task<WeakReference[]>)caller.GetMethod(nameof(ForeignCallerAsync), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [selectedCaller])!;
            roots = task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return (weak, roots);
    }

    private static void TrackRoots(ILlmAgentSession session, object invoker, Task turn, Task retirement)
    {
        if (_foreignRoots is null) return;
        _foreignRoots.AddRange([new(session), new(invoker), new(turn), new(retirement)]);
        object lifetime = session.GetType().GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        object stop = lifetime.GetType().GetField("_stop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lifetime)!;
        _foreignRoots.Add(new(stop));
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly LlmAgentTurnRequest Turn = new() { Input = [new LlmAgentTextContent("question")] };

    private static Task<ILlmAgentSession> SessionAsync(IBedrockAgentCoreInvoker invoker, LlmAgentToolHandler? tool = null) =>
        new BedrockAgentCoreLlmClient(invoker).StartSessionAsync("connection", new()
        {
            Version = "v1",
            Transport = LlmTransport.AwsBedrockAgentCore,
            Endpoint = "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/test-harness",
            Credential = new LlmCredential { Aws = new() { Region = "us-east-1", Profile = "controlled" } }.Serialize()
        }, new()
        {
            ModelId = "default",
            Runtime = new() { WorkingDirectory = Environment.CurrentDirectory, Policy = new LlmAgentHostRuntimePolicy { Sandbox = LlmAgentSandboxMode.ReadOnly, ApprovalPolicy = LlmAgentApprovalPolicy.Never } },
            Tools = tool is null ? [] : [new() { Name = "lookup", Description = "Controlled lookup", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = tool
        });

    private static async Task ReadAsync(IAsyncEnumerable<LlmAgentEvent> stream, List<LlmAgentEvent> events)
    { await foreach (LlmAgentEvent item in stream) events.Add(item); }

    private sealed class HeldInvoker(bool read, bool ignore, bool throwsOnCancel = false, bool throwsOnRelease = false, bool throwsOnCleanup = false) : IBedrockAgentCoreInvoker, IDisposable
    {
        internal TaskCompletionSource Started { get; } = Completion();
        internal TaskCompletionSource Stopped { get; } = Completion();
        internal TaskCompletionSource Release { get; } = Completion();
        internal CancellationToken Token { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool OwnerDisposed { get; private set; }
        internal int Calls { get; private set; }
        [SuppressMessage("Usage", "CA2219", Justification = "The controlled SDK reader injects a disposal failure to verify observable failure and lease drainage.")]
        public async IAsyncEnumerable<IEventStreamEvent> InvokeAsync(LlmAwsCredentials aws, InvokeHarnessRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            Token = cancellationToken;
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                Stopped.TrySetResult();
                if (throwsOnCancel) throw new InvalidOperationException("controlled cancellation callback failure");
            });
            try
            {
                if (read) yield return new HarnessContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { Text = "initial" } };
                Started.TrySetResult();
                await Release.Task.WaitAsync(ignore ? CancellationToken.None : cancellationToken);
                if (throwsOnRelease) throw new IOException("controlled SDK failure");
                yield return new HarnessContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { Text = "late" } };
                yield return new HarnessMessageStopEvent { StopReason = HarnessStopReason.End_turn };
            }
            finally
            {
                await Release.Task;
                Disposed = true;
                if (throwsOnCleanup) throw new IOException("controlled reader disposal failure");
            }
        }
        public void Dispose() => OwnerDisposed = true;
    }

    private sealed class ToolInvoker : IBedrockAgentCoreInvoker
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<IEventStreamEvent> InvokeAsync(LlmAwsCredentials aws, InvokeHarnessRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new HarnessContentBlockStartEvent { ContentBlockIndex = 0, Start = new() { ToolUse = new() { ToolUseId = "call", Name = "lookup", Type = HarnessToolUseType.Tool_use } } };
            yield return new HarnessMessageStopEvent { StopReason = HarnessStopReason.Tool_use };
        }
    }
}
