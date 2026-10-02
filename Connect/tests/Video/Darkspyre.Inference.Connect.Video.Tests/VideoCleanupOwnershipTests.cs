using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Video.Tests;

public sealed class VideoCleanupOwnershipTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static VideoConnection Connection => new() { Transport = VideoTransport.XaiImagine, Endpoint = "https://api.x.ai/v1", Xai = new("connection") };
    private static VideoOutput Output => new(new("controlled-endpoint", "controlled-account"), JsonElement.Parse("{}"));

    [Fact]
    public async Task MaterializedWriterRefusalReportsCleanupAndReachesRepeatedRetirement()
    {
        var source = new VideoOperationTests.Source();
        await using var access = new LlmConnectionAccess(source, []);
        var resources = new VideoOperationTests.Resources { FailCleanup = true };
        var transport = new VideoOperationTests.FixtureTransport();
        var inference = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access), resources);
        var error = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => inference.MaterializeAsync(Output));
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, error.Failure.Phase);
        Assert.Equal(InferenceResourceCommitOutcome.Committed, error.Failure.CommitOutcome);
        Assert.Equal(InferenceResourceCommitOutcome.Committed, error.Failure.SelectionOutcome);
        Assert.Equal(new InferenceResourceSelectionVersion("w", "s", 1), error.Failure.SelectionVersion);
        Assert.Same(error, await Record.ExceptionAsync(() => ((IAsyncDisposable)inference).DisposeAsync().AsTask()));
        Assert.Same(error, Record.Exception(() => ((IDisposable)inference).Dispose()));
        Assert.Equal(1, resources.Writer!.Disposes);
        Assert.Equal(0, resources.Disposes);
        Assert.Equal(0, transport.Disposes);
    }

    [Fact]
    public async Task CallerClosedDownloadRetainsActualRefusalUntilFacadeRetirement()
    {
        var source = new VideoOperationTests.Source();
        await using var access = new LlmConnectionAccess(source, []);
        var inner = new VideoCleanupForeignStream { Failure = new IOException("Controlled stream refusal.") };
        var transport = new VideoOperationTests.FixtureTransport { Download = inner };
        var inference = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access));
        Stream stream = await inference.DownloadAsync(Output);
        Exception? original = await Record.ExceptionAsync(() => stream.DisposeAsync().AsTask());
        var cleanup = Assert.IsType<InferenceResourceOperationException>(original);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
        Assert.Same(inner.Failure, cleanup.InnerException);
        Assert.Same(cleanup, await Record.ExceptionAsync(() => ((IAsyncDisposable)inference).DisposeAsync().AsTask()));
        Assert.Same(cleanup, Record.Exception(() => ((IDisposable)inference).Dispose()));
        Assert.Same(cleanup, await Record.ExceptionAsync(() => stream.DisposeAsync().AsTask()));
        Assert.Equal(1, inner.Disposals);
        Assert.Equal(0, transport.Disposes);
    }

    [Fact]
    public async Task TwoIndependentMaterializationRefusalsStayOrderedAndBothRetire()
    {
        var source = new VideoOperationTests.Source();
        await using var access = new LlmConnectionAccess(source, []);
        var download = new VideoCleanupForeignStream { Failure = new IOException("Controlled stream refusal.") };
        var resources = new VideoOperationTests.Resources { FailCleanup = true };
        var inference = new VideoProviderService([new VideoOperationTests.FixtureTransport { Download = download }])
            .Bind(Connection, new(new("connection"), access), resources);
        var errors = await Assert.ThrowsAsync<AggregateException>(() => inference.MaterializeAsync(Output));
        Assert.Equal(2, errors.InnerExceptions.Count);
        Assert.All(errors.InnerExceptions, error => Assert.Equal(InferenceResourceOperationPhase.Cleanup,
            Assert.IsType<InferenceResourceOperationException>(error).Failure.Phase));
        Assert.Same(download.Failure, errors.InnerExceptions[0].InnerException);
        var retired = Assert.IsType<AggregateException>(await Record.ExceptionAsync(() => ((IAsyncDisposable)inference).DisposeAsync().AsTask()));
        Assert.Equal(errors.InnerExceptions, retired.InnerExceptions);
        Assert.Same(retired, Record.Exception(() => ((IDisposable)inference).Dispose()));
        Assert.Equal(1, download.Disposals);
        Assert.Equal(1, resources.Writer!.Disposes);
        Assert.Equal(0, resources.Disposes);
    }

    [Fact]
    public async Task OrdinaryCommitFailureLeavesRetirementHealthy()
    {
        var source = new VideoOperationTests.Source();
        await using var access = new LlmConnectionAccess(source, []);
        var resources = new VideoOperationTests.Resources { FailCommit = true };
        var inference = new VideoProviderService([new VideoOperationTests.FixtureTransport()])
            .Bind(Connection, new(new("connection"), access), resources);
        var error = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => inference.MaterializeAsync(Output));
        Assert.Equal(InferenceResourceOperationPhase.Commit, error.Failure.Phase);
        await ((IAsyncDisposable)inference).DisposeAsync();
        Assert.Equal(1, resources.Writer!.Disposes);
        Assert.Equal(0, resources.Disposes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IgnoringWriterCleanupDrainsAndRetainsActualCommit(bool refuses)
    {
        var source = new VideoOperationTests.Source();
        await using var access = new LlmConnectionAccess(source, []);
        var writer = new VideoCleanupForeignWriter { Release = new(), Failure = refuses ? new IOException("refused") : null };
        var transport = new VideoCleanupForeignSource { Writer = writer };
        var inference = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access), transport);
        Task<InferenceResourceDescriptor> pending = inference.MaterializeAsync(Output);
        try
        {
            await writer.Entered.Task.WaitAsync(Bound);
            Task retirement = ((IAsyncDisposable)inference).DisposeAsync().AsTask();
            Assert.Same(retirement, ((IAsyncDisposable)inference).DisposeAsync().AsTask());
            Assert.False(retirement.IsCompleted);
            writer.Release.SetResult();
            Exception? operation = await Record.ExceptionAsync(() => pending.WaitAsync(Bound));
            var facts = refuses ? Assert.IsType<InferenceResourceOperationException>(operation).Failure
                : Assert.IsType<InferenceResourceOperationCanceledException>(operation).Failure;
            Assert.Equal(InferenceResourceCommitOutcome.Committed, facts.CommitOutcome);
            Assert.Equal(InferenceResourceCommitOutcome.Committed, facts.SelectionOutcome);
            Assert.Equal(new InferenceResourceSelectionVersion("w", "s", 9), facts.SelectionVersion);
            Assert.Equal(3, facts.BytesTransferred);
            Exception? retired = await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
            if (refuses) Assert.Same(operation, retired); else Assert.Null(retired);
            Assert.Equal(1, writer.Disposals);
            Assert.Equal(0, transport.Disposals);
        }
        finally
        {
            writer.Release.TrySetResult();
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
    public async Task ScopeAndReceiptContradictionsNeverPublish(string fault)
    {
        var source = new VideoOperationTests.Source();
        await using var access = new LlmConnectionAccess(source, []);
        var writer = new VideoCleanupForeignWriter();
        var transport = new VideoCleanupForeignSource { Writer = writer };
        if (fault == "scope-open") transport.AfterOpen = () => transport.SessionId = "changed";
        if (fault == "scope-cleanup") writer.AfterCleanup = () => transport.SessionId = "changed";
        if (fault == "bytes") writer.Transform = result => result with { Resource = result.Resource with { Bytes = 5 } };
        if (fault == "mime") writer.Transform = result => result with { Resource = result.Resource with { ContentType = "other/mime" } };
        if (fault == "selection") writer.Transform = result => result with { Selection = null };
        var inference = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access), transport);
        try
        {
            var error = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => inference.MaterializeAsync(Output));
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
    public async Task UnhandedIgnoringDownloadIsReleasedBeforeRetirementCompletes(bool refuses)
    {
        var source = new VideoOperationTests.Source();
        await using var access = new LlmConnectionAccess(source, []);
        var download = new VideoCleanupForeignStream { Failure = refuses ? new IOException("refused") : null };
        var transport = new VideoCleanupForeignSource { Download = download, ReleaseDownload = new() };
        var inference = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access));
        Task<Stream> pending = inference.DownloadAsync(Output);
        try
        {
            await transport.DownloadEntered.Task.WaitAsync(Bound);
            Task retirement = ((IAsyncDisposable)inference).DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            transport.ReleaseDownload.SetResult();
            Exception? error = await Record.ExceptionAsync(() => pending.WaitAsync(Bound));
            Exception? retired = await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
            if (refuses)
            {
                var errors = Assert.IsType<AggregateException>(error);
                Assert.IsAssignableFrom<OperationCanceledException>(errors.InnerExceptions[0]);
                var cleanup = Assert.IsType<InferenceResourceOperationException>(errors.InnerExceptions[1]);
                Assert.Same(cleanup, retired);
                Assert.Same(download.Failure, cleanup.InnerException);
            }
            else { Assert.IsAssignableFrom<OperationCanceledException>(error); Assert.Null(retired); }
            Assert.Equal(1, download.Disposals);
            Assert.Equal(0, transport.Disposals);
        }
        finally
        {
            transport.ReleaseDownload.TrySetResult();
            _ = await Record.ExceptionAsync(() => pending.WaitAsync(Bound));
            _ = await Record.ExceptionAsync(() => ((IAsyncDisposable)inference).DisposeAsync().AsTask().WaitAsync(Bound));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialReadAndTwoCleanupRefusalsPreserveAllActualOwners(bool outOfMemory)
    {
        var source = new VideoOperationTests.Source();
        await using var access = new LlmConnectionAccess(source, []);
        Exception original = ControlledFailure(outOfMemory, "Controlled read failure.");
        var download = new VideoCleanupForeignStream { ReadFailure = original, Failure = ControlledFailure(outOfMemory, "Controlled download cleanup failure.") };
        var writer = new VideoCleanupForeignWriter { Failure = new IOException("Controlled writer cleanup failure.") };
        var transport = new VideoCleanupForeignSource { Download = download, Writer = writer };
        var inference = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access), transport);
        var errors = await Assert.ThrowsAsync<AggregateException>(() => inference.MaterializeAsync(Output));
        Assert.Equal(3, errors.InnerExceptions.Count);
        var operation = Assert.IsType<InferenceResourceOperationException>(errors.InnerExceptions[0]);
        Assert.Same(original, operation.InnerException);
        Assert.Equal(InferenceResourceOperationPhase.Read, operation.Failure.Phase);
        Assert.Equal(3, operation.Failure.BytesTransferred);
        Assert.Equal(InferenceResourceCommitOutcome.NotAttempted, operation.Failure.CommitOutcome);
        Assert.Same(download.Failure, errors.InnerExceptions[1].InnerException);
        Assert.Same(writer.Failure, errors.InnerExceptions[2].InnerException);
        Assert.All(errors.InnerExceptions.Skip(1), error => Assert.Equal(InferenceResourceOperationPhase.Cleanup,
            Assert.IsType<InferenceResourceOperationException>(error).Failure.Phase));
        var retirement = Assert.IsType<AggregateException>(await Record.ExceptionAsync(() => ((IAsyncDisposable)inference).DisposeAsync().AsTask()));
        Assert.Equal(errors.InnerExceptions.Skip(1), retirement.InnerExceptions);
        Assert.Equal(0, writer.Commits);
        Assert.Equal(1, writer.Disposals);
        Assert.Equal(1, download.Disposals);
        Assert.Equal(0, transport.Disposals);
    }

    [SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Justification = "The fixture injects an allocation failure without allocating or exhausting memory.")]
    private static Exception ControlledFailure(bool outOfMemory, string message) => outOfMemory ? new OutOfMemoryException(message) : new IOException(message);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapabilityCallbacksCannotReturnAcrossTheOwnerRetirementFence(bool seek)
    {
        var source = new VideoOperationTests.Source();
        await using var access = new LlmConnectionAccess(source, []);
        var inner = new VideoCleanupForeignStream();
        var transport = new VideoCleanupForeignSource { Download = inner };
        var facade = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access));
        Stream stream = await facade.DownloadAsync(Output);
        Task? retired = null;
        inner.CapabilityCheck = () => retired = ((IAsyncDisposable)facade).DisposeAsync().AsTask();
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => { _ = seek ? stream.CanSeek : stream.CanRead; });
            await retired!.WaitAsync(Bound);
            Assert.Equal(1, inner.Disposals);
            Assert.False(stream.CanRead);
            Assert.False(stream.CanSeek);
        }
        finally { await ((IAsyncDisposable)facade).DisposeAsync(); await stream.DisposeAsync(); }
    }

    [Theory]
    [InlineData("writer", false)]
    [InlineData("writer", true)]
    [InlineData("materialized-stream", false)]
    [InlineData("materialized-stream", true)]
    [InlineData("escaped-stream", false)]
    [InlineData("escaped-stream", true)]
    public void ActualForeignOwnerRetentionExcludesHealthyBorrowedGeneration(string kind, bool refuses)
    {
        WeakReference[] roots = HoldThenReleaseForeign(kind, refuses);
        Collect(roots);
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] HoldThenReleaseForeign(string kind, bool refuses)
    {
        var proof = ExerciseForeign(kind, refuses);
        Collect(proof.Borrowed);
        Assert.All(proof.Borrowed, root => Assert.False(root.IsAlive));
        if (refuses)
        {
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
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
    private static (object Owner, WeakReference[] Owned, WeakReference[] Borrowed) ExerciseForeign(string kind, bool refuses)
    {
        var ownerGeneration = new FixtureGeneration();
        var sourceGeneration = new FixtureGeneration();
        try
        {
            Assembly ownerAssembly = ownerGeneration.LoadFromAssemblyPath(typeof(VideoCleanupOwnershipTests).Assembly.Location);
            Assembly sourceAssembly = sourceGeneration.LoadFromAssemblyPath(typeof(VideoCleanupOwnershipTests).Assembly.Location);
            foreach ((Assembly assembly, AssemblyLoadContext generation) in new[] { (ownerAssembly, ownerGeneration), (sourceAssembly, sourceGeneration) })
            {
                Assert.NotSame(typeof(VideoCleanupOwnershipTests).Assembly, assembly);
                Assert.Equal(typeof(VideoCleanupOwnershipTests).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(assembly));
            }
            Type ownerType = ownerAssembly.GetType(kind == "writer" ? typeof(VideoCleanupForeignWriter).FullName! : typeof(VideoCleanupForeignStream).FullName!, true)!;
            object actualOwner = Activator.CreateInstance(ownerType)!;
            Assert.Same(ownerGeneration, AssemblyLoadContext.GetLoadContext(actualOwner.GetType().Assembly));
            Assert.Same(ownerAssembly, ownerType.GetMethod(nameof(IAsyncDisposable.DisposeAsync))!.Module.Assembly);
            if (refuses) ownerType.GetProperty("Failure")!.SetValue(actualOwner, new IOException("refused"));
            Type sourceType = sourceAssembly.GetType(typeof(VideoCleanupForeignSource).FullName!, true)!;
            var borrowed = (IVideoTransport)Activator.CreateInstance(sourceType)!;
            sourceType.GetProperty(kind == "writer" ? nameof(VideoCleanupForeignSource.Writer) : nameof(VideoCleanupForeignSource.Download))!.SetValue(borrowed, actualOwner);
            Assert.Same(sourceGeneration, AssemblyLoadContext.GetLoadContext(borrowed.GetType().Assembly));
            Assert.Same(sourceAssembly, sourceType.GetMethod(nameof(IVideoTransport.DownloadAsync))!.Module.Assembly);
            var source = new VideoOperationTests.Source();
            using var access = new LlmConnectionAccess(source, []);
            var facade = new VideoProviderService([borrowed]).Bind(Connection, new(new("connection"), access), (IInferenceResourceAccess)borrowed);
            Exception? operation;
            if (kind == "escaped-stream")
            {
                Stream stream = facade.DownloadAsync(Output).GetAwaiter().GetResult();
                operation = Record.Exception(() => stream.Dispose());
            }
            else operation = Record.Exception(() => facade.MaterializeAsync(Output).GetAwaiter().GetResult());
            Exception? retired = Record.Exception(() => ((IDisposable)facade).Dispose());
            if (refuses) { Assert.IsType<InferenceResourceOperationException>(operation); Assert.Same(operation, retired); }
            else { Assert.Null(operation); Assert.Null(retired); }
            Assert.Equal(1, ownerType.GetProperty("Disposals")!.GetValue(actualOwner));
            Assert.Equal(0, sourceType.GetProperty(nameof(VideoCleanupForeignSource.Disposals))!.GetValue(borrowed));
            object ownedContent = actualOwner is IInferenceResourceWriteHandle writer ? writer.Content : actualOwner;
            return (facade, [new(ownerGeneration), new(actualOwner), new(ownedContent)], [new(sourceGeneration), new(borrowed)]);
        }
        finally { ownerGeneration.Unload(); sourceGeneration.Unload(); }
    }

    private static void Collect(IEnumerable<WeakReference> roots)
    {
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
    }

    private sealed class FixtureGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) == true
            ? Default.Assemblies.SingleOrDefault(assembly => assembly.GetName().Name == name.Name) : null;
    }
}

