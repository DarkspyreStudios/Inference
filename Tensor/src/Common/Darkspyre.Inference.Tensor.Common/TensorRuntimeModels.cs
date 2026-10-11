using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>One portable integrity declaration. Construction does not inspect files or links.</summary>
public sealed record TensorRuntimeNativeFile(string RelativePath, long Size, string Sha256)
{
    /// <summary>The file's portable path relative to the candidate directory.</summary>
    [JsonRequired]
    public string RelativePath { get; init => field = TensorRuntimeValidation.RelativePath(value); }
        = TensorRuntimeValidation.RelativePath(RelativePath);
    /// <summary>The file's exact length in bytes.</summary>
    [JsonRequired]
    public long Size { get; init => field = TensorRuntimeValidation.Size(value); } = TensorRuntimeValidation.Size(Size);
    /// <summary>The file's lowercase hexadecimal SHA-256 hash.</summary>
    [JsonRequired]
    public string Sha256 { get; init => field = TensorRuntimeValidation.Hash(value); } = TensorRuntimeValidation.Hash(Sha256);
}

/// <summary>An immutable GGML candidate declaration, not evidence of installation or successful initialization.</summary>
public sealed class TensorRuntimeCandidate
{
    /// <summary>Creates a validated candidate; throws when the identity is incomplete, the backend is not GGML, or the file list is empty or repeats a path.</summary>
    public TensorRuntimeCandidate(TensorNativeCandidate identity, TensorBackendKind backend,
        IReadOnlyList<TensorRuntimeNativeFile> files)
    {
        ArgumentNullException.ThrowIfNull(identity);
        TensorRuntimeValidation.Backend(backend);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.AbsoluteBridgePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Variant);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.TargetRid);
        TensorSharpBuildIdentity build = TensorRuntimeValidation.Build(identity.Build);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            throw new ArgumentException("A native candidate requires its complete file closure.", nameof(files));
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var snapshot = new TensorRuntimeNativeFile[files.Count];
        for (int index = 0; index < files.Count; index++)
        {
            TensorRuntimeNativeFile file = files[index]
                ?? throw new ArgumentException("A native file list cannot contain null entries.", nameof(files));
            snapshot[index] = new(file.RelativePath, file.Size, file.Sha256);
            if (!paths.Add(file.RelativePath))
                throw new ArgumentException("A native file list cannot contain duplicate paths.", nameof(files));
        }
        Identity = new(identity.Id, identity.AbsoluteBridgePath, identity.Variant, build, identity.TargetRid);
        Backend = backend;
        Files = Array.AsReadOnly(snapshot);
    }

    // Required generated metadata needs a setter; constructor binding preserves the validated snapshots.
    /// <summary>The candidate's id, bridge library path, variant, build and target RID.</summary>
    [JsonInclude, JsonRequired]
    public TensorNativeCandidate Identity { get; internal init; }
    /// <summary>The concrete GGML backend the candidate provides.</summary>
    [JsonInclude, JsonRequired]
    public TensorBackendKind Backend { get; internal init; }
    /// <summary>The complete set of native files the candidate needs, with their sizes and hashes.</summary>
    [JsonInclude, JsonRequired]
    public IReadOnlyList<TensorRuntimeNativeFile> Files { get; internal init; }
}

/// <summary>Null candidates select the standard package policy; an empty list admits no candidates.</summary>
public sealed class TensorRuntimePlan
{
    /// <summary>Creates a plan and snapshots the candidates; throws when a candidate's build or RID differs from the plan or an identity repeats.</summary>
    public TensorRuntimePlan(TensorSharpBuildIdentity build, string targetRid,
        IReadOnlyList<TensorRuntimeCandidate>? candidates = null)
    {
        Build = TensorRuntimeValidation.Build(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRid);
        TargetRid = targetRid;
        if (candidates is null) return;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var snapshot = new TensorRuntimeCandidate[candidates.Count];
        for (int index = 0; index < candidates.Count; index++)
        {
            TensorRuntimeCandidate candidate = candidates[index]
                ?? throw new ArgumentException("A candidate list cannot contain null entries.", nameof(candidates));
            if (candidate.Identity.Build != Build || candidate.Identity.TargetRid != targetRid)
                throw new ArgumentException("Every candidate must match the plan's exact build and RID.", nameof(candidates));
            if (!ids.Add(candidate.Identity.Id))
                throw new ArgumentException("A runtime plan cannot contain duplicate candidate identities.", nameof(candidates));
            snapshot[index] = new(candidate.Identity, candidate.Backend, candidate.Files);
        }
        Candidates = Array.AsReadOnly(snapshot);
    }

