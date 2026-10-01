using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedBorrowedAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedRetiredManagedSessionReleasesSeparatelyForeignBorrowedAuthorization(bool callbackFailure)
    {
        var retained = await ExerciseCaller(callbackFailure);
        for (int attempt = 0; retained.Generation.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10); }
        Assert.False(retained.Generation.IsAlive);
        Assert.Equal(1, retained.Tracker.Calls);
        Assert.Equal(0, retained.Tracker.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => retained.Session.GetCapabilitiesAsync());
        GC.KeepAlive(retained.Session);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Retained> ExerciseCaller(bool callbackFailure)
    {
        var tracker = new Tracker();
        var generation = new CollaboratorGeneration();
        var weak = new WeakReference(generation);
        var fixture = generation.LoadFromAssemblyPath(typeof(HostedAuthorizationFixture).Assembly.Location);
        Assert.Equal(typeof(HostedAuthorizationFixture).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
        var authorization = (IProviderAuthorization)Activator.CreateInstance(fixture.GetType(typeof(HostedAuthorizationFixture).FullName!)!,
            (Action)(() => tracker.Calls++), (Action)(() => tracker.Disposals++))!;
        Assert.Same(typeof(IProviderAuthorization), authorization.GetType().GetInterfaces().Single(type => type == typeof(IProviderAuthorization)));
        var harness = new Harness(callbackFailure);
        var session = new ManagedAgentSession(new() { Transport = LlmTransport.OpenAiResponses, Version = "1", Endpoint = "https://example.test" },
            new() { ModelId = "model", HostedTools = [new LlmRemoteMcpTool("https://example.test", "server", Authorization: authorization)] },
            [], harness, harness.Stream, _ => new()
            {
                SupportsAgentSessions = true,
                HostedTools = new HashSet<LlmHostedToolKind> { LlmHostedToolKind.RemoteMcp },
                Agent = ManagedAgentPolicy.Capabilities
            });
        Task turn = Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")] }));
        Task? retirement = null;
        Task? repeated = null;
        try
        {
            await harness.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, tracker.Calls);
            retirement = session.DisposeAsync().AsTask();
            repeated = session.DisposeAsync().AsTask();
            await harness.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(retirement.IsCompleted); Assert.False(repeated.IsCompleted);
            generation.Unload();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.True(weak.IsAlive);
            harness.Release.TrySetResult();
            if (callbackFailure)
            {
                var failure = await Assert.ThrowsAsync<AggregateException>(() => retirement);
                Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => repeated));
                Assert.Same(harness.Failure, Assert.Single(failure.Flatten().InnerExceptions));
            }
            else { await retirement; await repeated; }
            await turn;
            Assert.Equal(0, tracker.Disposals);
        }
        finally
        {
            retirement ??= session.DisposeAsync().AsTask();
            harness.Release.TrySetResult();
            await Drain(turn); await Drain(retirement);
            if (repeated is not null) await Drain(repeated);
            generation.Unload();
        }
        return new(weak, session, tracker);
    }

    private static async Task Read(IAsyncEnumerable<LlmAgentEvent> stream)
    { await foreach (var unused in stream) { } }
    private static async Task Drain(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) when (task.IsCompleted) { }
    }

    private sealed record Retained(WeakReference Generation, ManagedAgentSession Session, Tracker Tracker);
    private sealed class Tracker { internal int Calls; internal int Disposals; }

    private sealed class Harness(bool callbackFailure) : ILlmContextEstimator
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal InvalidOperationException Failure { get; } = new("default-context cancellation failure");
        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request) => new(1);
        public string? EstimateDiagnostic => null;
        internal async IAsyncEnumerable<LlmStreamEvent> Stream(LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            var authorization = Assert.IsType<LlmRemoteMcpTool>(Assert.Single(request.HostedTools)).Authorization!;
            Assert.Equal("fixture-token", (await authorization.GetAsync(token)).Value);
            using CancellationTokenRegistration registration = token.Register(() =>
            { Canceled.TrySetResult(); if (callbackFailure) throw Failure; });
            Entered.TrySetResult();
            await Release.Task;
            token.ThrowIfCancellationRequested();
            yield return new LlmChatCompletionEvent(new() { Text = "done", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
        }
    }

    private sealed class CollaboratorGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IProviderAuthorization).Assembly.GetName().Name) return typeof(IProviderAuthorization).Assembly;
            if (name.Name == typeof(ConnectLlmJson).Assembly.GetName().Name) return typeof(ConnectLlmJson).Assembly;
            return null;
        }
    }
}

public sealed class HostedAuthorizationFixture(Action called, Action disposed) : IProviderAuthorization, IDisposable
{
    public ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); called(); return ValueTask.FromResult(new ProviderAuthorizationHeader("Bearer", "fixture-token")); }
    public void Dispose() => disposed();
}
