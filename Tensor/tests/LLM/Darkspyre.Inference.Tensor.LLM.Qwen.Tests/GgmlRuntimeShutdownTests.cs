using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;
using TensorSharp.GGML;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class GgmlRuntimeShutdownTests
{
    [Fact]
    public async Task CancelledShutdownWaitDoesNotCancelSharedCleanupOrReopenAdmission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supplier = new Supplier(null)
        {
            OnShutdown = () =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return new(true, null);
            }
        };
        var owner = new GgmlRuntimeOwner(new(supplier.Build, supplier.Rid, []), supplier);
        using var cancellation = new CancellationTokenSource();
        Task<TensorRuntimeShutdownResult> cancelledWait = owner.ShutdownAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<TensorRuntimeShutdownResult> sharedWait = owner.ShutdownAsync();
            Task disposal = owner.DisposeAsync().AsTask();

            cancellation.Cancel();
            OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancelledWait.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, cancelled.CancellationToken);
            Assert.False(sharedWait.IsCompleted);
            Assert.False(disposal.IsCompleted);
            Assert.Equal(TensorRuntimeState.Stopping, owner.State);
            Assert.Throws<ObjectDisposedException>(() => owner.EnterExecution());

            release.SetResult();
            TensorRuntimeShutdownResult result = await sharedWait.WaitAsync(TimeSpan.FromSeconds(5));
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TensorRuntimeShutdownState.Completed, result.State);
            Assert.False(result.NativeStateMayRemain);
            Assert.Equal(TensorRuntimeState.Stopped, owner.State);
            Assert.Equal(1, supplier.Shutdowns);
            Assert.Equal(TensorRuntimeShutdownState.AlreadyStopped, (await owner.ShutdownAsync()).State);
        }
        finally
        {
            release.TrySetResult();
            await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SafeSupplierBusyRetainsExplicitReleaseAccessWithoutReopeningAdmission()
    {
        bool externalResourcesReleased = false;
        var lease = new FailedLease(new InvalidOperationException("unused failure")) { Refuse = false };
        var supplier = new Supplier(null, () => lease)
        {
            OnShutdown = () => new(externalResourcesReleased, null)
        };
        var candidate = new TensorRuntimeCandidate(
            new("managed-test", Path.GetFullPath(supplier.EntryLibraryName), "cpu", supplier.Build, supplier.Rid), TensorBackendKind.GgmlCpu,
            [new(supplier.EntryLibraryName, 1, new string('a', 64))]);
        var owner = new GgmlRuntimeOwner(new(supplier.Build, supplier.Rid, [candidate]), supplier);
        using (await owner.AcquireModelAsync(TensorBackendKind.GgmlCpu, CancellationToken.None))
        {
            TensorRuntimeShutdownResult active = await owner.ShutdownAsync();
            Assert.Equal(TensorRuntimeShutdownState.Busy, active.State);
            Assert.Equal(1, active.OutstandingLeases);
            Assert.Equal(0, supplier.Shutdowns);
            Assert.Equal(TensorRuntimeState.Ready, owner.State);
        }

        TensorRuntimeShutdownResult busy = await owner.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TensorRuntimeShutdownState.Busy, busy.State);
        Assert.Equal(0, busy.OutstandingLeases);
        Assert.True(busy.NativeStateMayRemain);
        Assert.Equal("native-resources-active", Assert.Single(busy.Diagnostics).Code);
        Assert.Throws<ObjectDisposedException>(() => owner.EnterExecution());
        Assert.Equal(1, lease.Attempts);
        Assert.Equal(1, supplier.Shutdowns);

        externalResourcesReleased = true;
        TensorRuntimeShutdownResult released = await owner.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TensorRuntimeShutdownState.Completed, released.State);
        Assert.False(released.NativeStateMayRemain);
        Assert.Equal(TensorRuntimeState.Stopped, owner.State);
        Assert.Null(owner.Current);
        Assert.Equal(2, supplier.Shutdowns);
        await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, supplier.Shutdowns);
    }

    [Fact]
    public async Task FailedSupplierShutdownPreservesItsActualCauseWithoutPublishingOrRetryingIt()
    {
        var original = new InvalidOperationException("private supplier shutdown cause");
        var supplier = new Supplier(original);
        var owner = new GgmlRuntimeOwner(new(supplier.Build, supplier.Rid, []), supplier);

        TensorRuntimeShutdownResult result = await owner.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TensorRuntimeShutdownState.FailedRequiresProcessExit, result.State);
        Assert.True(result.NativeStateMayRemain);
        Assert.Equal(TensorRuntimeState.RequiresProcessRestart, owner.State);
        Assert.Same(result, await owner.ShutdownAsync());
        string json = JsonSerializer.Serialize(result, TensorJson.TensorRuntimeShutdownResult);
        Assert.DoesNotContain(original.Message, json, StringComparison.Ordinal);
        Assert.Equal("runtime-requires-process-exit", Assert.Single(result.Diagnostics).Code);
        InferenceException disposal = await Assert.ThrowsAsync<InferenceException>(() => owner.DisposeAsync().AsTask());
        Assert.Same(original, disposal.InnerException);
        Assert.Same(disposal, await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask()));
        Assert.Throws<ObjectDisposedException>(() => owner.EnterExecution());
        Assert.Equal(1, supplier.Shutdowns);
    }

    [Fact]
    public async Task FailedAcquisitionLeaseReleasePreservesItsActualCauseAndExplicitRecovery()
    {
        using var cancellation = new CancellationTokenSource();
        var lease = new FailedLease(new InvalidOperationException("private lease cleanup cause"));
        var supplier = new Supplier(null, () =>
        {
            cancellation.Cancel();
            return lease;
        });
        var candidate = new TensorRuntimeCandidate(
            new("managed-test", Path.GetFullPath(supplier.EntryLibraryName), "cpu", supplier.Build, supplier.Rid), TensorBackendKind.GgmlCpu,
            [new(supplier.EntryLibraryName, 1, new string('a', 64))]);
        var owner = new GgmlRuntimeOwner(new(supplier.Build, supplier.Rid, [candidate]), supplier);

        AggregateException failedAcquisition = await Assert.ThrowsAsync<AggregateException>(async () =>
            await owner.AcquireModelAsync(TensorBackendKind.GgmlCpu, cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, failedAcquisition.InnerExceptions.Count);
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(failedAcquisition.InnerExceptions[0]).CancellationToken);
        Assert.Same(lease.Failure, failedAcquisition.InnerExceptions[1]);

        InferenceException disposal = await Assert.ThrowsAsync<InferenceException>(() => owner.DisposeAsync().AsTask());
        Assert.Same(lease.Failure, disposal.InnerException);
        Assert.Equal(2, lease.Attempts);
        Assert.Same(disposal, await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask()));
        Assert.Equal(2, lease.Attempts);
        Assert.Throws<ObjectDisposedException>(() => owner.EnterExecution());
        Assert.Equal(0, supplier.Shutdowns);

        TensorRuntimeShutdownResult refused = await owner.ShutdownAsync();
        Assert.Equal(TensorRuntimeShutdownState.FailedRequiresProcessExit, refused.State);
        Assert.Equal(1, refused.OutstandingLeases);
        Assert.True(refused.NativeStateMayRemain);
        string json = JsonSerializer.Serialize(refused, TensorJson.TensorRuntimeShutdownResult);
        Assert.DoesNotContain(lease.Failure.Message, json, StringComparison.Ordinal);
        lease.Refuse = false;
        TensorRuntimeShutdownResult released = await owner.ShutdownAsync();
        Assert.Equal(TensorRuntimeShutdownState.Completed, released.State);
        Assert.Equal(0, released.OutstandingLeases);
        Assert.False(released.NativeStateMayRemain);
        Assert.Equal(TensorRuntimeState.Stopped, owner.State);
        Assert.Equal(4, lease.Attempts);
        Assert.Equal(1, supplier.Shutdowns);
        Assert.Same(disposal, await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask()));
    }

    private sealed class FailedLease(Exception failure) : IDisposable
    {
        public Exception Failure { get; } = failure;
        public bool Refuse { get; set; } = true;
        public int Attempts { get; private set; }
        public void Dispose()
        {
            Attempts++;
            if (Refuse) throw Failure;
        }
    }

    private sealed class Supplier(Exception? failure, Func<IDisposable>? acquire = null) : IGgmlRuntime
    {
        private GgmlRuntimePlan? _plan;
        public TensorSharpBuildIdentity Build => new("test-managed", "test-abi", "test-commit");
        public string Rid => "osx-arm64";
        public string EntryLibraryName => "unused-test-library";
        public GgmlRuntimeState State { get; private set; } = GgmlRuntimeState.Unconfigured;
        public int Shutdowns { get; private set; }
        public Func<GgmlNativeShutdownResult>? OnShutdown { get; init; }
        public Task<IReadOnlyList<GgmlNativeCandidate>> ResolvePackageCandidatesAsync() => throw new NotSupportedException();
        public GgmlNativeRefusal? Check(GgmlNativeCandidate candidate) => null;
        public void Configure(GgmlRuntimePlan plan) => _plan = plan;
        public Task<GgmlInitializationResult> InitializeAsync()
        {
            State = GgmlRuntimeState.Ready;
            GgmlNativeCandidate candidate = Assert.Single(_plan!.Candidates!);
            return Task.FromResult(new GgmlInitializationResult(_plan, GgmlInitializationState.Ready,
                new(GgmlNativeSelectionState.Loaded, candidate, Path.Combine(candidate.Directory, EntryLibraryName),
                    GgmlBackendType.Cpu, new(Build.Version, "test-source", Build.GgmlCommit, "cpu", Rid, "generic", "")
                    { NativeAbi = Build.NativeAbi }, [], []), null));
        }
        public IDisposable AcquireModelLease() => acquire!();
        public GgmlNativeShutdownResult Shutdown()
        {
            Shutdowns++;
            if (failure is not null) throw failure;
            GgmlNativeShutdownResult result = OnShutdown?.Invoke() ?? new(true, null);
            if (result.Released) State = GgmlRuntimeState.Stopped;
            return result;
        }
    }
}
