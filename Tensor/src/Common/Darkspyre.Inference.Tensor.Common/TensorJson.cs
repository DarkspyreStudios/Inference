using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Assembly-owned metadata for Tensor settings, model manifests and runtime observations.</summary>
public static class TensorJson
{
    /// <summary>Serialization metadata for <see cref="TensorSetupConfiguration"/>.</summary>
    public static JsonTypeInfo<TensorSetupConfiguration> SetupConfiguration => Metadata<TensorSetupConfiguration>();
    /// <summary>Serialization metadata for <see cref="TensorSetupChanges"/>.</summary>
    public static JsonTypeInfo<TensorSetupChanges> SetupChanges => Metadata<TensorSetupChanges>();
    /// <summary>Serialization metadata for <see cref="TensorModelPackDefinition"/>.</summary>
    public static JsonTypeInfo<TensorModelPackDefinition> ModelPack => Metadata<TensorModelPackDefinition>();
    /// <summary>Serialization metadata for <see cref="TensorModelPackStatus"/>.</summary>
    public static JsonTypeInfo<TensorModelPackStatus> PackStatus => Metadata<TensorModelPackStatus>();
    /// <summary>Serialization metadata for <see cref="TensorModelObservation"/>.</summary>
    public static JsonTypeInfo<TensorModelObservation> ModelObservation => Metadata<TensorModelObservation>();
    /// <summary>Serialization metadata for <see cref="TensorBackendProbeRequest"/>.</summary>
    public static JsonTypeInfo<TensorBackendProbeRequest> BackendProbeRequest => Metadata<TensorBackendProbeRequest>();
    /// <summary>Serialization metadata for <see cref="TensorBackendAvailability"/>.</summary>
    public static JsonTypeInfo<TensorBackendAvailability> BackendAvailability => Metadata<TensorBackendAvailability>();
    /// <summary>Serialization metadata for <c>TensorRuntimeNativeFile</c>.</summary>
    public static JsonTypeInfo<TensorRuntimeNativeFile> TensorRuntimeNativeFile => Metadata<TensorRuntimeNativeFile>();
    /// <summary>Serialization metadata for <c>TensorRuntimeCandidate</c>.</summary>
    public static JsonTypeInfo<TensorRuntimeCandidate> TensorRuntimeCandidate => Metadata<TensorRuntimeCandidate>();
    /// <summary>Serialization metadata for <c>TensorRuntimePlan</c>.</summary>
    public static JsonTypeInfo<TensorRuntimePlan> TensorRuntimePlan => Metadata<TensorRuntimePlan>();
    /// <summary>Serialization metadata for <c>TensorRuntimeBackendIdentity</c>.</summary>
    public static JsonTypeInfo<TensorRuntimeBackendIdentity> TensorRuntimeBackendIdentity => Metadata<TensorRuntimeBackendIdentity>();
    /// <summary>Serialization metadata for <c>TensorRuntimeState</c>.</summary>
    public static JsonTypeInfo<TensorRuntimeState> TensorRuntimeState => Metadata<TensorRuntimeState>();

    private static JsonTypeInfo<T> Metadata<T>() => CreateOptions().GetTypeInfo<T>();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(TensorJsonContext.Default.Options);
        options.Converters.Add(new TensorRuntimeStateJsonConverter());
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
