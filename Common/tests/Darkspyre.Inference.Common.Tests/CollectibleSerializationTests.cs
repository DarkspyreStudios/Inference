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
using Darkspyre.Inference.Connect.TTS;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Common.Tests;

public sealed class CollectibleSerializationTests
{
    private static readonly (Type Owner, int Roots)[] MetadataOwners =
    [
        (typeof(InferenceJson), 183),
        (typeof(ConnectLlmJson), 123),
        (typeof(ConnectImageJson), 21),
        (typeof(ConnectTtsJson), 20),
        (typeof(ConnectAudioJson), 9),
        (typeof(TensorJson), 15)
    ];

    public static IEnumerable<object[]> MetadataCases =>
        MetadataOwners.Select(item => new object[] { item.Owner, item.Roots });

    [Theory]
    [MemberData(nameof(MetadataCases))]
    public void OwnedMetadataDoesNotKeepItsCollectibleGenerationAlive(Type owner, int expectedRoots)
        => AssertGenerationCollected(RoundTripInCollectibleGeneration([(owner, expectedRoots)], SerializeResultGraph(), SerializeContextGraph()));

    [Fact]
    public void CombinedSdkMetadataDoesNotKeepItsCollectibleGenerationAlive()
        => AssertGenerationCollected(RoundTripInCollectibleGeneration(MetadataOwners, SerializeResultGraph(), SerializeContextGraph()));

    private static string SerializeResultGraph()
    {
        var reference = new InferenceResourceReference("workspace-1", "resource-1");
        string json = JsonSerializer.Serialize(new LlmInferenceResult
        {
            Text = "done",
            Status = LlmCompletionStatus.Completed,
            Usage = new(7, 3, 10)
            {
                HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [LlmHostedToolKind.WebSearch] = 2 },
                HostedToolCallCount = 2,
                HostedSourcesUsed = 3
            },
            ToolCalls = [new LlmShellCall("call-1", ["pwd"], null, null)],
            GeneratedResources = [new(
                new(reference, "session-1", "result.json", "application/json", 2),
                new("workspace-1", "session-1", 1, [reference]))],
            Provenance = new() { ImplementationId = "test-provider", ModelId = "reported-model" }
        }, InferenceJson.LlmResult);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement usage = document.RootElement.GetProperty("Usage");
        Assert.Equal(2, usage.GetProperty("HostedToolCalls").GetProperty(nameof(LlmHostedToolKind.WebSearch)).GetInt32());
        Assert.Equal(2, usage.GetProperty("HostedToolCallCount").GetInt32());
        Assert.Equal(3, usage.GetProperty("HostedSourcesUsed").GetInt32());
        return json;
    }

    private static string SerializeContextGraph()
    {
        var reference = new InferenceResourceReference("workspace-1", "resource-1");
        string json = JsonSerializer.Serialize(new LlmAgentContext
        {
            Revision = 7,
            Entries = [new("entry-1", "turn-1", new("user", "Read the reference")
            {
                Attachments = [new LlmResourceAttachment(reference)]
            })],
            SelectedResources = [reference],
            ResourceSelectionVersion = new("workspace-1", "session-1", 2)
        }, ConnectLlmJson.AgentContext);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement context = document.RootElement;
        Assert.Equal(3, context.GetProperty("FormatVersion").GetInt32());
        Assert.Equal(7, context.GetProperty("Revision").GetInt64());
        JsonElement selected = Assert.Single(context.GetProperty("SelectedResources").EnumerateArray());
        Assert.Equal(reference.WorkspaceId, selected.GetProperty("WorkspaceId").GetString());
        Assert.Equal(reference.Id, selected.GetProperty("Id").GetString());
        JsonElement version = context.GetProperty("ResourceSelectionVersion");
        Assert.Equal("workspace-1", version.GetProperty("WorkspaceId").GetString());
        Assert.Equal("session-1", version.GetProperty("SessionId").GetString());
        Assert.Equal(2, version.GetProperty("Revision").GetInt64());
        return json;
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
    private static WeakReference[] RoundTripInCollectibleGeneration((Type Owner, int Roots)[] owners, string resultJson, string contextJson)
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
                    CollectContractGraph(typeInfo, assemblies, references);
                }
                if (owner == typeof(InferenceJson) || owner == typeof(ConnectLlmJson))
                {
                    string root = owner == typeof(InferenceJson) ? nameof(InferenceJson.LlmResult) : nameof(ConnectLlmJson.AgentContext);
                    string json = owner == typeof(InferenceJson) ? resultJson : contextJson;
                    var metadata = (JsonTypeInfo)metadataOwner.GetProperty(root)!.GetValue(null)!;
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

    private static void CollectContractGraph(JsonTypeInfo root, Dictionary<string, Assembly> assemblies,
        List<WeakReference> references)
    {
        var pending = new Queue<JsonTypeInfo>();
        var visited = new HashSet<JsonTypeInfo>(ReferenceEqualityComparer.Instance);
        pending.Enqueue(root);
        while (pending.TryDequeue(out JsonTypeInfo? metadata))
        {
            if (!visited.Add(metadata)) continue;
            metadata.MakeReadOnly();
            Assert.Same(root.Options, metadata.Options);
            Assert.Same(metadata, root.Options.GetTypeInfo(metadata.Type));
            if (assemblies.TryGetValue(metadata.Type.Assembly.GetName().Name!, out Assembly? expected))
                Assert.Same(expected, metadata.Type.Assembly);
            references.Add(new(metadata));
            references.Add(new(metadata.Options));

            foreach (JsonPropertyInfo property in metadata.Properties)
            {
                // An explicit property converter owns its wire representation, not the default type graph.
                if (property.CustomConverter is null && (property.Get is not null || property.Set is not null))
                    pending.Enqueue(root.Options.GetTypeInfo(property.PropertyType));
            }
            if (metadata.ElementType is { } element)
                pending.Enqueue(root.Options.GetTypeInfo(element));
            if (metadata.KeyType is { } key)
                pending.Enqueue(root.Options.GetTypeInfo(key));
            if (metadata.PolymorphismOptions is { } polymorphism)
                foreach (JsonDerivedType derived in polymorphism.DerivedTypes)
                    pending.Enqueue(root.Options.GetTypeInfo(derived.DerivedType));
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
