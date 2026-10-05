namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorModelPackLeaseTests
{
    [Fact]
    public async Task PendingReleaseFailureIsSharedByConcurrentAndLaterDisposals()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Release failed.");
        int releases = 0;
        var lease = new TensorModelPackLease(new("model", "revision"), new Dictionary<string, string>(), async () =>
        {
            Interlocked.Increment(ref releases);
            await release.Task;
            throw failure;
        });

        Task first = lease.DisposeAsync().AsTask();
        Task concurrent = lease.DisposeAsync().AsTask();
        bool firstPending = !first.IsCompleted;
        bool concurrentPending = !concurrent.IsCompleted;
        release.SetResult();

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => concurrent.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => lease.DisposeAsync().AsTask()));
        Assert.True(firstPending);
        Assert.True(concurrentPending);
        Assert.Equal(1, releases);
    }
}
