using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.STT.Tests;

public sealed class SttFacadeLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static SttConnection Connection => new() { Transport = SttTransport.XaiStt, Endpoint = new("https://api.x.ai/v1"), Xai = new("selected") };
    private static Task Retire(SttProviderService facade) => Assert.IsAssignableFrom<IAsyncDisposable>(facade).DisposeAsync().AsTask();
    private static Task Cleanup(SttProviderService facade) => (object)facade is IAsyncDisposable owner ? owner.DisposeAsync().AsTask() : Task.CompletedTask;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetirementDrainsAdmittedSynchronousCallbacksAndPreservesTheirOutcome(bool bind, bool fails)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var failure = new InvalidOperationException("Original admitted callback failure.");
        void Callback() { entered.TrySetResult(); release.Wait(); if (fails) throw failure; }
        var provider = new ForeignSttProvider(bind ? null : Callback, bind ? Callback : null, (_, _) => { });
        var facade = new SttProviderService([provider]);
        using var access = new LlmConnectionAccess(new NoSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        Task<object>? request = null; Task? retirement = null;
        try
        {
            request = Task.Run<object>(() => bind ? facade.Bind(Connection, binding) : facade.SetupDefinitions);
            await entered.Task.WaitAsync(Deadline);
            retirement = Retire(facade);
            Assert.Same(retirement, Retire(facade));
            Assert.False(retirement.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => facade.SetupDefinitions);
            Assert.Throws<ObjectDisposedException>(() => facade.GetRequiredProvider(SttTransport.XaiStt));
            Assert.Throws<ObjectDisposedException>(() => facade.Bind(Connection, binding));
            Assert.Throws<ObjectDisposedException>(() => facade.CreateSettingsManager(new Target()));
            release.Set();
            if (fails) Assert.Same(failure, await Record.ExceptionAsync(() => request.WaitAsync(Deadline)));
            else if (bind) Assert.Same(provider, await request.WaitAsync(Deadline));
            else Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<InferenceSetupDefinition>>(await request.WaitAsync(Deadline)));
            await retirement.WaitAsync(Deadline);
            Assert.Same(retirement, Retire(facade));
            Assert.Equal(0, provider.DisposeCalls);
        }
        finally
        {
            retirement ??= Cleanup(facade); release.Set();
            try { if (request is not null) await Record.ExceptionAsync(() => request.WaitAsync(Deadline)); }
            finally { await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline)); }
        }
    }

    [Fact]
    public async Task HandedOffClientAndSettingsRemainCallerOwned()
    {
        var provider = new ForeignSttProvider(null, null, (_, _) => { });
        var facade = new SttProviderService([provider]); var target = new Target();
        using var access = new LlmConnectionAccess(new NoSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        ISpeechTranscriptionClient client = facade.Bind(Connection, binding);
        SttSettingsManager manager = facade.CreateSettingsManager(target);
        try
        {
            Assert.IsAssignableFrom<IDisposable>(facade).Dispose(); await Retire(facade);
            Assert.Same(provider, client); Assert.Equal(0, provider.DisposeCalls); Assert.Equal(0, target.DisposeCalls);
            Assert.False((await client.GetCapabilitiesAsync()).Transcription.Available);
            await manager.DisposeAsync(); Assert.Equal(0, target.DisposeCalls);
            provider.Dispose(); Assert.Equal(1, provider.DisposeCalls);
        }
        finally { await manager.DisposeAsync(); await Cleanup(facade); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RetainedDefaultFacadeReleasesActualForeignBorrower(bool bind, bool fails)
    {
        Evidence evidence = ExerciseForeign(bind, fails);
        try
        {
            for (int attempt = 0; evidence.Roots.Any(root => root.IsAlive) && attempt < 30; attempt++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
            foreach (WeakReference root in evidence.Roots) Assert.False(root.IsAlive);
        }
        finally { GC.KeepAlive(evidence.Facade); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Evidence ExerciseForeign(bool bind, bool fails)
    {
        var generation = new CollaboratorGeneration();
        try
        {
            CallerEvidence caller = ExerciseCallerAsync(generation, bind, fails).GetAwaiter().GetResult();
            return new([new(generation), .. caller.Roots], caller.Facade);
        }
        finally { generation.Unload(); }
    }

    private static async Task<CallerEvidence> ExerciseCallerAsync(AssemblyLoadContext generation, bool bind, bool fails)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var failure = new InvalidOperationException("Default-context callback failure.");
        var executed = new List<(string Method, Guid Mvid)>();
        void Callback() { entered.TrySetResult(); release.Wait(); if (fails) throw failure; }
        Assembly fixture = generation.LoadFromAssemblyPath(typeof(SttFacadeLifetimeTests).Assembly.Location);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
        Assert.Equal(typeof(SttFacadeLifetimeTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
        Type type = fixture.GetType(typeof(ForeignSttProvider).FullName!, true)!;
        MethodInfo method = type.GetMethod(bind ? nameof(ISttProvider.Bind) : "get_SetupDefinition")!;
        Assert.Same(type, method.DeclaringType); Assert.Equal(fixture.ManifestModule.ModuleVersionId, method.Module.ModuleVersionId);
        Assert.Contains(typeof(ISttProvider), type.GetInterfaces());
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(ISttProvider).Assembly));
        var provider = (ISttProvider)Activator.CreateInstance(type, bind ? null : (Action)Callback, bind ? (Action)Callback : null,
            (Action<string, Guid>)((name, mvid) => executed.Add((name, mvid))))!;
        var facade = new SttProviderService([provider]);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(facade.GetType().Assembly));
        Assert.Same(provider, facade.GetRequiredProvider(SttTransport.XaiStt));
        using var access = new LlmConnectionAccess(new NoSource(), []); var binding = new XaiFamilyBinding(new("selected"), access);
        Task<object>? request = null; Task? retirement = null;
        try
        {
            request = Task.Run<object>(() => bind ? facade.Bind(Connection, binding) : facade.SetupDefinitions);
            await entered.Task.WaitAsync(Deadline); retirement = Cleanup(facade);
            release.Set();
            if (fails) Assert.Same(failure, await Record.ExceptionAsync(() => request.WaitAsync(Deadline)));
            else await request.WaitAsync(Deadline);
            await retirement.WaitAsync(Deadline);
            Assert.Contains(executed, item => item.Method == (bind ? "Bind" : "SetupDefinition"));
            Assert.DoesNotContain(executed, item => item.Method == "Dispose");
            foreach (var item in executed) Assert.Equal(fixture.ManifestModule.ModuleVersionId, item.Mvid);
            return new([new(fixture), new(type), new(method), new(provider)], facade);
        }
        finally
        {
            retirement ??= Cleanup(facade); release.Set();
            try { if (request is not null) await Record.ExceptionAsync(() => request.WaitAsync(Deadline)); }
            finally { await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline)); }
        }
    }

    private sealed record Evidence(WeakReference[] Roots, SttProviderService Facade);
    private sealed record CallerEvidence(WeakReference[] Roots, SttProviderService Facade);
    private sealed class CollaboratorGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }
    private sealed class NoSource : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No acquisition.");
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No save.");
    }
    private sealed class Target : ISetupCommitTarget<SttSetupChanges>, IDisposable
    {
        public int DisposeCalls { get; private set; }
        public Task<SetupCommitResult> CommitAsync(SttSetupChanges changes, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void Dispose() => DisposeCalls++;
    }
}

