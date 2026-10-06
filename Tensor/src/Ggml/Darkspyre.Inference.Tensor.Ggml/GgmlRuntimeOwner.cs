using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.GGML;

namespace Darkspyre.Inference.Tensor.Ggml;

/// <summary>
/// One generation-owned GGML selection and terminal lifetime. Every Tensor engine that runs on GGML borrows it: a model
/// holds a lease while it is loaded and enters the owner for each call, so one native runtime serves them all.
/// Direct MLX borrowers share the same admission fence and terminal cleanup without using a GGML native identity.
/// </summary>
public sealed class GgmlRuntimeOwner : ITensorRuntimeOwner
{
    private static readonly object GenerationGate = new();
    private static GgmlRuntimeOwner? _generation;
    private readonly object _gate = new();
    private IGgmlRuntime? _supplier;
    private readonly string _supplierRid;
    private IReadOnlyList<TensorRuntimeCandidate>? _candidates;
    private Task<IReadOnlyList<GgmlNativeCandidate>>? _catalogResolution;
    private Task<Initialization>? _initialization;
    // Publish the cause with state before the shared initialization task finishes.
    private Exception? _initializationError;
    private TensorBackendKind _initializationBackend;
    private Task<ShutdownOutcome>? _shutdown;
    private Task? _disposal;
    private TensorRuntimeState _state;
    private TensorRuntimeBackendIdentity? _current;
    private int _leases;
    private readonly List<ModelLease> _failedRollbackLeases = [];
    private bool _fenced;
    private bool _nativeMayRemain;
    private Action? _mlxShutdown;

    public static GgmlRuntimeOwner GetOrCreate(TensorRuntimePlan? plan = null)
    {
        lock (GenerationGate)
        {
            if (_generation is not null)
            {
                if (plan is not null && !SamePlan(_generation.Plan, plan))
                    throw new ArgumentException("The generation already owns a different GGML runtime plan.", nameof(plan));
                return _generation;
            }
            var supplier = new GgmlRuntime();
            return _generation = new(plan ?? new(supplier.Build, supplier.Rid), supplier);
        }
    }

