using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Common.Tests;

public sealed class CollectibleSerializationTests
{
    private static readonly Type[] MetadataOwners = [typeof(InferenceJson), typeof(ConnectLlmJson), typeof(TensorJson)];

    [Fact]
    public void CombinedSdkMetadataDoesNotKeepItsCollectibleGenerationAlive()
    {
        WeakReference[] generation = RoundTripInCollectibleGeneration(SerializeResultGraph(), SerializeContextGraph());

        // The runtime's unload guidance bounds collection at ten finalizer passes.
        for (int attempt = 0; attempt < 10 && generation.Any(item => item.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.All(generation, item => Assert.False(item.IsAlive));
    }

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

    private static string SerializeContextGraph()
    {
        var reference = new InferenceResourceReference("workspace-1", "resource-1");
        return JsonSerializer.Serialize(new LlmAgentContext
        {
            Revision = 7,
            Entries = [new("entry-1", "turn-1", new("user", "Read the reference")
            {
                Attachments = [new LlmResourceAttachment(reference)]
            })],
            SelectedResources = [reference],
            ResourceSelectionVersion = new("workspace-1", "session-1", 2)
        }, ConnectLlmJson.AgentContext);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RoundTripInCollectibleGeneration(string resultJson, string contextJson)
    {
        var context = new AssemblyLoadContext("SdkMetadataGeneration", isCollectible: true);
        try
        {
            var assemblies = MetadataOwners.ToDictionary(owner => owner.Assembly.GetName().Name!,
                owner => context.LoadFromAssemblyPath(owner.Assembly.Location));
            var references = new List<WeakReference> { new(context) };
            foreach (Type owner in MetadataOwners)
            {
                Assembly assembly = assemblies[owner.Assembly.GetName().Name!];
                Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
                references.Add(new(assembly));
                Type metadataOwner = assembly.GetType(owner.FullName!, throwOnError: true)!;
                foreach (PropertyInfo root in metadataOwner.GetProperties(BindingFlags.Public | BindingFlags.Static)
                    .Where(property => typeof(JsonTypeInfo).IsAssignableFrom(property.PropertyType)))
                {
                    CollectContractGraph((JsonTypeInfo)root.GetValue(null)!, assemblies, references);
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
}
