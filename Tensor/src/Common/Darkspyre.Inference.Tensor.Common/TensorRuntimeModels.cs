using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>One portable integrity declaration. Construction does not inspect files or links.</summary>
public sealed record TensorRuntimeNativeFile(string RelativePath, long Size, string Sha256)
{
    [JsonRequired]
    public string RelativePath { get; init => field = TensorRuntimeValidation.RelativePath(value); }
        = TensorRuntimeValidation.RelativePath(RelativePath);
    [JsonRequired]
    public long Size { get; init => field = TensorRuntimeValidation.Size(value); } = TensorRuntimeValidation.Size(Size);
    [JsonRequired]
    public string Sha256 { get; init => field = TensorRuntimeValidation.Hash(value); } = TensorRuntimeValidation.Hash(Sha256);
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

    // Required generated metadata needs a setter; constructor binding preserves the validated snapshots.
    [JsonInclude, JsonRequired]
    public TensorNativeCandidate Identity { get; internal init; }
    [JsonInclude, JsonRequired]
    public TensorBackendKind Backend { get; internal init; }
    [JsonInclude, JsonRequired]
    public IReadOnlyList<TensorRuntimeNativeFile> Files { get; internal init; }
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

    // Internal init permits required generated metadata without exposing a public setter.
    [JsonInclude, JsonRequired]
    public TensorSharpBuildIdentity Build { get; internal init; }
    [JsonInclude, JsonRequired]
    public string TargetRid { get; internal init; }
    public IReadOnlyList<TensorRuntimeCandidate>? Candidates { get; }
}

/// <summary>The identity of an actually initialized GGML backend, supplied by the executing runtime.</summary>
public sealed record TensorRuntimeBackendIdentity(TensorBackendKind Backend, TensorSharpBuildIdentity Build,
    string TargetRid, string Variant, string? CandidateId, string NativeSourceCommit)
{
    [JsonRequired]
    public TensorBackendKind Backend { get; init => field = TensorRuntimeValidation.Backend(value); }
        = TensorRuntimeValidation.Backend(Backend);
    [JsonRequired]
    public TensorSharpBuildIdentity Build { get; init => field = TensorRuntimeValidation.Build(value); }
        = TensorRuntimeValidation.Build(Build);
    [JsonRequired]
    public string TargetRid { get; init => field = TensorRuntimeValidation.Text(value, nameof(TargetRid)); }
        = TensorRuntimeValidation.Text(TargetRid, nameof(TargetRid));
    [JsonRequired]
    public string Variant { get; init => field = TensorRuntimeValidation.Text(value, nameof(Variant)); }
        = TensorRuntimeValidation.Text(Variant, nameof(Variant));
    [JsonRequired]
    public string? CandidateId { get; init => field = TensorRuntimeValidation.CandidateId(value); }
        = TensorRuntimeValidation.CandidateId(CandidateId);
    [JsonRequired]
    public string NativeSourceCommit { get; init => field = TensorRuntimeValidation.Text(value, nameof(NativeSourceCommit)); }
        = TensorRuntimeValidation.Text(NativeSourceCommit, nameof(NativeSourceCommit));
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
public sealed record TensorRuntimeDiagnostic(string Code, string Message)
{
    [JsonRequired]
    public string Code { get; init => field = TensorRuntimeValidation.Text(value, nameof(Code)); }
        = TensorRuntimeValidation.Text(Code, nameof(Code));
    [JsonRequired]
    public string Message { get; init => field = TensorRuntimeValidation.Text(value, nameof(Message)); }
        = TensorRuntimeValidation.Text(Message, nameof(Message));
}

/// <summary>An immutable shutdown observation; completion does not assert OS library unloading.</summary>
public sealed record TensorRuntimeShutdownResult(TensorRuntimeShutdownState State, Guid OperationId,
    DateTimeOffset ObservedAt, int OutstandingLeases, bool NativeStateMayRemain,
    IReadOnlyList<TensorRuntimeDiagnostic> Diagnostics)
{
    [JsonRequired]
    public TensorRuntimeShutdownState State { get; init => field = TensorRuntimeValidation.ShutdownState(value); }
        = TensorRuntimeValidation.ShutdownState(State);
    [JsonRequired]
    public Guid OperationId { get; init => field = TensorRuntimeValidation.OperationId(value); }
        = TensorRuntimeValidation.OperationId(OperationId);
    [JsonRequired]
    public DateTimeOffset ObservedAt { get; init; } = ObservedAt;
    [JsonRequired]
    public int OutstandingLeases { get; init => field = TensorRuntimeValidation.OutstandingLeases(value); }
        = TensorRuntimeValidation.OutstandingLeases(OutstandingLeases);
    [JsonRequired]
    public bool NativeStateMayRemain { get; init; } = NativeStateMayRemain;
    [JsonRequired]
    public IReadOnlyList<TensorRuntimeDiagnostic> Diagnostics { get; init => field = TensorRuntimeValidation.Diagnostics(value); }
        = TensorRuntimeValidation.Diagnostics(Diagnostics);
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
