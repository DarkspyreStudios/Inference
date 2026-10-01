using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Assembly-owned metadata for Tensor settings, model manifests and runtime observations.</summary>
public static class TensorJson
{
    public static JsonTypeInfo<TensorSetupConfiguration> SetupConfiguration => TensorJsonContext.Default.TensorSetupConfiguration;
    public static JsonTypeInfo<TensorSetupChanges> SetupChanges => TensorJsonContext.Default.TensorSetupChanges;
    public static JsonTypeInfo<TensorModelPackDefinition> ModelPack => TensorJsonContext.Default.TensorModelPackDefinition;
    public static JsonTypeInfo<TensorModelPackStatus> PackStatus => TensorJsonContext.Default.TensorModelPackStatus;
    public static JsonTypeInfo<TensorModelObservation> ModelObservation => TensorJsonContext.Default.TensorModelObservation;
    public static JsonTypeInfo<TensorBackendProbeRequest> BackendProbeRequest => TensorJsonContext.Default.TensorBackendProbeRequest;
    public static JsonTypeInfo<TensorBackendAvailability> BackendAvailability => TensorJsonContext.Default.TensorBackendAvailability;
    public static JsonTypeInfo<TensorRuntimeNativeFile> TensorRuntimeNativeFile => TensorJsonContext.Default.TensorRuntimeNativeFile;
    public static JsonTypeInfo<TensorRuntimeCandidate> TensorRuntimeCandidate => TensorJsonContext.Default.TensorRuntimeCandidate;
    public static JsonTypeInfo<TensorRuntimePlan> TensorRuntimePlan => TensorJsonContext.Default.TensorRuntimePlan;
    public static JsonTypeInfo<TensorRuntimeBackendIdentity> TensorRuntimeBackendIdentity => TensorJsonContext.Default.TensorRuntimeBackendIdentity;
    public static JsonTypeInfo<TensorRuntimeState> TensorRuntimeState => TensorJsonContext.Default.TensorRuntimeState;
    public static JsonTypeInfo<TensorRuntimeShutdownState> TensorRuntimeShutdownState => TensorJsonContext.Default.TensorRuntimeShutdownState;
    public static JsonTypeInfo<TensorRuntimeDiagnostic> TensorRuntimeDiagnostic => TensorJsonContext.Default.TensorRuntimeDiagnostic;
    public static JsonTypeInfo<TensorRuntimeShutdownResult> TensorRuntimeShutdownResult => TensorJsonContext.Default.TensorRuntimeShutdownResult;
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TensorSetupConfiguration))]
[JsonSerializable(typeof(TensorSetupChanges))]
[JsonSerializable(typeof(TensorModelPackDefinition))]
[JsonSerializable(typeof(TensorModelPackStatus))]
[JsonSerializable(typeof(TensorModelObservation))]
[JsonSerializable(typeof(TensorBackendProbeRequest))]
[JsonSerializable(typeof(TensorBackendAvailability))]
[JsonSerializable(typeof(TensorRuntimeNativeFile))]
[JsonSerializable(typeof(TensorRuntimeCandidate))]
[JsonSerializable(typeof(TensorRuntimePlan))]
[JsonSerializable(typeof(TensorRuntimeBackendIdentity))]
[JsonSerializable(typeof(TensorRuntimeState))]
[JsonSerializable(typeof(TensorRuntimeShutdownState))]
[JsonSerializable(typeof(TensorRuntimeDiagnostic))]
[JsonSerializable(typeof(TensorRuntimeShutdownResult))]
[JsonSerializable(typeof(PersistedModelManifest))]
[JsonSerializable(typeof(TensorManifestFingerprint))]
internal sealed partial class TensorJsonContext : JsonSerializerContext;

internal sealed record PersistedArtifact(string Role, string RelativePath, long Length, string Sha256);

internal sealed record PersistedModelManifest(string ModelId, string Revision, IReadOnlyList<PersistedArtifact> Artifacts)
{
    public static PersistedModelManifest Create(TensorModelPackDefinition definition) => new(
        definition.Key.ModelId, definition.Key.Revision,
        definition.Artifacts.Select(static artifact => new PersistedArtifact(
            artifact.Role, artifact.RelativePath, artifact.Length, artifact.Sha256.ToLowerInvariant())).ToArray());
}

internal sealed record TensorManifestFingerprint(string SourceId, string? Repository, string Variant,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("capability")] TensorCapability Capability,
    TensorBackendKind[] Backends, TensorFingerprintArtifact[] Artifacts);

internal sealed record TensorFingerprintArtifact(string Role, string RelativePath, long Length, string Sha256,
    TensorArtifactProvenance? Provenance, string[] Sources);
