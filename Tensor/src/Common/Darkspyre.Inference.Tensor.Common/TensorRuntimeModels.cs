namespace Darkspyre.Inference.Tensor.Common;

/// <summary>One portable integrity declaration. Construction does not inspect files or links.</summary>
public sealed record TensorRuntimeNativeFile
{
    public TensorRuntimeNativeFile(string relativePath, long size, string sha256)
    {
        TensorRuntimeValidation.RelativePath(relativePath);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64 || sha256.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A native file requires an exact lowercase SHA-256 hash.", nameof(sha256));
        RelativePath = relativePath;
        Size = size;
        Sha256 = sha256;
    }

    public string RelativePath { get; }
    public long Size { get; }
    public string Sha256 { get; }
}

/// <summary>An immutable GGML candidate declaration, not evidence of installation or successful initialization.</summary>
public sealed class TensorRuntimeCandidate
{
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

    public TensorNativeCandidate Identity { get; }
    public TensorBackendKind Backend { get; }
    public IReadOnlyList<TensorRuntimeNativeFile> Files { get; }
}

/// <summary>Null candidates select the standard package policy; an empty list admits no candidates.</summary>
public sealed class TensorRuntimePlan
{
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

    public TensorSharpBuildIdentity Build { get; }
    public string TargetRid { get; }
    public IReadOnlyList<TensorRuntimeCandidate>? Candidates { get; }
}

/// <summary>The identity of an actually initialized GGML backend, supplied by the executing runtime.</summary>
public sealed record TensorRuntimeBackendIdentity
{
    public TensorRuntimeBackendIdentity(TensorBackendKind backend, TensorSharpBuildIdentity build,
        string targetRid, string variant, string? candidateId, string nativeSourceCommit)
    {
        TensorRuntimeValidation.Backend(backend);
        Backend = backend;
        Build = TensorRuntimeValidation.Build(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRid);
        ArgumentException.ThrowIfNullOrWhiteSpace(variant);
        if (candidateId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeSourceCommit);
        TargetRid = targetRid;
        Variant = variant;
        CandidateId = candidateId;
        NativeSourceCommit = nativeSourceCommit;
    }

    public TensorBackendKind Backend { get; }
    public TensorSharpBuildIdentity Build { get; }
    public string TargetRid { get; }
    public string Variant { get; }
    public string? CandidateId { get; }
    public string NativeSourceCommit { get; }
}

public enum TensorRuntimeState
{
    Uninitialized = 0, Initializing = 1, Ready = 2, Unavailable = 3,
    RequiresProcessRestart = 4, Stopping = 5, Stopped = 6
}

public enum TensorRuntimeShutdownState
{
    Completed = 0, AlreadyStopped = 1, Busy = 2,
    FailedRequiresProcessExit = 3
}

/// <summary>A safe runtime observation. Producers sanitize messages before publishing diagnostics.</summary>
public sealed record TensorRuntimeDiagnostic
{
    public TensorRuntimeDiagnostic(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
    }

    public string Code { get; }
    public string Message { get; }
}

/// <summary>An immutable shutdown observation; completion does not assert OS library unloading.</summary>
public sealed record TensorRuntimeShutdownResult
{
    public TensorRuntimeShutdownResult(TensorRuntimeShutdownState state, Guid operationId,
        DateTimeOffset observedAt, int outstandingLeases, bool nativeStateMayRemain,
        IReadOnlyList<TensorRuntimeDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (operationId == Guid.Empty)
            throw new ArgumentException("A shutdown observation requires a nonempty operation identity.", nameof(operationId));
        ArgumentOutOfRangeException.ThrowIfNegative(outstandingLeases);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var snapshot = new TensorRuntimeDiagnostic[diagnostics.Count];
        for (int index = 0; index < diagnostics.Count; index++)
        {
            TensorRuntimeDiagnostic diagnostic = diagnostics[index]
                ?? throw new ArgumentException("A diagnostic list cannot contain null entries.", nameof(diagnostics));
            snapshot[index] = new(diagnostic.Code, diagnostic.Message);
        }
        State = state;
        OperationId = operationId;
        ObservedAt = observedAt;
        OutstandingLeases = outstandingLeases;
        NativeStateMayRemain = nativeStateMayRemain;
        Diagnostics = Array.AsReadOnly(snapshot);
    }

    public TensorRuntimeShutdownState State { get; }
    public Guid OperationId { get; }
    public DateTimeOffset ObservedAt { get; }
    public int OutstandingLeases { get; }
    public bool NativeStateMayRemain { get; }
    public IReadOnlyList<TensorRuntimeDiagnostic> Diagnostics { get; }
}

/// <summary>Neutral control and observations for one generation-owned GGML runtime.</summary>
public interface ITensorRuntimeOwner : ITensorBackendProbe, IAsyncDisposable
{
    TensorRuntimePlan Plan { get; }
    TensorRuntimeState State { get; }
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

    internal static void Backend(TensorBackendKind backend)
    {
        if (backend is not (TensorBackendKind.GgmlCpu or TensorBackendKind.GgmlMetal
            or TensorBackendKind.GgmlCuda or TensorBackendKind.GgmlVulkan))
            throw new ArgumentOutOfRangeException(nameof(backend), "A runtime candidate requires a concrete GGML backend.");
    }

    internal static void RelativePath(string path)
    {
        TensorModelDefinitionValidator.ValidateRelativePath(path);
        if (path.Any(char.IsControl) || path.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("A native file requires a portable relative path.", nameof(path));
    }
}
