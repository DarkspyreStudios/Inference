using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.GGML;
using TensorSharp.MLX;

namespace Darkspyre.Inference.Tensor.Ggml;

/// <summary>
/// The process's one native runtime for TensorSharp's GGML and MLX backends. It verifies a candidate's files against
/// the catalog, loads its files by absolute path and binds TensorSharp's GGML library name to the loaded entry, then
/// lets TensorSharp initialize the backend. A process loads at most one candidate.
/// </summary>
/// <gotcha>Native state stays until the process exits. After a library loaded, a failed start leaves the owner
/// Unavailable, and every other candidate is reported not-selected so the caller can try it in a fresh process.</gotcha>
public sealed class GgmlRuntimeOwner : ITensorRuntimeOwner
{
    private static readonly object GenerationGate = new();
    private static GgmlRuntimeOwner? _generation;
    private readonly object _gate = new();
    private readonly INativeLibraries _native;
    private Task<Selection>? _selection;
    private TensorBackendKind _selectionBackend;
    private bool _loaded;
    private TensorRuntimeState _state;
    private TensorRuntimeBackendIdentity? _current;
    private bool _fenced;

    /// <summary>The process's owner, created on first use; throws when <paramref name="plan"/> differs from the plan
    /// the process already owns. Without a plan the owner offers no native candidate.</summary>
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
            return _generation = new(plan ?? new(new(TensorSharpRelease.Version), RuntimeInformation.RuntimeIdentifier),
                NativeLibraries.Instance);
        }
    }

    internal GgmlRuntimeOwner(TensorRuntimePlan plan, INativeLibraries native)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(native);
        Plan = new(plan.Build, plan.TargetRid, plan.Candidates);
        _native = native;
    }

    /// <inheritdoc />
    public TensorRuntimePlan Plan { get; }
    /// <inheritdoc />
    public TensorRuntimeState State { get { lock (_gate) return _state; } }
    /// <inheritdoc />
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

    /// <summary>True for Auto, the four GGML backends and MLX.</summary>
    internal static bool IsNative(TensorBackendKind backend) => backend is TensorBackendKind.Auto
        or TensorBackendKind.GgmlCpu or TensorBackendKind.GgmlCuda or TensorBackendKind.GgmlVulkan or TensorBackendKind.GgmlMetal
        or TensorBackendKind.Mlx;

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

    /// <summary>Answers a probe by running the same selection as a load.</summary>
    public async Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        // Each engine's probe checks its own implementation before it delegates here.
        if (request.TargetRid != Plan.TargetRid || !IsNative(request.RequestedBackend))
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported,
                "The bound native runtime cannot execute this implementation, backend or target.");
        if (request.ExpectedTensorSharpBuild is not null && request.ExpectedTensorSharpBuild != Plan.Build
            || request.NativeCandidates is not null && !request.NativeCandidates.SequenceEqual(
                (Plan.Candidates ?? []).Select(candidate => candidate.Identity)))
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unknown,
                "The probe request does not match the bound runtime plan.");
        Selection result = await SelectAsync(request.RequestedBackend, cancellationToken).ConfigureAwait(false);
        return new(request.OperationId, request.ImplementationId, request.RequestedBackend, result.State, result.Reason,
            DateTimeOffset.UtcNow, Plan.TargetRid, result.Backend, Plan.Build, result.Identity?.CandidateId,
            result.Backend is null ? null : true, candidatePresence: result.Presence, candidateDiagnostics: result.Diagnostics);
    }

    /// <summary>The concrete backend a model loads on for <paramref name="requested"/>: managed CPU when nothing native
    /// is needed or, for Auto, when the plan offers no candidate; otherwise the backend of the candidate that
    /// initialized. Throws <see cref="InferenceException"/> with <see cref="InferenceFailureCode.BackendUnavailable"/>
    /// and the selection's reason when no candidate initialized.</summary>
    internal async Task<TensorBackendKind> AcquireBackendAsync(TensorBackendKind requested, CancellationToken cancellationToken)
    {
        if (!IsNative(requested)) return requested;
        Selection result = await SelectAsync(requested, cancellationToken).ConfigureAwait(false);
        if (result.Backend is { } backend) return backend;
        throw result.Error is { } error
            ? new InferenceException(InferenceFailureCode.BackendUnavailable, result.Reason, error)
            : new InferenceException(InferenceFailureCode.BackendUnavailable, result.Reason);
    }

    private async Task<Selection> SelectAsync(TensorBackendKind requested, CancellationToken cancellationToken)
    {
        Task<Selection> pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_fenced, this);
            if (_current is not null)
                return requested is TensorBackendKind.Auto || requested == _current.Backend
                    ? Selection.Ready(_current, [], []) : Selection.Conflict();
            // A selection that loaded nothing may run again for another backend; one that loaded a library may not.
            if (_selection is null || _selection.IsCompleted && !_loaded && requested != _selectionBackend)
            {
                _selectionBackend = requested;
                _state = TensorRuntimeState.Initializing;
                _selection = Task.Run(() => RunSelectionAsync(requested));
            }
            pending = _selection;
        }
        Selection result = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result.Identity is not null && requested is not TensorBackendKind.Auto && requested != result.Identity.Backend)
            return Selection.Conflict();
        return result;
    }

    private async Task<Selection> RunSelectionAsync(TensorBackendKind requested)
    {
        Selection result;
        try { result = await SelectCoreAsync(requested).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            result = Selection.Failed(TensorBackendAvailabilityState.Unavailable,
                "The native runtime could not be selected.", [], []) with { Error = error };
        }
        lock (_gate)
        {
            _current = result.Identity;
            _state = result.Identity is not null ? TensorRuntimeState.Ready
                : result.Backend is not null ? TensorRuntimeState.Uninitialized
                : TensorRuntimeState.Unavailable;
        }
        return result;
    }

    private async Task<Selection> SelectCoreAsync(TensorBackendKind requested)
    {
        IReadOnlyList<TensorBackendKind> backends = CompatibleBackends(requested, Plan.TargetRid);
        TensorRuntimeCandidate[] ordered = [.. backends.SelectMany(backend =>
            (Plan.Candidates ?? []).Where(candidate => candidate.Backend == backend))];
        if (ordered.Length == 0)
            return requested == TensorBackendKind.Auto ? Selection.ManagedCpu()
                : Selection.Failed(TensorBackendAvailabilityState.Unavailable, $"No installed driver offers {requested}.", [], []);

        var presence = new List<TensorNativeCandidatePresence>();
        var diagnostics = new List<TensorBackendCandidateDiagnostic>();
        TensorRuntimeCandidate? chosen = null;
        foreach (TensorRuntimeCandidate candidate in ordered)
        {
            if (chosen is not null)
            {
                diagnostics.Add(new(candidate.Identity.Id, TensorBackendProbePhase.Initialization, "not-selected",
                    "The candidate was not tried because a native library is already loaded in this process.", NativeStateMayRemain: true));
                continue;
            }
            TensorBackendCandidateDiagnostic? refusal = await VerifyAsync(candidate).ConfigureAwait(false);
            presence.Add(new(candidate.Identity.Id, refusal is null ? true : null));
            if (refusal is null) chosen = candidate;
            else diagnostics.Add(refusal);
        }
        if (chosen is null)
            return Selection.Failed(TensorBackendAvailabilityState.Unavailable,
                $"No installed driver for {requested} passed its catalog check.", presence, diagnostics);

        lock (_gate) _loaded = true;
        string directory = Path.GetDirectoryName(chosen.Identity.AbsoluteBridgePath)!;
        IntPtr entry;
        string loading = chosen.Identity.AbsoluteBridgePath;
        try
        {
            foreach (string preload in chosen.Preload)
            {
                loading = Path.Combine(directory, preload);
                _native.Load(loading);
            }
            loading = chosen.Identity.AbsoluteBridgePath;
            entry = _native.Load(loading);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            string message = $"`{Path.GetRelativePath(directory, loading)}` could not be loaded: {error.Message}";
            diagnostics.Insert(0, new(chosen.Identity.Id, TensorBackendProbePhase.Load, "load-failed", message, NativeStateMayRemain: true));
            return Selection.Failed(TensorBackendAvailabilityState.RequiresProcessRestart, message, presence, diagnostics) with { Error = error };
        }

        try
        {
            if (chosen.Backend == TensorBackendKind.Mlx) _native.StartMlx(chosen.Identity.AbsoluteBridgePath);
            else _native.StartGgml(chosen.Backend, entry);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            string message = $"TensorSharp could not start the {chosen.Backend} backend from the {chosen.Identity.Variant} driver ({error.GetType().Name}).";
            diagnostics.Insert(0, new(chosen.Identity.Id, TensorBackendProbePhase.Initialization, "incompatible-hardware", message,
                NativeStateMayRemain: true));
            return Selection.Failed(TensorBackendAvailabilityState.RequiresProcessRestart, message, presence, diagnostics) with { Error = error };
        }

        return Selection.Ready(new(chosen.Backend, Plan.Build, Plan.TargetRid, chosen.Identity.Variant, chosen.Identity.Id),
            presence, diagnostics);
    }

    // The first file of the candidate that is absent or differs from the catalog, in catalog order, or null.
    private static async Task<TensorBackendCandidateDiagnostic?> VerifyAsync(TensorRuntimeCandidate candidate)
    {
        string directory = Path.GetDirectoryName(candidate.Identity.AbsoluteBridgePath)!;
        string variant = candidate.Identity.Variant;
        foreach (TensorRuntimeNativeFile file in candidate.Files)
        {
            var info = new FileInfo(Path.Combine(directory, file.RelativePath));
            if (!info.Exists)
                return Refusal(candidate, "file-missing", $"`{file.RelativePath}` is missing from the {variant} driver.");
            if (info.Length != file.Size)
                return Refusal(candidate, "size-mismatch", $"`{file.RelativePath}` has {info.Length} bytes; the driver catalog lists {file.Size}.");
            byte[] hash;
            await using (var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
                hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
            if (Convert.ToHexStringLower(hash) != file.Sha256)
                return Refusal(candidate, "hash-mismatch", $"`{file.RelativePath}` does not match the driver catalog's SHA-256.");
        }
        return null;
    }

    private static TensorBackendCandidateDiagnostic Refusal(TensorRuntimeCandidate candidate, string code, string message) =>
        new(candidate.Identity.Id, TensorBackendProbePhase.Validation, code, message);

    /// <summary>Fences new selections. Native state stays until the process exits.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate) _fenced = true;
        return ValueTask.CompletedTask;
    }

    private sealed record Selection(TensorBackendAvailabilityState State, string Reason, TensorBackendKind? Backend,
        TensorRuntimeBackendIdentity? Identity, IReadOnlyList<TensorNativeCandidatePresence> Presence,
        IReadOnlyList<TensorBackendCandidateDiagnostic> Diagnostics)
    {
        internal Exception? Error { get; init; }

        internal static Selection Ready(TensorRuntimeBackendIdentity identity, IReadOnlyList<TensorNativeCandidatePresence> presence,
            IReadOnlyList<TensorBackendCandidateDiagnostic> diagnostics) => new(TensorBackendAvailabilityState.Available,
            $"The {identity.Backend} backend initialized. Model allocation is not guaranteed.", identity.Backend, identity, presence, diagnostics);
        internal static Selection ManagedCpu() => new(TensorBackendAvailabilityState.Available,
            "No installed driver offers an accelerated backend; models run on TensorSharp's managed CPU backend.",
            TensorBackendKind.Cpu, null, [], []);
        internal static Selection Conflict() => new(TensorBackendAvailabilityState.Unavailable,
            "The initialized backend conflicts with this request.", null, null, [], []);
        internal static Selection Failed(TensorBackendAvailabilityState state, string reason,
            IReadOnlyList<TensorNativeCandidatePresence> presence, IReadOnlyList<TensorBackendCandidateDiagnostic> diagnostics) =>
            new(state, reason, null, null, presence, diagnostics);
    }
}

