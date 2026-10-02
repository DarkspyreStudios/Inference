using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Video.Tests;

public sealed class VideoFacadeLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static VideoConnection Connection => new() { Transport = VideoTransport.XaiImagine, Endpoint = "https://api.x.ai/v1", Xai = new("selected") };
    private static Task Retire(VideoProviderService facade) => Assert.IsAssignableFrom<IAsyncDisposable>(facade).DisposeAsync().AsTask();
    private static Task Cleanup(VideoProviderService facade) => (object)facade is IAsyncDisposable owner ? owner.DisposeAsync().AsTask() : Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementDrainsAdmittedSynchronousGetterAndPreservesItsOutcome(bool fails)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var failure = new InvalidOperationException("Original admitted callback failure.");
        var transport = new ForeignVideoTransport(() => { entered.TrySetResult(); release.Wait(); if (fails) throw failure; }, (_, _) => { });
        var facade = new VideoProviderService([transport]);
        Task<IReadOnlyList<InferenceSetupDefinition>>? request = null; Task? retirement = null;
        try
        {
            request = Task.Run(() => facade.SetupDefinitions);
            await entered.Task.WaitAsync(Deadline); retirement = Retire(facade);
            Assert.Same(retirement, Retire(facade)); Assert.False(retirement.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => facade.SetupDefinitions);
            Assert.Throws<ObjectDisposedException>(() => facade.GetRequiredTransport(VideoTransport.XaiImagine));
            Assert.Throws<ObjectDisposedException>(() => facade.Bind(Connection));
            Assert.Throws<ObjectDisposedException>(() => facade.CreateSettingsManager(new Target()));
            release.Set();
            if (fails) Assert.Same(failure, await Record.ExceptionAsync(() => request.WaitAsync(Deadline)));
            else Assert.Single(await request.WaitAsync(Deadline));
            await retirement.WaitAsync(Deadline); Assert.Same(retirement, Retire(facade));
            Assert.Equal(0, transport.DisposeCalls);
        }
        finally
        {
            retirement ??= Cleanup(facade); release.Set();
            try { if (request is not null) await Record.ExceptionAsync(() => request.WaitAsync(Deadline)); }
            finally { await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline)); }
        }
    }

    [Fact]
    public async Task HandedOffInferenceAndSettingsRemainCallerOwned()
    {
        var transport = new ForeignVideoTransport(null, (_, _) => { });
        var facade = new VideoProviderService([transport]); var target = new Target();
        using var access = new LlmConnectionAccess(new NoSource(), []); var binding = new XaiFamilyBinding(new("selected"), access);
        IVideoInference inference = facade.Bind(Connection, binding);
        VideoSettingsManager manager = facade.CreateSettingsManager(target);
        try
        {
            Assert.IsAssignableFrom<IDisposable>(facade).Dispose(); await Retire(facade);
            Assert.Equal(0, transport.DisposeCalls); Assert.Equal(0, target.DisposeCalls);
            await Assert.IsAssignableFrom<IAsyncDisposable>(inference).DisposeAsync();
            await manager.DisposeAsync(); Assert.Equal(0, transport.DisposeCalls); Assert.Equal(0, target.DisposeCalls);
            transport.Dispose(); Assert.Equal(1, transport.DisposeCalls);
        }
        finally
        {
            try { await Assert.IsAssignableFrom<IAsyncDisposable>(inference).DisposeAsync(); }
            finally { await manager.DisposeAsync(); await Cleanup(facade); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedDefaultFacadeReleasesActualForeignBorrower(bool fails)
    {
        Evidence evidence = ExerciseForeign(fails);
        try
        {
            for (int attempt = 0; evidence.Roots.Any(root => root.IsAlive) && attempt < 30; attempt++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
            foreach (WeakReference root in evidence.Roots) Assert.False(root.IsAlive);
        }
        finally { GC.KeepAlive(evidence.Facade); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Evidence ExerciseForeign(bool fails)
    {
        var generation = new CollaboratorGeneration();
        try
        {
            CallerEvidence caller = ExerciseCallerAsync(generation, fails).GetAwaiter().GetResult();
            return new([new(generation), .. caller.Roots], caller.Facade);
        }
        finally { generation.Unload(); }
    }

    private static async Task<CallerEvidence> ExerciseCallerAsync(AssemblyLoadContext generation, bool fails)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var failure = new InvalidOperationException("Default-context callback failure.");
        var executed = new List<(string Method, Guid Mvid)>();
        void Callback() { entered.TrySetResult(); release.Wait(); if (fails) throw failure; }
        Assembly fixture = generation.LoadFromAssemblyPath(typeof(VideoFacadeLifetimeTests).Assembly.Location);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
        Assert.Equal(typeof(VideoFacadeLifetimeTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
        Type type = fixture.GetType(typeof(ForeignVideoTransport).FullName!, true)!;
        MethodInfo method = type.GetMethod("get_SetupDefinition")!;
        Assert.Same(type, method.DeclaringType); Assert.Equal(fixture.ManifestModule.ModuleVersionId, method.Module.ModuleVersionId);
        Assert.Contains(typeof(IVideoTransport), type.GetInterfaces());
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(IVideoTransport).Assembly));
        var transport = (IVideoTransport)Activator.CreateInstance(type, (Action)Callback,
            (Action<string, Guid>)((name, mvid) => executed.Add((name, mvid))))!;
        var facade = new VideoProviderService([transport]);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(facade.GetType().Assembly));
        Assert.Same(transport, facade.GetRequiredTransport(VideoTransport.XaiImagine));
        Task<IReadOnlyList<InferenceSetupDefinition>>? request = null; Task? retirement = null;
        try
        {
            request = Task.Run(() => facade.SetupDefinitions);
            await entered.Task.WaitAsync(Deadline); retirement = Cleanup(facade); release.Set();
            if (fails) Assert.Same(failure, await Record.ExceptionAsync(() => request.WaitAsync(Deadline)));
            else await request.WaitAsync(Deadline);
            await retirement.WaitAsync(Deadline);
            Assert.Contains(executed, item => item.Method == "SetupDefinition");
            Assert.DoesNotContain(executed, item => item.Method == "Dispose");
            foreach (var item in executed) Assert.Equal(fixture.ManifestModule.ModuleVersionId, item.Mvid);
            return new([new(fixture), new(type), new(method), new(transport)], facade);
        }
        finally
        {
            retirement ??= Cleanup(facade); release.Set();
            try { if (request is not null) await Record.ExceptionAsync(() => request.WaitAsync(Deadline)); }
            finally { await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline)); }
        }
    }

    private sealed record Evidence(WeakReference[] Roots, VideoProviderService Facade);
    private sealed record CallerEvidence(WeakReference[] Roots, VideoProviderService Facade);
    private sealed class CollaboratorGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }
    private sealed class NoSource : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No acquisition.");
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No save.");
    }
    private sealed class Target : ISetupCommitTarget<VideoSetupChanges>, IDisposable
    {
        public int DisposeCalls { get; private set; }
        public Task<SetupCommitResult> CommitAsync(VideoSetupChanges changes, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void Dispose() => DisposeCalls++;
    }
}

public sealed class ForeignVideoTransport(Action? definition, Action<string, Guid> executed) : IVideoTransport, IDisposable
{
    public int DisposeCalls { get; private set; }
    private void Record(string method) => executed(method, typeof(ForeignVideoTransport).Assembly.ManifestModule.ModuleVersionId);
    public VideoTransport Transport { get { Record("Transport"); return VideoTransport.XaiImagine; } }
    public InferenceSetupDefinition SetupDefinition
    {
        get { Record("SetupDefinition"); definition?.Invoke(); return new(new("controlled", "Controlled", InferenceImplementationKind.Connect, InferenceTask.VideoGeneration), "Controlled transport"); }
    }
    public Task<VideoCapabilities> GetCapabilitiesAsync(VideoConnection connection, string? modelId = null, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoJob> SubmitAsync(VideoConnection connection, VideoRequest request, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoJobSnapshot> GetAsync(VideoConnection connection, VideoJob job, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> DownloadAsync(VideoConnection connection, VideoOutput output, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Dispose() { DisposeCalls++; Record("Dispose"); }
}
