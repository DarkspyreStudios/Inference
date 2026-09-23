using System.Security.Cryptography;
using System.Text.Json;

namespace Darkspyre.Inference.Tensor.Common;

public enum TensorCompatibilityState { Supported, Unsupported, Unverified }
public sealed record TensorCompatibilityEvidence(string ArtifactPath, string Check, string Detail);
public sealed record TensorModelEvaluation(TensorCompatibilityState State,
    IReadOnlyList<TensorSourceIssue> Issues, IReadOnlyList<TensorCompatibilityEvidence> Evidence,
    TensorSourceState SourceState = TensorSourceState.Available);
public sealed record TensorModelSelection(TensorCandidateSelection Candidate, TensorBackendKind Backend = TensorBackendKind.Auto);
public sealed record TensorModelResolution(TensorModelEvaluation Evaluation, TensorModelPackDefinition? Definition = null);

/// <summary>Engine-owned metadata evaluation and complete immutable pack resolution. Never loads model weights.</summary>
public interface ITensorModelResolver
{
    ITensorModelSource Source { get; }
    Task<TensorModelEvaluation> EvaluateAsync(TensorModelCandidate candidate, TensorBackendKind backend, CancellationToken cancellationToken = default);
    Task<TensorModelResolution> ResolveAsync(TensorModelSelection selection, CancellationToken cancellationToken = default);
}

/// <summary>Actual runtime prerequisites, separately observed from a pack's supported backend declarations.</summary>
public sealed record TensorBackendAvailability(TensorBackendKind Backend, bool? IsAvailable, string Reason, DateTimeOffset ObservedAt);
public interface ITensorBackendProbe
{
    Task<TensorBackendAvailability> CheckAsync(TensorBackendKind backend, CancellationToken cancellationToken = default);
}

/// <summary>One identity rule shared by engine resolvers. Does not register or install the definition.</summary>
public static class TensorResolvedPack
{
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
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            candidate.SourceId,
            candidate.Selection.Repository,
            candidate.Selection.Variant,
            architecture,
            capability,
            Backends = backends.Order(),
            Artifacts = artifacts.OrderBy(artifact => artifact.Role, StringComparer.Ordinal).Select(artifact => new
            {
                artifact.Role,
                artifact.RelativePath,
                artifact.Length,
                Sha256 = artifact.Sha256.ToLowerInvariant(),
                artifact.Provenance,
                Sources = artifact.Sources.Select(uri => uri.AbsoluteUri).Order(StringComparer.Ordinal)
            })
        });
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(manifest));
        var definition = new TensorModelPackDefinition
        {
            Key = new(candidate.Selection.Repository + "/" + candidate.Selection.Variant,
                candidate.Selection.Revision + ":" + fingerprint),
            Capability = capability,
            RuntimeArchitecture = architecture,
            License = string.Join(" AND ", artifacts.Select(artifact => artifact.Provenance!.License!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            ProjectUri = candidate.Artifacts.Count == 0 ? null : candidate.Artifacts[0].Provenance.Origin,
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
