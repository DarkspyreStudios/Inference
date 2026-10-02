using System.Text.Json;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Video.Tests;

public sealed class VideoCleanupOwnershipTests
{
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
}

public sealed class VideoCleanupForeignStream : MemoryStream
{
    public VideoCleanupForeignStream() : base([1, 2, 3], false) { }
    public Exception? Failure { get; set; }
    public int Disposals { get; private set; }
    public override ValueTask DisposeAsync()
    {
        Disposals++;
        if (Failure is not null) return ValueTask.FromException(Failure);
        return base.DisposeAsync();
    }
}
