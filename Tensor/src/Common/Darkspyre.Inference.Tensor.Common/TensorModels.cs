using System.Collections.Frozen;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>The kind of inference a model pack serves.</summary>
public enum TensorCapability
{
/// <summary>Large language model text generation.</summary>
    Llm,
/// <summary>Image generation.</summary>
    Image,
/// <summary>Decision models, such as Laya.</summary>
    Decision,
/// <summary>Speech-to-text transcription.</summary>
    SpeechTranscription,
/// <summary>Text embedding.</summary>
    Embedding,
    /// <summary>Text recognition in images.</summary>
    TextRecognition,
    /// <summary>Decoding images and sampling video frames.</summary>
    MediaDecoding
}

/// <summary>An execution backend a Tensor engine can run a model pack on.</summary>
public enum TensorBackendKind
{
/// <summary>The engine picks a backend the pack supports.</summary>
    Auto,
/// <summary>The CPU outside GGML.</summary>
    Cpu,
/// <summary>GGML on the CPU.</summary>
    GgmlCpu,
/// <summary>GGML on Apple Metal.</summary>
    GgmlMetal,
/// <summary>GGML on NVIDIA CUDA.</summary>
    GgmlCuda,
/// <summary>GGML on Vulkan.</summary>
    GgmlVulkan,
/// <summary>NVIDIA CUDA outside GGML.</summary>
    Cuda,
/// <summary>Apple MLX.</summary>
    Mlx,
/// <summary>Apple Core ML.</summary>
    CoreMl,
/// <summary>ONNX Runtime through the packaged WebGPU execution provider.</summary>
    WebGpu
}

/// <summary>The identity of a model pack.</summary>
/// <param name="ModelId">The model id.</param>
/// <param name="Revision">The model revision.</param>
public sealed record TensorModelKey(string ModelId, string Revision)
{
    /// <summary>Returns the key as <c>modelId@revision</c>.</summary>
    public override string ToString() => $"{ModelId}@{Revision}";
}

/// <summary>One file in a model pack, with its expected size, hash and sources.</summary>
public sealed record TensorModelArtifactDefinition
{
    /// <summary>The name an engine uses to find this file in the pack, such as <c>model</c> or <c>tokenizer</c>.</summary>
    public required string Role { get; init; }

    /// <summary>The portable path of the file inside the pack directory, with forward slashes and no rooted, empty, <c>.</c> or <c>..</c> segments.</summary>
    public required string RelativePath { get; init; }

    /// <summary>The file size in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>The SHA-256 hash of the file as 64 hexadecimal characters.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Absolute URIs the artifact can be downloaded or copied from.</summary>
    public IReadOnlyList<Uri> Sources { get; init; } = [];

    /// <summary>Where the artifact was discovered, when it came from a source.</summary>
    public TensorArtifactProvenance? Provenance { get; init; }
}

/// <summary>Source metadata only. Never includes an access token or a transient signed download URL.</summary>
/// <param name="Origin">The base address of the source the artifact came from.</param>
/// <param name="Repository">The repository in the source, or null when the source has none.</param>
/// <param name="Revision">The repository revision, or null when unknown.</param>
/// <param name="Path">The artifact's path in the repository or source.</param>
/// <param name="License">The artifact license, or null when unknown.</param>
public sealed record TensorArtifactProvenance(Uri Origin, string? Repository, string? Revision, string Path, string? License);

/// <summary>The portable manifest of one model pack: its identity, capability, license, supported backends and artifacts.</summary>
public sealed record TensorModelPackDefinition
{
    /// <summary>The model id and revision that identify the pack.</summary>
    public required TensorModelKey Key { get; init; }

    /// <summary>The kind of inference the pack serves.</summary>
    public required TensorCapability Capability { get; init; }

    /// <summary>The model architecture name an engine checks to decide whether it can run the pack.</summary>
    public required string RuntimeArchitecture { get; init; }

    /// <summary>The license that covers the pack's weights.</summary>
    public required string License { get; init; }

    /// <summary>The model's project page, when known.</summary>
    public Uri? ProjectUri { get; init; }

    /// <summary>The execution backends the pack runs on.</summary>
    public IReadOnlySet<TensorBackendKind> SupportedBackends { get; init; } =
        new HashSet<TensorBackendKind>();

    /// <summary>The files that make up the pack. Each role and relative path appears once.</summary>
    public IReadOnlyList<TensorModelArtifactDefinition> Artifacts { get; init; } = [];
}

/// <summary>A thread-safe registry of validated, immutable model pack definitions keyed by <see cref="TensorModelKey"/>.</summary>
public sealed class TensorModelCatalog
{
    private readonly Dictionary<TensorModelKey, TensorModelPackDefinition> _definitions = [];
    private readonly Lock _gate = new();

    /// <summary>Creates a catalog and registers each definition through <see cref="Register"/>.</summary>
    public TensorModelCatalog(IEnumerable<TensorModelPackDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (TensorModelPackDefinition definition in definitions) { Register(definition); }
    }

    /// <summary>A snapshot of every registered definition.</summary>
    public IReadOnlyList<TensorModelPackDefinition> Definitions
    {
        get { lock (_gate) { return Array.AsReadOnly(_definitions.Values.ToArray()); } }
    }

