using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed partial class ComfyUiImageTransportTests
{
    private static readonly TimeSpan ResourceBound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task EnteredBinaryBytesSurviveHeldCapabilityAndReachActualComfyUpload()
    {
        byte[] bytes = "entered"u8.ToArray();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        string? uploaded = null;
        var handler = new StubHandler(async request =>
        {
            uploaded = await request.Content!.ReadAsStringAsync();
            return JsonResponse("{\"name\":\"observed.png\",\"subfolder\":\"\",\"type\":\"input\"}");
        });
        var transport = new ImageTestTransport
        {
            CapabilityPause = () => { entered.SetResult(); return release.Task; },
            ReferenceMaterializer = CreateTransport(handler)
        };
        var inference = new ImageProviderService([transport]).Bind(Connection(), ImageOperationTests.Workflow(), pollInterval: TimeSpan.Zero);
        try
        {
            Task pending = inference.GenerateAsync(ImageOperationTests.Request() with { References = [new BinaryMediaSource(null, bytes)] });
            await entered.Task.WaitAsync(ResourceBound);
            "changed"u8.CopyTo(bytes);
            release.SetResult();
            await pending.WaitAsync(ResourceBound);
            Assert.Contains("entered", uploaded!, StringComparison.Ordinal);
            Assert.DoesNotContain("changed", uploaded!, StringComparison.Ordinal);
            Assert.Single(handler.Headers);
        }
        finally { await ((IAsyncDisposable)inference).DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualComfyCompositeKeepsAdmittedScopeAndCommitsOriginalBeforeUpload(bool binary)
    {
        var headers = new Dictionary<string, string> { ["X-Account"] = "original" };
        var resources = new ImageTestResources();
        var logical = new InferenceResourceReference("workspace", "original");
        resources.Committed[logical] = (new(logical, "session", null, null, 3), [1, 2, 3]);
        resources.ReadProvider = (reference, _) =>
        {
            headers["X-Account"] = "changed";
            var committed = resources.Committed[reference];
            var reader = new ImageTestResources.Reader(committed.Resource, new MemoryStream(committed.Data, writable: false));
            resources.Readers.Add(reader);
            return Task.FromResult<IInferenceResourceReadHandle>(reader);
        };
        bool uploaded = false;
        var handler = new StubHandler(async request =>
        {
            Assert.Equal("original", request.Headers.GetValues("X-Account").Single());
            if (request.RequestUri!.AbsolutePath == "/upload/image")
            {
                if (binary) Assert.Single(resources.Writers);
                Assert.Contains((byte)1, await request.Content!.ReadAsByteArrayAsync());
                uploaded = true;
                return JsonResponse("{\"name\":\"actual.png\",\"subfolder\":\"staged\",\"type\":\"input\"}");
            }
            if (request.RequestUri.AbsolutePath == "/prompt")
            {
                Assert.True(uploaded);
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("staged/actual.png", body.RootElement.GetProperty("prompt").GetProperty("1").GetProperty("inputs").GetProperty("reference").GetString());
                return JsonResponse("{\"prompt_id\":\"job\"}");
            }
            if (request.RequestUri.AbsolutePath == "/history/job")
                return JsonResponse("{\"job\":{\"outputs\":{\"10\":{\"images\":[{\"filename\":\"output.png\"}]}}}}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([9, 8]) { Headers = { ContentType = new("image/webp") } } };
        });
        var transport = CreateTransport(handler);
        var inference = new ImageProviderService([transport]).Bind(Connection() with { AdditionalHeaders = headers },
            Workflow(true), resources: resources, pollInterval: TimeSpan.Zero);
        try
        {
            var result = await inference.GenerateAsync(new()
            {
                ModelId = "scene",
                Prompt = "draw",
                References = binary ? [new BinaryMediaSource(null, new byte[] { 1, 2, 3 })] : [new ResourceMediaSource(logical)]
            });
            var image = Assert.Single(result.Images);
            Assert.Equal("image/webp", image.ContentType);
            Assert.Equal(new byte[] { 9, 8 }, image.Data.ToArray());
            Assert.Equal("image/webp", image.Resource!.ContentType);
            Assert.Equal(binary ? 2 : 1, resources.Writers.Count);
            Assert.All(resources.Readers, reader => Assert.Equal(1, reader.Disposals));
            Assert.Equal(0, resources.Disposals);
            Assert.Equal(4, handler.Headers.Count);
        }
        finally { await ((IAsyncDisposable)inference).DisposeAsync(); }
    }

    [Fact]
    public async Task StandaloneResourceResolutionFreezesHeaderSelectionBeforeIgnoredRead()
    {
        var headers = new Dictionary<string, string> { ["X-Account"] = "original" };
        ImageConnection connection = Connection() with { AdditionalHeaders = headers };
        var release = new TaskCompletionSource<IInferenceResourceReadHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new InferenceResourceReference("workspace", "source");
        var resources = new ImageTestResources { ReadProvider = (_, _) => { entered.SetResult(); return release.Task; } };
        var handler = new StubHandler(request =>
        {
            Assert.Equal("original", request.Headers.GetValues("X-Account").Single());
            return JsonResponse("{\"name\":\"native.png\",\"subfolder\":\"\",\"type\":\"input\"}");
        });
        var transport = CreateTransport(handler);
        Task<ImagePreparedReference> pending = transport.ResolveReferenceAsync(connection, new ResourceMediaSource(resource), resources: resources);
        await entered.Task.WaitAsync(ResourceBound);
        headers["X-Account"] = "changed";
        var reader = new ImageTestResources.Reader(new(resource, "session", null, null, 1), new MemoryStream([1]));
        release.SetResult(reader);
        ImagePreparedReference receipt = await pending.WaitAsync(ResourceBound);
        Assert.Equal(resource, receipt.Resource);
        Assert.Equal(1, reader.Disposals);
        var prepared = transport.Prepare(Request(Workflow(true)) with { References = [receipt] });
        await Assert.ThrowsAsync<InferenceException>(() => transport.SubmitAsync(connection, prepared));
        Assert.Single(handler.Headers);
        Assert.Equal(0, resources.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiringCompositeDrainsIgnoredReaderAndNeverUploadsOrRewindsItsStream(bool refuses)
    {
        var release = new TaskCompletionSource<IInferenceResourceReadHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logical = new InferenceResourceReference("workspace", "source");
        var resources = new ImageTestResources { ReadProvider = (_, _) => { entered.SetResult(); return release.Task; } };
        var handler = new StubHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException("HTTP must not be entered.")));
        var inference = new ImageProviderService([CreateTransport(handler)]).Bind(Connection(), Workflow(true), resources: resources);
        Task pending = inference.GenerateAsync(new() { ModelId = "scene", Prompt = "draw", References = [new ResourceMediaSource(logical)] });
        await entered.Task.WaitAsync(ResourceBound);
        Task retirement = ((IAsyncDisposable)inference).DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        var reader = new ImageTestResources.Reader(new(logical, "session", null, null, 1), new MemoryStream([1]))
        { CleanupFailure = refuses ? new IOException("Controlled reader refusal.") : null };
        release.SetResult(reader);
        Exception? operation = await Record.ExceptionAsync(() => pending.WaitAsync(ResourceBound));
        var error = refuses ? Assert.IsType<InferenceResourceOperationCanceledException>(Assert.IsType<AggregateException>(operation).InnerExceptions[0])
            : Assert.IsType<InferenceResourceOperationCanceledException>(operation);
        Exception? retired = await Record.ExceptionAsync(() => retirement.WaitAsync(ResourceBound));
        if (refuses)
        {
            var cleanup = Assert.IsType<InferenceResourceOperationException>(retired);
            Assert.Same(Assert.IsType<AggregateException>(operation).InnerExceptions[1], cleanup);
            Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
            Assert.Same(reader.CleanupFailure, cleanup.InnerException);
            Assert.Same(cleanup, await Record.ExceptionAsync(() => ((IAsyncDisposable)inference).DisposeAsync().AsTask()));
            Assert.Same(cleanup, Record.Exception(() => ((IDisposable)inference).Dispose()));
        }
        else Assert.Null(retired);
        Assert.Equal(InferenceResourceOperationPhase.OpenRead, error.Failure.Phase);
        Assert.Equal(logical, error.Failure.Reference);
        Assert.Equal(1, reader.Disposals);
        Assert.Empty(handler.Headers);
        Assert.Equal(0, resources.Disposals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BoundReaderRefusalBeforeSubmitRetainsBothCausesAndOneRetirement(bool partial, bool outOfMemory)
    {
        var reference = new InferenceResourceReference("workspace", "requested");
        Exception readFailure = ControlledReadFailure(outOfMemory, "Controlled partial read failure.");
        Exception cleanupFailure = ControlledReadFailure(outOfMemory, "Controlled reader cleanup failure.");
        var reader = new ImageTestResources.Reader(new(reference, "session", null, null, 5),
            partial ? new PartialReadStream(readFailure) : new MemoryStream([1]))
        { CleanupFailure = cleanupFailure };
        var resources = new ImageTestResources { ReadProvider = (_, _) => Task.FromResult<IInferenceResourceReadHandle>(reader) };
        var handler = new StubHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException("No HTTP expected.")));
        var inference = new ImageProviderService([CreateTransport(handler)]).Bind(Connection(), Workflow(true), resources: resources);
        try
        {
            var errors = await Assert.ThrowsAsync<AggregateException>(() => inference.GenerateAsync(new()
            { ModelId = "scene", Prompt = "draw", References = [new ResourceMediaSource(reference)] }));
            var operation = Assert.IsType<InferenceResourceOperationException>(errors.InnerExceptions[0]);
            var cleanup = Assert.IsType<InferenceResourceOperationException>(errors.InnerExceptions[1]);
            if (partial) Assert.Same(readFailure, operation.InnerException);
            else Assert.Equal(InferenceFailureCode.ProtocolFailure, operation.Code);
            Assert.Equal(partial ? 2 : 1, operation.Failure.BytesTransferred);
            Assert.Equal(InferenceResourceOperationPhase.Read, operation.Failure.Phase);
            Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
            Assert.Equal(operation.Failure.BytesTransferred, cleanup.Failure.BytesTransferred);
            Assert.Same(cleanupFailure, cleanup.InnerException);
            Assert.Same(cleanup, await Record.ExceptionAsync(() => ((IAsyncDisposable)inference).DisposeAsync().AsTask()));
            Assert.Same(cleanup, Record.Exception(() => ((IDisposable)inference).Dispose()));
            Assert.Equal(1, reader.Disposals);
            Assert.Equal(0, resources.Disposals);
            Assert.Empty(handler.Headers);
        }
        finally { _ = await Record.ExceptionAsync(() => ((IAsyncDisposable)inference).DisposeAsync().AsTask()); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryReaderFailureWithHealthyCleanupDoesNotPoisonRetirement(bool outOfMemory)
    {
        var reference = new InferenceResourceReference("workspace", "requested");
        Exception original = ControlledReadFailure(outOfMemory, "Controlled read failure.");
        var reader = new ImageTestResources.Reader(new(reference, "session", null, null, 5), new PartialReadStream(original));
        var resources = new ImageTestResources { ReadProvider = (_, _) => Task.FromResult<IInferenceResourceReadHandle>(reader) };
        var handler = new StubHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException("No HTTP expected.")));
        var inference = new ImageProviderService([CreateTransport(handler)]).Bind(Connection(), Workflow(true), resources: resources);
        var failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => inference.GenerateAsync(new()
        { ModelId = "scene", Prompt = "draw", References = [new ResourceMediaSource(reference)] }));
        Assert.Same(original, failure.InnerException);
        Assert.Equal(2, failure.Failure.BytesTransferred);
        Assert.Equal(InferenceResourceOperationPhase.Read, failure.Failure.Phase);
        await ((IAsyncDisposable)inference).DisposeAsync();
        ((IDisposable)inference).Dispose();
        Assert.Equal(1, reader.Disposals);
        Assert.Equal(0, resources.Disposals);
        Assert.Empty(handler.Headers);
    }

    [Fact]
    public async Task MismatchedReadIdentityRefusesBeforeStreamOrHttpAndKeepsProtocolFailure()
    {
        var requested = new InferenceResourceReference("workspace", "requested");
        var reader = new ImageTestResources.Reader(new(new("workspace", "other"), "session", null, null, 0), new MemoryStream());
        var resources = new ImageTestResources { ReadProvider = (_, _) => Task.FromResult<IInferenceResourceReadHandle>(reader) };
        var handler = new StubHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException("No HTTP expected.")));
        InferenceResourceOperationException error = await Assert.ThrowsAsync<InferenceResourceOperationException>(() =>
            CreateTransport(handler).ResolveReferenceAsync(Connection(), new ResourceMediaSource(requested), resources: resources));
        Assert.Equal(InferenceFailureCode.ProtocolFailure, error.Code);
        Assert.Equal(requested, error.Failure.Reference);
        Assert.Equal(InferenceResourceOperationPhase.OpenRead, error.Failure.Phase);
        Assert.Equal(1, reader.Disposals);
        Assert.Empty(handler.Headers);
    }

    [Fact]
    public async Task PartialResourceReadAndReaderCleanupFailuresRemainSeparatelyObservable()
    {
        var requested = new InferenceResourceReference("workspace", "requested");
        var readFailure = new IOException("Controlled partial read failure.");
        var cleanupFailure = new IOException("Controlled reader cleanup failure.");
        var stream = new PartialReadStream(readFailure);
        var reader = new ImageTestResources.Reader(new(requested, "session", null, null, 5), stream) { CleanupFailure = cleanupFailure };
        var resources = new ImageTestResources { ReadProvider = (_, _) => Task.FromResult<IInferenceResourceReadHandle>(reader) };
        var handler = new StubHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException("No HTTP expected.")));
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            CreateTransport(handler).ResolveReferenceAsync(Connection(), new ResourceMediaSource(requested), resources: resources));
        Assert.Equal(2, error.InnerExceptions.Count);
        InferenceResourceOperationException operation = Assert.IsType<InferenceResourceOperationException>(error.InnerExceptions[0]);
        Assert.Same(readFailure, operation.InnerException);
        var cleanup = Assert.IsType<InferenceResourceOperationException>(error.InnerExceptions[1]);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
        Assert.Same(cleanupFailure, cleanup.InnerException);
        Assert.Equal(2, operation.Failure.BytesTransferred);
        Assert.Equal(InferenceResourceOperationPhase.Read, operation.Failure.Phase);
        Assert.Equal(InferenceResourceCommitOutcome.NotAttempted, operation.Failure.CommitOutcome);
        Assert.Equal(1, reader.Disposals);
        Assert.Empty(handler.Headers);
    }

    private sealed class PartialReadStream(Exception failure) : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 4, 5 }, cancellationToken);
            throw failure;
        }
    }

    [SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Justification = "The fixture injects a runtime allocation failure without allocating or exhausting process memory.")]
    private static Exception ControlledReadFailure(bool outOfMemory, string message) => outOfMemory ? new OutOfMemoryException(message) : new IOException(message);

    [Theory]
    [InlineData("scope-open")]
    [InlineData("scope-cleanup")]
    [InlineData("bytes")]
    [InlineData("session")]
    public async Task ReaderScopeAndDescriptorContradictionsNeverPublish(string fault)
    {
        var reader = new ImageCleanupForeignReader();
        var resources = new ImageCleanupForeignAccess(new ImageCleanupForeignWriter()) { Reader = reader };
        if (fault == "scope-open") resources.AfterRead = () => resources.SessionId = "changed";
        if (fault == "scope-cleanup") reader.AfterCleanup = () => resources.SessionId = "changed";
        if (fault == "bytes") reader.Resource = reader.Resource with { Bytes = 9 };
        if (fault == "session") reader.Resource = reader.Resource with { SessionId = "other" };
        var handler = new StubHandler(_ => JsonResponse("{\"name\":\"actual.png\",\"type\":\"input\",\"subfolder\":\"\"}"));
        var error = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => CreateTransport(handler)
            .ResolveReferenceAsync(Connection(), new ResourceMediaSource(reader.Resource.Reference), resources: resources));
        Assert.Equal(InferenceFailureCode.ProtocolFailure, error.Code);
        Assert.Equal(1, reader.Disposals);
        Assert.Equal(0, resources.Disposals);
        Assert.Equal(fault == "scope-cleanup" ? 1 : 0, handler.Headers.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualForeignReaderRetentionExcludesBorrowedSource(bool refuses)
    {
        WeakReference[] roots = HoldThenReleaseForeignReader(refuses);
        CollectReaders(roots);
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] HoldThenReleaseForeignReader(bool refuses)
    {
        var proof = ExerciseForeignReader(refuses);
        CollectReaders(proof.Borrowed);
        Assert.All(proof.Borrowed, root => Assert.False(root.IsAlive));
        if (refuses)
        {
            for (int count = 0; count < 3; count++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.All(proof.Owned, root => Assert.True(root.IsAlive));
        }
        else
        {
            CollectReaders(proof.Owned);
            Assert.All(proof.Owned, root => Assert.False(root.IsAlive));
        }
        GC.KeepAlive(proof.Owner);
        return [.. proof.Owned, .. proof.Borrowed];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (object Owner, WeakReference[] Owned, WeakReference[] Borrowed) ExerciseForeignReader(bool refuses)
    {
        var readerGeneration = new ImageCleanupOwnershipTests.FixtureGeneration();
        var sourceGeneration = new ImageCleanupOwnershipTests.FixtureGeneration();
        try
        {
            Assembly readerAssembly = readerGeneration.LoadFromAssemblyPath(typeof(ComfyUiImageTransportTests).Assembly.Location);
            Assembly sourceAssembly = sourceGeneration.LoadFromAssemblyPath(typeof(ComfyUiImageTransportTests).Assembly.Location);
            foreach ((Assembly assembly, AssemblyLoadContext generation) in new[] { (readerAssembly, readerGeneration), (sourceAssembly, sourceGeneration) })
            {
                Assert.NotSame(typeof(ComfyUiImageTransportTests).Assembly, assembly);
                Assert.Equal(typeof(ComfyUiImageTransportTests).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(assembly));
            }
            Type readerType = readerAssembly.GetType(typeof(ImageCleanupForeignReader).FullName!, true)!;
            var reader = (IInferenceResourceReadHandle)Activator.CreateInstance(readerType)!;
            if (refuses) readerType.GetProperty(nameof(ImageCleanupForeignReader.CleanupFailure))!.SetValue(reader, new IOException("refused"));
            Type sourceType = sourceAssembly.GetType(typeof(ImageCleanupForeignAccess).FullName!, true)!;
            var source = (IInferenceResourceAccess)Activator.CreateInstance(sourceType, new ImageCleanupForeignWriter())!;
            sourceType.GetProperty(nameof(ImageCleanupForeignAccess.Reader))!.SetValue(source, reader);
            Assert.Same(readerGeneration, AssemblyLoadContext.GetLoadContext(reader.GetType().Assembly));
            Assert.Same(sourceGeneration, AssemblyLoadContext.GetLoadContext(source.GetType().Assembly));
            Assert.Same(readerAssembly, readerType.GetMethod(nameof(IInferenceResourceReadHandle.DisposeAsync))!.Module.Assembly);
            Assert.Same(sourceAssembly, sourceType.GetMethod(nameof(IInferenceResourceAccess.OpenReadAsync))!.Module.Assembly);
            var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/upload/image" => JsonResponse("{\"name\":\"actual.png\",\"type\":\"input\",\"subfolder\":\"\"}"),
                "/prompt" => JsonResponse("{\"prompt_id\":\"job\"}"),
                "/history/job" => JsonResponse("{\"job\":{\"outputs\":{\"10\":{\"images\":[{\"filename\":\"output.png\"}]}}}}"),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 8]) }
            });
            var owner = new ImageProviderService([CreateTransport(handler)]).Bind(Connection(), Workflow(true), resources: source);
            Exception? operation = Record.Exception(() => owner.GenerateAsync(new()
            { ModelId = "scene", Prompt = "draw", References = [new ResourceMediaSource(reader.Resource.Reference)] }).GetAwaiter().GetResult());
            Exception? retired = Record.Exception(() => ((IDisposable)owner).Dispose());
            if (refuses)
            {
                Assert.IsType<InferenceResourceOperationException>(operation);
                Assert.Same(operation, retired);
            }
            else { Assert.Null(operation); Assert.Null(retired); }
            Assert.Equal(1, readerType.GetProperty(nameof(ImageCleanupForeignReader.Disposals))!.GetValue(reader));
            Assert.Equal(0, sourceType.GetProperty(nameof(ImageCleanupForeignAccess.Disposals))!.GetValue(source));
            return (owner, [new(readerGeneration), new(reader), new(reader.Content)], [new(sourceGeneration), new(source)]);
        }
        finally { readerGeneration.Unload(); sourceGeneration.Unload(); }
    }

    private static void CollectReaders(IEnumerable<WeakReference> roots)
    {
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
    }
}