    internal GgmlRuntimeOwner(TensorRuntimePlan plan, IGgmlRuntime supplier)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(supplier);
        Plan = new(plan.Build, plan.TargetRid, plan.Candidates);
        _supplier = supplier;
        _supplierRid = supplier.Rid;
        _candidates = Plan.Candidates;
    }

    public TensorRuntimePlan Plan { get; }
    public TensorRuntimeState State { get { lock (_gate) return _state; } }
    public TensorRuntimeBackendIdentity? Current { get { lock (_gate) return _current; } }

    internal static bool SamePlan(TensorRuntimePlan left, TensorRuntimePlan right) =>
        left.Build == right.Build && left.TargetRid == right.TargetRid
        && (left.Candidates is null ? right.Candidates is null : right.Candidates is not null
            && left.Candidates.Count == right.Candidates.Count
            && left.Candidates.Zip(right.Candidates).All(pair => pair.First.Identity == pair.Second.Identity
                && pair.First.Backend == pair.Second.Backend && pair.First.Files.SequenceEqual(pair.Second.Files)));

    internal static void ValidateGenerationPlan(TensorRuntimePlan plan)
    {
        lock (GenerationGate)
            if (_generation is not null && !SamePlan(_generation.Plan, plan))
                throw new ArgumentException("The generation already owns a different GGML runtime plan.", nameof(plan));
    }

    internal static bool IsGgml(TensorBackendKind backend) => backend is TensorBackendKind.Auto
        or TensorBackendKind.GgmlCpu or TensorBackendKind.GgmlCuda or TensorBackendKind.GgmlVulkan or TensorBackendKind.GgmlMetal;

    internal static IReadOnlyList<TensorBackendKind> CompatibleBackends(TensorBackendKind requested, string rid) => requested switch
    {
        TensorBackendKind.Auto when rid == "osx-arm64" => [TensorBackendKind.GgmlMetal, TensorBackendKind.GgmlCpu],
        TensorBackendKind.Auto when rid is "linux-x64" or "linux-arm64" or "win-x64" =>
            [TensorBackendKind.GgmlCuda, TensorBackendKind.GgmlVulkan, TensorBackendKind.GgmlCpu],
        TensorBackendKind.Auto when rid == "win-arm64" => [TensorBackendKind.GgmlVulkan, TensorBackendKind.GgmlCpu],
        TensorBackendKind.Auto => [],
        TensorBackendKind.GgmlMetal when rid != "osx-arm64" => [],
        TensorBackendKind.GgmlCuda when rid is not ("linux-x64" or "linux-arm64" or "win-x64") => [],
        TensorBackendKind.GgmlVulkan when rid is not ("linux-x64" or "linux-arm64" or "win-x64" or "win-arm64") => [],
        TensorBackendKind.GgmlCpu when rid is not ("osx-arm64" or "linux-x64" or "linux-arm64" or "win-x64" or "win-arm64") => [],
        TensorBackendKind.Mlx when rid != "osx-arm64" => [],
        TensorBackendKind.Cuda when rid.StartsWith("osx-", StringComparison.Ordinal) => [],
        TensorBackendKind.CoreMl => [],
        _ when Enum.IsDefined(requested) => [requested],
        _ => []
    };

    public async Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable operation = Enter();
        // Each engine's probe checks its own implementation before it delegates here.
        if (request.TargetRid != Plan.TargetRid
            || request.TargetRid != _supplierRid || !IsGgml(request.RequestedBackend)
            || CompatibleBackends(request.RequestedBackend, Plan.TargetRid).Count == 0)
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported,
                "The bound GGML runtime cannot execute this implementation, backend or target.");
        if (request.NativeCandidates is { Count: 0 })
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unavailable,
                "The probe request supplies no native candidates.");
        if (request.ExpectedTensorSharpBuild is not null && request.ExpectedTensorSharpBuild != Plan.Build
            || request.NativeCandidates is not null && (Plan.Candidates is null
                || !request.NativeCandidates.SequenceEqual(Plan.Candidates.Select(candidate => candidate.Identity))))
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unknown,
                "The probe request does not match the bound runtime plan.");
        Initialization result = await InitializeAsync(request.RequestedBackend, cancellationToken).ConfigureAwait(false);
        ThrowIfOperationCanceled(cancellationToken);
        return new(request.OperationId, request.ImplementationId, request.RequestedBackend, result.State, result.Reason,
            DateTimeOffset.UtcNow, Plan.TargetRid, result.Identity?.Backend, Plan.Build, result.Identity?.CandidateId,
            result.Identity is null ? null : true, candidatePresence: result.Presence, candidateDiagnostics: result.Diagnostics);
    }

    internal async Task<ModelLease> AcquireModelAsync(TensorBackendKind requested, CancellationToken cancellationToken)
    {
        IDisposable operation = Enter();
        IDisposable? native = null;
        try
        {
            Initialization result = await InitializeAsync(requested, cancellationToken).ConfigureAwait(false);
            ThrowIfOperationCanceled(cancellationToken);
            if (result.Identity is null)
            {
                if (result.Error is { } error)
                    throw new InferenceException(InferenceFailureCode.BackendUnavailable, result.Reason, error);
                throw new InferenceException(InferenceFailureCode.BackendUnavailable, result.Reason);
            }
            native = _supplier!.AcquireModelLease();
            ThrowIfOperationCanceled(cancellationToken);
            return new(result.Identity.Backend, operation, native);
        }
        catch (Exception original)
        {
            if (native is null) operation.Dispose();
            else
            {
                var rollback = new ModelLease(requested, operation, native);
                try { rollback.Dispose(); }
                catch (Exception cleanup)
                {
                    lock (_gate)
                    {
                        _failedRollbackLeases.Add(rollback);
                        _state = TensorRuntimeState.RequiresProcessRestart;
                        _nativeMayRemain = true;
                    }
                    throw new AggregateException(original, cleanup);
                }
            }
            throw;
        }
    }

    internal IDisposable EnterExecution() => Enter();

    internal IDisposable EnterMlx(Action shutdown)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        lock (_gate)
        {
            if (_mlxShutdown is not null && _mlxShutdown != shutdown)
                throw new InvalidOperationException("The generation already owns a different MLX terminal action.");
            IDisposable lease = Enter();
            _mlxShutdown = shutdown;
            _nativeMayRemain = true;
            return lease;
        }
    }

    internal ModelLease AcquireMlxModel(Action shutdown) => new(TensorBackendKind.Mlx, EnterMlx(shutdown), null);

    private OperationLease Enter()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_fenced, this);
            if (_state == TensorRuntimeState.RequiresProcessRestart)
            {
                if (_initializationError is { } error)
                    throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The GGML runtime requires a fresh process.", error);
                throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The GGML runtime requires a fresh process.");
            }
            _leases++;
            return new OperationLease(this);
        }
    }

    private void Release()
    {
        lock (_gate) _leases--;
    }

    internal void ThrowIfOperationCanceled(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
            if (_fenced) throw new OperationCanceledException("The GGML runtime owner is retiring.", token);
    }

    private async Task<Initialization> InitializeAsync(TensorBackendKind requested, CancellationToken token)
    {
        Task<Initialization> pending;
        lock (_gate)
        {
            if (_fenced) throw new OperationCanceledException("The GGML runtime owner is retiring.", token);
            if (_current is not null)
                return requested is TensorBackendKind.Auto || requested == _current.Backend
                    ? Initialization.Ready(_current) : Initialization.Refused("The initialized GGML backend conflicts with this request.");
            if (_initialization is null || _state == TensorRuntimeState.Unavailable && _initialization.IsCompleted
                && requested != _initializationBackend)
            {
                _leases++;
                _state = TensorRuntimeState.Initializing;
                _initializationBackend = requested;
                _initialization = Task.Run(() => InitializeCoreAsync(requested));
            }
            pending = _initialization;
        }
        Initialization result = await pending.WaitAsync(token).ConfigureAwait(false);
        if (result.Identity is not null && requested is not TensorBackendKind.Auto && requested != result.Identity.Backend)
            return Initialization.Refused("The initialized GGML backend conflicts with this request.");
        return result;
    }

    private async Task<Initialization> InitializeCoreAsync(TensorBackendKind requested)
    {
        Initialization result = Initialization.Refused("The GGML runtime could not be initialized.", retained: true);
        Exception? initializationError = null;
        try
        {
            result = await SelectCoreAsync(requested).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            initializationError = error;
            if (error is OutOfMemoryException) throw;
            bool retained = _supplier!.State is GgmlRuntimeState.Poisoned or GgmlRuntimeState.Ready or GgmlRuntimeState.Stopping or GgmlRuntimeState.Stopped;
            result = Initialization.Refused(retained ? "GGML initialization requires a fresh process." : "The GGML runtime could not be initialized.", retained, error);
        }
        finally
        {
            lock (_gate)
            {
                _leases--;
                _nativeMayRemain |= result.Identity is not null || result.State == TensorBackendAvailabilityState.RequiresProcessRestart;
                if (!_fenced)
                {
                    _initializationError = initializationError ?? result.Error;
                    _current = result.Identity;
                    _state = result.Identity is not null ? TensorRuntimeState.Ready
                        : result.State == TensorBackendAvailabilityState.RequiresProcessRestart ? TensorRuntimeState.RequiresProcessRestart
                        : TensorRuntimeState.Unavailable;
                }
            }
        }
        return result;
    }

    private async Task<Initialization> SelectCoreAsync(TensorBackendKind requested)
    {
        IGgmlRuntime supplier = _supplier!;
        if (Plan.Build != supplier.Build || Plan.TargetRid != supplier.Rid)
            return Initialization.Unsupported("The runtime plan differs from the executing supplier build or target.");
        IReadOnlyList<TensorBackendKind> backends = CompatibleBackends(requested, Plan.TargetRid);
        if (!IsGgml(requested) || backends.Count == 0)
            return Initialization.Unsupported("The requested GGML backend is unsupported on this target.");
        if (_candidates is null)
        {
            _catalogResolution ??= supplier.ResolvePackageCandidatesAsync();
            IReadOnlyList<GgmlNativeCandidate> resolved = await _catalogResolution.ConfigureAwait(false);
            _candidates = Array.AsReadOnly(resolved.Select((candidate, index) => new TensorRuntimeCandidate(
                new($"package:{candidate.Variant}:{candidate.Rid}:{candidate.Backend}:{index}",
                    Path.Combine(candidate.Directory, supplier.EntryLibraryName), candidate.Variant,
                    new(candidate.TensorSharpBuild, candidate.NativeAbi, Plan.Build.GgmlCommit), candidate.Rid),
                FromSupplier(candidate.Backend), candidate.Files.Select(file => new TensorRuntimeNativeFile(file.Path, file.Size, file.Sha256)).ToArray())).ToArray());
        }
        var diagnostics = new List<TensorBackendCandidateDiagnostic>();
        var presence = new List<TensorNativeCandidatePresence>();
        foreach (TensorBackendKind backend in backends)
        {
            var candidates = new List<(TensorRuntimeCandidate Original, GgmlNativeCandidate Native)>();
            foreach (TensorRuntimeCandidate original in _candidates.Where(candidate => candidate.Backend == backend))
            {
                // The supplier validates physical links, identity and complete bytes after this exact lexical binding.
                if (!Path.IsPathFullyQualified(original.Identity.AbsoluteBridgePath)
                    || Path.GetFileName(original.Identity.AbsoluteBridgePath) != supplier.EntryLibraryName)
                {
                    presence.Add(new(original.Identity.Id, null));
                    diagnostics.Add(new(original.Identity.Id, TensorBackendProbePhase.Validation,
                        "candidate-entry-invalid", "The candidate does not bind the supplier entry library."));
                    continue;
                }
                GgmlNativeCandidate native = ToSupplier(original);
                GgmlNativeRefusal? refusal = supplier.Check(native);
                presence.Add(new(original.Identity.Id, refusal is null ? true : null));
                if (refusal is not null)
                    diagnostics.Add(new(original.Identity.Id, TensorBackendProbePhase.Validation, SafeCode(refusal.Code), "The native candidate failed validation."));
                else candidates.Add((original, native));
            }
            if (candidates.Count == 0) continue;
            supplier.Configure(new(Plan.Build.Version, Plan.Build.NativeAbi, Plan.TargetRid,
                candidates.Select(candidate => candidate.Native).ToArray(), DefaultNuGetProbing: false, RequestedBackend: ToSupplier(backend)));
            lock (_gate) _nativeMayRemain = true;
            GgmlInitializationResult observed = await supplier.InitializeAsync().ConfigureAwait(false);
            foreach (GgmlNativeRefusal refusal in observed.Selection.Refusals)
            {
                TensorRuntimeCandidate? original = Match(candidates, refusal.Candidate);
                diagnostics.Add(new(original?.Identity.Id, TensorBackendProbePhase.Initialization, SafeCode(refusal.Code),
                    "The native candidate was refused during initialization.", observed.State == GgmlInitializationState.RequiresProcessRestart));
            }
            if (observed.State == GgmlInitializationState.Ready && observed.Selection.Identity is { } identity
                && observed.Selection.Backend is { } actual)
            {
                TensorRuntimeCandidate? selected = observed.Selection.Candidate is { } selectedNative ? Match(candidates, selectedNative) : null;
                var current = new TensorRuntimeBackendIdentity(FromSupplier(actual),
                    new(identity.TensorSharpBuild, identity.NativeAbi, identity.GgmlCommit), identity.Rid, identity.Variant,
                    selected?.Identity.Id, identity.SourceCommit);
                return new(TensorBackendAvailabilityState.Available, "The GGML backend initialized. Model allocation is not guaranteed.",
                    current, presence.ToArray(), diagnostics.ToArray());
            }
            // Never try another group after the supplier may have retained native state.
            if (observed.State == GgmlInitializationState.RequiresProcessRestart
                || observed.Selection.State == GgmlNativeSelectionState.PartiallyInitialized
                || observed.Selection.LibraryPath is not null || supplier.State == GgmlRuntimeState.Poisoned)
            {
                foreach (GgmlNativeCandidate untried in observed.Selection.Untried)
                    diagnostics.Add(new(Match(candidates, untried)?.Identity.Id, TensorBackendProbePhase.Initialization,
                        GgmlNativeRefusalCodes.NotSelected, "The candidate was not tried because native state may remain.", true));
                foreach (TensorRuntimeCandidate untried in _candidates.Where(candidate =>
                    backends.SkipWhile(value => value != backend).Skip(1).Contains(candidate.Backend)))
                    diagnostics.Add(new(untried.Identity.Id, TensorBackendProbePhase.Initialization,
                        GgmlNativeRefusalCodes.NotSelected, "The candidate was not tried because native state may remain.", true));
                return new(TensorBackendAvailabilityState.RequiresProcessRestart, "GGML initialization requires a fresh process.",
                    null, presence.ToArray(), diagnostics.ToArray());
            }
            lock (_gate) _nativeMayRemain = _mlxShutdown is not null;
            if (observed.State == GgmlInitializationState.Unsupported)
                return new(TensorBackendAvailabilityState.Unsupported, "The supplier does not support this GGML plan.",
                    null, presence.ToArray(), diagnostics.ToArray());
        }
        return new(TensorBackendAvailabilityState.Unavailable, "No compatible GGML candidate initialized.", null,
            presence.ToArray(), diagnostics.ToArray());
    }

    private static GgmlNativeCandidate ToSupplier(TensorRuntimeCandidate candidate) => new(
        Path.GetDirectoryName(candidate.Identity.AbsoluteBridgePath) ?? string.Empty, candidate.Identity.Build.Version,
        candidate.Identity.TargetRid, candidate.Identity.Variant, ToSupplier(candidate.Backend),
        candidate.Files.Select(file => new GgmlNativeFile(file.RelativePath, file.Size, file.Sha256)).ToArray())
    { NativeAbi = candidate.Identity.Build.NativeAbi };

    private static TensorRuntimeCandidate? Match(List<(TensorRuntimeCandidate Original, GgmlNativeCandidate Native)> candidates,
        GgmlNativeCandidate native) => candidates.FirstOrDefault(candidate => candidate.Native.Directory == native.Directory
            && candidate.Native.Backend == native.Backend && candidate.Native.Variant == native.Variant
            && candidate.Native.TensorSharpBuild == native.TensorSharpBuild && candidate.Native.NativeAbi == native.NativeAbi
            && candidate.Native.Rid == native.Rid && candidate.Native.Files.SequenceEqual(native.Files)).Original;

    private static string SafeCode(string code) => code is GgmlNativeRefusalCodes.MissingPrerequisite
        or GgmlNativeRefusalCodes.IncompatibleHardware or GgmlNativeRefusalCodes.LoadFailed or GgmlNativeRefusalCodes.HashMismatch
        or GgmlNativeRefusalCodes.NotSelected ? code : "native-refused";

    internal static GgmlBackendType ToSupplier(TensorBackendKind backend) => backend switch
    {
        TensorBackendKind.GgmlCpu => GgmlBackendType.Cpu,
        TensorBackendKind.GgmlMetal => GgmlBackendType.Metal,
        TensorBackendKind.GgmlCuda => GgmlBackendType.Cuda,
        TensorBackendKind.GgmlVulkan => GgmlBackendType.Vulkan,
        _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };
    private static TensorBackendKind FromSupplier(GgmlBackendType backend) => backend switch
    {
        GgmlBackendType.Cpu => TensorBackendKind.GgmlCpu,
        GgmlBackendType.Metal => TensorBackendKind.GgmlMetal,
        GgmlBackendType.Cuda => TensorBackendKind.GgmlCuda,
        GgmlBackendType.Vulkan => TensorBackendKind.GgmlVulkan,
        _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };

    public Task<TensorRuntimeShutdownResult> ShutdownAsync(CancellationToken cancellationToken = default) =>
        ReadShutdownResultAsync(ShutdownWithCauseAsync(cancellationToken));

    private static async Task<TensorRuntimeShutdownResult> ReadShutdownResultAsync(Task<ShutdownOutcome> pending) =>
        (await pending.ConfigureAwait(false)).Result;

    private Task<ShutdownOutcome> ShutdownWithCauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<ShutdownOutcome> pending;
        lock (_gate)
        {
            if (_state == TensorRuntimeState.Stopped)
                return Task.FromResult(new ShutdownOutcome(ShutdownResult(TensorRuntimeShutdownState.AlreadyStopped, 0, false)));
            if (_shutdown is { IsCompleted: true })
                return _shutdown.WaitAsync(cancellationToken);
            if (_leases != 0 && _leases != _failedRollbackLeases.Count)
                return Task.FromResult(new ShutdownOutcome(ShutdownResult(TensorRuntimeShutdownState.Busy, _leases, _nativeMayRemain)));
            _fenced = true;
            _state = TensorRuntimeState.Stopping;
            pending = _shutdown ??= Task.Run(ShutdownCore);
        }
        return pending.WaitAsync(cancellationToken);
    }

    private ShutdownOutcome ShutdownCore()
    {
        ModelLease[] rollbacks;
        lock (_gate) rollbacks = _failedRollbackLeases.ToArray();
        var rollbackErrors = new List<Exception>();
        foreach (ModelLease rollback in rollbacks)
        {
            try
            {
                rollback.Dispose();
                lock (_gate) _failedRollbackLeases.Remove(rollback);
            }
            catch (Exception error) { rollbackErrors.Add(error); }
        }
        lock (_gate)
        {
            if (_failedRollbackLeases.Count != 0)
            {
                Exception error = rollbackErrors.Count == 1 ? rollbackErrors[0] : new AggregateException(rollbackErrors);
                _state = TensorRuntimeState.RequiresProcessRestart;
                // Only this exact release failed; actual guarded native teardown has not begun.
                _shutdown = null;
                return new(ShutdownResult(TensorRuntimeShutdownState.FailedRequiresProcessExit, _leases, true), error);
            }
        }
        bool released;
        bool safeBusy = false;
        Exception? shutdownError = null;
        try
        {
            IGgmlRuntime supplier = _supplier!;
            bool readyBefore = _current is not null && supplier.State == GgmlRuntimeState.Ready;
            released = supplier.Shutdown().Released;
            // The real guard leaves Ready unchanged only when it refused teardown over live external resources.
            safeBusy = !released && readyBefore && supplier.State == GgmlRuntimeState.Ready;
        }
        catch (Exception error) { released = false; shutdownError = error; }
        if (!safeBusy && _mlxShutdown is { } mlxShutdown)
        {
            try { mlxShutdown(); }
            catch (Exception error)
            {
                released = false;
                shutdownError = shutdownError is null ? error : new AggregateException(shutdownError, error);
            }
        }
        lock (_gate)
        {
            if (safeBusy)
            {
                _state = TensorRuntimeState.Stopping;
                _shutdown = null;
                return new(new(TensorRuntimeShutdownState.Busy, Guid.NewGuid(), DateTimeOffset.UtcNow, 0, true,
                    [new("native-resources-active", "The guarded supplier refused teardown while its external native resources remain active.")]));
            }
            _state = released ? TensorRuntimeState.Stopped : TensorRuntimeState.RequiresProcessRestart;
            if (released) { _current = null; _nativeMayRemain = false; }
            // No admitted work remains. Drop drained supplier/catalog task roots, not borrowed dependencies.
            _supplier = null;
            _catalogResolution = null;
            _initialization = null;
            _initializationError = null;
            _mlxShutdown = null;
            _candidates = Plan.Candidates;
            return new(ShutdownResult(released ? TensorRuntimeShutdownState.Completed : TensorRuntimeShutdownState.FailedRequiresProcessExit,
                0, !released), shutdownError);
        }
    }

    private static TensorRuntimeShutdownResult ShutdownResult(TensorRuntimeShutdownState state, int leases, bool retained) =>
        new(state, Guid.NewGuid(), DateTimeOffset.UtcNow, leases, retained, state switch
        {
            TensorRuntimeShutdownState.Busy => [new("runtime-busy", "The runtime still owns admitted work.")],
            TensorRuntimeShutdownState.FailedRequiresProcessExit => [new("runtime-requires-process-exit", "The supplier could not prove safe runtime release.")],
            _ => []
        });

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _fenced = true;
            _state = _state is TensorRuntimeState.Stopped or TensorRuntimeState.RequiresProcessRestart
                ? _state : TensorRuntimeState.Stopping;
            return new(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        ShutdownOutcome outcome = await ShutdownWithCauseAsync().ConfigureAwait(false);
        if (outcome.Result.State is not (TensorRuntimeShutdownState.Completed or TensorRuntimeShutdownState.AlreadyStopped))
        {
            const string message = "The Tensor runtime did not retire safely. Drain instances and retry terminal shutdown explicitly.";
            throw outcome.Error is null
                ? new InferenceException(InferenceFailureCode.BackendUnavailable, message)
                : new InferenceException(InferenceFailureCode.BackendUnavailable, message, outcome.Error);
        }
    }

    private sealed record ShutdownOutcome(TensorRuntimeShutdownResult Result, Exception? Error = null);

    private sealed class OperationLease(GgmlRuntimeOwner owner) : IDisposable
    {
        private GgmlRuntimeOwner? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    internal sealed class ModelLease(TensorBackendKind backend, IDisposable operation, IDisposable? native) : IDisposable
    {
        public TensorBackendKind Backend { get; } = backend;
        private readonly object _gate = new();
        private IDisposable? _operation = operation;
        private IDisposable? _native = native;
        public void Dispose()
        {
            lock (_gate)
            {
                if (_operation is null) return;
                _native?.Dispose();
                _operation!.Dispose();
                _native = null;
                _operation = null;
            }
        }
    }

    private sealed record Initialization(TensorBackendAvailabilityState State, string Reason,
        TensorRuntimeBackendIdentity? Identity, IReadOnlyList<TensorNativeCandidatePresence> Presence,
        IReadOnlyList<TensorBackendCandidateDiagnostic> Diagnostics)
    {
        internal Exception? Error { get; init; }

        internal static Initialization Ready(TensorRuntimeBackendIdentity identity) => new(TensorBackendAvailabilityState.Available,
            "The GGML backend initialized. Model allocation is not guaranteed.", identity, [], []);
        internal static Initialization Refused(string reason, bool retained = false, Exception? error = null) => new(retained
            ? TensorBackendAvailabilityState.RequiresProcessRestart : TensorBackendAvailabilityState.Unavailable, reason, null, [], [])
        { Error = error };
        internal static Initialization Unsupported(string reason) => new(TensorBackendAvailabilityState.Unsupported, reason, null, [], []);
    }
}

