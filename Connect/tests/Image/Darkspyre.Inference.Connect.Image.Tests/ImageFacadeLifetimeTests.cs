using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed class ImageFacadeLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static ImageConnection Connection => new() { Transport = ImageTransport.ComfyUi, Endpoint = "http://127.0.0.1:8188" };
    private static Task Retire(ImageProviderService facade) => Assert.IsAssignableFrom<IAsyncDisposable>(facade).DisposeAsync().AsTask();
    private static Task Cleanup(ImageProviderService facade) => (object)facade is IAsyncDisposable owner ? owner.DisposeAsync().AsTask() : Task.CompletedTask;

    [Fact]
    public async Task LiveListingPreservesExactProviderResultAndBorrowing()
    {
        IReadOnlyList<ImageWorkflow> expected = [ForeignImageFacadeTransport.Workflow];
        var transport = new ForeignImageFacadeTransport(_ => Task.FromResult(expected), (_, _) => { });
        var facade = new ImageProviderService([transport]);
        try
        {
            Assert.Same(expected, await facade.GetWorkflowsAsync(Connection));
            Assert.Same(transport, facade.GetRequiredTransport(ImageTransport.ComfyUi));
            Assert.Single(facade.SetupDefinitions); Assert.Single(facade.AvailableTransports);
            await Retire(facade); Assert.Equal(0, transport.DisposeCalls);
        }
        finally { await Cleanup(facade); }
    }

    [Fact]
    public async Task CooperativeListingObservesOwnerCancellationAndRetirementDrainsIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ForeignImageFacadeTransport(async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() => canceled.TrySetResult());
            entered.TrySetResult(); await canceled.Task; await release.Task; token.ThrowIfCancellationRequested(); return [];
        }, (_, _) => { });
        var facade = new ImageProviderService([transport]); Task<IReadOnlyList<ImageWorkflow>>? request = null; Task? retirement = null;
        try
        {
            request = facade.GetWorkflowsAsync(Connection); await entered.Task.WaitAsync(Deadline);
            retirement = Retire(facade); await canceled.Task.WaitAsync(Deadline); Assert.False(retirement.IsCompleted);
            release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Deadline));
            await retirement.WaitAsync(Deadline); Assert.Equal(0, transport.DisposeCalls);
        }
        finally
        {
            retirement ??= Cleanup(facade); canceled.TrySetResult(); release.TrySetResult();
            try { if (request is not null) await Record.ExceptionAsync(() => request.WaitAsync(Deadline)); }
            finally { await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline)); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementDrainsIgnoringListingAndPreservesProviderFailure(bool fails)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Original listing failure.");
        CancellationToken observed = default;
        var transport = new ForeignImageFacadeTransport(async token =>
        { observed = token; entered.TrySetResult(); await release.Task; if (fails) throw failure; return []; }, (_, _) => { });
        var facade = new ImageProviderService([transport]);
        Task<IReadOnlyList<ImageWorkflow>>? request = null; Task? retirement = null;
        try
        {
            request = facade.GetWorkflowsAsync(Connection); await entered.Task.WaitAsync(Deadline);
            retirement = Retire(facade); Assert.Same(retirement, Retire(facade)); Assert.False(retirement.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => facade.GetRequiredTransport(ImageTransport.ComfyUi));
            Assert.Throws<ObjectDisposedException>(() => facade.SetupDefinitions);
            Assert.Throws<ObjectDisposedException>(() => facade.Bind(Connection, ForeignImageFacadeTransport.Workflow));
            Assert.Throws<ObjectDisposedException>(() => facade.CreateSettingsManager(new Target()));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.GetWorkflowsAsync(Connection));
            release.TrySetResult();
            Exception? error = await Record.ExceptionAsync(() => request.WaitAsync(Deadline));
            if (fails) Assert.Same(failure, error); else Assert.IsAssignableFrom<OperationCanceledException>(error);
            await retirement.WaitAsync(Deadline); Assert.True(observed.IsCancellationRequested); Assert.Equal(0, transport.DisposeCalls);
        }
        finally
        {
            retirement ??= Cleanup(facade); release.TrySetResult();
            try { if (request is not null) await Record.ExceptionAsync(() => request.WaitAsync(Deadline)); }
            finally { await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline)); }
        }
    }

    [Fact]
    public async Task SynchronousFenceRejectsListingBeforeLinkedCancellationPropagates()
    {
        using var caller = new CancellationTokenSource(); using var unblockCancellation = new ManualResetEventSlim();
        var targetEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var anchorEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTarget = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAnchor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var transport = new ForeignImageFacadeTransport(async token =>
        {
            if (Interlocked.Increment(ref calls) == 1) { targetEntered.TrySetResult(token); await releaseTarget.Task; }
            else { anchorEntered.TrySetResult(token); await releaseAnchor.Task; }
            return [];
        }, (_, _) => { });
        var facade = new ImageProviderService([transport]);
        Task<IReadOnlyList<ImageWorkflow>>? target = null, anchor = null; Task? retirement = null;
        CancellationTokenRegistration registration = default;
        try
        {
            target = facade.GetWorkflowsAsync(Connection, cancellationToken: caller.Token);
            CancellationToken linked = await targetEntered.Task.WaitAsync(Deadline);
            anchor = facade.GetWorkflowsAsync(Connection);
            CancellationToken ownerToken = await anchorEntered.Task.WaitAsync(Deadline);
            registration = ownerToken.Register(() => { cancellationEntered.TrySetResult(); unblockCancellation.Wait(); });
            retirement = Retire(facade); await cancellationEntered.Task.WaitAsync(Deadline);
            Assert.False(linked.IsCancellationRequested);
            releaseTarget.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.WaitAsync(Deadline));
            Assert.False(retirement.IsCompleted);
            unblockCancellation.Set(); releaseAnchor.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => anchor.WaitAsync(Deadline));
            await retirement.WaitAsync(Deadline); Assert.Equal(0, transport.DisposeCalls);
        }
        finally
        {
            retirement ??= Cleanup(facade); unblockCancellation.Set(); releaseTarget.TrySetResult(); releaseAnchor.TrySetResult();
            try
            {
                if (target is not null) await Record.ExceptionAsync(() => target.WaitAsync(Deadline));
                if (anchor is not null) await Record.ExceptionAsync(() => anchor.WaitAsync(Deadline));
            }
            finally { try { await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline)); } finally { registration.Dispose(); } }
        }
    }

    [Fact]
    public async Task CancellationCallbackFailureIsSharedAndDoesNotDisposeBorrowedTransport()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Original cancellation callback failure.");
        var transport = new ForeignImageFacadeTransport(async token => { entered.TrySetResult(token); await release.Task; return []; }, (_, _) => { });
        var facade = new ImageProviderService([transport]); Task<IReadOnlyList<ImageWorkflow>>? request = null; Task? retirement = null;
        CancellationTokenRegistration registration = default;
        try
        {
            request = facade.GetWorkflowsAsync(Connection); CancellationToken token = await entered.Task.WaitAsync(Deadline);
            registration = token.Register(() => throw failure); retirement = Retire(facade);
            Assert.Same(retirement, Retire(facade)); release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Deadline));
            Exception error = Assert.IsAssignableFrom<Exception>(await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline)));
            Assert.Contains(failure, Assert.IsType<AggregateException>(error).Flatten().InnerExceptions);
            Assert.Same(error, await Record.ExceptionAsync(() => Retire(facade)));
            Assert.Equal(0, transport.DisposeCalls);
        }
        finally
        {
            retirement ??= Cleanup(facade); release.TrySetResult();
            try { if (request is not null) await Record.ExceptionAsync(() => request.WaitAsync(Deadline)); }
            finally { try { await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline)); } finally { registration.Dispose(); } }
        }
    }

    [Fact]
    public async Task CallerCancellationAndReturnedObjectOwnershipRemainIndependent()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ForeignImageFacadeTransport(async _ => { entered.TrySetResult(); await release.Task; return []; }, (_, _) => { });
        var facade = new ImageProviderService([transport]); var target = new Target();
        using var caller = new CancellationTokenSource(); Task<IReadOnlyList<ImageWorkflow>>? request = null;
        var inference = facade.Bind(Connection, ForeignImageFacadeTransport.Workflow);
        ImageSettingsManager manager = facade.CreateSettingsManager(target);
        try
        {
            request = facade.GetWorkflowsAsync(Connection, cancellationToken: caller.Token); await entered.Task.WaitAsync(Deadline);
            caller.Cancel(); release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Deadline));
            Assert.Same(transport, facade.GetRequiredTransport(ImageTransport.ComfyUi));
            Assert.IsAssignableFrom<IDisposable>(facade).Dispose(); await Retire(facade);
            await inference.GetCapabilitiesAsync(); Assert.Equal(0, transport.DisposeCalls); Assert.Equal(0, target.DisposeCalls);
            await manager.DisposeAsync(); Assert.Equal(0, target.DisposeCalls);
            await Assert.IsAssignableFrom<IAsyncDisposable>(inference).DisposeAsync(); Assert.Equal(0, transport.DisposeCalls);
        }
        finally
        {
            Task retirement = Cleanup(facade); release.TrySetResult();
            try { if (request is not null) await Record.ExceptionAsync(() => request.WaitAsync(Deadline)); }
            finally
            {
                try { await manager.DisposeAsync(); }
                finally
                {
                    try { await Assert.IsAssignableFrom<IAsyncDisposable>(inference).DisposeAsync(); }
                    finally { await Record.ExceptionAsync(() => retirement.WaitAsync(Deadline)); }
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedDefaultFacadeReleasesActualForeignCollaborator(bool fails)
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
        Assembly fixture = generation.LoadFromAssemblyPath(typeof(ImageFacadeLifetimeTests).Assembly.Location);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
        Assert.Equal(typeof(ImageFacadeLifetimeTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
        Type type = fixture.GetType(typeof(ForeignImageFacadeTransport).FullName!, true)!;
        MethodInfo method = type.GetMethod(nameof(IImageTransport.GetWorkflowsAsync))!;
        Assert.Same(type, method.DeclaringType); Assert.Equal(fixture.ManifestModule.ModuleVersionId, method.Module.ModuleVersionId);
        Assert.Contains(typeof(IImageTransport), type.GetInterfaces());
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(IImageTransport).Assembly));
        var executed = new List<(string Method, Guid Mvid)>(); var failure = new InvalidOperationException("Default-context provider failure.");
        var transport = (IImageTransport)Activator.CreateInstance(type,
            (Func<CancellationToken, Task<IReadOnlyList<ImageWorkflow>>>)(_ => fails ? Task.FromException<IReadOnlyList<ImageWorkflow>>(failure) : Task.FromResult<IReadOnlyList<ImageWorkflow>>([])),
            (Action<string, Guid>)((name, mvid) => executed.Add((name, mvid))))!;
        var facade = new ImageProviderService([transport]);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(facade.GetType().Assembly));
        try
        {
            if (fails) Assert.Same(failure, await Record.ExceptionAsync(() => facade.GetWorkflowsAsync(Connection)));
            else Assert.Empty(await facade.GetWorkflowsAsync(Connection));
            await Cleanup(facade);
            Assert.Contains(executed, item => item.Method == "GetWorkflowsAsync");
            Assert.DoesNotContain(executed, item => item.Method == "Dispose");
            foreach (var item in executed) Assert.Equal(fixture.ManifestModule.ModuleVersionId, item.Mvid);
            return new([new(fixture), new(type), new(method), new(transport)], facade);
        }
        finally { await Cleanup(facade); }
    }

    private sealed record Evidence(WeakReference[] Roots, ImageProviderService Facade);
    private sealed record CallerEvidence(WeakReference[] Roots, ImageProviderService Facade);
    private sealed class CollaboratorGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }
    private sealed class Target : ISetupCommitTarget<ImageSetupChanges>, IDisposable
    {
        public int DisposeCalls { get; private set; }
        public Task<SetupCommitResult> CommitAsync(ImageSetupChanges changes, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void Dispose() => DisposeCalls++;
    }
}

