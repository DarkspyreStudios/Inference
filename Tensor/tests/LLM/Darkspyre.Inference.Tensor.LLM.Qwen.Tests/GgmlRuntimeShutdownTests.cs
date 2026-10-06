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

    private sealed class Supplier(Exception failure) : IGgmlRuntime
    {
        public TensorSharpBuildIdentity Build => new("test-managed", "test-abi", "test-commit");
        public string Rid => "osx-arm64";
        public string EntryLibraryName => "unused-test-library";
        public GgmlRuntimeState State => GgmlRuntimeState.Unconfigured;
        public int Shutdowns { get; private set; }
        public Task<IReadOnlyList<GgmlNativeCandidate>> ResolvePackageCandidatesAsync() => throw new NotSupportedException();
        public GgmlNativeRefusal? Check(GgmlNativeCandidate candidate) => throw new NotSupportedException();
        public void Configure(GgmlRuntimePlan plan) => throw new NotSupportedException();
        public Task<GgmlInitializationResult> InitializeAsync() => throw new NotSupportedException();
        public IDisposable AcquireModelLease() => throw new NotSupportedException();
        public GgmlNativeShutdownResult Shutdown()
        {
            Shutdowns++;
            throw failure;
        }
    }
}
