using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Common.Tests;

public sealed class ResourceJsonTests
{
    private const string ReferenceJson = "{\"WorkspaceId\":\"workspace\",\"Id\":\"original\"}";
    private const string VersionJson = "{\"WorkspaceId\":\"workspace\",\"SessionId\":\"session\",\"Revision\":5}";
    private const string SelectionJson = "{\"WorkspaceId\":\"workspace\",\"SessionId\":\"session\",\"Revision\":5,\"SelectedResources\":[" + ReferenceJson + "]}";
    private const string DescriptorJson = "{\"Reference\":" + ReferenceJson + ",\"SessionId\":null,\"FileName\":null,\"ContentType\":null,\"Bytes\":0}";

    public static TheoryData<string, string> DataRoots => new()
    {
        { "InferenceResourceReference", ReferenceJson },
        { "InferenceResourceDescriptor", DescriptorJson },
        { "InferenceResourceCreate", "{\"SessionId\":null,\"FileName\":null,\"ContentType\":null,\"ExpectedBytes\":null}" },
        { "InferenceResourceSelectionVersion", VersionJson },
        { "InferenceResourceSelection", SelectionJson },
        { "InferenceResourceCommitOptions", "{\"SelectForSession\":true}" },
        { "InferenceResourceCommitResult", "{\"Resource\":" + DescriptorJson + ",\"Selection\":" + SelectionJson + "}" },
        { "InferenceResourceOperationPhase", "8" },
        { "InferenceResourceCommitOutcome", "3" },
        { "InferenceResourceOperationFailure", "{\"Phase\":4,\"Reference\":" + ReferenceJson + ",\"CommitOutcome\":3,\"BytesTransferred\":0,\"SelectionOutcome\":3,\"SelectionVersion\":null}" }
    };

    [Theory]
    [MemberData(nameof(DataRoots))]
    public void ExactPublicResourceGetterOwnsCompleteRoundTrip(string name, string json)
    {
        JsonTypeInfo metadata = PublicMetadata(typeof(InferenceJson), name);
        Assert.Equal("Darkspyre.Inference.Common.Resources." + name, metadata.Type.FullName);
        Assert.Same(typeof(InferenceJson).Assembly, metadata.Type.Assembly);
        object value = JsonSerializer.Deserialize(json, metadata)!;
        Assert.Equal(json, JsonSerializer.Serialize(value, metadata));
    }

    [Theory]
    [MemberData(nameof(DataRoots))]
    public void ActualForeignResourceDataMetadataRetires(string name, string json)
    {
        WeakReference generation = ExerciseForeignGeneration(name, json);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The actual foreign resource metadata generation remains rooted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeignGeneration(string name, string json)
    {
        var generation = new AssemblyLoadContext("Common resource metadata", isCollectible: true);
        var weak = new WeakReference(generation);
        try
        {
            Assembly assembly = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assert.NotSame(typeof(InferenceJson).Assembly, assembly);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
            Type owner = assembly.GetType("Darkspyre.Inference.Common.InferenceJson", throwOnError: true)!;
            JsonTypeInfo metadata = PublicMetadata(owner, name);
            Assert.Same(assembly, metadata.Type.Assembly);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
            Assert.Same(assembly, metadata.Options.TypeInfoResolver!.GetType().Assembly);
            object value = JsonSerializer.Deserialize(json, metadata)!;
            Assert.Same(assembly, value.GetType().Assembly);
            Assert.Equal(json, JsonSerializer.Serialize(value, metadata));
        }
        finally
        {
            generation.Unload();
        }
        return weak;
    }

    private static JsonTypeInfo PublicMetadata(Type owner, string name)
    {
        PropertyInfo? property = owner.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<JsonTypeInfo>(property.GetValue(null));
    }
}