internal interface IGgmlRuntime
{
    TensorSharpBuildIdentity Build { get; }
    string Rid { get; }
    string EntryLibraryName { get; }
    GgmlRuntimeState State { get; }
    Task<IReadOnlyList<GgmlNativeCandidate>> ResolvePackageCandidatesAsync();
    GgmlNativeRefusal? Check(GgmlNativeCandidate candidate);
    void Configure(GgmlRuntimePlan plan);
    Task<GgmlInitializationResult> InitializeAsync();
    IDisposable AcquireModelLease();
    GgmlNativeShutdownResult Shutdown();
}

internal sealed class GgmlRuntime : IGgmlRuntime
{
    public TensorSharpBuildIdentity Build => new(GgmlNativeLoader.TensorSharpBuild, GgmlNativeLoader.NativeAbi, GgmlNativeLoader.GgmlCommit);
    public string Rid => GgmlNativeLoader.RuntimeIdentifier;
    public string EntryLibraryName => GgmlNativeLoader.EntryLibraryName;
    public GgmlRuntimeState State => GgmlNativeLoader.State;
    public Task<IReadOnlyList<GgmlNativeCandidate>> ResolvePackageCandidatesAsync() => GgmlNativeLoader.ResolvePackageCandidatesAsync();
    public GgmlNativeRefusal? Check(GgmlNativeCandidate candidate) => GgmlNativeLoader.Check(candidate);
    public void Configure(GgmlRuntimePlan plan) => GgmlNativeLoader.Configure(plan);
    public Task<GgmlInitializationResult> InitializeAsync() => GgmlNativeLoader.InitializeAsync();
    public IDisposable AcquireModelLease() => GgmlNativeLoader.AcquireLease(GgmlRuntimeResourceKind.Model);
    public GgmlNativeShutdownResult Shutdown() => GgmlNativeLoader.Shutdown();
}
