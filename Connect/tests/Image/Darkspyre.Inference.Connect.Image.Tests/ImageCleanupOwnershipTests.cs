using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed class ImageCleanupOwnershipTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("cleanup")]
    [InlineData("commit-cleanup")]
    public async Task ActualWriterRefusalReachesOneCachedRetirementOutcome(string outcome)
    {
        var resources = new ImageTestResources { Outcome = outcome };
        var transport = new ImageTestTransport();
        IImageInference inference = new ImageProviderService([transport]).Bind(ImageOperationTests.Connection(),
            ImageOperationTests.Workflow(), resources: resources, pollInterval: TimeSpan.Zero);
        Exception? operation = await Record.ExceptionAsync(() => inference.GenerateAsync(ImageOperationTests.Request()));
        Assert.NotNull(operation);
        Exception? retirement = await Record.ExceptionAsync(() => ((IAsyncDisposable)inference).DisposeAsync().AsTask());
        var cleanup = Assert.IsType<InferenceResourceOperationException>(retirement);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
        Assert.Same(resources.CleanupFailure, cleanup.InnerException);
        Assert.Equal(resources.Writers[0].Reference, cleanup.Failure.Reference);
        Assert.Equal(outcome == "cleanup" ? InferenceResourceCommitOutcome.Committed : InferenceResourceCommitOutcome.Unknown,
            cleanup.Failure.CommitOutcome);
        Assert.Same(cleanup, await Record.ExceptionAsync(() => ((IAsyncDisposable)inference).DisposeAsync().AsTask()));
        Assert.Same(cleanup, Record.Exception(() => ((IDisposable)inference).Dispose()));
        Assert.Equal(1, resources.Writers[0].Disposals);
        Assert.Equal(0, resources.Disposals);
        Assert.Equal(0, transport.Disposals);
    }

    [Fact]
    public async Task SuccessfulCommitCleanupReportsCleanupPhaseAndKeepsActualSelection()
    {
        var resources = new ImageTestResources { Outcome = "cleanup" };
        IImageInference inference = new ImageProviderService([new ImageTestTransport()]).Bind(ImageOperationTests.Connection(),
            ImageOperationTests.Workflow(), resources: resources, pollInterval: TimeSpan.Zero);
        try
        {
            var failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => inference.GenerateAsync(ImageOperationTests.Request()));
            Assert.Equal(InferenceResourceOperationPhase.Cleanup, failure.Failure.Phase);
            Assert.Equal(InferenceResourceCommitOutcome.Committed, failure.Failure.CommitOutcome);
            Assert.Equal(InferenceResourceCommitOutcome.Committed, failure.Failure.SelectionOutcome);
            Assert.Equal(new InferenceResourceSelectionVersion("workspace", "session", 1), failure.Failure.SelectionVersion);
            Assert.Equal(2, failure.Failure.BytesTransferred);
        }
        finally { _ = await Record.ExceptionAsync(() => ((IAsyncDisposable)inference).DisposeAsync().AsTask()); }
    }

    [Fact]
    public async Task OrdinaryCommitFailureDoesNotPoisonRetirement()
    {
        var resources = new ImageTestResources { Outcome = "commit" };
        IImageInference inference = new ImageProviderService([new ImageTestTransport()]).Bind(ImageOperationTests.Connection(),
            ImageOperationTests.Workflow(), resources: resources, pollInterval: TimeSpan.Zero);
        _ = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => inference.GenerateAsync(ImageOperationTests.Request()));
        await ((IAsyncDisposable)inference).DisposeAsync();
        ((IDisposable)inference).Dispose();
        Assert.Equal(1, resources.Writers[0].Disposals);
        Assert.Equal(0, resources.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementDrainsIgnoringCleanupAndPreservesActualCommit(bool refuses)
    {
        var writer = new ImageCleanupForeignWriter { CleanupRelease = new(), CleanupFailure = refuses ? new IOException("refused") : null };
        var resources = new ImageCleanupForeignAccess(writer);
        IImageInference inference = Bind(resources);
        Task<ImageGenerationResult> pending = inference.GenerateAsync(ImageOperationTests.Request());
        Task? retirement = null;
        try
        {
            await writer.CleanupEntered.Task.WaitAsync(Bound);
            retirement = ((IAsyncDisposable)inference).DisposeAsync().AsTask();
            Assert.Same(retirement, ((IAsyncDisposable)inference).DisposeAsync().AsTask());
            Assert.False(retirement.IsCompleted);
            writer.CleanupRelease.SetResult();
            Exception? error = await Record.ExceptionAsync(() => pending.WaitAsync(Bound));
            var facts = refuses ? Assert.IsType<InferenceResourceOperationException>(error).Failure
                : Assert.IsType<InferenceResourceOperationCanceledException>(error).Failure;
            Assert.Equal(InferenceResourceCommitOutcome.Committed, facts.CommitOutcome);
            Assert.Equal(InferenceResourceCommitOutcome.Committed, facts.SelectionOutcome);
            Assert.Equal(new InferenceResourceSelectionVersion("workspace", "session", 7), facts.SelectionVersion);
            Exception? retired = await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
            if (refuses) Assert.Same(error, retired); else Assert.Null(retired);
            Assert.Equal(1, writer.Disposals);
            Assert.Equal(0, resources.Disposals);
        }
        finally
        {
            writer.CleanupRelease.TrySetResult();
            _ = await Record.ExceptionAsync(() => pending.WaitAsync(Bound));
            _ = await Record.ExceptionAsync(() => ((IAsyncDisposable)inference).DisposeAsync().AsTask().WaitAsync(Bound));
        }
    }

    [Theory]
    [InlineData("scope-open")]
    [InlineData("scope-cleanup")]
    [InlineData("bytes")]
    [InlineData("mime")]
    [InlineData("selection")]
    public async Task CallerScopeAndReceiptContradictionsNeverPublish(string fault)
    {
        var writer = new ImageCleanupForeignWriter();
        var resources = new ImageCleanupForeignAccess(writer);
        if (fault == "scope-open") resources.AfterOpen = () => resources.SessionId = "changed";
        if (fault == "scope-cleanup") writer.AfterCleanup = () => resources.SessionId = "changed";
        if (fault == "bytes") writer.Transform = result => result with { Resource = result.Resource with { Bytes = 8 } };
        if (fault == "mime") writer.Transform = result => result with { Resource = result.Resource with { ContentType = "other/mime" } };
        if (fault == "selection") writer.Transform = result => result with { Selection = null };
        IImageInference inference = Bind(resources);
        try
        {
            var error = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => inference.GenerateAsync(ImageOperationTests.Request()));
            Assert.Equal(fault == "scope-open" ? InferenceResourceCommitOutcome.NotAttempted : InferenceResourceCommitOutcome.Committed,
                error.Failure.CommitOutcome);
            Assert.Equal(fault == "scope-open" ? 0 : 1, writer.Commits);
            Assert.Equal(1, writer.Disposals);
        }
        finally { await ((IAsyncDisposable)inference).DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualRefusedForeignWriterSurvivesOnlyItsRetainedDefaultOwner(bool refuses)
    {
        WeakReference[] roots = HoldThenReleaseForeign(refuses);
        Collect(roots);
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] HoldThenReleaseForeign(bool refuses)
    {
        var proof = ExerciseForeign(refuses);
        Collect(proof.Borrowed);
        Assert.All(proof.Borrowed, root => Assert.False(root.IsAlive));
        if (refuses)
        {
            for (int count = 0; count < 3; count++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.All(proof.Owned, root => Assert.True(root.IsAlive));
        }
        else
        {
            Collect(proof.Owned);
            Assert.All(proof.Owned, root => Assert.False(root.IsAlive));
        }
        GC.KeepAlive(proof.Owner);
        return [.. proof.Owned, .. proof.Borrowed];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IImageInference Owner, WeakReference[] Owned, WeakReference[] Borrowed) ExerciseForeign(bool refuses)
    {
        var writerGeneration = new FixtureGeneration();
        var sourceGeneration = new FixtureGeneration();
        try
        {
            Assembly writerAssembly = writerGeneration.LoadFromAssemblyPath(typeof(ImageCleanupOwnershipTests).Assembly.Location);
            Assembly sourceAssembly = sourceGeneration.LoadFromAssemblyPath(typeof(ImageCleanupOwnershipTests).Assembly.Location);
            foreach ((Assembly assembly, AssemblyLoadContext generation) in new[] { (writerAssembly, writerGeneration), (sourceAssembly, sourceGeneration) })
            {
                Assert.NotSame(typeof(ImageCleanupOwnershipTests).Assembly, assembly);
                Assert.Equal(typeof(ImageCleanupOwnershipTests).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(assembly));
            }
            Type writerType = writerAssembly.GetType(typeof(ImageCleanupForeignWriter).FullName!, true)!;
            var writer = (IInferenceResourceWriteHandle)Activator.CreateInstance(writerType)!;
            if (refuses) writerType.GetProperty(nameof(ImageCleanupForeignWriter.CleanupFailure))!.SetValue(writer, new IOException("refused"));
            Type sourceType = sourceAssembly.GetType(typeof(ImageCleanupForeignAccess).FullName!, true)!;
            var source = (IInferenceResourceAccess)Activator.CreateInstance(sourceType, writer)!;
            Assert.Same(writerGeneration, AssemblyLoadContext.GetLoadContext(writer.GetType().Assembly));
            Assert.Same(sourceGeneration, AssemblyLoadContext.GetLoadContext(source.GetType().Assembly));
            Assert.Same(writerAssembly, writerType.GetMethod(nameof(IInferenceResourceWriteHandle.CommitAsync))!.Module.Assembly);
            Assert.Same(sourceAssembly, sourceType.GetMethod(nameof(IInferenceResourceAccess.OpenWriteAsync))!.Module.Assembly);
            IImageInference owner = Bind(source);
            Exception? error = Record.Exception(() => owner.GenerateAsync(ImageOperationTests.Request()).GetAwaiter().GetResult());
            Exception? retired = Record.Exception(() => ((IDisposable)owner).Dispose());
            if (refuses) { Assert.NotNull(error); Assert.Same(error, retired); }
            else { Assert.Null(error); Assert.Null(retired); }
            Assert.Equal(1, writerType.GetProperty(nameof(ImageCleanupForeignWriter.Disposals))!.GetValue(writer));
            Assert.Equal(0, sourceType.GetProperty(nameof(ImageCleanupForeignAccess.Disposals))!.GetValue(source));
            return (owner, [new(writerGeneration), new(writer), new(writer.Content)], [new(sourceGeneration), new(source)]);
        }
        finally { writerGeneration.Unload(); sourceGeneration.Unload(); }
    }

    private static IImageInference Bind(IInferenceResourceAccess resources) => new ImageProviderService([new ImageTestTransport
        { Outputs = new ImageTestTransport().Outputs!.Take(1).ToArray() }])
        .Bind(ImageOperationTests.Connection(), ImageOperationTests.Workflow(), resources: resources, pollInterval: TimeSpan.Zero);

    private static void Collect(IEnumerable<WeakReference> roots)
    {
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
    }

    internal sealed class FixtureGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) == true
            ? Default.Assemblies.SingleOrDefault(assembly => assembly.GetName().Name == name.Name) : null;
    }
}

