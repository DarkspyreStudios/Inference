using System.Security.Cryptography;
using System.Text.Json;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Outcome of a family's metadata compatibility check for a model candidate.</summary>
public enum TensorCompatibilityState
{
    /// <summary>The family can run the candidate.</summary>
    Supported,
    /// <summary>The candidate's metadata shows the family cannot run it.</summary>
    Unsupported,
    /// <summary>The candidate could not be inspected completely, so compatibility is unknown.</summary>
    Unverified
}

/// <summary>One check a resolver made against one artifact while evaluating compatibility.</summary>
/// <param name="ArtifactPath">The artifact path the check inspected.</param>
/// <param name="Check">The name of the check.</param>
/// <param name="Detail">What the check found.</param>
public sealed record TensorCompatibilityEvidence(string ArtifactPath, string Check, string Detail);

/// <summary>A resolver's compatibility verdict for a candidate, with the issues and evidence behind it.</summary>
/// <param name="State">The compatibility verdict.</param>
/// <param name="Issues">The problems that prevented a supported verdict.</param>
/// <param name="Evidence">The checks the resolver made.</param>
/// <param name="SourceState">The state of the source the candidate came from.</param>
public sealed record TensorModelEvaluation(TensorCompatibilityState State,
    IReadOnlyList<TensorSourceIssue> Issues, IReadOnlyList<TensorCompatibilityEvidence> Evidence,
    TensorSourceState SourceState = TensorSourceState.Available);

/// <summary>The acquisition input for a model: the primary location plus optional companion locations and expected integrity.</summary>
/// <param name="Location">Where the primary artifact comes from.</param>
/// <param name="Backend">The backend the model is evaluated for.</param>
/// <param name="License">The license to record for artifacts whose source declares none.</param>
/// <param name="ExpectedLength">The expected byte length of the primary artifact, when known.</param>
/// <param name="ExpectedSha256">The expected SHA-256 of the primary artifact, when known.</param>
/// <param name="Companions">Explicit locations for companion artifacts, keyed by family role.</param>
public sealed record TensorModelSelection(TensorModelLocation Location, TensorBackendKind Backend = TensorBackendKind.Auto,
    string? License = null, long? ExpectedLength = null, string? ExpectedSha256 = null,
    IReadOnlyList<TensorArtifactLocation>? Companions = null);

/// <summary>The result of resolving a candidate: its evaluation and, when supported, the complete pack definition.</summary>
/// <param name="Evaluation">The compatibility verdict.</param>
/// <param name="Definition">The resolved pack definition, or null when resolution failed.</param>
public sealed record TensorModelResolution(TensorModelEvaluation Evaluation, TensorModelPackDefinition? Definition = null);

/// <summary>Engine-owned metadata evaluation and complete immutable pack resolution. Never loads model weights.</summary>
public interface ITensorModelResolver
{
    /// <summary>The repository source used for repository locations, or null when only file locations are read.</summary>
    ITensorModelSource? Source { get; }
    /// <summary>The artifact roles the family needs; the first entry is the primary artifact and the rest are companions.</summary>
    IReadOnlyList<TensorArtifactRequirement> Requirements { get; }
    /// <summary>The role prefix for numbered extra artifacts such as split model shards, or null when the family accepts none.</summary>
    string? AdditionalArtifactRolePrefix { get; }
    /// <summary>Reads the selection's locations into one candidate with every required role assigned to an artifact.</summary>
    Task<TensorSourceResult<TensorModelCandidate>> DiscoverAsync(TensorModelSelection selection, CancellationToken cancellationToken = default);
    /// <summary>Checks a discovered candidate's metadata through <paramref name="reader"/> and builds its pack definition when the family supports it.</summary>
    Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend,
        ITensorArtifactReader reader, CancellationToken cancellationToken = default);
    /// <summary>Returns only the compatibility verdict for a discovered candidate.</summary>
    Task<TensorModelEvaluation> EvaluateAsync(TensorModelCandidate candidate, TensorBackendKind backend, CancellationToken cancellationToken = default);
    /// <summary>Discovers the selection and resolves the resulting candidate in one step.</summary>
    Task<TensorModelResolution> ResolveAsync(TensorModelSelection selection, CancellationToken cancellationToken = default);
}

