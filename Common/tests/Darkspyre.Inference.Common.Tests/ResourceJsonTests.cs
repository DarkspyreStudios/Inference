using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.Resources;

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
        { "InferenceResourceDescriptor", "{\"Reference\":" + ReferenceJson + ",\"SessionId\":\"session\",\"FileName\":\"report\\u0022\\\\line\\n.json\",\"ContentType\":\"application/\\u003Cjson\\u003E\",\"Bytes\":9223372036854775807}" },
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
    [InlineData("InferenceResourceReference", "{\"Id\":\"id\"}")]
    [InlineData("InferenceResourceReference", "{\"WorkspaceId\":\"workspace\"}")]
    [InlineData("InferenceResourceSelectionVersion", "{\"WorkspaceId\":\"workspace\",\"SessionId\":\"session\"}")]
    [InlineData("InferenceResourceSelection", "{\"WorkspaceId\":\"workspace\",\"SessionId\":\"session\",\"Revision\":0}")]
    [InlineData("InferenceResourceOperationFailure", "{\"CommitOutcome\":3}")]
    [InlineData("InferenceResourceOperationFailure", "{\"Phase\":4}")]
    [InlineData("InferenceResourceCommitResult", "{\"Selection\":null}")]
    public void MissingRequiredResourceFactsRefuseInsteadOfInventingDefaults(string name, string json)
    {
        JsonTypeInfo metadata = PublicMetadata(typeof(InferenceJson), name);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, metadata));
    }

    [Fact]
    public void InvalidResourceJsonRefusesNegativeCountsNullAndCrossScopeMembership()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSerializer.Deserialize("{\"SessionId\":null,\"ExpectedBytes\":-1}", InferenceJson.InferenceResourceCreate));
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSerializer.Deserialize("{\"WorkspaceId\":\"workspace\",\"SessionId\":\"session\",\"Revision\":-1}", InferenceJson.InferenceResourceSelectionVersion));
        Assert.Throws<ArgumentNullException>(() => JsonSerializer.Deserialize("{\"Resource\":null,\"Selection\":null}", InferenceJson.InferenceResourceCommitResult));
        Assert.Throws<ArgumentNullException>(() => JsonSerializer.Deserialize("{\"WorkspaceId\":\"workspace\",\"SessionId\":\"session\",\"Revision\":0,\"SelectedResources\":null}", InferenceJson.InferenceResourceSelection));
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize("{\"WorkspaceId\":\"workspace\",\"SessionId\":\"session\",\"Revision\":0,\"SelectedResources\":[{\"WorkspaceId\":\"other\",\"Id\":\"id\"}]}", InferenceJson.InferenceResourceSelection));
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize("{\"WorkspaceId\":\"workspace\",\"SessionId\":\"session\",\"Revision\":0,\"SelectedResources\":[" + ReferenceJson + "," + ReferenceJson + "]}", InferenceJson.InferenceResourceSelection));
    }

    [Fact]
    public void EmptySelectionUnknownCountsAndScopedFailureRemainExact()
    {
        const string empty = "{\"WorkspaceId\":\"workspace\",\"SessionId\":\"session\",\"Revision\":0,\"SelectedResources\":[]}";
        var selection = JsonSerializer.Deserialize(empty, InferenceJson.InferenceResourceSelection)!;
        Assert.Empty(selection.SelectedResources);
        Assert.Equal(empty, JsonSerializer.Serialize(selection, InferenceJson.InferenceResourceSelection));
        var descriptor = JsonSerializer.Deserialize(DescriptorJson.Replace("\"Bytes\":0", "\"Bytes\":null", StringComparison.Ordinal), InferenceJson.InferenceResourceDescriptor)!;
        Assert.Null(descriptor.Bytes);
        Assert.Null(JsonSerializer.Deserialize("{}", InferenceJson.InferenceResourceCreate)!.ExpectedBytes);
        Assert.False(JsonSerializer.Deserialize("{}", InferenceJson.InferenceResourceCommitOptions)!.SelectForSession);
        var failure = new InferenceResourceOperationFailure(InferenceResourceOperationPhase.Select, null, InferenceResourceCommitOutcome.NotAttempted)
        {
            SelectionOutcome = InferenceResourceCommitOutcome.Committed,
            SelectionVersion = new("workspace", "session", 0)
        };
        string json = JsonSerializer.Serialize(failure, InferenceJson.InferenceResourceOperationFailure);
        Assert.Equal("{\"Phase\":7,\"Reference\":null,\"CommitOutcome\":0,\"BytesTransferred\":null,\"SelectionOutcome\":2,\"SelectionVersion\":{\"WorkspaceId\":\"workspace\",\"SessionId\":\"session\",\"Revision\":0}}", json);
        Assert.Equal(failure, JsonSerializer.Deserialize(json, InferenceJson.InferenceResourceOperationFailure));
    }

    [Fact]
    public void RuntimeAccessHandlesAndExceptionsHaveNoOwnedPortableMetadata()
    {
        var resolver = InferenceJson.InferenceResourceReference.Options.TypeInfoResolver!;
        foreach (Type type in new[] { typeof(IInferenceResourceAccess), typeof(IInferenceResourceReadHandle),
            typeof(IInferenceResourceWriteHandle), typeof(InferenceResourceOperationException),
            typeof(InferenceResourceOperationCanceledException), typeof(InferenceResourceSelectionConflictException) })
        {
            Assert.Null(resolver.GetTypeInfo(type, InferenceJson.InferenceResourceReference.Options));
        }
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