    // Internal init permits required generated metadata without exposing a public setter.
    /// <summary>The exact TensorSharp build every candidate must match.</summary>
    [JsonInclude, JsonRequired]
    public TensorSharpBuildIdentity Build { get; internal init; }
    /// <summary>The runtime identifier every candidate must target.</summary>
    [JsonInclude, JsonRequired]
    public string TargetRid { get; internal init; }
    /// <summary>The admitted native candidates, or null to use the standard package candidates.</summary>
    public IReadOnlyList<TensorRuntimeCandidate>? Candidates { get; }
}

/// <summary>The identity of an actually initialized GGML backend, supplied by the executing runtime.</summary>
public sealed record TensorRuntimeBackendIdentity(TensorBackendKind Backend, TensorSharpBuildIdentity Build,
    string TargetRid, string Variant, string? CandidateId, string NativeSourceCommit)
{
    /// <summary>The concrete GGML backend that initialized.</summary>
    [JsonRequired]
    public TensorBackendKind Backend { get; init => field = TensorRuntimeValidation.Backend(value); }
        = TensorRuntimeValidation.Backend(Backend);
    /// <summary>The TensorSharp build identity the loaded native library reports.</summary>
    [JsonRequired]
    public TensorSharpBuildIdentity Build { get; init => field = TensorRuntimeValidation.Build(value); }
        = TensorRuntimeValidation.Build(Build);
    /// <summary>The runtime identifier the loaded native library targets.</summary>
    [JsonRequired]
    public string TargetRid { get; init => field = TensorRuntimeValidation.Text(value, nameof(TargetRid)); }
        = TensorRuntimeValidation.Text(TargetRid, nameof(TargetRid));
    /// <summary>The native build variant that loaded.</summary>
    [JsonRequired]
    public string Variant { get; init => field = TensorRuntimeValidation.Text(value, nameof(Variant)); }
        = TensorRuntimeValidation.Text(Variant, nameof(Variant));
    /// <summary>The plan candidate that was selected, or null when the selection matches no plan candidate.</summary>
    [JsonRequired]
    public string? CandidateId { get; init => field = TensorRuntimeValidation.CandidateId(value); }
        = TensorRuntimeValidation.CandidateId(CandidateId);
    /// <summary>The source commit the loaded native library was built from.</summary>
    [JsonRequired]
    public string NativeSourceCommit { get; init => field = TensorRuntimeValidation.Text(value, nameof(NativeSourceCommit)); }
        = TensorRuntimeValidation.Text(NativeSourceCommit, nameof(NativeSourceCommit));
}

/// <summary>The lifecycle state of a generation-owned GGML runtime.</summary>
public enum TensorRuntimeState
{
    /// <summary>No backend initialization has started.</summary>
    Uninitialized = 0,
    /// <summary>A backend initialization is in progress.</summary>
    Initializing = 1,
    /// <summary>A backend is initialized and admits work.</summary>
    Ready = 2,
    /// <summary>The last initialization found no usable backend and left no native state.</summary>
    Unavailable = 3,
    /// <summary>Native state may remain after a failure, so only a fresh process can use the runtime.</summary>
    RequiresProcessRestart = 4,
    /// <summary>The runtime is fenced and terminal cleanup is in progress or was refused.</summary>
    Stopping = 5,
    /// <summary>Terminal cleanup released the runtime.</summary>
    Stopped = 6
}

/// <summary>How a runtime shutdown attempt ended.</summary>
public enum TensorRuntimeShutdownState
{
    /// <summary>The runtime was released.</summary>
    Completed = 0,
    /// <summary>The runtime had already stopped.</summary>
    AlreadyStopped = 1,
    /// <summary>Admitted work or active native resources prevented shutdown.</summary>
    Busy = 2,
    /// <summary>Release could not be proven, so the process must exit to free native state.</summary>
    FailedRequiresProcessExit = 3
}

/// <summary>A safe runtime observation. Producers sanitize messages before publishing diagnostics.</summary>
public sealed record TensorRuntimeDiagnostic(string Code, string Message)
{
    /// <summary>A stable machine-readable code for the observation.</summary>
    [JsonRequired]
    public string Code { get; init => field = TensorRuntimeValidation.Text(value, nameof(Code)); }
        = TensorRuntimeValidation.Text(Code, nameof(Code));
    /// <summary>A sanitized human-readable description of the observation.</summary>
    [JsonRequired]
    public string Message { get; init => field = TensorRuntimeValidation.Text(value, nameof(Message)); }
        = TensorRuntimeValidation.Text(Message, nameof(Message));
}