/// <summary>One identity rule shared by engine resolvers. Does not register or install the definition.</summary>
public static class TensorResolvedPack
{
    /// <summary>Builds a validated pack definition keyed by a fingerprint of the canonical manifest, or returns <paramref name="builtIn"/> when it has the same artifacts and provenance.</summary>
    public static TensorModelPackDefinition Create(TensorModelCandidate candidate, TensorCapability capability,
        string architecture, IReadOnlySet<TensorBackendKind> backends, IReadOnlyList<TensorModelArtifactDefinition> artifacts,
        TensorModelPackDefinition? builtIn = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (artifacts.Count == 0 || string.IsNullOrWhiteSpace(candidate.License)
            || artifacts.Any(artifact => string.IsNullOrWhiteSpace(artifact.Provenance?.License)))
        { throw new ArgumentException("A resolved pack requires complete artifact and license provenance."); }
        artifacts = artifacts.OrderBy(artifact => artifact.Role, StringComparer.Ordinal).Select(artifact => artifact with
        {
            Sha256 = artifact.Sha256.ToLowerInvariant(),
            Sources = artifact.Sources.OrderBy(uri => uri.AbsoluteUri, StringComparer.Ordinal).ToArray()
        }).ToArray();
        // Canonical field order and artifact order make the identity independent of source enumeration order.
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new TensorManifestFingerprint(
            candidate.SourceId, candidate.Selection.Repository, candidate.Selection.Variant, architecture, capability,
            backends.Order().ToArray(), artifacts.OrderBy(artifact => artifact.Role, StringComparer.Ordinal)
                .Select(artifact => new TensorFingerprintArtifact(artifact.Role, artifact.RelativePath, artifact.Length,
                    artifact.Sha256.ToLowerInvariant(), artifact.Provenance,
                    artifact.Sources.Select(uri => uri.AbsoluteUri).Order(StringComparer.Ordinal).ToArray())).ToArray()),
            TensorJsonContext.Default.TensorManifestFingerprint);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(manifest));
        var definition = new TensorModelPackDefinition
        {
            Key = new((candidate.Selection.Repository ?? "imported") + "/" + candidate.Selection.Variant,
                (candidate.Selection.Revision is { Length: > 0 } revision ? revision + ":" : string.Empty) + fingerprint),
            Capability = capability,
            RuntimeArchitecture = architecture,
            License = string.Join(" AND ", artifacts.Select(artifact => artifact.Provenance!.License!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            ProjectUri = candidate.Artifacts.Count == 0 || candidate.Artifacts[0].Provenance.Origin.IsFile ? null : candidate.Artifacts[0].Provenance.Origin,
            SupportedBackends = backends,
            Artifacts = artifacts
        };
        TensorModelDefinitionValidator.Validate(definition);
        // A built-in match preserves its established key and immutable manifest, including pinned provenance.
        if (builtIn is not null && builtIn.Capability == capability && builtIn.RuntimeArchitecture == architecture
            && builtIn.SupportedBackends.SetEquals(backends) && builtIn.Artifacts.Count == artifacts.Count
            && builtIn.Artifacts.All(expected => artifacts.Any(actual => actual.Role == expected.Role
                && actual.RelativePath == expected.RelativePath && actual.Length == expected.Length
                && expected.Provenance is not null && actual.Provenance is not null
                && expected.Provenance.Origin == actual.Provenance.Origin
                && expected.Provenance.Repository == actual.Provenance.Repository
                && expected.Provenance.Revision == actual.Provenance.Revision
                && expected.Provenance.Path == actual.Provenance.Path
                && string.Equals(expected.Provenance.License, actual.Provenance.License, StringComparison.OrdinalIgnoreCase)
                && string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))))
        { return new TensorModelCatalog([builtIn]).GetRequired(builtIn.Key); }
        return new TensorModelCatalog([definition]).GetRequired(definition.Key);
    }

    /// <summary>Converts a source artifact into a pack artifact under the given role and path; throws when its length or SHA-256 is unknown.</summary>
    public static TensorModelArtifactDefinition Artifact(TensorSourceArtifact source, string role, string relativePath) => new()
    {
        Role = role,
        RelativePath = relativePath,
        Length = source.Length ?? throw new ArgumentException("Artifact length is unknown."),
        Sha256 = source.Sha256 ?? throw new ArgumentException("Artifact integrity is unknown."),
        Sources = [source.DownloadUri],
        Provenance = source.Provenance
    };
}
