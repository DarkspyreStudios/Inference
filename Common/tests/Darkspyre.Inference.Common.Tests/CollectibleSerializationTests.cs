using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.Audio;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.Realtime;
using Darkspyre.Inference.Connect.Resources;
using Darkspyre.Inference.Connect.STT;
using Darkspyre.Inference.Connect.TTS;
using Darkspyre.Inference.Connect.Video;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Common.Tests;

public sealed class CollectibleSerializationTests
{
    private static readonly (Type Owner, int Roots)[] MetadataOwners =
    [
        (typeof(InferenceJson), 183),
        (typeof(ConnectLlmJson), 167),
        (typeof(ConnectImageJson), 21),
        (typeof(ConnectVideoJson), 22),
        (typeof(ConnectTtsJson), 21),
        (typeof(ConnectSttJson), 5),
        (typeof(ConnectRealtimeJson), 48),
        (typeof(ConnectResourcesJson), 28),
        (typeof(ConnectAudioJson), 9),
        (typeof(TensorJson), 15)
    ];

    public static IEnumerable<object[]> MetadataCases =>
        MetadataOwners.Select(item => new object[] { item.Owner, item.Roots });

    [Theory]
    [MemberData(nameof(MetadataCases))]
    public void OwnedMetadataDoesNotKeepItsCollectibleGenerationAlive(Type owner, int expectedRoots)
        => AssertGenerationCollected(RoundTripInCollectibleGeneration([(owner, expectedRoots)], SerializeResultGraph()));

    [Fact]
    public void CombinedSdkMetadataDoesNotKeepItsCollectibleGenerationAlive()
        => AssertGenerationCollected(RoundTripInCollectibleGeneration(MetadataOwners, SerializeResultGraph()));

    private static string SerializeResultGraph()
    {
        var reference = new InferenceResourceReference("workspace-1", "resource-1");
        return JsonSerializer.Serialize(new LlmInferenceResult
        {
            Text = "done",
            Status = LlmCompletionStatus.Completed,
            Usage = new(7, 3, 10),
            ToolCalls = [new LlmShellCall("call-1", ["pwd"], null, null)],
            GeneratedResources = [new(
                new(reference, "session-1", "result.json", "application/json", 2),
                new("workspace-1", "session-1", 1, [reference]))],
            Provenance = new() { ImplementationId = "test-provider", ModelId = "reported-model" }
        }, InferenceJson.LlmResult);
    }

    private static void AssertGenerationCollected(WeakReference[] generation)
    {
        // The runtime's unload guidance bounds collection at ten finalizer passes.
        for (int attempt = 0; attempt < 10 && generation.Any(item => item.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.All(generation, item => Assert.False(item.IsAlive));
    }

    [Fact]
    public void RejectingAnUnknownCollectibleTypeDoesNotKeepItAlive()
    {
        WeakReference type = AttemptUnsupportedSerialization();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(type.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RoundTripInCollectibleGeneration((Type Owner, int Roots)[] owners, string json)
    {
        var context = new AssemblyLoadContext("SdkMetadataGeneration", isCollectible: true);
        try
        {
            var assemblies = owners.ToDictionary(item => item.Owner.Assembly.GetName().Name!,
                item => context.LoadFromAssemblyPath(item.Owner.Assembly.Location));
            var references = new List<WeakReference> { new(context) };
            foreach ((Type owner, int expectedRoots) in owners)
            {
                Assembly assembly = assemblies[owner.Assembly.GetName().Name!];
                Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
                references.Add(new(assembly));
                Type metadataOwner = assembly.GetType(owner.FullName!, throwOnError: true)!;
                PropertyInfo[] roots = metadataOwner.GetProperties(BindingFlags.Public | BindingFlags.Static)
                    .Where(property => typeof(JsonTypeInfo).IsAssignableFrom(property.PropertyType)).ToArray();
                Assert.Equal(expectedRoots, roots.Length);
                foreach (PropertyInfo root in roots)
                {
                    var typeInfo = (JsonTypeInfo)root.GetValue(null)!;
                    typeInfo.MakeReadOnly();
                    _ = typeInfo.Properties.Count;
                    Assert.Same(typeInfo, typeInfo.Options.GetTypeInfo(typeInfo.Type));
                    if (assemblies.TryGetValue(typeInfo.Type.Assembly.GetName().Name!, out Assembly? expected))
                        Assert.Same(expected, typeInfo.Type.Assembly);
                    references.Add(new(typeInfo));
                    references.Add(new(typeInfo.Options));
                }
                if (owner == typeof(InferenceJson))
                {
                    var metadata = (JsonTypeInfo)metadataOwner.GetProperty(nameof(InferenceJson.LlmResult))!.GetValue(null)!;
                    Assert.Same(assembly, metadata.Type.Assembly);

                    object value = JsonSerializer.Deserialize(json, metadata)!;
                    Assert.Same(assembly, value.GetType().Assembly);
                    Assert.Equal(json, JsonSerializer.Serialize(value, metadata));

                    references.Add(new(metadata));
                    references.Add(new(value));
                }
            }
            return references.ToArray();
        }
        finally
        {
            context.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttemptUnsupportedSerialization()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("UnknownContract"), AssemblyBuilderAccess.RunAndCollect);
        TypeBuilder builder = assembly.DefineDynamicModule("Contracts").DefineType("UnknownContract", TypeAttributes.Public);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        Type type = builder.CreateType()!;
        object value = Activator.CreateInstance(type)!;

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(value, type, InferenceJson.LlmRequest.Options));

        return new WeakReference(type);
    }
}
