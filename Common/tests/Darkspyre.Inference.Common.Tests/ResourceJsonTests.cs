using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public void ExceptionsHaveNoOwnedPortableMetadata()
    {
        var resolver = InferenceJson.InferenceResourceReference.Options.TypeInfoResolver!;
        foreach (Type type in new[] { typeof(InferenceResourceOperationException),
            typeof(InferenceResourceOperationCanceledException), typeof(InferenceResourceSelectionConflictException) })
        {
            Assert.Null(resolver.GetTypeInfo(type, InferenceJson.InferenceResourceReference.Options));
        }
    }

    [Fact]
    public void RuntimeAuthorityRefusesDirectAndNestedSerializationWithoutEffects() => Assert.Equal(0, ExerciseRuntimeRefusal());

    public static int ExerciseRuntimeRefusal()
    {
        var authority = new UntouchableAuthority();
        JsonSerializerOptions options = InferenceJson.InferenceResourceReference.Options;
        var consumerOptions = new JsonSerializerOptions(options) { TypeInfoResolver = ResourceRuntimeContext.Default };
        consumerOptions.MakeReadOnly();
        foreach (Type type in new[] { typeof(IInferenceResourceAccess), typeof(IInferenceResourceReadHandle), typeof(IInferenceResourceWriteHandle) })
        {
            JsonTypeInfo metadata = consumerOptions.GetTypeInfo(type);
            Assert.Equal(JsonTypeInfoKind.None, metadata.Kind);
            Assert.Same(typeof(InferenceJson).Assembly, metadata.Converter.GetType().Assembly);
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(authority, metadata));
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{}", metadata));
            Assert.Equal("null", JsonSerializer.Serialize((object?)null, metadata));
            Assert.Null(JsonSerializer.Deserialize("null", metadata));
            Assert.Null(typeof(InferenceJson).GetProperty(type.Name));
        }
        JsonTypeInfo<ResourceRuntimeEnvelope> consumer = consumerOptions.GetTypeInfo<ResourceRuntimeEnvelope>();
        foreach (ResourceRuntimeEnvelope envelope in new[]
        {
            new ResourceRuntimeEnvelope { Access = authority },
            new ResourceRuntimeEnvelope { Read = authority },
            new ResourceRuntimeEnvelope { Write = authority }
        }) Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(envelope, consumer));
        foreach (string property in new[] { "Access", "Read", "Write" })
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"" + property + "\":{}}", consumer));
        const string nulls = "{\"Access\":null,\"Read\":null,\"Write\":null}";
        Assert.Equal(nulls, JsonSerializer.Serialize(JsonSerializer.Deserialize(nulls, consumer)!, consumer));
        return authority.Effects;
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
        var generation = new ResourceGeneration(Path.GetDirectoryName(typeof(InferenceJson).Assembly.Location)!);
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
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(ResourceJsonTests).Assembly.Location);
            Assert.NotSame(typeof(ResourceJsonTests).Assembly, fixture);
            Assert.Equal(typeof(ResourceJsonTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            MethodInfo refusal = fixture.GetType(typeof(ResourceJsonTests).FullName!, true)!.GetMethod(nameof(ExerciseRuntimeRefusal))!;
            Assert.Same(fixture, refusal.Module.Assembly);
            Assert.Equal(0, refusal.Invoke(null, null));
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

    private sealed class ResourceGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class UntouchableAuthority : IInferenceResourceAccess, IInferenceResourceReadHandle, IInferenceResourceWriteHandle
    {
        public int Effects { get; private set; }
        private InvalidOperationException Touched() { Effects++; return new("Runtime authority was inspected."); }
        public string WorkspaceId => throw Touched();
        public string SessionId => throw Touched();
        public InferenceResourceDescriptor Resource => throw Touched();
        public InferenceResourceReference Reference => throw Touched();
        public Stream Content => throw Touched();
        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw Touched();
        public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw Touched();
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => throw Touched();
        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => throw Touched();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw Touched();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw Touched();
        public Task<InferenceResourceCommitResult> CommitAsync(long bytesWritten, InferenceResourceCommitOptions? options = null, CancellationToken cancellationToken = default) => throw Touched();
        public ValueTask DisposeAsync() => throw Touched();
        public override string ToString() => throw Touched();
    }
}

public sealed record ResourceRuntimeEnvelope
{
    public IInferenceResourceAccess? Access { get; init; }
    public IInferenceResourceReadHandle? Read { get; init; }
    public IInferenceResourceWriteHandle? Write { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ResourceRuntimeEnvelope))]
internal sealed partial class ResourceRuntimeContext : JsonSerializerContext;