public sealed class VideoCleanupForeignStream : MemoryStream
{
    public VideoCleanupForeignStream() : base([1, 2, 3], false) { }
    public Exception? Failure { get; set; }
    public Exception? ReadFailure { get; set; }
    public Action? CapabilityCheck { get; set; }
    public override bool CanRead { get { CapabilityCheck?.Invoke(); return base.CanRead; } }
    public override bool CanSeek { get { CapabilityCheck?.Invoke(); return base.CanSeek; } }
    private bool _read;
    public int Disposals { get; private set; }
    [SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "The controlled refusal deliberately retains an open base stream; the healthy path calls the base cleanup.")]
    public override ValueTask DisposeAsync()
    {
        Disposals++;
        if (Failure is not null) return ValueTask.FromException(Failure);
        return base.DisposeAsync();
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_read && ReadFailure is not null) return ValueTask.FromException<int>(ReadFailure);
        _read = true;
        return base.ReadAsync(buffer, cancellationToken);
    }
}

public sealed class VideoCleanupForeignWriter : IInferenceResourceWriteHandle
{
    public InferenceResourceReference Reference { get; } = new("w", "written");
    public Stream Content { get; } = new MemoryStream();
    public Exception? Failure { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? Release { get; init; }
    public Action? AfterCleanup { get; set; }
    public Func<InferenceResourceCommitResult, InferenceResourceCommitResult>? Transform { get; set; }
    public int Disposals { get; private set; }
    public int Commits { get; private set; }
    public Task<InferenceResourceCommitResult> CommitAsync(long bytesWritten, InferenceResourceCommitOptions? options = null, CancellationToken cancellationToken = default)
    {
        Commits++;
        if (options?.SelectForSession != true) throw new InvalidOperationException("Atomic session selection is required.");
        var result = new InferenceResourceCommitResult(new(Reference, "s", null, null, bytesWritten), new("w", "s", 9, [Reference]));
        return Task.FromResult(Transform?.Invoke(result) ?? result);
    }
    public async ValueTask DisposeAsync()
    {
        Disposals++;
        Entered.TrySetResult();
        if (Release is not null) await Release.Task;
        AfterCleanup?.Invoke();
        if (Failure is not null) throw Failure;
        Content.Dispose();
    }
}

public sealed class VideoCleanupForeignSource : IVideoTransport, IInferenceResourceAccess, IDisposable
{
    public Stream Download { get; set; } = new MemoryStream([1, 2, 3], false);
    public IInferenceResourceWriteHandle Writer { get; set; } = new VideoCleanupForeignWriter();
    public Action? AfterOpen { get; set; }
    public TaskCompletionSource? ReleaseDownload { get; init; }
    public TaskCompletionSource DownloadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string WorkspaceId => "w";
    public string SessionId { get; set; } = "s";
    public int Disposals { get; private set; }
    public VideoTransport Transport => VideoTransport.XaiImagine;
    public InferenceSetupDefinition SetupDefinition { get; } = new(new("controlled-video", "Controlled Video", Common.InferenceImplementationKind.Connect,
        Common.InferenceTask.VideoGeneration), "Controlled ownership fixture.");
    public Task<VideoCapabilities> GetCapabilitiesAsync(VideoConnection connection, string? modelId = null, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoJob> SubmitAsync(VideoConnection connection, VideoRequest request, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoJobSnapshot> GetAsync(VideoConnection connection, VideoJob job, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public async Task<Stream> DownloadAsync(VideoConnection connection, VideoOutput output, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    { DownloadEntered.TrySetResult(); if (ReleaseDownload is not null) await ReleaseDownload.Task; return Download; }
    public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) { AfterOpen?.Invoke(); return Task.FromResult(Writer); }
    public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Dispose() => Disposals++;
}