    /// <summary>Registers an immutable snapshot. Identical registration is idempotent; a key never changes its manifest.</summary>
    public TensorModelPackDefinition Register(TensorModelPackDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        TensorModelDefinitionValidator.Validate(definition);
        TensorModelPackDefinition snapshot = definition with
        {
            SupportedBackends = definition.SupportedBackends.ToFrozenSet(),
            Artifacts = Array.AsReadOnly(definition.Artifacts.Select(artifact => artifact with
            { Sources = Array.AsReadOnly(artifact.Sources.ToArray()), Sha256 = artifact.Sha256.ToLowerInvariant() }).ToArray())
        };
        TensorModelDefinitionValidator.Validate(snapshot);
        lock (_gate)
        {
            if (_definitions.TryGetValue(snapshot.Key, out TensorModelPackDefinition? existing))
            {
                if (!Equivalent(existing, snapshot)) { throw new ArgumentException("The model key is already registered with a different definition.", nameof(definition)); }
                return existing;
            }
            _definitions.Add(snapshot.Key, snapshot);
            return snapshot;
        }
    }

    /// <summary>Returns the registered definition for the key, or throws <see cref="KeyNotFoundException"/> when the key is not registered.</summary>
    public TensorModelPackDefinition GetRequired(TensorModelKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return _definitions.TryGetValue(key, out TensorModelPackDefinition? definition)
                ? definition
                : throw new KeyNotFoundException($"Tensor model pack '{key}' is not registered.");
        }
    }

    private static bool Equivalent(TensorModelPackDefinition left, TensorModelPackDefinition right) =>
        left.Capability == right.Capability && left.RuntimeArchitecture == right.RuntimeArchitecture
        && left.License == right.License && left.ProjectUri == right.ProjectUri && left.SupportedBackends.SetEquals(right.SupportedBackends)
        && left.Artifacts.Count == right.Artifacts.Count && left.Artifacts.All(artifact => right.Artifacts.Any(other =>
            artifact.Role == other.Role && artifact.RelativePath == other.RelativePath && artifact.Length == other.Length
            && artifact.Sha256 == other.Sha256 && artifact.Provenance == other.Provenance && artifact.Sources.SequenceEqual(other.Sources)));
}

/// <summary>Validates portable pack metadata before an engine applies its own compatibility policy.</summary>
public static class TensorModelDefinitionValidator
{
    /// <summary>Throws an <see cref="ArgumentException"/> when the key, license, architecture, capability, backends or any artifact path, hash, source or provenance is missing or invalid.</summary>
    public static void Validate(TensorModelPackDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Key);
        ArgumentNullException.ThrowIfNull(definition.Artifacts);
        ArgumentNullException.ThrowIfNull(definition.SupportedBackends);
        if (!Enum.IsDefined(definition.Capability) || definition.SupportedBackends.Any(backend => !Enum.IsDefined(backend)))
        { throw new ArgumentException("Unknown capability or backend.", nameof(definition)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Key.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Key.Revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.RuntimeArchitecture);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.License);
        if (definition.Artifacts.Count == 0)
        {
            throw new ArgumentException("A tensor model pack must contain at least one artifact.", nameof(definition));
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (TensorModelArtifactDefinition artifact in definition.Artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ArgumentNullException.ThrowIfNull(artifact.Sources);
            ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Role);
            ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Sha256);
            ValidateRelativePath(artifact.RelativePath);
            if (artifact.Length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(definition), "Artifact length cannot be negative.");
            }

            if (artifact.Sha256.Length != 64 || !artifact.Sha256.All(Uri.IsHexDigit))
            {
                throw new ArgumentException(
                    $"Artifact '{artifact.RelativePath}' must declare a lowercase or uppercase SHA-256 hash.",
                    nameof(definition));
            }

            if (!roles.Add(artifact.Role))
            {
                throw new ArgumentException($"Duplicate tensor model artifact role '{artifact.Role}'.", nameof(definition));
            }

            if (!paths.Add(artifact.RelativePath))
            {
                throw new ArgumentException(
                    $"Duplicate tensor model artifact path '{artifact.RelativePath}'.",
                    nameof(definition));
            }
            foreach (Uri source in artifact.Sources)
            {
                ArgumentNullException.ThrowIfNull(source);
                if (!source.IsAbsoluteUri) { throw new ArgumentException("Artifact sources must be absolute URIs.", nameof(definition)); }
                if (!source.IsFile) { TensorSourceHttp.ValidateUri(source); }
            }
            if (artifact.Provenance is TensorArtifactProvenance provenance)
            {
                if (!provenance.Origin.IsFile) { TensorSourceHttp.ValidateUri(provenance.Origin); }
                if (provenance.Origin != (provenance.Origin.IsFile ? new Uri("file:///") : TensorSourceHttp.Origin(provenance.Origin)))
                { throw new ArgumentException("Provenance must contain an origin, not a download URL.", nameof(definition)); }
                if (provenance.Repository is not null || provenance.Revision is not null)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(provenance.Repository);
                    ArgumentException.ThrowIfNullOrWhiteSpace(provenance.Revision);
                }
                ValidateRelativePath(provenance.Path);
            }
        }
    }

    internal static void ValidateRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || relativePath.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException($"Invalid portable model artifact path '{relativePath}'.", nameof(relativePath));
        }
    }
}
