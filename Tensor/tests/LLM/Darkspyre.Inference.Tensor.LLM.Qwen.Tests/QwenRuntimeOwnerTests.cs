using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.GGML;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenRuntimeOwnerTests
{
    private static readonly string[] RefusedCandidateCodes = ["candidate-entry-invalid", "native-refused"];
    private static TensorSharpBuildIdentity Build => new("2.8.6.8", new('a', 64), new('b', 40));
    private const string Rid = "linux-x64";
    private static TensorRuntimeCandidate Candidate(string id, TensorBackendKind backend) => new(
        new(id, Path.Combine(Path.GetTempPath(), "controlled-qwen", id, "libGgmlOps.so"), id, Build, Rid), backend,
        [new("libGgmlOps.so", 1, new('c', 64))]);
    private static TensorBackendProbeRequest Request(TensorBackendKind backend = TensorBackendKind.Auto) =>
        new(Guid.NewGuid(), QwenSetup.Definition.Implementation.Id, backend, Rid);

    internal static QwenRuntimeOwner CreateControlledCpuOwner(out Supplier supplier)
    {
        supplier = new();
        return new(new(Build, Rid, [Candidate("cpu", TensorBackendKind.GgmlCpu)]), supplier);
    }

    [Fact]
    public void ConstructionMetadataAndPlanComparisonHaveNoSupplierEffects()
    {
        var supplier = new Supplier();
        var input = new[] { Candidate("cpu", TensorBackendKind.GgmlCpu) };
        var plan = new TensorRuntimePlan(Build, Rid, input);
        var owner = new QwenRuntimeOwner(plan, supplier);
        input[0] = Candidate("other", TensorBackendKind.GgmlCpu);
        Assert.Equal("cpu", owner.Plan.Candidates![0].Identity.Id);
        Assert.Equal(TensorRuntimeState.Uninitialized, owner.State);
        Assert.Null(owner.Current);
        Assert.True(QwenRuntimeOwner.SamePlan(plan, owner.Plan));
        Assert.False(QwenRuntimeOwner.SamePlan(plan, new(Build, Rid, [])));
        Assert.False(QwenRuntimeOwner.SamePlan(plan, new(Build, Rid)));
        Assert.Equal(0, supplier.Effects);
    }

    [Fact]
    public async Task ExplicitEmptyNeverResolvesCatalogOrEntersNativeSelection()
    {
        var supplier = new Supplier();
        var owner = new QwenRuntimeOwner(new(Build, Rid, []), supplier);
        TensorBackendAvailability result = await owner.CheckAsync(Request());
        Assert.Equal(TensorBackendAvailabilityState.Unavailable, result.State);
        Assert.Null(result.ActualBackend);
        Assert.Null(result.IsInitialized);
        Assert.Equal(0, supplier.Effects);
        await owner.DisposeAsync();
        Assert.Equal(1, supplier.Shutdowns);
    }

    [Fact]
    public async Task AutomaticSelectionFallsBackOnlyAfterProvenNoRetainedState()
    {
        var supplier = new Supplier { RefusedBackends = [GgmlBackendType.Cuda, GgmlBackendType.Vulkan] };
        var owner = new QwenRuntimeOwner(new(Build, Rid,
            [Candidate("cuda13", TensorBackendKind.GgmlCuda), Candidate("vulkan", TensorBackendKind.GgmlVulkan), Candidate("cpu", TensorBackendKind.GgmlCpu)]), supplier);
        TensorBackendAvailability result = await owner.CheckAsync(Request());
        Assert.Equal(new[] { GgmlBackendType.Cuda, GgmlBackendType.Vulkan, GgmlBackendType.Cpu }, supplier.Configured);
        Assert.True(result.IsAvailable);
        Assert.Equal(TensorBackendKind.GgmlCpu, result.ActualBackend);
        Assert.True(result.IsInitialized);
        Assert.Equal("cpu", result.LoadedCandidateId);
        Assert.Equal(new string('d', 40), owner.Current!.NativeSourceCommit);
        Assert.Equal(Build, owner.Current.Build);
        await owner.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitBackendNeverFallsBackAndCannotSwitchReadyBackend()
    {
        var supplier = new Supplier { RefusedBackends = [GgmlBackendType.Cuda] };
        var owner = new QwenRuntimeOwner(new(Build, Rid,
            [Candidate("cuda13", TensorBackendKind.GgmlCuda), Candidate("cpu", TensorBackendKind.GgmlCpu)]), supplier);
        Assert.False((await owner.CheckAsync(Request(TensorBackendKind.GgmlCuda))).IsAvailable);
        Assert.Equal(new[] { GgmlBackendType.Cuda }, supplier.Configured);
        Assert.True((await owner.CheckAsync(Request(TensorBackendKind.GgmlCpu))).IsAvailable);
        int effects = supplier.Effects;
        Assert.False((await owner.CheckAsync(Request(TensorBackendKind.GgmlCuda))).IsAvailable);
        Assert.Equal(effects, supplier.Effects);
        await owner.DisposeAsync();
    }

    [Fact]
    public async Task PartialInitializationStopsAllFallbackAndSanitizesDiagnostics()
    {
        var supplier = new Supplier { Poison = true };
        var owner = new QwenRuntimeOwner(new(Build, Rid,
            [Candidate("cuda13", TensorBackendKind.GgmlCuda), Candidate("cpu", TensorBackendKind.GgmlCpu)]), supplier);
        TensorBackendAvailability result = await owner.CheckAsync(Request());
        Assert.Equal(TensorBackendAvailabilityState.RequiresProcessRestart, result.State);
        Assert.Equal(new[] { GgmlBackendType.Cuda }, supplier.Configured);
        Assert.Null(owner.Current);
        Assert.Contains(result.CandidateDiagnostics, diagnostic => diagnostic.CandidateId == "cpu"
            && diagnostic.Code == GgmlNativeRefusalCodes.NotSelected && diagnostic.NativeStateMayRemain);
        Assert.DoesNotContain("controlled", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("private-error", string.Join(" ", result.CandidateDiagnostics.Select(diagnostic => diagnostic.Message)), StringComparison.Ordinal);
        Assert.Throws<InferenceException>(() => owner.EnterExecution());
        await Assert.ThrowsAsync<InferenceException>(() => owner.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task TerminalShutdownFailureKeepsItsStateAndSharedOutcomeOnRepeatedCalls()
    {
        var supplier = new Supplier { Poison = true };
        var owner = new QwenRuntimeOwner(new(Build, Rid, [Candidate("cpu", TensorBackendKind.GgmlCpu)]), supplier);
        await owner.CheckAsync(Request());
        TensorRuntimeShutdownResult failure = await owner.ShutdownAsync();
        Assert.Equal(TensorRuntimeShutdownState.FailedRequiresProcessExit, failure.State);
        Assert.Equal(TensorRuntimeState.RequiresProcessRestart, owner.State);

        Assert.Same(failure, await owner.ShutdownAsync());
        Assert.Equal(TensorRuntimeState.RequiresProcessRestart, owner.State);
        Task disposal = owner.DisposeAsync().AsTask();
        InferenceException first = await Assert.ThrowsAsync<InferenceException>(() => disposal);
        Assert.Equal(TensorRuntimeState.RequiresProcessRestart, owner.State);
        Assert.Same(disposal, owner.DisposeAsync().AsTask());
        Assert.Same(first, await Assert.ThrowsAsync<InferenceException>(() => owner.DisposeAsync().AsTask()));
        Assert.Equal(TensorRuntimeState.RequiresProcessRestart, owner.State);
        Assert.Same(failure, await owner.ShutdownAsync());
        Assert.Equal(TensorRuntimeState.RequiresProcessRestart, owner.State);
        Assert.Equal(1, supplier.Shutdowns);
        Assert.Throws<ObjectDisposedException>(() => owner.EnterExecution());
    }

    [Fact]
    public async Task DefaultCatalogFreezesOnlyAtFirstAdmittedCheck()
    {
        var supplier = new Supplier { PackageCandidates = [Native(Candidate("cpu", TensorBackendKind.GgmlCpu))] };
        var owner = new QwenRuntimeOwner(new(Build, Rid), supplier);
        Assert.Equal(0, supplier.Catalogs);
        Assert.True((await owner.CheckAsync(Request())).IsAvailable);
        Assert.Equal(1, supplier.Catalogs);
        Assert.Null(owner.Plan.Candidates);
        Assert.True((await owner.CheckAsync(Request())).IsAvailable);
        Assert.Equal(1, supplier.Catalogs);
        await owner.DisposeAsync();
    }

    [Fact]
    public async Task InvalidEntryAndSupplierRefusalDoNotBecomeInstallationOrInitializationEvidence()
    {
        TensorRuntimeCandidate valid = Candidate("cpu", TensorBackendKind.GgmlCpu);
        var invalid = new TensorRuntimeCandidate(valid.Identity with { Id = "invalid", AbsoluteBridgePath = "relative/libGgmlOps.so" },
            valid.Backend, valid.Files);
        var supplier = new Supplier { RefuseValidation = true };
        var owner = new QwenRuntimeOwner(new(Build, Rid, [invalid, valid]), supplier);
        try
        {
            TensorBackendAvailability result = await owner.CheckAsync(Request());
            Assert.Equal(TensorBackendAvailabilityState.Unavailable, result.State);
            Assert.All(result.CandidatePresence, candidate => Assert.Null(candidate.IsInstalled));
            Assert.Equal(RefusedCandidateCodes, result.CandidateDiagnostics.Select(item => item.Code));
            Assert.DoesNotContain("private-error", string.Join(" ", result.CandidateDiagnostics.Select(item => item.Message)), StringComparison.Ordinal);
            Assert.Empty(supplier.Configured);
            Assert.Equal(0, supplier.Initializations);
        }
        finally { await owner.DisposeAsync(); }
    }

    [Fact]
    public async Task CancelledShutdownWaitDoesNotCancelSharedCleanupOrReopenAdmission()
    {
        var supplier = new Supplier { HoldShutdown = true };
        var owner = new QwenRuntimeOwner(new(Build, Rid, []), supplier);
        using var cancellation = new CancellationTokenSource();
        Task<TensorRuntimeShutdownResult> first = owner.ShutdownAsync(cancellation.Token);
        Task<TensorRuntimeShutdownResult>? second = null;
        Task? disposal = null;
        try
        {
            await supplier.ShutdownStarted.Task;
            second = owner.ShutdownAsync();
            disposal = owner.DisposeAsync().AsTask();
            Assert.Same(disposal, owner.DisposeAsync().AsTask());
            Assert.Throws<ObjectDisposedException>(() => owner.EnterExecution());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(second.IsCompleted);
            Assert.False(disposal.IsCompleted);
            supplier.ShutdownRelease.TrySetResult();
            Assert.Equal(TensorRuntimeShutdownState.Completed, (await second).State);
            await disposal;
            Assert.Equal(TensorRuntimeState.Stopped, owner.State);
            Assert.Equal(1, supplier.Shutdowns);
        }
        finally
        {
            disposal ??= owner.DisposeAsync().AsTask();
            supplier.ShutdownRelease.TrySetResult();
            try { await first; } catch (OperationCanceledException) { }
            if (second is not null) await second;
            await disposal;
        }
    }

    [Fact]
    public async Task CancelledAcquisitionRetainsExactFailedCleanupAndExplicitRetryProvesRelease()
    {
        using var cancellation = new CancellationTokenSource();
        var supplier = new Supplier { OnAcquire = cancellation.Cancel, CleanupFailuresRemaining = 2 };
        var owner = new QwenRuntimeOwner(new(Build, Rid, [Candidate("cpu", TensorBackendKind.GgmlCpu)]), supplier);
        try
        {
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
                owner.AcquireModelAsync(TensorBackendKind.GgmlCpu, cancellation.Token));
            Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerExceptions[0]);
            Assert.Same(supplier.CleanupFailure, failure.InnerExceptions[1]);
            Assert.Equal(0, supplier.ModelReleases);
            Assert.Equal(0, supplier.Shutdowns);
            Assert.Throws<InferenceException>(() => owner.EnterExecution());

            TensorRuntimeShutdownResult refused = await owner.ShutdownAsync();
            Assert.Equal(TensorRuntimeShutdownState.FailedRequiresProcessExit, refused.State);
            Assert.Equal(1, refused.OutstandingLeases);
            Assert.True(refused.NativeStateMayRemain);
            Assert.Equal(0, supplier.Shutdowns);
            Assert.Throws<ObjectDisposedException>(() => owner.EnterExecution());
            Assert.DoesNotContain("private-error", refused.Diagnostics[0].Message, StringComparison.Ordinal);

            TensorRuntimeShutdownResult released = await owner.ShutdownAsync();
            Assert.Equal(TensorRuntimeShutdownState.Completed, released.State);
            Assert.Equal(0, released.OutstandingLeases);
            Assert.Equal(1, supplier.ModelReleases);
            Assert.Equal(1, supplier.Shutdowns);
        }
        finally
        {
            supplier.CleanupFailuresRemaining = 0;
            await owner.ShutdownAsync();
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProvenReadyGuardRefusalKeepsTheFenceAndAllowsExplicitRetryAfterExternalDrain()
    {
        var supplier = new Supplier { ExternalResourcesActive = true };
        var owner = new QwenRuntimeOwner(new(Build, Rid, [Candidate("cpu", TensorBackendKind.GgmlCpu)]), supplier);
        Task? disposal = null;
        try
        {
            Assert.True((await owner.CheckAsync(Request())).IsAvailable);
            TensorRuntimeShutdownResult busy = await owner.ShutdownAsync();
            Assert.Equal(TensorRuntimeShutdownState.Busy, busy.State);
            Assert.Equal(0, busy.OutstandingLeases);
            Assert.True(busy.NativeStateMayRemain);
            Assert.NotNull(owner.Current);
            Assert.Equal(0, supplier.Teardowns);
            Assert.Throws<ObjectDisposedException>(() => owner.EnterExecution());
            disposal = owner.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<InferenceException>(() => disposal);
            Assert.Same(disposal, owner.DisposeAsync().AsTask());

            supplier.ExternalResourcesActive = false;
            Assert.Equal(TensorRuntimeShutdownState.Completed, (await owner.ShutdownAsync()).State);
            Assert.Equal(1, supplier.Teardowns);
            Assert.Null(owner.Current);
            Assert.Throws<ObjectDisposedException>(() => owner.EnterExecution());
        }
        finally
        {
            supplier.ExternalResourcesActive = false;
            await owner.ShutdownAsync();
            if (disposal is not null) { try { await disposal; } catch (InferenceException) { } }
            else { try { await owner.DisposeAsync(); } catch (InferenceException) { } }
        }
    }

    [Fact]
    public async Task ModelAndExecutionLeasesPreventTeardownAndDisposalFencesSynchronously()
    {
        var supplier = new Supplier();
        var owner = new QwenRuntimeOwner(new(Build, Rid, [Candidate("cpu", TensorBackendKind.GgmlCpu)]), supplier);
        QwenRuntimeOwner.ModelLease model = await owner.AcquireModelAsync(TensorBackendKind.GgmlCpu, default);
        IDisposable execution = owner.EnterExecution();
        try
        {
            TensorRuntimeShutdownResult busy = await owner.ShutdownAsync();
            Assert.Equal(TensorRuntimeShutdownState.Busy, busy.State);
            Assert.Equal(2, busy.OutstandingLeases);
            Assert.Equal(0, supplier.Shutdowns);
            Task disposal = owner.DisposeAsync().AsTask();
            Assert.Throws<ObjectDisposedException>(() => owner.EnterExecution());
            await Assert.ThrowsAsync<InferenceException>(() => disposal);
            Assert.Same(disposal, owner.DisposeAsync().AsTask());
            Assert.Equal(0, supplier.Shutdowns);
        }
        finally { execution.Dispose(); model.Dispose(); }
        Assert.Equal(TensorRuntimeShutdownState.Completed, (await owner.ShutdownAsync()).State);
        Assert.Equal(1, supplier.Shutdowns);
        Assert.Equal(1, supplier.ModelReleases);
        Assert.Equal(TensorRuntimeShutdownState.AlreadyStopped, (await owner.ShutdownAsync()).State);
    }

    [Fact]
    public async Task WaiterCancellationDoesNotCancelSharedInitializationAndRetirementRefusesLateResults()
    {
        var supplier = new Supplier { HoldInitialization = true };
        var owner = new QwenRuntimeOwner(new(Build, Rid, [Candidate("cpu", TensorBackendKind.GgmlCpu)]), supplier);
        using var cancelled = new CancellationTokenSource();
        Task<TensorBackendAvailability> first = owner.CheckAsync(Request(), cancelled.Token);
        Task<TensorBackendAvailability>? second = null;
        Task? disposal = null;
        try
        {
            await supplier.Started.Task;
            second = owner.CheckAsync(Request());
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(second.IsCompleted);
            disposal = owner.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<InferenceException>(() => disposal);
            Assert.Equal(0, supplier.Shutdowns);
            supplier.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Null(owner.Current);
        }
        finally
        {
            disposal ??= owner.DisposeAsync().AsTask();
            supplier.Release.TrySetResult();
            try { await first; } catch (OperationCanceledException) { }
            if (second is not null) { try { await second; } catch (OperationCanceledException) { } }
            try { await disposal; } catch (InferenceException) { }
            await owner.ShutdownAsync();
        }
        Assert.Equal(1, supplier.Initializations);
        Assert.Equal(1, supplier.Shutdowns);
    }

    [Theory]
    [InlineData("linux-x64", TensorBackendKind.GgmlCuda, TensorBackendKind.GgmlVulkan, TensorBackendKind.GgmlCpu)]
    [InlineData("linux-arm64", TensorBackendKind.GgmlCuda, TensorBackendKind.GgmlVulkan, TensorBackendKind.GgmlCpu)]
    [InlineData("win-x64", TensorBackendKind.GgmlCuda, TensorBackendKind.GgmlVulkan, TensorBackendKind.GgmlCpu)]
    public void NonMacAutomaticPolicyIsAnOrderedCompatibleSet(string rid, TensorBackendKind first, TensorBackendKind second, TensorBackendKind third)
        => Assert.Equal(new[] { first, second, third }, QwenRuntimeOwner.CompatibleBackends(TensorBackendKind.Auto, rid));

    [Fact]
    public void MacAndWindowsArmPolicyPreserveTheirActualAdmittedBackends()
    {
        Assert.Equal(new[] { TensorBackendKind.GgmlMetal, TensorBackendKind.GgmlCpu }, QwenRuntimeOwner.CompatibleBackends(TensorBackendKind.Auto, "osx-arm64"));
        Assert.Equal(new[] { TensorBackendKind.GgmlVulkan, TensorBackendKind.GgmlCpu }, QwenRuntimeOwner.CompatibleBackends(TensorBackendKind.Auto, "win-arm64"));
        Assert.Empty(QwenRuntimeOwner.CompatibleBackends(TensorBackendKind.GgmlCuda, "win-arm64"));
        Assert.Empty(QwenRuntimeOwner.CompatibleBackends(TensorBackendKind.Auto, "unknown"));
    }

    private static GgmlNativeCandidate Native(TensorRuntimeCandidate candidate) => new(
        Path.GetDirectoryName(candidate.Identity.AbsoluteBridgePath)!, candidate.Identity.Build.Version, candidate.Identity.TargetRid,
        candidate.Identity.Variant, QwenRuntimeOwner.ToSupplier(candidate.Backend),
        candidate.Files.Select(file => new GgmlNativeFile(file.RelativePath, file.Size, file.Sha256)).ToArray())
    { NativeAbi = Build.NativeAbi };

    internal sealed class Supplier : IQwenGgmlRuntime
    {
        public TensorSharpBuildIdentity Build => QwenRuntimeOwnerTests.Build;
        public string Rid => QwenRuntimeOwnerTests.Rid;
        public string EntryLibraryName => "libGgmlOps.so";
        public GgmlRuntimeState State { get; private set; }
        public int Effects => Catalogs + Configured.Count + Initializations + Shutdowns + ModelReleases;
        public int Catalogs { get; private set; }
        public int Initializations { get; private set; }
        public int Shutdowns { get; private set; }
        public int Teardowns { get; private set; }
        public int ModelReleases { get; private set; }
        public List<GgmlBackendType> Configured { get; } = [];
        public IReadOnlyList<GgmlNativeCandidate> PackageCandidates { get; init; } = [];
        public IReadOnlyList<GgmlBackendType> RefusedBackends { get; init; } = [];
        public bool Poison { get; init; }
        public bool HoldInitialization { get; init; }
        public bool HoldShutdown { get; init; }
        public bool RefuseValidation { get; init; }
        public bool ExternalResourcesActive { get; set; }
        public Action? OnAcquire { get; init; }
        public int CleanupFailuresRemaining { get; set; }
        public Exception CleanupFailure { get; } = new InvalidOperationException("private-error cleanup");
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ShutdownStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ShutdownRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GgmlRuntimePlan? _plan;
        public Task<IReadOnlyList<GgmlNativeCandidate>> ResolvePackageCandidatesAsync()
        { Catalogs++; return Task.FromResult(PackageCandidates); }
        public GgmlNativeRefusal? Check(GgmlNativeCandidate candidate) => RefuseValidation
            ? new(candidate, "private-error-code", "private-error-path-and-token") : null;
        public void Configure(GgmlRuntimePlan plan)
        { _plan = plan; Configured.Add(plan.RequestedBackend); State = GgmlRuntimeState.Configured; }
        public async Task<GgmlInitializationResult> InitializeAsync()
        {
            Initializations++;
            Started.TrySetResult();
            if (HoldInitialization) await Release.Task;
            GgmlRuntimePlan plan = _plan!;
            GgmlNativeCandidate candidate = plan.Candidates![0];
            if (Poison)
            {
                State = GgmlRuntimeState.Poisoned;
                return new(plan, GgmlInitializationState.RequiresProcessRestart,
                    new(GgmlNativeSelectionState.PartiallyInitialized, candidate, "/private-error/library", null, null,
                        [new(candidate, "load-failed", "private-error")], []), "private-error");
            }
            if (RefusedBackends.Contains(plan.RequestedBackend))
            {
                State = GgmlRuntimeState.Unavailable;
                return new(plan, GgmlInitializationState.Unavailable,
                    new(GgmlNativeSelectionState.Unavailable, null, null, null, null,
                        [new(candidate, "missing-prerequisite", "private-error")], []), "private-error");
            }
            State = GgmlRuntimeState.Ready;
            return new(plan, GgmlInitializationState.Ready,
                new(GgmlNativeSelectionState.Loaded, candidate, Path.Combine(candidate.Directory, EntryLibraryName), candidate.Backend,
                    new(Build.Version, new('d', 40), Build.GgmlCommit, candidate.Variant, Rid, "portable", "private-error") { NativeAbi = Build.NativeAbi }, [], []), null);
        }
        public IDisposable AcquireModelLease()
        {
            var lease = new ModelRelease(this);
            OnAcquire?.Invoke();
            return lease;
        }
        public GgmlNativeShutdownResult Shutdown()
        {
            Shutdowns++;
            ShutdownStarted.TrySetResult();
            if (HoldShutdown) ShutdownRelease.Task.GetAwaiter().GetResult();
            if (Poison) return new(false, "private-error");
            if (ExternalResourcesActive) return new(false, "private-error unrelated diagnostic");
            Teardowns++;
            State = GgmlRuntimeState.Stopped;
            return new(true, null);
        }
        private sealed class ModelRelease(Supplier supplier) : IDisposable
        {
            public void Dispose()
            {
                if (supplier.CleanupFailuresRemaining > 0)
                {
                    supplier.CleanupFailuresRemaining--;
                    throw supplier.CleanupFailure;
                }
                supplier.ModelReleases++;
            }
        }
    }
}
