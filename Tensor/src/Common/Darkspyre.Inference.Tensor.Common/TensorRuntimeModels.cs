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

/// <summary>An immutable GGML or MLX candidate declaration, not evidence of installation or successful initialization.</summary>
public sealed class TensorRuntimeCandidate
{
    /// <summary>Creates a validated candidate. Throws when the identity is incomplete, the backend is not GGML or MLX, the
    /// file list is empty or repeats a path, or a preload path is not a listed file other than the entry library.</summary>
    public TensorRuntimeCandidate(TensorNativeCandidate identity, TensorBackendKind backend,
        IReadOnlyList<TensorRuntimeNativeFile> files, IReadOnlyList<string> preload)
    {
        ArgumentNullException.ThrowIfNull(identity);
        TensorRuntimeValidation.Backend(backend);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.AbsoluteBridgePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Variant);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.TargetRid);
        TensorSharpBuildIdentity build = TensorRuntimeValidation.Build(identity.Build);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(preload);
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
        string entry = Path.GetFileName(identity.AbsoluteBridgePath);
        if (preload.Any(path => path is null || !paths.Contains(path) || path == entry))
            throw new ArgumentException("Every preload path must be a listed file other than the entry library.", nameof(preload));
        Identity = new(identity.Id, identity.AbsoluteBridgePath, identity.Variant, build, identity.TargetRid);
        Backend = backend;
        Files = Array.AsReadOnly(snapshot);
        Preload = Array.AsReadOnly(preload.ToArray());
    }

    // Required generated metadata needs a setter; constructor binding preserves the validated snapshots.
    /// <summary>The candidate's id, bridge library path, variant, build and target RID.</summary>
    [JsonInclude, JsonRequired]
    public TensorNativeCandidate Identity { get; internal init; }
    /// <summary>The concrete GGML or MLX backend the candidate provides.</summary>
    [JsonInclude, JsonRequired]
    public TensorBackendKind Backend { get; internal init; }
    /// <summary>The complete set of native files the candidate needs, with their sizes and hashes.</summary>
    [JsonInclude, JsonRequired]
    public IReadOnlyList<TensorRuntimeNativeFile> Files { get; internal init; }
    /// <summary>Files loaded by absolute path, in this order, before the entry library.</summary>
    [JsonInclude, JsonRequired]
    public IReadOnlyList<string> Preload { get; internal init; }
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
            snapshot[index] = new(candidate.Identity, candidate.Backend, candidate.Files, candidate.Preload);
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

/// <summary>The identity of an actually initialized GGML or MLX backend, supplied by the executing runtime.</summary>
public sealed record TensorRuntimeBackendIdentity(TensorBackendKind Backend, TensorSharpBuildIdentity Build,
    string TargetRid, string Variant, string? CandidateId)
{
    /// <summary>The concrete GGML or MLX backend that initialized.</summary>
    [JsonRequired]
    public TensorBackendKind Backend { get; init => field = TensorRuntimeValidation.Backend(value); }
        = TensorRuntimeValidation.Backend(Backend);
    /// <summary>The TensorSharp release the runtime is bound to.</summary>
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
}

/// <summary>The lifecycle state of a process's native runtime.</summary>
public enum TensorRuntimeState
{
    /// <summary>No backend initialization has started.</summary>
    Uninitialized = 0,
    /// <summary>A backend initialization is in progress.</summary>
    Initializing = 1,
    /// <summary>A backend is initialized and admits work.</summary>
    Ready = 2,
    /// <summary>The selection found no usable backend; native state may remain until the process exits.</summary>
    Unavailable = 3
}

/// <summary>Neutral control and observations for a process's native runtime.</summary>
public interface ITensorRuntimeOwner : ITensorBackendProbe, IAsyncDisposable
{
    /// <summary>The build, target RID and native candidates this runtime is bound to.</summary>
    TensorRuntimePlan Plan { get; }
    /// <summary>The runtime's current lifecycle state.</summary>
    TensorRuntimeState State { get; }
    /// <summary>The identity of the initialized backend, or null when no backend is initialized.</summary>
    TensorRuntimeBackendIdentity? Current { get; }
}

internal static class TensorRuntimeValidation
{
    internal static TensorSharpBuildIdentity Build(TensorSharpBuildIdentity build)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(build.Version);
        return new(build.Version);
    }

    internal static TensorBackendKind Backend(TensorBackendKind backend)
    {
        if (backend is not (TensorBackendKind.GgmlCpu or TensorBackendKind.GgmlMetal
            or TensorBackendKind.GgmlCuda or TensorBackendKind.GgmlVulkan or TensorBackendKind.Mlx))
            throw new ArgumentOutOfRangeException(nameof(backend), "A runtime candidate requires a concrete GGML or MLX backend.");
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
}