/// <summary>The native calls the owner makes once a candidate is verified.</summary>
internal interface INativeLibraries
{
    /// <summary>Loads the library at an absolute path and returns its handle.</summary>
    IntPtr Load(string path);
    /// <summary>Binds TensorSharp's GGML library name to <paramref name="entry"/> and initializes the backend.</summary>
    void StartGgml(TensorBackendKind backend, IntPtr entry);
    /// <summary>Points TensorSharp's MLX loader at <paramref name="entryPath"/> and checks that MLX starts.</summary>
    void StartMlx(string entryPath);
}

internal sealed class NativeLibraries : INativeLibraries
{
    // The library name TensorSharp's GGML assembly imports (TSU:TensorSharp.Backends.GGML/GgmlNative.cs:1446).
    private const string GgmlLibraryName = "GgmlOps";

    internal static NativeLibraries Instance { get; } = new();

    public IntPtr Load(string path) => NativeLibrary.Load(path);

    public void StartGgml(TensorBackendKind backend, IntPtr entry)
    {
        // Upstream claims the assembly's one DllImport resolver; this handler answers only after that resolver found nothing.
        Assembly ggml = typeof(GgmlBackendType).Assembly;
        AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(ggml) ?? AssemblyLoadContext.Default;
        context.ResolvingUnmanagedDll += (assembly, name) =>
            assembly == ggml && name == GgmlLibraryName ? entry : IntPtr.Zero;
        GgmlBasicOps.EnsureBackendAvailable(backend switch
        {
            TensorBackendKind.GgmlCpu => GgmlBackendType.Cpu,
            TensorBackendKind.GgmlMetal => GgmlBackendType.Metal,
            TensorBackendKind.GgmlCuda => GgmlBackendType.Cuda,
            TensorBackendKind.GgmlVulkan => GgmlBackendType.Vulkan,
            _ => throw new ArgumentOutOfRangeException(nameof(backend))
        });
    }

    public void StartMlx(string entryPath)
    {
        Environment.SetEnvironmentVariable("TENSORSHARP_MLX_LIBRARY", entryPath);
        if (!MlxBackend.IsAvailable())
            throw new InvalidOperationException("TensorSharp's MLX backend reported that it is not available.");
    }
}