public sealed class ImageCleanupForeignAccess(IInferenceResourceWriteHandle writer) : IInferenceResourceAccess, IDisposable
{
    public string WorkspaceId => "workspace";
    public string SessionId { get; set; } = "session";
    public Action? AfterOpen { get; set; }
    public IInferenceResourceReadHandle? Reader { get; set; }
    public Action? AfterRead { get; set; }
    public int Disposals { get; private set; }
    public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default)
    { AfterOpen?.Invoke(); return Task.FromResult(writer); }
    public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
    { AfterRead?.Invoke(); return Task.FromResult(Reader ?? throw new NotSupportedException()); }
    public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Dispose() => Disposals++;
}

public sealed class ImageCleanupForeignReader : IInferenceResourceReadHandle
{
    public InferenceResourceDescriptor Resource { get; set; } = new(new("workspace", "source"), "session", null, null, 1);
    public Stream Content { get; } = new MemoryStream([1]);
    public Exception? CleanupFailure { get; set; }
    public Action? AfterCleanup { get; set; }
    public int Disposals { get; private set; }
    public ValueTask DisposeAsync()
    {
        Disposals++;
        AfterCleanup?.Invoke();
        if (CleanupFailure is not null) return ValueTask.FromException(CleanupFailure);
        Content.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ImageCleanupForeignWriter : IInferenceResourceWriteHandle
{
    public InferenceResourceReference Reference { get; } = new("workspace", "observed");
    public Stream Content { get; } = new MemoryStream();
    public int Disposals { get; private set; }
    public int Commits { get; private set; }
    public Exception? CleanupFailure { get; set; }
    public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? CleanupRelease { get; init; }
    public Action? AfterCleanup { get; set; }
    public Func<InferenceResourceCommitResult, InferenceResourceCommitResult>? Transform { get; set; }
    public Task<InferenceResourceCommitResult> CommitAsync(long bytesWritten, InferenceResourceCommitOptions? options = null, CancellationToken cancellationToken = default)
    {
        Commits++;
        if (options?.SelectForSession != true) throw new InvalidOperationException("Atomic session selection is required.");
        var receipt = new InferenceResourceCommitResult(new(Reference, "session", null, null, bytesWritten),
            new("workspace", "session", 7, [Reference]));
        return Task.FromResult(Transform?.Invoke(receipt) ?? receipt);
    }
    public async ValueTask DisposeAsync()
    {
        Disposals++;
        CleanupEntered.TrySetResult();
        if (CleanupRelease is not null) await CleanupRelease.Task;
        AfterCleanup?.Invoke();
        if (CleanupFailure is not null) throw CleanupFailure;
        Content.Dispose();
    }
}
