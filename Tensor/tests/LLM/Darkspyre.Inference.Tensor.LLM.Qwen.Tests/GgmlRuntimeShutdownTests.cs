using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;
using TensorSharp.GGML;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class GgmlRuntimeShutdownTests
{
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
            State = GgmlRuntimeState.Stopped;
            return new(true, null);
        }
    }
}