public sealed class ForeignSttProvider(Action? definition, Action? bind, Action<string, Guid> executed) : ISttProvider, ISpeechTranscriptionClient, IDisposable
{
    public int DisposeCalls { get; private set; }
    private void Record(string method) => executed(method, typeof(ForeignSttProvider).Assembly.ManifestModule.ModuleVersionId);
    public SttTransport Transport { get { Record("Transport"); return SttTransport.XaiStt; } }
    public InferenceSetupDefinition SetupDefinition
    {
        get { Record("SetupDefinition"); definition?.Invoke(); return new(new("controlled", "Controlled", InferenceImplementationKind.Connect, InferenceTask.SpeechTranscription), "Controlled provider"); }
    }
    public ISpeechTranscriptionClient Bind(SttConnection connection, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null)
    { Record("Bind"); bind?.Invoke(); return this; }
    public Task<SpeechTranscriptionCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) => Task.FromResult(new SpeechTranscriptionCapabilities());
    public Task<SpeechTranscriptionResult> TranscribeAsync(SpeechTranscriptionInput input, SpeechTranscriptionOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ISpeechTranscriptionSession> OpenStreamAsync(SpeechStreamOptions options, InferenceStreamLimits limits, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<SpeechTranscriptEvent> TranscribeStreamAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> audio, SpeechStreamOptions options, InferenceStreamLimits limits, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Dispose() { DisposeCalls++; Record("Dispose"); }
}
