using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed class ImageCleanupOwnershipTests
{
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
}
