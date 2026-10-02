using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Realtime.Tests;

public sealed class RealtimeFacadeLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static RealtimeConnection Connection => new() { Transport = RealtimeTransport.XaiRealtime, Endpoint = new("wss://api.x.ai/v1") };
    private static InferenceStreamLimits Limits => new(2, 3, 4);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Retire(RealtimeProviderService facade) => Assert.IsAssignableFrom<IAsyncDisposable>(facade).DisposeAsync().AsTask();
    private static Task Cleanup(RealtimeProviderService facade) => (object)facade is IAsyncDisposable owner ? owner.DisposeAsync().AsTask() : Task.CompletedTask;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RetirementDrainsIgnoringCallbacksAndSharesCancellationFailure(bool open, bool callbackFails)
    {
        var entered = Signal(); var release = Signal(); var canceled = Signal();
        var cleanupFailure = new InvalidOperationException("Default-context cancellation callback failure.");
        var session = new ControlledRealtimeSession();
        async Task Wait(CancellationToken token)
        {
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                canceled.TrySetResult();
                if (callbackFails) throw cleanupFailure;
            });
            entered.TrySetResult(); await release.Task.ConfigureAwait(false);
        }
        var provider = new ControlledRealtimeProvider
        {
            Capabilities = async (_, _, token) => { await Wait(token).ConfigureAwait(false); return new(); },
            Open = async (_, _, _, _, _, token) => { await Wait(token).ConfigureAwait(false); return session; }
        };
        var facade = new RealtimeProviderService([provider]);
        Task request = open ? facade.OpenSessionAsync(Connection, new(), Limits) : facade.GetCapabilitiesAsync(Connection);
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            retirement = Retire(facade); Task second = Retire(facade);
            Assert.Same(retirement, second); await canceled.Task.WaitAsync(Deadline);
            Assert.False(retirement.IsCompleted); Assert.False(request.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => facade.GetRequiredProvider(RealtimeTransport.XaiRealtime));
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Deadline));
            if (callbackFails)
            {
                Exception? error = await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline));
                Assert.Contains(cleanupFailure, Assert.IsType<AggregateException>(error).Flatten().InnerExceptions);
                Assert.Same(error, await Record.ExceptionAsync(() => second.WaitAsync(Deadline)));
                Assert.Same(error, await Record.ExceptionAsync(() => Retire(facade).WaitAsync(Deadline)));
            }
            else await retirement.WaitAsync(Deadline);
            Assert.Equal(open ? 1 : 0, session.DisposeCalls);
        }
        finally
        {
            retirement ??= Cleanup(facade); release.TrySetResult();
            await Record.ExceptionAsync(() => request.WaitAsync(Deadline));
            await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline));
        }
    }

    [Fact]
    public async Task RetirementDrainsAdmittedSynchronousSetupGetter()
    {
        var entered = Signal(); using var release = new ManualResetEventSlim();
        var provider = new ControlledRealtimeProvider { ReadDefinition = () => { entered.TrySetResult(); release.Wait(); } };
        var facade = new RealtimeProviderService([provider]);
        Task<IReadOnlyList<InferenceSetupDefinition>> read = Task.Run(() => facade.SetupDefinitions);
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(Deadline); retirement = Retire(facade);
            Assert.False(retirement.IsCompleted); release.Set();
            Assert.Single(await read.WaitAsync(Deadline)); await retirement.WaitAsync(Deadline);
        }
        finally
        {
            retirement ??= Cleanup(facade); release.Set();
            await Record.ExceptionAsync(() => read.WaitAsync(Deadline));
            await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateUnhandedSessionCleanupFinishesBeforeRetirement(bool cleanupFails)
    {
        var entered = Signal(); var release = Signal(); var canceled = Signal();
        var cleanupEntered = Signal(); var cleanupRelease = Signal();
        var failure = new InvalidOperationException("Default-context unhanded-session cleanup failure.");
        var session = new HeldSession(async () =>
        {
            cleanupEntered.TrySetResult(); await cleanupRelease.Task.ConfigureAwait(false);
            if (cleanupFails) throw failure;
        });
        var provider = new ControlledRealtimeProvider { Open = async (_, _, _, _, _, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() => canceled.TrySetResult());
            entered.TrySetResult(); await release.Task.ConfigureAwait(false); return session;
        } };
        var facade = new RealtimeProviderService([provider]);
        Task<IRealtimeVoiceSession> opening = facade.OpenSessionAsync(Connection, new(), Limits);
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(Deadline); retirement = Retire(facade); await canceled.Task.WaitAsync(Deadline);
            release.TrySetResult(); await cleanupEntered.Task.WaitAsync(Deadline);
            Assert.False(retirement.IsCompleted); Assert.False(opening.IsCompleted);
            cleanupRelease.TrySetResult(); Exception? error = await Record.ExceptionAsync(() => opening.WaitAsync(Deadline));
            if (cleanupFails)
            {
                var aggregate = Assert.IsType<AggregateException>(error);
                Assert.Contains(aggregate.InnerExceptions, item => item is OperationCanceledException);
                Assert.Contains(failure, aggregate.InnerExceptions);
            }
            else Assert.IsAssignableFrom<OperationCanceledException>(error);
            await retirement.WaitAsync(Deadline); Assert.Equal(1, session.DisposeCalls);
        }
        finally
        {
            retirement ??= Cleanup(facade); release.TrySetResult(); cleanupRelease.TrySetResult();
            await Record.ExceptionAsync(() => opening.WaitAsync(Deadline));
            await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline));
        }
    }

    [Fact]
    public async Task HandedOffSessionStaysCallerOwnedAndEveryNewEntryRefuses()
    {
        var session = new ControlledRealtimeSession();
        var facade = new RealtimeProviderService([new ControlledRealtimeProvider { Open = (_, _, _, _, _, _) => Task.FromResult<IRealtimeVoiceSession>(session) }]);
        Assert.Same(session, await facade.OpenSessionAsync(Connection, new(), Limits));
        Assert.IsAssignableFrom<IDisposable>(facade).Dispose(); await Retire(facade);
        Assert.Equal(0, session.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => facade.SetupDefinitions);
        Assert.Throws<ObjectDisposedException>(() => facade.GetRequiredProvider(RealtimeTransport.XaiRealtime));
        Assert.Throws<ObjectDisposedException>(() => facade.CreateSettingsManager(new ControlledCommitTarget()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.GetCapabilitiesAsync(Connection));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.OpenSessionAsync(Connection, new(), Limits));
        using var canceled = new CancellationTokenSource(); await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => facade.GetCapabilitiesAsync(Connection, cancellationToken: canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => facade.OpenSessionAsync(Connection, new(), Limits, cancellationToken: canceled.Token));
        await session.DisposeAsync(); Assert.Equal(1, session.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedDefaultFacadeReleasesSeparatelyCollectibleBorrowedProvider(bool open)
    {
        Evidence evidence = ExerciseGeneration(open);
        try
        {
            for (int attempt = 0; evidence.Generation.IsAlive && attempt < 30; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10);
            }
            Assert.False(evidence.Generation.IsAlive, "The retained disposed facade roots its separately collectible borrowed provider.");
        }
        finally { GC.KeepAlive(evidence.Facade); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Evidence ExerciseGeneration(bool open)
    {
        var generation = new CollaboratorGeneration(); var weak = new WeakReference(generation);
        try { return new(weak, ExerciseCallerAsync(generation, open).GetAwaiter().GetResult()); }
        finally { generation.Unload(); }
    }

    private static async Task<RealtimeProviderService> ExerciseCallerAsync(AssemblyLoadContext generation, bool open)
    {
        var entered = Signal(); var release = Signal(); var canceled = Signal();
        var executed = new List<(string Method, Guid Mvid)>();
        async Task Wait(CancellationToken token)
        {
            using CancellationTokenRegistration registration = token.Register(() => canceled.TrySetResult());
            entered.TrySetResult(); await release.Task.ConfigureAwait(false);
        }
        var session = new ControlledRealtimeSession();
        async Task<RealtimeCapabilities> Capabilities(CancellationToken token) { await Wait(token).ConfigureAwait(false); return new(); }
        async Task<IRealtimeVoiceSession> Open(CancellationToken token) { await Wait(token).ConfigureAwait(false); return session; }
        Assembly fixture = generation.LoadFromAssemblyPath(typeof(RealtimeFacadeLifetimeTests).Assembly.Location);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
        Assert.Equal(typeof(RealtimeFacadeLifetimeTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
        Type type = fixture.GetType(typeof(ForeignRealtimeProvider).FullName!, true)!;
        Assert.Contains(typeof(IRealtimeProvider), type.GetInterfaces());
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(IRealtimeProvider).Assembly));
        var provider = (IRealtimeProvider)Activator.CreateInstance(type, (Func<CancellationToken, Task<RealtimeCapabilities>>)Capabilities,
            (Func<CancellationToken, Task<IRealtimeVoiceSession>>)Open, (Action<string, Guid>)((method, mvid) => executed.Add((method, mvid))))!;
        var facade = new RealtimeProviderService([provider]);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(facade.GetType().Assembly));
        Assert.Same(provider, facade.GetRequiredProvider(RealtimeTransport.XaiRealtime)); Assert.Single(facade.SetupDefinitions);
        Task request = open ? facade.OpenSessionAsync(Connection, new(), Limits) : facade.GetCapabilitiesAsync(Connection);
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(Deadline); retirement = Retire(facade);
            await canceled.Task.WaitAsync(Deadline); Assert.False(retirement.IsCompleted);
            release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Deadline));
            await retirement.WaitAsync(Deadline);
            Assert.Contains(executed, entry => entry.Method == "Transport");
            Assert.Contains(executed, entry => entry.Method == "SetupDefinition");
            Assert.Contains(executed, entry => entry.Method == (open ? "OpenSessionAsync" : "GetCapabilitiesAsync"));
            Assert.DoesNotContain(executed, entry => entry.Method == "Dispose");
            Assert.All(executed, entry => Assert.Equal(fixture.ManifestModule.ModuleVersionId, entry.Mvid));
            Assert.Equal(open ? 1 : 0, session.DisposeCalls);
            return facade;
        }
        finally
        {
            retirement ??= Cleanup(facade); release.TrySetResult();
            await Record.ExceptionAsync(() => request.WaitAsync(Deadline));
            await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline));
        }
    }

    private sealed record Evidence(WeakReference Generation, RealtimeProviderService Facade);
    private sealed class CollaboratorGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }

    private sealed class HeldSession(Func<Task> cleanup) : IRealtimeVoiceSession
    {
        internal int DisposeCalls { get; private set; }
        public ValueTask DisposeAsync() { DisposeCalls++; return new(cleanup()); }
        public Task UpdateSessionAsync(RealtimeSessionConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AppendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CommitAudioAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearAudioAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddItemAsync(RealtimeConversationItem item, string? previousItemId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteItemAsync(string itemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TruncateItemAsync(string itemId, int contentIndex, TimeSpan audioEnd, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateResponseAsync(RealtimeResponseOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelResponseAsync(string? responseId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SubmitToolResultAsync(RealtimeToolResult result, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<RealtimeEvent> Events => throw new NotSupportedException();
    }

    public sealed class ForeignRealtimeProvider(
        Func<CancellationToken, Task<RealtimeCapabilities>> capabilities,
        Func<CancellationToken, Task<IRealtimeVoiceSession>> open,
        Action<string, Guid> record) : IRealtimeProvider, IDisposable
    {
        private void Record(string method) => record(method, typeof(ForeignRealtimeProvider).Assembly.ManifestModule.ModuleVersionId);
        public RealtimeTransport Transport { get { Record(nameof(Transport)); return RealtimeTransport.XaiRealtime; } }
        public InferenceSetupDefinition SetupDefinition
        {
            get
            {
                Record(nameof(SetupDefinition));
                return new(new("foreign", "Foreign", InferenceImplementationKind.Connect, [InferenceTask.RealtimeVoice]), "Controlled foreign callback");
            }
        }
        public Task<RealtimeCapabilities> GetCapabilitiesAsync(RealtimeConnection connection, string? modelId = null, CancellationToken cancellationToken = default)
        { Record(nameof(GetCapabilitiesAsync)); return capabilities(cancellationToken); }
        public Task<IRealtimeVoiceSession> OpenSessionAsync(RealtimeConnection connection, RealtimeSessionConfig config, InferenceStreamLimits limits,
            XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
        { Record(nameof(OpenSessionAsync)); return open(cancellationToken); }
        public void Dispose() => Record(nameof(Dispose));
    }
}