public sealed class ForeignImageFacadeTransport(Func<CancellationToken, Task<IReadOnlyList<ImageWorkflow>>> listing,
    Action<string, Guid> executed) : IImageTransport, IDisposable
{
    public static ImageWorkflow Workflow => new() { Id = "controlled", Label = "Controlled", Transport = ImageTransport.ComfyUi };
    public int DisposeCalls { get; private set; }
    private void Record(string method) => executed(method, typeof(ForeignImageFacadeTransport).Assembly.ManifestModule.ModuleVersionId);
    public ImageTransportDescriptor Descriptor { get { Record("Descriptor"); return new(ImageTransport.ComfyUi, "Controlled", "Controlled", new(false, false, false) { SupportsBuiltInWorkflows = true }); } }
    public Task<IReadOnlyList<ImageWorkflow>> GetWorkflowsAsync(ImageConnection connection, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    { Record("GetWorkflowsAsync"); return listing(cancellationToken); }
    public Task<ImageCapabilities> GetCapabilitiesAsync(ImageConnection connection, ImageWorkflow workflow, string? modelId = null, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => Task.FromResult(new ImageCapabilities { SupportedSizes = [] });
    public PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request) => throw new NotSupportedException();
    public Task<ImageProviderHealth> TestAsync(ImageConnection connection, ImageWorkflow? workflow = null, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IImageTransportProgressConnection?> TryConnectProgressAsync(ImageConnection connection, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ImagePreparedReference> UploadReferenceAsync(ImageConnection connection, ImageReference reference, Stream content, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ImagePreparedReference> ResolveReferenceAsync(ImageConnection connection, InferenceMediaSource source, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> SubmitAsync(ImageConnection connection, PreparedImageTransportRequest request, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ImageTransportJobSnapshot> GetJobAsync(ImageConnection connection, string providerJobId, ImageWorkflow workflow, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ImageTransportDownload> DownloadAsync(ImageConnection connection, ImageTransportOutput output, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> TryCancelAsync(ImageConnection connection, string providerJobId, bool interruptRunning, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Dispose() { DisposeCalls++; Record("Dispose"); }
}
