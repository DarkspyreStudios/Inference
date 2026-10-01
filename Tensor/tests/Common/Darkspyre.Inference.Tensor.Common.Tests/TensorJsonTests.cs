using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorJsonTests
{
    private const string PackJson = """
        {"Key":{"ModelId":"model","Revision":"r1"},"Capability":1,"RuntimeArchitecture":"fixture","License":"test","ProjectUri":null,"SupportedBackends":[1,6],"Artifacts":[{"Role":"model","RelativePath":"model.bin","Length":1,"Sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Sources":[],"Provenance":null}]}
        """;
    private const string ConfigurationJson = """
        {"ImplementationId":"test.tensor","Model":{"ModelId":"model","Revision":"r1"},"Backend":6}
        """;
    private const string RuntimeBuildJson = "{\"Version\":\"version\",\"NativeAbi\":\"abi\",\"GgmlCommit\":\"ggml-commit\"}";
    private const string RuntimeFileJson = "{\"RelativePath\":\"bridge.so\",\"Size\":1,\"Sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}";
    private const string RuntimeCandidateJson = "{\"Identity\":{\"Id\":\"cpu\",\"AbsoluteBridgePath\":\"/unused/bridge.so\",\"Variant\":\"cpu\",\"Build\":"
        + RuntimeBuildJson + ",\"TargetRid\":\"linux-x64\"},\"Backend\":2,\"Files\":[" + RuntimeFileJson + "]}";

    [Fact]
    public void FingerprintPreservesEstablishedCanonicalBytes()
    {
        var candidate = new TensorModelCandidate("fixture", new("owner/model", "rev", "model.bin"),
            new("fixture", null, null), "test", false, false, []);
        var artifact = new TensorModelArtifactDefinition
        {
            Role = "model",
            RelativePath = "model.bin",
            Length = 1,
            Sha256 = new('A', 64),
            Sources = [new("https://example.test/z"), new("https://example.test/a")],
            Provenance = new(new("https://example.test/"), "owner/model", "rev", "model.bin", "test")
        };
        TensorModelPackDefinition pack = TensorResolvedPack.Create(candidate, TensorCapability.Image, "fixture",
            new HashSet<TensorBackendKind> { TensorBackendKind.Cuda, TensorBackendKind.Cpu }, [artifact]);
        const string manifest = """
            {"SourceId":"fixture","Repository":"owner/model","Variant":"model.bin","architecture":"fixture","capability":1,"Backends":[1,6],"Artifacts":[{"Role":"model","RelativePath":"model.bin","Length":1,"Sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Provenance":{"Origin":"https://example.test/","Repository":"owner/model","Revision":"rev","Path":"model.bin","License":"test"},"Sources":["https://example.test/a","https://example.test/z"]}]}
            """;
        Assert.Equal("rev:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), pack.Key.Revision);
    }

    [Fact]
    public void OwnedModelMetadataPreservesWireNamesNumericEnumsNullAndEmptyData()
    {
        TensorModelPackDefinition pack = JsonSerializer.Deserialize(PackJson, TensorJson.ModelPack)!;
        Assert.Equal(TensorCapability.Image, pack.Capability);
        Assert.Equal([TensorBackendKind.Cpu, TensorBackendKind.Cuda], pack.SupportedBackends.Order());
        Assert.Null(pack.ProjectUri);
        Assert.Empty(pack.Artifacts[0].Sources);
        Assert.Null(pack.Artifacts[0].Provenance);
        Assert.Equal(PackJson, JsonSerializer.Serialize(pack, TensorJson.ModelPack));
    }

    [Fact]
    public void UnknownEnumValuesAndUnknownPropertiesRemainData()
    {
        TensorSetupConfiguration configuration = JsonSerializer.Deserialize(
            ConfigurationJson.Replace("\"Backend\":6", "\"Backend\":999,\"future\":true", StringComparison.Ordinal), TensorJson.SetupConfiguration)!;
        Assert.Equal((TensorBackendKind)999, configuration.Backend);
        Assert.Equal(ConfigurationJson.Replace("\"Backend\":6", "\"Backend\":999", StringComparison.Ordinal),
            JsonSerializer.Serialize(configuration, TensorJson.SetupConfiguration));
    }

    public static TheoryData<string, string> CollectibleFamilies => new()
    {
        { "SetupConfiguration", ConfigurationJson },
        { "SetupChanges", "{\"Identity\":{\"Intent\":0,\"Id\":\"entry\",\"ExpectedVersion\":null},\"Configuration\":"
            + ConfigurationJson + ",\"RequiresUnload\":false,\"Definition\":" + PackJson + "}" },
        { "ModelPack", PackJson },
        { "PackStatus", "{\"Key\":{\"ModelId\":\"model\",\"Revision\":\"r1\"},\"State\":0,\"Detail\":null}" },
        { "ModelObservation", "{\"Model\":null,\"Backend\":null,\"State\":5,\"ObservedAt\":\"1970-01-01T00:00:00+00:00\"}" },
        { "BackendProbeRequest", "{\"OperationId\":\"11111111-1111-1111-1111-111111111111\",\"ImplementationId\":\"test\",\"RequestedBackend\":2,\"TargetRid\":\"linux-x64\",\"ExpectedTensorSharpBuild\":null,\"NativeCandidates\":[]}" },
        { "BackendAvailability", "{\"OperationId\":\"11111111-1111-1111-1111-111111111111\",\"ImplementationId\":\"test\",\"RequestedBackend\":2,\"State\":4,\"Reason\":\"restart required\",\"ObservedAt\":\"1970-01-01T00:00:00+00:00\",\"TargetRid\":\"linux-x64\",\"ActualBackend\":null,\"TensorSharpBuild\":null,\"LoadedCandidateId\":null,\"IsInitialized\":false,\"DetectedDeviceCapabilities\":[],\"CandidatePresence\":[],\"CandidateDiagnostics\":[{\"CandidateId\":\"cpu\",\"Phase\":2,\"Code\":\"initialization-failed\",\"Message\":\"restart required\",\"NativeStateMayRemain\":true}]}" },
        { "TensorRuntimeNativeFile", RuntimeFileJson },
        { "TensorRuntimeCandidate", RuntimeCandidateJson },
        { "TensorRuntimePlan", "{\"Build\":" + RuntimeBuildJson + ",\"TargetRid\":\"linux-x64\",\"Candidates\":[" + RuntimeCandidateJson + "]}" },
        { "TensorRuntimeBackendIdentity", "{\"Backend\":2,\"Build\":" + RuntimeBuildJson + ",\"TargetRid\":\"linux-x64\",\"Variant\":\"cpu\",\"CandidateId\":null,\"NativeSourceCommit\":\"native-source\"}" },
        { "TensorRuntimeState", "4" },
        { "TensorRuntimeShutdownState", "3" },
        { "TensorRuntimeDiagnostic", "{\"Code\":\"native-state-remains\",\"Message\":\"Restart is required.\"}" },
        { "TensorRuntimeShutdownResult", "{\"State\":2,\"OperationId\":\"11111111-1111-1111-1111-111111111111\",\"ObservedAt\":\"1970-01-01T00:00:00+00:00\",\"OutstandingLeases\":2,\"NativeStateMayRemain\":true,\"Diagnostics\":[{\"Code\":\"busy\",\"Message\":\"Owned operations remain active.\"}]}" },
        { "PersistedModelManifest", "{\"ModelId\":\"model\",\"Revision\":\"r1\",\"Artifacts\":[{\"Role\":\"model\",\"RelativePath\":\"model.bin\",\"Length\":1,\"Sha256\":\"hash\"}]}" },
        { "TensorManifestFingerprint", "{\"SourceId\":\"fixture\",\"Repository\":null,\"Variant\":\"model.bin\",\"architecture\":\"fixture\",\"capability\":1,\"Backends\":[1],\"Artifacts\":[]}" }
    };

    [Theory]
    [MemberData(nameof(CollectibleFamilies))]
    public void RealLibraryGenerationCollectsAfterOwnedSerializerMetadataRetires(string family, string json)
    {
        WeakReference generation = ExerciseCollectibleGeneration(family, json);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The Tensor generation remains rooted after its serialization work and metadata retire.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseCollectibleGeneration(string family, string json)
    {
        string assemblyPath = typeof(TensorJson).Assembly.Location;
        var generation = new TensorGeneration(Path.GetDirectoryName(assemblyPath)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly tensor = generation.LoadFromAssemblyPath(assemblyPath);
            Assert.NotSame(typeof(TensorJson).Assembly, tensor);
            Assert.Equal(typeof(TensorJson).Assembly.ManifestModule.ModuleVersionId, tensor.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(tensor));
            Type owner = tensor.GetType("Darkspyre.Inference.Tensor.Common.TensorJson", throwOnError: true)!;
            JsonTypeInfo metadata;
            if (owner.GetProperty(family) is { } exported)
            {
                Assert.Equal(tensor.ManifestModule.ModuleVersionId, exported.GetMethod!.Module.ModuleVersionId);
                metadata = (JsonTypeInfo)exported.GetValue(null)!;
            }
            else
            {
                Assert.DoesNotContain("TensorRuntime", family, StringComparison.Ordinal);
                Type context = tensor.GetType("Darkspyre.Inference.Tensor.Common.TensorJsonContext", throwOnError: true)!;
                object instance = context.GetProperty("Default")!.GetValue(null)!;
                metadata = (JsonTypeInfo)context.GetProperty(family)!.GetValue(instance)!;
            }
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
            Assert.Same(tensor, metadata.Type.Assembly);
            Assert.Equal("TensorJsonContext", metadata.Options.TypeInfoResolver!.GetType().Name);
            Assert.Same(tensor, metadata.Options.TypeInfoResolver.GetType().Assembly);
            object value = JsonSerializer.Deserialize(json, metadata)!;
            string written = JsonSerializer.Serialize(value, metadata);
            if (family.StartsWith("TensorRuntime", StringComparison.Ordinal)) Assert.Equal(json, written);
            Assert.NotNull(JsonSerializer.Deserialize(written, metadata));
        }
        finally
        {
            generation.Unload();
        }
        return weak;
    }

    private sealed class TensorGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
