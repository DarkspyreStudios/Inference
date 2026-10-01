using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorPackJsonRetirementTests
{
    [Theory]
    [InlineData("malformed")]
    [InlineData("null")]
    [InlineData("mismatch")]
    [InlineData("unknown")]
    public void ActualForeignPackManifestAndFingerprintCallersRetire(string manifestCase)
    {
        string directory = Path.Combine(Path.GetTempPath(), "tensor-pack-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            WeakReference weak = ExerciseGeneration(directory, manifestCase);
            for (int attempt = 0; weak.IsAlive && attempt < 20; ++attempt)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(10);
            }
            Assert.False(weak.IsAlive, "The settled foreign pack/manifest/fingerprint caller graph remains rooted.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration(string directory, string manifestCase)
    {
        string path = typeof(TensorPackJsonRetirementTests).Assembly.Location;
        var generation = new PackGeneration(Path.GetDirectoryName(path)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(path);
            Type caller = fixture.GetType(typeof(TensorPackJsonRetirementTests).FullName!, throwOnError: true)!;
            var settled = (Task)caller.GetMethod(nameof(RunForeignPackAsync), BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [directory, manifestCase])!;
            settled.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Assert.Equal(0, generation.NativeRequests);
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static async Task RunForeignPackAsync(string directory, string manifestCase)
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(TensorPackJsonRetirementTests).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.NotSame(AssemblyLoadContext.Default, generation);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(TensorModelPackManager).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(TensorJson.ModelPack.Type.Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(TensorJson.ModelPack.Options.TypeInfoResolver!.GetType().Assembly));
        AssertInternalMetadataOwner(generation, "PersistedModelManifest");
        AssertInternalMetadataOwner(generation, "TensorManifestFingerprint");
        AssertCanonicalFingerprint();

        byte[] first = "A"u8.ToArray(), second = "BC"u8.ToArray();
        string firstSource = Path.Combine(directory, "first.bin"), secondSource = Path.Combine(directory, "second.bin");
        await File.WriteAllBytesAsync(firstSource, first);
        await File.WriteAllBytesAsync(secondSource, second);
        string firstHash = Convert.ToHexStringLower(SHA256.HashData(first)), secondHash = Convert.ToHexStringLower(SHA256.HashData(second));
        var definition = new TensorModelPackDefinition
        {
            Key = new("fixture-model", "r1"), Capability = TensorCapability.Llm,
            RuntimeArchitecture = "fixture", License = "test",
            SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu },
            Artifacts =
            [
                new() { Role = "z", RelativePath = "first.bin", Length = 1, Sha256 = firstHash, Sources = [new Uri(firstSource)] },
                new() { Role = "a", RelativePath = "second.bin", Length = 2, Sha256 = secondHash, Sources = [new Uri(secondSource)] }
            ]
        };
        string packRoot = Path.Combine(directory, "packs");
        using var http = new HttpClient(new NoNetworkHandler());
        using var manager = new TensorModelPackManager(new([definition]), packRoot, http);
        Assert.Equal(TensorModelPackState.NotInstalled, (await manager.GetStatusAsync(definition.Key)).State);
        await manager.InstallAsync(definition.Key);
        string manifestPath = Assert.Single(Directory.GetFiles(packRoot, "install.json", SearchOption.AllDirectories));
        string expected = "{\"ModelId\":\"fixture-model\",\"Revision\":\"r1\",\"Artifacts\":["
            + "{\"Role\":\"z\",\"RelativePath\":\"first.bin\",\"Length\":1,\"Sha256\":\"" + firstHash + "\"},"
            + "{\"Role\":\"a\",\"RelativePath\":\"second.bin\",\"Length\":2,\"Sha256\":\"" + secondHash + "\"}]}";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), await File.ReadAllBytesAsync(manifestPath));
        TensorModelPackStatus installed = await manager.GetStatusAsync(definition.Key);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(installed.GetType().Assembly));
        Assert.Equal(TensorModelPackState.Installed, installed.State);
        Assert.Null(installed.Detail);
        Assert.Equal(TensorModelPackState.Installed, (await manager.VerifyAsync(definition.Key)).State);
        await using (TensorModelPackLease lease = await manager.AcquireAsync(definition.Key))
        {
            Assert.Equal(first, await File.ReadAllBytesAsync(lease.GetRequiredArtifact("z")));
            Assert.Equal(second, await File.ReadAllBytesAsync(lease.GetRequiredArtifact("a")));
        }

        JsonObject changed = JsonNode.Parse(expected)!.AsObject();
        if (manifestCase == "mismatch") changed["Revision"] = "another-revision";
        if (manifestCase == "unknown") changed["future"] = new JsonObject { ["empty"] = new JsonArray(), ["missing"] = null };
        string replacement = manifestCase switch { "malformed" => "{", "null" => "null", _ => changed.ToJsonString() };
        await File.WriteAllTextAsync(manifestPath, replacement);
        TensorModelPackStatus observed = await manager.GetStatusAsync(definition.Key);
        Assert.Equal(manifestCase == "unknown" ? TensorModelPackState.Installed : TensorModelPackState.Invalid, observed.State);
        Assert.Equal(observed.State, (await manager.VerifyAsync(definition.Key)).State);
        if (manifestCase != "unknown") Assert.False(string.IsNullOrWhiteSpace(observed.Detail));
        await File.WriteAllTextAsync(manifestPath, expected);
        Assert.Equal(TensorModelPackState.Installed, (await manager.VerifyAsync(definition.Key)).State);
        await manager.RemoveAsync(definition.Key);
        Assert.Equal(TensorModelPackState.NotInstalled, (await manager.GetStatusAsync(definition.Key)).State);
        Assert.False(File.Exists(manifestPath));
        manager.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.GetStatusAsync(definition.Key));
    }

    private static void AssertInternalMetadataOwner(AssemblyLoadContext generation, string family)
    {
        Type owner = typeof(TensorJson).Assembly.GetType("Darkspyre.Inference.Tensor.Common.TensorJsonContext", throwOnError: true)!;
        object context = owner.GetProperty("Default")!.GetValue(null)!;
        var metadata = (JsonTypeInfo)owner.GetProperty(family)!.GetValue(context)!;
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Options.TypeInfoResolver!.GetType().Assembly));
    }

    private static void AssertCanonicalFingerprint()
    {
        var candidate = new TensorModelCandidate("fixture", new("owner/model", "rev", "model.bin"),
            new("fixture", null, null), "test", false, false, []);
        var artifact = new TensorModelArtifactDefinition
        {
            Role = "model", RelativePath = "model.bin", Length = 1, Sha256 = new('A', 64),
            Sources = [new("https://example.test/z"), new("https://example.test/a")],
            Provenance = new(new("https://example.test/"), "owner/model", "rev", "model.bin", "test")
        };
        TensorModelPackDefinition pack = TensorResolvedPack.Create(candidate, TensorCapability.Image, "fixture",
            new HashSet<TensorBackendKind> { TensorBackendKind.Cuda, TensorBackendKind.Cpu }, [artifact]);
        const string canonical = """
            {"SourceId":"fixture","Repository":"owner/model","Variant":"model.bin","architecture":"fixture","capability":1,"Backends":[1,6],"Artifacts":[{"Role":"model","RelativePath":"model.bin","Length":1,"Sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Provenance":{"Origin":"https://example.test/","Repository":"owner/model","Revision":"rev","Path":"model.bin","License":"test"},"Sources":["https://example.test/a","https://example.test/z"]}]}
            """;
        Assert.Equal("rev:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), pack.Key.Revision);
        string wire = JsonSerializer.Serialize(pack, TensorJson.ModelPack);
        TensorModelPackDefinition restored = JsonSerializer.Deserialize(wire, TensorJson.ModelPack)!;
        Assert.Equal(wire, JsonSerializer.Serialize(restored, TensorJson.ModelPack));
        Assert.Same(AssemblyLoadContext.GetLoadContext(typeof(TensorJson).Assembly),
            AssemblyLoadContext.GetLoadContext(restored.Artifacts[0].Provenance!.GetType().Assembly));
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This local fixture cannot make network requests.");
    }

    private sealed class PackGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        public int NativeRequests { get; private set; }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            ++NativeRequests;
            throw new DllNotFoundException("This controlled pack generation cannot execute native libraries.");
        }
    }
}