/// <summary>An immutable shutdown observation; completion does not assert OS library unloading.</summary>
public sealed record TensorRuntimeShutdownResult(TensorRuntimeShutdownState State, Guid OperationId,
    DateTimeOffset ObservedAt, int OutstandingLeases, bool NativeStateMayRemain,
    IReadOnlyList<TensorRuntimeDiagnostic> Diagnostics)
{
    /// <summary>How the shutdown attempt ended.</summary>
    [JsonRequired]
    public TensorRuntimeShutdownState State { get; init => field = TensorRuntimeValidation.ShutdownState(value); }
        = TensorRuntimeValidation.ShutdownState(State);
    /// <summary>A nonempty identity for this shutdown observation.</summary>
    [JsonRequired]
    public Guid OperationId { get; init => field = TensorRuntimeValidation.OperationId(value); }
        = TensorRuntimeValidation.OperationId(OperationId);
    /// <summary>When the outcome was observed.</summary>
    [JsonRequired]
    public DateTimeOffset ObservedAt { get; init; } = ObservedAt;
    /// <summary>The number of admitted operations and model leases still held when the outcome was observed.</summary>
    [JsonRequired]
    public int OutstandingLeases { get; init => field = TensorRuntimeValidation.OutstandingLeases(value); }
        = TensorRuntimeValidation.OutstandingLeases(OutstandingLeases);
    /// <summary>True when native libraries or state may still be held by the process.</summary>
    [JsonRequired]
    public bool NativeStateMayRemain { get; init; } = NativeStateMayRemain;
    /// <summary>Sanitized observations that explain the outcome.</summary>
    [JsonRequired]
    public IReadOnlyList<TensorRuntimeDiagnostic> Diagnostics { get; init => field = TensorRuntimeValidation.Diagnostics(value); }
        = TensorRuntimeValidation.Diagnostics(Diagnostics);
}

/// <summary>Neutral control and observations for one generation-owned GGML runtime.</summary>
public interface ITensorRuntimeOwner : ITensorBackendProbe, IAsyncDisposable
{
    /// <summary>The build, target RID and native candidates this runtime is bound to.</summary>
    TensorRuntimePlan Plan { get; }
    /// <summary>The runtime's current lifecycle state.</summary>
    TensorRuntimeState State { get; }
    /// <summary>The identity of the initialized backend, or null when no backend is initialized.</summary>
    TensorRuntimeBackendIdentity? Current { get; }

    /// <summary>Cancels the caller's wait, not shared terminal cleanup. Busy does not assert native release.</summary>
    Task<TensorRuntimeShutdownResult> ShutdownAsync(CancellationToken cancellationToken = default);
}

internal static class TensorRuntimeValidation
{
    internal static TensorSharpBuildIdentity Build(TensorSharpBuildIdentity build)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(build.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(build.NativeAbi);
        ArgumentException.ThrowIfNullOrWhiteSpace(build.GgmlCommit);
        return new(build.Version, build.NativeAbi, build.GgmlCommit);
    }

    internal static TensorBackendKind Backend(TensorBackendKind backend)
    {
        if (backend is not (TensorBackendKind.GgmlCpu or TensorBackendKind.GgmlMetal
            or TensorBackendKind.GgmlCuda or TensorBackendKind.GgmlVulkan))
            throw new ArgumentOutOfRangeException(nameof(backend), "A runtime candidate requires a concrete GGML backend.");
        return backend;
    }

    internal static string RelativePath(string path)
    {
        TensorModelDefinitionValidator.ValidateRelativePath(path);
        if (path.Any(char.IsControl) || path.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) >= 0
            || path.Split('/').Any(static segment => segment.EndsWith('.') || segment.EndsWith(' ')))
            throw new ArgumentException("A native file requires a portable relative path.", nameof(path));
        return path;
    }

    internal static long Size(long size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return size;
    }

    internal static string Hash(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64 || sha256.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A native file requires an exact lowercase SHA-256 hash.", nameof(sha256));
        return sha256;
    }

    internal static string Text(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }

    internal static string? CandidateId(string? value) => value is null ? null : Text(value, nameof(CandidateId));

    internal static TensorRuntimeShutdownState ShutdownState(TensorRuntimeShutdownState state) => Enum.IsDefined(state)
        ? state : throw new ArgumentOutOfRangeException(nameof(state));

    internal static Guid OperationId(Guid operationId) => operationId != Guid.Empty ? operationId
        : throw new ArgumentException("A shutdown observation requires a nonempty operation identity.", nameof(operationId));

    internal static int OutstandingLeases(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count;
    }

    internal static IReadOnlyList<TensorRuntimeDiagnostic> Diagnostics(IReadOnlyList<TensorRuntimeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var snapshot = new TensorRuntimeDiagnostic[diagnostics.Count];
        for (int index = 0; index < diagnostics.Count; index++)
        {
            TensorRuntimeDiagnostic diagnostic = diagnostics[index]
                ?? throw new ArgumentException("A diagnostic list cannot contain null entries.", nameof(diagnostics));
            snapshot[index] = new(diagnostic.Code, diagnostic.Message);
        }
        return Array.AsReadOnly(snapshot);
    }
}
