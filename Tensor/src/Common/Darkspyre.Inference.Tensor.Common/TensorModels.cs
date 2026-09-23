using System.Collections.Frozen;

namespace Darkspyre.Inference.Tensor.Common;

public enum TensorCapability
{
    Llm,
    Image,
    Decision,
    SpeechTranscription
}

public enum TensorBackendKind
{
    Auto,
    Cpu,
    GgmlCpu,
    GgmlMetal,
    GgmlCuda,
    GgmlVulkan,
    Cuda,
    Mlx
}

public sealed record TensorModelKey(string ModelId, string Revision)
{
    public override string ToString() => $"{ModelId}@{Revision}";
}

public sealed record TensorModelArtifactDefinition
{
    public required string Role { get; init; }

    public required string RelativePath { get; init; }

    public required long Length { get; init; }

    public required string Sha256 { get; init; }

    public IReadOnlyList<Uri> Sources { get; init; } = [];

    public TensorArtifactProvenance? Provenance { get; init; }
}

/// <summary>Source metadata only. Never includes an access token or a transient signed download URL.</summary>
public sealed record TensorArtifactProvenance(Uri Origin, string Repository, string Revision, string Path, string? License);

public sealed record TensorModelPackDefinition
{
    public required TensorModelKey Key { get; init; }

    public required TensorCapability Capability { get; init; }

    public required string RuntimeArchitecture { get; init; }

    public required string License { get; init; }

    public Uri? ProjectUri { get; init; }

    public IReadOnlySet<TensorBackendKind> SupportedBackends { get; init; } =
        new HashSet<TensorBackendKind>();

    public IReadOnlyList<TensorModelArtifactDefinition> Artifacts { get; init; } = [];
}

public sealed class TensorModelCatalog
{
    private readonly Dictionary<TensorModelKey, TensorModelPackDefinition> _definitions = [];
    private readonly Lock _gate = new();

    public TensorModelCatalog(IEnumerable<TensorModelPackDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (TensorModelPackDefinition definition in definitions) { Register(definition); }
    }

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
                TensorSourceHttp.ValidateUri(provenance.Origin);
                if (provenance.Origin != TensorSourceHttp.Origin(provenance.Origin))
                { throw new ArgumentException("Provenance must contain an origin, not a download URL.", nameof(definition)); }
                ArgumentException.ThrowIfNullOrWhiteSpace(provenance.Repository);
                ArgumentException.ThrowIfNullOrWhiteSpace(provenance.Revision);
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
