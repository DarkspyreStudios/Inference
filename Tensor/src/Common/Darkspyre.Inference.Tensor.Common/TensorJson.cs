using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Assembly-owned metadata for Tensor settings, model manifests and runtime observations.</summary>
public static class TensorJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static JsonTypeInfo<TensorSetupConfiguration> SetupConfiguration => Metadata<TensorSetupConfiguration>();
    public static JsonTypeInfo<TensorSetupChanges> SetupChanges => Metadata<TensorSetupChanges>();
    public static JsonTypeInfo<TensorModelPackDefinition> ModelPack => Metadata<TensorModelPackDefinition>();
    public static JsonTypeInfo<TensorModelPackStatus> PackStatus => Metadata<TensorModelPackStatus>();
    public static JsonTypeInfo<TensorModelObservation> ModelObservation => Metadata<TensorModelObservation>();
    public static JsonTypeInfo<TensorBackendProbeRequest> BackendProbeRequest => Metadata<TensorBackendProbeRequest>();
    public static JsonTypeInfo<TensorBackendAvailability> BackendAvailability => Metadata<TensorBackendAvailability>();
    public static JsonTypeInfo<TensorRuntimeNativeFile> TensorRuntimeNativeFile => Metadata<TensorRuntimeNativeFile>();
    public static JsonTypeInfo<TensorRuntimeCandidate> TensorRuntimeCandidate => Metadata<TensorRuntimeCandidate>();
    public static JsonTypeInfo<TensorRuntimePlan> TensorRuntimePlan => Metadata<TensorRuntimePlan>();
    public static JsonTypeInfo<TensorRuntimeBackendIdentity> TensorRuntimeBackendIdentity => Metadata<TensorRuntimeBackendIdentity>();
    public static JsonTypeInfo<TensorRuntimeState> TensorRuntimeState => Metadata<TensorRuntimeState>();
    public static JsonTypeInfo<TensorRuntimeShutdownState> TensorRuntimeShutdownState => Metadata<TensorRuntimeShutdownState>();
    public static JsonTypeInfo<TensorRuntimeDiagnostic> TensorRuntimeDiagnostic => Metadata<TensorRuntimeDiagnostic>();
    public static JsonTypeInfo<TensorRuntimeShutdownResult> TensorRuntimeShutdownResult => Metadata<TensorRuntimeShutdownResult>();

    private static JsonTypeInfo<T> Metadata<T>() => Options.GetTypeInfo<T>();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(TensorJsonContext.Default.Options);
        options.Converters.Add(new TensorRuntimeStateJsonConverter());
        options.Converters.Add(new TensorRuntimeShutdownStateJsonConverter());
        options.MakeReadOnly();
        return options;
    }
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
