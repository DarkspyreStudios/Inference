using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Video.Tests;

public sealed class VideoOperationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly InferenceResourceScope Scope = new("controlled-endpoint", "controlled-account");
    private static VideoConnection Connection => new() { Transport = VideoTransport.XaiImagine, Endpoint = "https://api.x.ai/v1", Xai = new("connection") };
    private static VideoOutput Output => new(Scope, JsonSerializer.SerializeToElement(new { actual = "output" }));

    [Fact]
    public void DiDoesNotActivateAnUnimplementedTransportOrSetupOption()
    {
        using ServiceProvider container = new ServiceCollection().AddVideoConnect().BuildServiceProvider();
        VideoProviderService service = container.GetRequiredService<VideoProviderService>();
        Assert.Empty(container.GetServices<IVideoTransport>()); Assert.Empty(service.SetupDefinitions);
        InferenceException error = Assert.Throws<InferenceException>(() => service.Bind(Connection));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
        Assert.Throws<ArgumentException>(() => new VideoProviderService([new FixtureTransport(), new FixtureTransport()]));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("edit")]
    [InlineData("extension")]
    public async Task EachRequestFreezesBinaryBytesBeforeHeldCredentialAcquisition(string kind)
    {
        var source = new Source { Release = new() }; await using var access = new LlmConnectionAccess(source, []);
        var binding = new XaiFamilyBinding(new("connection"), access); var transport = new FixtureTransport();
        IVideoInference facade = new VideoProviderService([transport]).Bind(Connection, binding);
        byte[] bytes = [1, 2, 3]; var media = new BinaryMediaSource(null, bytes);
        VideoRequest request = kind switch
        {
            "edit" => new VideoEditRequest { ModelId = "manual-model", Prompt = "edit", SourceVideo = media },
            "extension" => new VideoExtensionRequest { ModelId = "manual-model", SourceVideo = media },
            _ => new VideoGenerationRequest
            {
                ModelId = "manual-model",
                FirstFrame = media,
                LastFrame = media,
                Keyframes = [new(media, TimeSpan.Zero)],
                ReferenceImages = [media],
                ReferenceAudio = [new VideoAudioReference(media)]
            }
        };
        Task<VideoJob> pending = facade.SubmitAsync(request);
        await source.Entered.Task.WaitAsync(Bound); bytes[0] = 9; source.Release.SetResult();
        await pending.WaitAsync(Bound);
        Assert.Equal([1, 2, 3], transport.SubmittedBytes);
        Assert.Equal(1, source.Reads); Assert.Equal(1, transport.Submits);
        Assert.NotSame(binding, transport.Binding);
        Assert.Equal("protected-token", (await transport.Binding!.Authorization.GetAsync()).Value);
        Assert.Equal(1, source.Reads);
        await ((IAsyncDisposable)facade).DisposeAsync(); Assert.Equal(0, transport.Disposes); Assert.Equal(0, source.Disposes);
    }

    [Theory]
    [InlineData("unknown-options")]
    [InlineData("extension-user")]
    [InlineData("missing-resources")]
    public async Task KnownIncompatibleInputRefusesBeforeCredentialOrTransportEffects(string kind)
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        var transport = new FixtureTransport(); IVideoInference facade = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access));
        VideoRequest request = kind switch
        {
            "extension-user" => new VideoExtensionRequest { ModelId = "m", SourceVideo = new UrlMediaSource(new("https://example.invalid")), ProviderOptions = new XaiVideoOptions(User: "explicit") },
            "missing-resources" => new VideoGenerationRequest { ModelId = "m", FirstFrame = new ResourceMediaSource(new("w", "r")) },
            _ => new VideoGenerationRequest { ModelId = "m", ProviderOptions = new UnknownOptions() }
        };
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, (await Assert.ThrowsAsync<InferenceException>(() => facade.SubmitAsync(request))).Code);
        Assert.Equal(0, source.Reads); Assert.Equal(0, transport.Submits);
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, (await Assert.ThrowsAsync<InferenceException>(() => facade.MaterializeAsync(Output))).Code);
        Assert.Equal(0, transport.Downloads); Assert.Equal(0, source.Reads);
        await ((IAsyncDisposable)facade).DisposeAsync();
    }

    [Fact]
    public async Task NativeOperationsKeepActualIdentityAndPollExactlyOnce()
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        var transport = new FixtureTransport(); IVideoInference facade = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access));
        Assert.Empty((await facade.GetCapabilitiesAsync("unlisted-manual")).Operations);
        VideoJob job = await facade.SubmitAsync(new VideoGenerationRequest { ModelId = "unlisted-manual" });
        VideoJobSnapshot snapshot = await facade.GetAsync(job);
        Assert.Same(job, snapshot.Job); Assert.Equal(VideoJobState.Completed, snapshot.State);
        Assert.Null(snapshot.ProgressPercent); Assert.Null(snapshot.Output!.ContentType); Assert.Null(snapshot.Output.Duration);
        Assert.Equal(1, transport.Gets); Assert.Equal(0, transport.Downloads);
        await using Stream download = await facade.DownloadAsync(snapshot.Output);
        Assert.Equal(1, download.ReadByte());
        await download.DisposeAsync();
        await ((IAsyncDisposable)facade).DisposeAsync();
        Assert.Equal(1, transport.Downloads); Assert.Equal(1, transport.StreamDisposals);
        Assert.Equal(0, transport.Disposes); Assert.Equal(0, source.Disposes);
    }

    [Fact]
    public async Task HeldIgnoringProviderOperationDrainsAndNeverReturnsLateSuccess()
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        var transport = new FixtureTransport { ReleaseGet = new() }; IVideoInference facade = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access));
        Task<VideoJobSnapshot> pending = facade.GetAsync(new("job", Scope)); await transport.Entered.Task.WaitAsync(Bound);
        Task retire = ((IAsyncDisposable)facade).DisposeAsync().AsTask(); Assert.False(retire.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.GetCapabilitiesAsync());
        transport.ReleaseGet.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Bound));
        await retire.WaitAsync(Bound); await ((IAsyncDisposable)facade).DisposeAsync(); Assert.Equal(0, transport.Disposes);
    }

    [Fact]
    public async Task RetirementClosesOwnedDownloadAndDrainsIgnoringReadBeforeRelease()
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        var stream = new HeldStream(); var transport = new FixtureTransport { Download = stream };
        IVideoInference facade = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access));
        Stream result = await facade.DownloadAsync(Output);
        Task<int> pending = result.ReadAsync(new byte[1]).AsTask(); await stream.Entered.Task.WaitAsync(Bound);
        Task retire = ((IAsyncDisposable)facade).DisposeAsync().AsTask();
        await stream.Closed.Task.WaitAsync(Bound); Assert.False(retire.IsCompleted);
        stream.Release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Bound));
        await retire.WaitAsync(Bound); await result.DisposeAsync(); Assert.Equal(1, stream.Disposes);
    }

    [Theory]
    [InlineData("async")]
    [InlineData("array")]
    [InlineData("span")]
    public async Task SynchronousOwnerFenceRejectsReadBeforeDelayedLinkedCancellation(string mode)
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        using var caller = new CancellationTokenSource(); using var unblock = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new HeldStream(); var transport = new FixtureTransport { Download = stream, ReleaseGet = new() };
        IVideoInference facade = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access));
        Task<VideoJobSnapshot>? anchor = null;
        Task<int>? pending = null;
        Stream? owned = null;
        CancellationTokenRegistration blocker = default;
        Task? retirement = null;
        try
        {
            anchor = facade.GetAsync(new("job", Scope));
            await transport.Entered.Task.WaitAsync(Bound);
            CancellationToken stop = transport.LastGetToken;
            owned = await facade.DownloadAsync(Output, caller.Token);
            pending = mode switch
            {
                "array" => Task.Run(() => owned.Read(new byte[1], 0, 1)),
                "span" => Task.Run(() => owned.Read(new byte[1].AsSpan())),
                _ => owned.ReadAsync(new byte[1]).AsTask()
            };
            await stream.Entered.Task.WaitAsync(Bound);
            blocker = stop.Register(() =>
            {
                callbackEntered.TrySetResult();
                if (!unblock.Wait(Bound)) throw new TimeoutException("The controlled cancellation callback was not released.");
            });
            retirement = ((IAsyncDisposable)facade).DisposeAsync().AsTask();
            await callbackEntered.Task.WaitAsync(Bound);
            Assert.True(stop.IsCancellationRequested); Assert.False(stream.LastReadToken.IsCancellationRequested);
            stream.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Bound));
            Assert.ThrowsAny<OperationCanceledException>(() => owned.Read(new byte[1], 0, 1));
            Assert.Equal(1, stream.Reads);
            Assert.False(retirement.IsCompleted);
        }
        finally
        {
            retirement ??= ((IAsyncDisposable)facade).DisposeAsync().AsTask();
            unblock.Set(); stream.Release.TrySetResult(); transport.ReleaseGet.TrySetResult();
            try { await Task.WhenAll(new Task?[] { anchor, pending }.OfType<Task>().Select(task => Record.ExceptionAsync(() => task))); }
            finally
            {
                try { await retirement.WaitAsync(Bound); }
                finally { try { if (owned is not null) await owned.DisposeAsync(); } finally { blocker.Dispose(); } }
            }
        }
        Assert.Equal(1, stream.Disposes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaterializationStreamsActualBytesAndPreservesCommitAndCleanupFacts(bool failCleanup)
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        var resources = new Resources { FailCleanup = failCleanup }; var transport = new FixtureTransport();
        IVideoInference facade = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access), resources);
        if (failCleanup)
        {
            InferenceResourceOperationException error = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => facade.MaterializeAsync(Output));
            Assert.Equal(InferenceResourceCommitOutcome.Committed, error.Failure.CommitOutcome);
            Assert.Equal(InferenceResourceCommitOutcome.Committed, error.Failure.SelectionOutcome);
        }
        else
        {
            InferenceResourceDescriptor result = await facade.MaterializeAsync(Output);
            Assert.Equal(resources.Writer!.Reference, result.Reference); Assert.Null(result.ContentType);
        }
        Assert.Equal([1, 2, 3], resources.Writer!.Data.ToArray()); Assert.Equal(3, resources.Writer.Written);
        Assert.True(resources.Writer.SelectForSession); Assert.Equal(1, resources.Writer.Commits); Assert.Equal(1, resources.Writer.Disposes);
        Assert.Equal(1, transport.StreamDisposals); Assert.Equal(1, source.Reads); Assert.Equal(0, resources.Disposes);
        await ((IAsyncDisposable)facade).DisposeAsync(); Assert.Equal(0, resources.Disposes);
    }

    [Fact]
    public async Task PartialDownloadAndWriterCleanupFailuresRemainDistinctWithoutCommitRetry()
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        var resources = new Resources { FailCleanup = true };
        var transport = new FixtureTransport { Download = new PartialStream() };
        IVideoInference facade = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access), resources);
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => facade.MaterializeAsync(Output));
        InferenceResourceOperationException original = Assert.IsType<InferenceResourceOperationException>(error.InnerExceptions[0]);
        Assert.Equal(InferenceResourceOperationPhase.Read, original.Failure.Phase);
        Assert.Equal(3, original.Failure.BytesTransferred); Assert.Equal(InferenceResourceCommitOutcome.NotAttempted, original.Failure.CommitOutcome);
        Assert.IsType<IOException>(original.InnerException); Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(0, resources.Writer!.Commits); Assert.Equal(1, resources.Writer.Disposes);
        await ((IAsyncDisposable)facade).DisposeAsync();
    }

    [Fact]
    public async Task RefusedCommitReportsUnknownOutcomeAndNeverRetriesOrDeletes()
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        var resources = new Resources { FailCommit = true };
        IVideoInference facade = new VideoProviderService([new FixtureTransport()]).Bind(Connection, new(new("connection"), access), resources);
        InferenceResourceOperationException error = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => facade.MaterializeAsync(Output));
        Assert.Equal(InferenceResourceOperationPhase.Commit, error.Failure.Phase);
        Assert.Equal(InferenceResourceCommitOutcome.Unknown, error.Failure.CommitOutcome);
        Assert.Equal(InferenceResourceCommitOutcome.Unknown, error.Failure.SelectionOutcome);
        Assert.Equal(3, error.Failure.BytesTransferred); Assert.Equal(resources.Writer!.Reference, error.Failure.Reference);
        Assert.Equal(1, resources.Writer.Commits); Assert.Equal(1, resources.Writer.Disposes);
        await ((IAsyncDisposable)facade).DisposeAsync();
    }

    [Fact]
    public async Task IgnoredCancellationAfterRealCommitPreservesCommittedFactsAndDrains()
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        var resources = new Resources { ReleaseCommit = new() };
        IVideoInference facade = new VideoProviderService([new FixtureTransport()]).Bind(Connection, new(new("connection"), access), resources);
        Task<InferenceResourceDescriptor> pending = facade.MaterializeAsync(Output);
        await resources.Writer!.Entered.Task.WaitAsync(Bound);
        Task retirement = ((IAsyncDisposable)facade).DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted);
        resources.ReleaseCommit.SetResult();
        InferenceResourceOperationCanceledException error = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => pending.WaitAsync(Bound));
        Assert.Equal(InferenceResourceCommitOutcome.Committed, error.Failure.CommitOutcome);
        Assert.Equal(InferenceResourceCommitOutcome.Committed, error.Failure.SelectionOutcome);
        Assert.Equal(1, error.Failure.SelectionVersion!.Revision); Assert.Equal(3, error.Failure.BytesTransferred);
        await retirement.WaitAsync(Bound); Assert.Equal(1, resources.Writer.Disposes); Assert.Equal(0, resources.Disposes);
    }

    [Fact]
    public async Task RetirementDuringSuccessfulCleanupPreservesCommittedResourceFacts()
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        var resources = new Resources { ReleaseCleanup = new() };
        IVideoInference facade = new VideoProviderService([new FixtureTransport()]).Bind(Connection, new(new("connection"), access), resources);
        Task<InferenceResourceDescriptor>? pending = null;
        Task? retirement = null;
        try
        {
            pending = facade.MaterializeAsync(Output);
            await resources.Writer!.CleanupEntered.Task.WaitAsync(Bound);
            retirement = ((IAsyncDisposable)facade).DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted); resources.ReleaseCleanup.TrySetResult();
            InferenceResourceOperationCanceledException error = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => pending.WaitAsync(Bound));
            Assert.Equal(resources.Writer.Reference, error.Failure.Reference);
            Assert.Equal(InferenceResourceCommitOutcome.Committed, error.Failure.CommitOutcome);
            Assert.Equal(InferenceResourceCommitOutcome.Committed, error.Failure.SelectionOutcome);
            Assert.Equal(new InferenceResourceSelectionVersion("w", "s", 1), error.Failure.SelectionVersion);
            Assert.Equal(3, error.Failure.BytesTransferred); Assert.Equal(1, resources.Writer.Commits);
        }
        finally
        {
            retirement ??= ((IAsyncDisposable)facade).DisposeAsync().AsTask();
            resources.ReleaseCleanup.TrySetResult();
            try { if (pending is not null) await Record.ExceptionAsync(() => pending); }
            finally { await retirement.WaitAsync(Bound); }
        }
        Assert.Equal(1, resources.Writer!.Disposes); Assert.Equal(0, resources.Disposes);
    }

    [Fact]
    public async Task ControlledTransportReceiptGatePrecedesDownloadAndResourceCallbacks()
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        var resources = new Resources(); var transport = new FixtureTransport();
        var binding = new XaiFamilyBinding(new("connection"), access);
        IVideoInference facade = new VideoProviderService([transport]).Bind(Connection, binding, resources);
        VideoOutput imported = Output with { Scope = new("unproven", "imported") };
        InferenceResourceOperationException error = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => facade.MaterializeAsync(imported));
        Assert.IsType<ArgumentException>(error.InnerException); Assert.Equal(0, transport.Downloads); Assert.Null(resources.Writer);
        Assert.Same(binding, transport.ReceiptBinding); Assert.Equal(0, source.Reads);
        await Assert.ThrowsAsync<ArgumentException>(() => facade.GetAsync(new("imported", imported.Scope)));
        Assert.Equal(0, source.Reads); Assert.Equal(0, transport.Gets);
        await ((IAsyncDisposable)facade).DisposeAsync();
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Host")]
    [InlineData("Accept")]
    [InlineData("Content-Type")]
    public async Task FamilyReservedHeadersRefuseBeforeCredentialAcquisition(string header)
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        var service = new VideoProviderService([new FixtureTransport()]);
        Assert.Throws<InvalidOperationException>(() => service.Bind(Connection with { AdditionalHeaders = new Dictionary<string, string> { [header] = "invalid" } }, new(new("connection"), access)));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task AcquiredSnapshotReservedHeadersRefuseBeforeTransportOrRefresh()
    {
        var source = new Source { Headers = new Dictionary<string, string> { ["Authorization"] = "invalid" } };
        var authenticator = new UncallableAuthenticator();
        await using var access = new LlmConnectionAccess(source, [authenticator]);
        var transport = new FixtureTransport(); IVideoInference facade = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access));
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.SubmitAsync(new VideoGenerationRequest { ModelId = "m" }));
        Assert.Equal(1, source.Reads); Assert.Equal(0, transport.Submits);
        Assert.Equal(0, authenticator.Calls);
        await ((IAsyncDisposable)facade).DisposeAsync();
    }

    [Fact]
    public async Task ReturnedStreamCleanupFailureIsSharedByEveryRetirementCaller()
    {
        var source = new Source(); await using var access = new LlmConnectionAccess(source, []);
        var stream = new FailingCleanupStream(); var transport = new FixtureTransport { Download = stream };
        IVideoInference facade = new VideoProviderService([transport]).Bind(Connection, new(new("connection"), access));
        Stream owned = await facade.DownloadAsync(Output);
        Exception? first = await Record.ExceptionAsync(() => ((IAsyncDisposable)facade).DisposeAsync().AsTask());
        Exception? second = await Record.ExceptionAsync(() => ((IAsyncDisposable)facade).DisposeAsync().AsTask());
        Assert.NotNull(first); Assert.Same(first, second);
        Assert.Equal(1, stream.Disposes); Assert.Equal(0, transport.Disposes);
        Assert.NotNull(await Record.ExceptionAsync(() => owned.DisposeAsync().AsTask()));
        Assert.Equal(1, stream.Disposes);
    }

    internal sealed class Source : ILlmConnectionSource, IDisposable
    {
        internal IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
        internal TaskCompletionSource? Release { get; init; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads { get; private set; }
        internal int Disposes { get; private set; }
        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Reads++; Entered.TrySetResult(); if (Release is not null) await Release.Task.ConfigureAwait(false);
            return new()
            {
                Transport = LlmTransport.XaiResponses,
                Endpoint = "https://api.x.ai/v1",
                Xai = new(),
                Version = "actual-version",
                AdditionalHeaders = Headers,
                Credential = new LlmCredential { OAuth = new() { AccessToken = "protected-token", ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } }.Serialize()
            };
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No refresh is expected.");
        public void Dispose() => Disposes++;
    }
    internal sealed class FixtureTransport : IVideoTransport, IDisposable
    {
        public VideoTransport Transport => VideoTransport.XaiImagine;
        public InferenceSetupDefinition SetupDefinition { get; } = new(new("controlled-video", "Controlled Video", InferenceImplementationKind.Connect, InferenceTask.VideoGeneration), "Managed test fixture.");
        internal XaiFamilyBinding? Binding { get; private set; }
        internal XaiFamilyBinding? ReceiptBinding { get; private set; }
        internal byte[]? SubmittedBytes { get; private set; }
        internal int Submits { get; private set; }
        internal int Gets { get; private set; }
        internal int Downloads { get; private set; }
        internal int Disposes { get; private set; }
        internal int StreamDisposals { get; private set; }
        internal TaskCompletionSource? ReleaseGet { get; init; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Stream? Download { get; init; }
        internal CancellationToken LastGetToken { get; private set; }
        public Task<VideoCapabilities> GetCapabilitiesAsync(VideoConnection connection, string? modelId = null, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default) => Task.FromResult(new VideoCapabilities());
        public Task<VideoJob> SubmitAsync(VideoConnection connection, VideoRequest request, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
        {
            Submits++; Binding = xai;
            InferenceMediaSource? media = request switch { VideoGenerationRequest generation => generation.FirstFrame, VideoEditRequest edit => edit.SourceVideo, VideoExtensionRequest extension => extension.SourceVideo, _ => null };
            SubmittedBytes = (media as BinaryMediaSource)?.Data.ToArray();
            if (request is VideoGenerationRequest { FirstFrame: BinaryMediaSource first } generationRequest)
            {
                Assert.Equal(first.Data.ToArray(), Assert.IsType<BinaryMediaSource>(generationRequest.LastFrame).Data.ToArray());
                Assert.Equal(first.Data.ToArray(), Assert.IsType<BinaryMediaSource>(generationRequest.Keyframes![0].Image).Data.ToArray());
                Assert.Equal(first.Data.ToArray(), Assert.IsType<BinaryMediaSource>(generationRequest.ReferenceImages![0]).Data.ToArray());
                Assert.Equal(first.Data.ToArray(), Assert.IsType<BinaryMediaSource>(Assert.IsType<VideoAudioReference>(generationRequest.ReferenceAudio![0]).Source).Data.ToArray());
            }
            return Task.FromResult(new VideoJob("actual-job", Scope));
        }
        public async Task<VideoJobSnapshot> GetAsync(VideoConnection connection, VideoJob job, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
        {
            ReceiptBinding = xai;
            if (job.Scope != Scope) throw new ArgumentException("The controlled receipt authority is not proven.");
            await xai!.AcquireAsync(cancellationToken);
            Gets++; LastGetToken = cancellationToken; Entered.TrySetResult(); if (ReleaseGet is not null) await ReleaseGet.Task.ConfigureAwait(false); return new(job, VideoJobState.Completed, Output: Output);
        }
        public async Task<Stream> DownloadAsync(VideoConnection connection, VideoOutput output, XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
        {
            ReceiptBinding = xai;
            if (output.Scope != Scope) throw new ArgumentException("The controlled receipt authority is not proven.");
            await xai!.AcquireAsync(cancellationToken);
            Downloads++; return Download ?? new CountingStream(() => StreamDisposals++);
        }
        public void Dispose() => Disposes++;
    }
    private sealed class CountingStream(Action closed) : MemoryStream([1, 2, 3], false)
    {
        private bool _disposed;
        protected override void Dispose(bool disposing) { if (disposing && !_disposed) { _disposed = true; closed(); } base.Dispose(disposing); }
    }
    private sealed class HeldStream : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposes { get; private set; }
        internal int Reads { get; private set; }
        internal CancellationToken LastReadToken { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Reads++; LastReadToken = cancellationToken; Entered.TrySetResult(); await Release.Task.ConfigureAwait(false); return 0; }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; Entered.TrySetResult(); Release.Task.GetAwaiter().GetResult(); return 0; }
        public override int Read(Span<byte> buffer) { Reads++; Entered.TrySetResult(); Release.Task.GetAwaiter().GetResult(); return 0; }
        protected override void Dispose(bool disposing) { if (disposing) { Disposes++; Closed.TrySetResult(); } base.Dispose(disposing); }
    }
    private sealed class PartialStream() : MemoryStream([1, 2, 3], false)
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) return ValueTask.FromException<int>(new IOException("controlled partial read"));
            _read = true; return base.ReadAsync(buffer, cancellationToken);
        }
    }
    private sealed class FailingCleanupStream : MemoryStream
    {
        internal int Disposes { get; private set; }
        public override async ValueTask DisposeAsync()
        {
            Disposes++;
            await base.DisposeAsync();
            throw new IOException("controlled owned stream cleanup");
        }
    }
    internal sealed class Resources : IInferenceResourceAccess, IDisposable
    {
        public string WorkspaceId => "w";
        public string SessionId => "s";
        internal bool FailCleanup { get; init; }
        internal bool FailCommit { get; init; }
        internal TaskCompletionSource? ReleaseCommit { get; init; }
        internal TaskCompletionSource? ReleaseCleanup { get; init; }
        internal Writer? Writer { get; private set; }
        internal int Disposes { get; private set; }
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default)
        { Writer = new(resource, FailCleanup, FailCommit, ReleaseCommit, ReleaseCleanup); return Task.FromResult<IInferenceResourceWriteHandle>(Writer); }
        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void Dispose() => Disposes++;
    }
    internal sealed class Writer(InferenceResourceCreate metadata, bool failCleanup, bool failCommit, TaskCompletionSource? release, TaskCompletionSource? releaseCleanup) : IInferenceResourceWriteHandle
    {
        public InferenceResourceReference Reference { get; } = new("w", "actual-written");
        internal MemoryStream Data { get; } = new();
        public Stream Content => Data;
        internal long Written { get; private set; }
        internal bool SelectForSession { get; private set; }
        internal int Commits { get; private set; }
        internal int Disposes { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<InferenceResourceCommitResult> CommitAsync(long bytesWritten, InferenceResourceCommitOptions? options = null, CancellationToken cancellationToken = default)
        {
            Commits++; Written = bytesWritten; SelectForSession = options?.SelectForSession == true;
            Entered.TrySetResult();
            if (release is not null) await release.Task.ConfigureAwait(false);
            if (failCommit) throw new IOException("controlled unknown commit");
            return new(new(Reference, metadata.SessionId, metadata.FileName, metadata.ContentType, bytesWritten), new("w", "s", 1, [Reference]));
        }
        public async ValueTask DisposeAsync() { Disposes++; CleanupEntered.TrySetResult(); if (releaseCleanup is not null) await releaseCleanup.Task.ConfigureAwait(false); Data.Dispose(); if (failCleanup) throw new InvalidOperationException("controlled cleanup"); }
    }
    private sealed record UnknownOptions : VideoProviderOptions;
    private sealed class UncallableAuthenticator : ILlmAuthenticator
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        internal int Calls { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Credential refresh must not be entered."); }
    }
}
