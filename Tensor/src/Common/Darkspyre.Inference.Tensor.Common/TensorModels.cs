namespace Darkspyre.Inference.Tensor.Common;

public enum TensorCapability
{
    Llm,
    Image,
    Decision
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
}

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
    private readonly Dictionary<TensorModelKey, TensorModelPackDefinition> _definitions;

    public TensorModelCatalog(IEnumerable<TensorModelPackDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        TensorModelPackDefinition[] all = [.. definitions];
        foreach (TensorModelPackDefinition definition in all)
        {
            TensorModelDefinitionValidator.Validate(definition);
        }

        IGrouping<TensorModelKey, TensorModelPackDefinition>? duplicate = all
            .GroupBy(static definition => definition.Key)
            .FirstOrDefault(static group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate tensor model pack '{duplicate.Key}'.", nameof(definitions));
        }

        _definitions = all.ToDictionary(static definition => definition.Key);
        Definitions = Array.AsReadOnly(all);
    }

    public IReadOnlyList<TensorModelPackDefinition> Definitions { get; }

    public TensorModelPackDefinition GetRequired(TensorModelKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _definitions.TryGetValue(key, out TensorModelPackDefinition? definition)
            ? definition
            : throw new KeyNotFoundException($"Tensor model pack '{key}' is not registered.");
    }
}

/// <summary>Validates portable pack metadata before an engine applies its own compatibility policy.</summary>
public static class TensorModelDefinitionValidator
{
    public static void Validate(TensorModelPackDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
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
            ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Role);
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
