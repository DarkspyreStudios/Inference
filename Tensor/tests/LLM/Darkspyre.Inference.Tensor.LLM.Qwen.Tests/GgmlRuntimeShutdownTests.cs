using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;
using TensorSharp.GGML;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class GgmlRuntimeShutdownTests
{
    [Fact]
    public async Task MlxModelsAndProbesKeepTheGenerationReusableUntilExplicitShutdown()
    {
        int retirements = 0;
        Action retire = () => retirements++;
        var supplier = new Supplier(null);
        await using var owner = new GgmlRuntimeOwner(new(supplier.Build, supplier.Rid, []), supplier);
        using (GgmlRuntimeOwner.ModelLease first = owner.AcquireMlxModel(retire))
        using (GgmlRuntimeOwner.ModelLease second = owner.AcquireMlxModel(retire))
        using (IDisposable probe = owner.EnterMlx(retire))
        {
            TensorRuntimeShutdownResult busy = await owner.ShutdownAsync();
            Assert.Equal(TensorRuntimeShutdownState.Busy, busy.State);
            Assert.Equal(3, busy.OutstandingLeases);
            Assert.Equal(TensorBackendKind.Mlx, first.Backend);
            Assert.Equal(0, retirements);
            first.Dispose();
            first.Dispose();
            using GgmlRuntimeOwner.ModelLease replacement = owner.AcquireMlxModel(retire);
            Assert.Equal(3, (await owner.ShutdownAsync()).OutstandingLeases);
            Assert.Null(owner.Current);
            Assert.Equal(supplier.Build, owner.Plan.Build);
        }
        Assert.Equal(0, supplier.Shutdowns);
        Assert.Equal(0, retirements);
        TensorRuntimeShutdownResult stopped = await owner.ShutdownAsync();
        Assert.Equal(TensorRuntimeShutdownState.Completed, stopped.State);
        Assert.False(stopped.NativeStateMayRemain);
        Assert.Throws<ObjectDisposedException>(() => owner.EnterMlx(retire));
        Assert.Equal(TensorRuntimeShutdownState.AlreadyStopped, (await owner.ShutdownAsync()).State);
        Assert.Equal(1, retirements);
    }

    [Fact]
    public async Task CancelledMlxShutdownWaitKeepsSharedCleanupAndAdmissionFence()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int retirements = 0;
        Action retire = () =>
        {
            Interlocked.Increment(ref retirements);
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var supplier = new Supplier(null);
        var owner = new GgmlRuntimeOwner(new(supplier.Build, supplier.Rid, []), supplier);
        owner.AcquireMlxModel(retire).Dispose();
        using var cancellation = new CancellationTokenSource();
        Task<TensorRuntimeShutdownResult> canceledWait = owner.ShutdownAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<TensorRuntimeShutdownResult> sharedWait = owner.ShutdownAsync();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
            Assert.False(sharedWait.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => owner.AcquireMlxModel(retire));
            release.TrySetResult();
            Assert.Equal(TensorRuntimeShutdownState.Completed, (await sharedWait.WaitAsync(TimeSpan.FromSeconds(5))).State);
            Assert.Equal(1, retirements);
        }
        finally
        {
            release.TrySetResult();
            await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IndependentGgmlAndMlxShutdownFailuresRemainSharedWithoutReplay(bool ggmlOutOfMemory, bool mlxOutOfMemory)
    {
#pragma warning disable CA2201 // Inject native cleanup allocation failures without exhausting the test process.
        Exception ggmlFailure = ggmlOutOfMemory
            ? new OutOfMemoryException("private GGML terminal cause") : new IOException("private GGML terminal cause");
        Exception mlxFailure = mlxOutOfMemory
            ? new OutOfMemoryException("private MLX terminal cause") : new IOException("private MLX terminal cause");
#pragma warning restore CA2201
        int retirements = 0;
        var supplier = new Supplier(ggmlFailure);
        var owner = new GgmlRuntimeOwner(new(supplier.Build, supplier.Rid, []), supplier);
        owner.AcquireMlxModel(() => { retirements++; throw mlxFailure; }).Dispose();

        TensorRuntimeShutdownResult failed = await owner.ShutdownAsync();
        Assert.Equal(TensorRuntimeShutdownState.FailedRequiresProcessExit, failed.State);
        Assert.Equal(TensorRuntimeState.RequiresProcessRestart, owner.State);
        Assert.True(failed.NativeStateMayRemain);
        Assert.Same(failed, await owner.ShutdownAsync());
        InferenceException disposal = await Assert.ThrowsAsync<InferenceException>(() => owner.DisposeAsync().AsTask());
        AggregateException errors = Assert.IsType<AggregateException>(disposal.InnerException);
        Assert.Equal(2, errors.InnerExceptions.Count);
        Assert.Same(ggmlFailure, errors.InnerExceptions[0]);
        Assert.Same(mlxFailure, errors.InnerExceptions[1]);
        Assert.Same(disposal, await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask()));
        string json = JsonSerializer.Serialize(failed, TensorJson.TensorRuntimeShutdownResult);
        Assert.DoesNotContain(ggmlFailure.Message, json, StringComparison.Ordinal);
        Assert.DoesNotContain(mlxFailure.Message, json, StringComparison.Ordinal);
        Assert.Equal(1, retirements);
        Assert.Equal(1, supplier.Shutdowns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInitializationPreservesItsCauseAcrossLaterRefusalWithoutPublishingIt(bool probeFirst)
    {
        var original = new InvalidOperationException("private supplier initialization cause");
        var supplier = new Supplier(null) { InitializationFailure = original };
        var candidate = new TensorRuntimeCandidate(
            new("managed-test", Path.GetFullPath(supplier.EntryLibraryName), "cpu", supplier.Build, supplier.Rid), TensorBackendKind.GgmlCpu,
            [new(supplier.EntryLibraryName, 1, new string('a', 64))]);
        await using var owner = new GgmlRuntimeOwner(new(supplier.Build, supplier.Rid, [candidate]), supplier);
        if (probeFirst)
        {
            var request = new TensorBackendProbeRequest(Guid.NewGuid(), "managed-test", TensorBackendKind.GgmlCpu, supplier.Rid);
            TensorBackendAvailability observation = await owner.CheckAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TensorBackendAvailabilityState.RequiresProcessRestart, observation.State);
            string json = JsonSerializer.Serialize(observation, TensorJson.BackendAvailability);
            Assert.DoesNotContain(original.Message, json, StringComparison.Ordinal);
        }
        else
        {
            InferenceException initialAdmission = await Assert.ThrowsAsync<InferenceException>(async () =>
                await owner.AcquireModelAsync(TensorBackendKind.GgmlCpu, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(original, initialAdmission.InnerException);
        }
        Assert.Equal(TensorRuntimeState.RequiresProcessRestart, owner.State);
        Assert.Null(owner.Current);
        InferenceException admission = await Assert.ThrowsAsync<InferenceException>(async () =>
            await owner.AcquireModelAsync(TensorBackendKind.GgmlCpu, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(original, admission.InnerException);
        InferenceException execution = Assert.Throws<InferenceException>(() => owner.EnterExecution());
        Assert.Same(original, execution.InnerException);
    }

    [Fact]
    public async Task InitializationOutOfMemoryEscapesAndRemainsTheCauseOfLaterRefusal()
    {
#pragma warning disable CA2201 // Inject the runtime failure without exhausting the test process.
        var original = new OutOfMemoryException("private supplier allocation cause");
#pragma warning restore CA2201
        var supplier = new Supplier(null) { InitializationFailure = original };
        var candidate = new TensorRuntimeCandidate(
            new("managed-test", Path.GetFullPath(supplier.EntryLibraryName), "cpu", supplier.Build, supplier.Rid), TensorBackendKind.GgmlCpu,
            [new(supplier.EntryLibraryName, 1, new string('a', 64))]);
        await using var owner = new GgmlRuntimeOwner(new(supplier.Build, supplier.Rid, [candidate]), supplier);

        OutOfMemoryException initial = await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
            await owner.AcquireModelAsync(TensorBackendKind.GgmlCpu, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(original, initial);
        Assert.Equal(TensorRuntimeState.RequiresProcessRestart, owner.State);
        Assert.Null(owner.Current);
        InferenceException admission = await Assert.ThrowsAsync<InferenceException>(async () =>
            await owner.AcquireModelAsync(TensorBackendKind.GgmlCpu, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(original, admission.InnerException);
        InferenceException execution = Assert.Throws<InferenceException>(() => owner.EnterExecution());
        Assert.Same(original, execution.InnerException);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAcquisitionLeaseReleasePreservesItsActualCauseAndExplicitRecovery(bool outOfMemory)
    {
        using var cancellation = new CancellationTokenSource();
#pragma warning disable CA2201 // Inject a cleanup allocation failure without exhausting the test process.
        Exception failure = outOfMemory
            ? new OutOfMemoryException("private lease cleanup cause") : new InvalidOperationException("private lease cleanup cause");
#pragma warning restore CA2201
        var lease = new FailedLease(failure);
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

    internal sealed class Supplier(Exception? failure, Func<IDisposable>? acquire = null) : IGgmlRuntime
    {
        private GgmlRuntimePlan? _plan;
        public TensorSharpBuildIdentity Build => new("test-managed", "test-abi", "test-commit");
        public string Rid => "osx-arm64";
        public string EntryLibraryName => "unused-test-library";
        public GgmlRuntimeState State { get; private set; } = GgmlRuntimeState.Unconfigured;
        public int Shutdowns { get; private set; }
        public Func<GgmlNativeShutdownResult>? OnShutdown { get; init; }
        public Exception? InitializationFailure { get; init; }
        public Task<IReadOnlyList<GgmlNativeCandidate>> ResolvePackageCandidatesAsync() => throw new NotSupportedException();
        public GgmlNativeRefusal? Check(GgmlNativeCandidate candidate) => null;
        public void Configure(GgmlRuntimePlan plan) => _plan = plan;
        public Task<GgmlInitializationResult> InitializeAsync()
        {
            if (InitializationFailure is { } initializationFailure)
            {
                State = GgmlRuntimeState.Poisoned;
                return Task.FromException<GgmlInitializationResult>(initializationFailure);
            }
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
