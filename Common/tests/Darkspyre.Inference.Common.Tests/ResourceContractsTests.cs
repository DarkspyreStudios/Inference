using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.Tests;

public sealed class ResourceContractsTests
{
    [Fact]
    public void SelectionCopiesOrderedInputAndWithReplacement()
    {
        var first = new InferenceResourceReference("workspace", "original");
        var second = new InferenceResourceReference("workspace", "generated");
        var input = new[] { first, second };
        var selection = new InferenceResourceSelection("workspace", "session", 5, input);
        input[0] = second;
        Assert.Equal(new[] { first, second }, selection.SelectedResources);
        Assert.Throws<NotSupportedException>(() => ((IList<InferenceResourceReference>)selection.SelectedResources)[0] = second);
        var replacement = new[] { second };
        var copy = selection with { SelectedResources = replacement };
        replacement[0] = first;
        Assert.Equal(new[] { second }, copy.SelectedResources);
        var (workspace, session, revision, resources) = selection;
        Assert.Equal("workspace", workspace);
        Assert.Equal("session", session);
        Assert.Equal(5, revision);
        Assert.Equal(new[] { first, second }, resources);
    }

    [Fact]
    public void ScopeVersionDoesNotConfuseEqualRevisionsInDifferentLogicalSessions()
    {
        Assert.NotEqual(new InferenceResourceSelectionVersion("workspace", "one", 5),
            new InferenceResourceSelectionVersion("workspace", "two", 5));
        Assert.NotEqual(new InferenceResourceSelectionVersion("one", "session", 5),
            new InferenceResourceSelectionVersion("two", "session", 5));
        var selection = new InferenceResourceSelection("workspace", "session", long.MaxValue, []);
        Assert.Equal(long.MaxValue, selection.Revision);
        Assert.Empty(selection.SelectedResources);
    }

    [Fact]
    public void CallerCreatedWithCopiesRequireOperationBoundaryValidationAndLeaveSnapshotsUnchanged()
    {
        var reference = new InferenceResourceReference("workspace", "id");
        var descriptor = new InferenceResourceDescriptor(reference, null, null, null, 0);
        var selection = new InferenceResourceSelection("workspace", "session", 0, [reference]);
        Assert.Equal("", (reference with { Id = "" }).Id);
        Assert.Equal(-1, (descriptor with { Bytes = -1 }).Bytes);
        var differentScope = selection with { WorkspaceId = "other" };
        Assert.Equal("other", differentScope.WorkspaceId);
        Assert.Equal("workspace", differentScope.SelectedResources[0].WorkspaceId);
        Assert.Equal("id", reference.Id);
        Assert.Equal(0, descriptor.Bytes);
        Assert.Equal("workspace", selection.WorkspaceId);
        Assert.Equal(new[] { reference }, selection.SelectedResources);
    }

    [Fact]
    public void UnknownMetadataAndUnselectedCommitRemainDistinctFromZeroAndEmpty()
    {
        var reference = new InferenceResourceReference("workspace", "id");
        var unknown = new InferenceResourceDescriptor(reference, null, null, null, null);
        var empty = unknown with { Bytes = 0, FileName = "", ContentType = "" };
        Assert.Null(unknown.Bytes);
        Assert.Equal(0, empty.Bytes);
        Assert.Null(new InferenceResourceCreate(null).ExpectedBytes);
        Assert.False(new InferenceResourceCommitOptions().SelectForSession);
        Assert.Null(new InferenceResourceCommitResult(empty, null).Selection);
        Assert.Equal(new InferenceResourceReference("workspace", "id"), reference);
    }

    [Fact]
    public void MetadataDiagnosticsDoNotExposeNamesOrContentTypes()
    {
        var descriptor = new InferenceResourceDescriptor(new("workspace", "id"), "session", "secret-name", "secret-type", 0);
        var create = new InferenceResourceCreate("session", "secret-name", "secret-type", 0);
        Assert.Equal("InferenceResourceDescriptor { <resource metadata> }", descriptor.ToString());
        Assert.Equal("InferenceResourceCreate { <resource metadata> }", create.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RequiredOpaqueIdsRefuseMissingOrEmpty(string? id)
    {
        Assert.ThrowsAny<ArgumentException>(() => new InferenceResourceReference(id!, "id"));
        Assert.ThrowsAny<ArgumentException>(() => new InferenceResourceReference("workspace", id!));
        Assert.ThrowsAny<ArgumentException>(() => new InferenceResourceSelectionVersion("workspace", id!, 0));
        Assert.ThrowsAny<ArgumentException>(() => new InferenceResourceSelection("workspace", id!, 0, []));
    }

    [Fact]
    public void NegativeCountsAndInvalidMembershipRefuseConstruction()
    {
        var reference = new InferenceResourceReference("workspace", "id");
        Assert.Throws<ArgumentOutOfRangeException>(() => new InferenceResourceDescriptor(reference, null, null, null, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InferenceResourceCreate(null, ExpectedBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InferenceResourceSelectionVersion("workspace", "session", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InferenceResourceOperationFailure(InferenceResourceOperationPhase.Read, reference, InferenceResourceCommitOutcome.NotAttempted, -1));
        Assert.Throws<ArgumentException>(() => new InferenceResourceSelection("workspace", "session", 0, [reference, reference]));
        Assert.Throws<ArgumentException>(() => new InferenceResourceSelection("workspace", "session", 0, [new("other", "id")]));
        Assert.Throws<ArgumentNullException>(() => new InferenceResourceSelection("workspace", "session", 0, [null!]));
        Assert.Throws<ArgumentNullException>(() => new InferenceResourceSelection("workspace", "session", 0, null!));
        Assert.Throws<ArgumentNullException>(() => new InferenceResourceDescriptor(null!, null, null, null, null));
        Assert.Throws<ArgumentNullException>(() => new InferenceResourceCommitResult(null!, null));
    }

    [Fact]
    public void PrimitiveFailureAndRuntimeExceptionsPreserveActualFactsAndOriginalIdentity()
    {
        var inner = new IOException("controlled failure");
        using var cancellation = new CancellationTokenSource();
        var failure = new InferenceResourceOperationFailure(InferenceResourceOperationPhase.Commit,
            new("workspace", "id"), InferenceResourceCommitOutcome.Unknown, 0)
        {
            SelectionOutcome = InferenceResourceCommitOutcome.Unknown
        };
        var operation = new InferenceResourceOperationException(InferenceFailureCode.ExecutionFailed, "Resource commit failed.", failure, inner);
        var canceled = new InferenceResourceOperationCanceledException(failure, cancellation.Token, inner);
        var conflict = new InferenceResourceSelectionConflictException(4, 5);
        Assert.Same(failure, operation.Failure);
        Assert.Same(failure, canceled.Failure);
        Assert.Same(inner, operation.InnerException);
        Assert.Same(inner, canceled.InnerException);
        Assert.Equal(InferenceFailureCode.ExecutionFailed, operation.Code);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.IsAssignableFrom<OperationCanceledException>(canceled);
        Assert.Equal(4, conflict.ExpectedRevision);
        Assert.Equal(5, conflict.ActualRevision);
        Assert.Null(failure.SelectionVersion);
        var unattempted = failure with { SelectionOutcome = InferenceResourceCommitOutcome.NotAttempted };
        Assert.Equal(InferenceResourceCommitOutcome.NotAttempted, unattempted.SelectionOutcome);
    }

    [Fact]
    public void ExactAsyncBoundaryRemainsTaskBasedAndBorrowedAccessHasNoDisposal()
    {
        Type access = typeof(IInferenceResourceAccess);
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(access));
        Assert.False(typeof(IDisposable).IsAssignableFrom(access));
        Assert.Equal(6, access.GetMethods().Count(method => method.Name.EndsWith("Async", StringComparison.Ordinal)));
        foreach (MethodInfo method in access.GetMethods().Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal)))
        {
            Assert.Equal(typeof(Task<>), method.ReturnType.GetGenericTypeDefinition());
            ParameterInfo token = method.GetParameters()[^1];
            Assert.Equal(typeof(CancellationToken), token.ParameterType);
            Assert.True(token.HasDefaultValue);
        }
        MethodInfo commit = typeof(IInferenceResourceWriteHandle).GetMethod("CommitAsync")!;
        Assert.Equal(typeof(Task<InferenceResourceCommitResult>), commit.ReturnType);
        Assert.Equal(typeof(long), commit.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(InferenceResourceCommitOptions), commit.GetParameters()[1].ParameterType);
        Assert.True(commit.GetParameters()[1].HasDefaultValue);
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IInferenceResourceReadHandle)));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IInferenceResourceWriteHandle)));
    }

    [Fact]
    public void RuntimeAuthorityNullsRoundTripButValuesRefuseWithoutCallbacks() =>
        Assert.Equal("refused-without-callbacks", ResourceAuthorityFixture.Exercise());

    [Fact]
    public void ActualForeignRuntimeAuthorityRefusalGenerationRetires()
    {
        WeakReference generation = ExerciseForeignAuthority();
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "Foreign resource interface/converter/fixture metadata remains rooted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeignAuthority()
    {
        var generation = new ResourceFixtureLoadContext();
        var weak = new WeakReference(generation);
        try
        {
            Assembly assembly = generation.LoadFromAssemblyPath(typeof(ResourceAuthorityFixture).Assembly.Location);
            Assert.NotSame(typeof(ResourceAuthorityFixture).Assembly, assembly);
            Assert.Equal(typeof(ResourceAuthorityFixture).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
            Type fixture = assembly.GetType(typeof(ResourceAuthorityFixture).FullName!, throwOnError: true)!;
            Assert.Equal("refused-without-callbacks", fixture.GetMethod("Exercise")!.Invoke(null, null));
            Assembly common = generation.Assemblies.Single(value => value.GetName().Name == typeof(InferenceJson).Assembly.GetName().Name);
            Assert.NotSame(typeof(InferenceJson).Assembly, common);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
        }
        finally
        {
            generation.Unload();
        }
        return weak;
    }

    private sealed class ResourceFixtureLoadContext() : AssemblyLoadContext("Common resource caller fixture", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => assemblyName.Name == typeof(InferenceJson).Assembly.GetName().Name
            ? LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location) : null;
    }
}

public static class ResourceAuthorityFixture
{
    public static string Exercise()
    {
        var authority = new ThrowingResourceAuthority();
        var options = new JsonSerializerOptions();
        foreach (Type type in new[] { typeof(IInferenceResourceAccess), typeof(IInferenceResourceReadHandle), typeof(IInferenceResourceWriteHandle) })
        {
            var converter = InferenceJson.InferenceResourceReference.Options.Converters.Single(value => value.CanConvert(type));
            if (!ReferenceEquals(converter.GetType().Assembly, typeof(InferenceJson).Assembly))
            {
                throw new InvalidOperationException("The fixture did not use Common-owned refusal converters.");
            }
            options.Converters.Add(converter);
        }
        var metadata = new ResourceAuthorityJsonContext(options).ResourceAuthorityEnvelope;
        if (!ReferenceEquals(metadata.Options.TypeInfoResolver!.GetType().Assembly, typeof(ResourceAuthorityFixture).Assembly))
        {
            throw new InvalidOperationException("The fixture did not execute its owned generated metadata.");
        }
        const string nulls = "{\"Access\":null,\"Read\":null,\"Write\":null}";
        if (JsonSerializer.Serialize(new ResourceAuthorityEnvelope(null, null, null), metadata) != nulls
            || JsonSerializer.Serialize(JsonSerializer.Deserialize(nulls, metadata), metadata) != nulls)
        {
            throw new InvalidOperationException("Runtime authority nulls changed shape.");
        }
        ResourceAuthorityEnvelope[] values = [new(authority, null, null), new(null, authority, null), new(null, null, authority)];
        foreach (ResourceAuthorityEnvelope value in values)
        {
            try { JsonSerializer.Serialize(value, metadata); throw new InvalidOperationException("Live authority serialized."); }
            catch (NotSupportedException) { }
        }
        foreach (string property in new[] { "Access", "Read", "Write" })
        {
            try { JsonSerializer.Deserialize("{\"" + property + "\":{}}", metadata); throw new InvalidOperationException("Live authority deserialized."); }
            catch (NotSupportedException) { }
        }
        if (authority.Touched != 0) { throw new InvalidOperationException("Serialization invoked borrowed resource authority."); }
        return "refused-without-callbacks";
    }

    private sealed class ThrowingResourceAuthority : IInferenceResourceAccess, IInferenceResourceReadHandle, IInferenceResourceWriteHandle
    {
        public int Touched { get; private set; }
        public string WorkspaceId => Forbidden<string>();
        public string SessionId => Forbidden<string>();
        public InferenceResourceDescriptor Resource => Forbidden<InferenceResourceDescriptor>();
        public InferenceResourceReference Reference => Forbidden<InferenceResourceReference>();
        public Stream Content => Forbidden<Stream>();
        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => Forbidden<Task<InferenceResourceDescriptor?>>();
        public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => Forbidden<Task<IInferenceResourceReadHandle>>();
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => Forbidden<Task<IInferenceResourceWriteHandle>>();
        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => Forbidden<Task<InferenceResourceSelection>>();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => Forbidden<Task<InferenceResourceSelection>>();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden<Task<InferenceResourceSelection>>();
        public Task<InferenceResourceCommitResult> CommitAsync(long bytesWritten, InferenceResourceCommitOptions? options = null, CancellationToken cancellationToken = default) => Forbidden<Task<InferenceResourceCommitResult>>();
        public ValueTask DisposeAsync() => Forbidden<ValueTask>();
        public override string ToString() => Forbidden<string>();

        private T Forbidden<T>()
        {
            Touched++;
            throw new InvalidOperationException("Borrowed runtime authority was touched.");
        }
    }
}

internal sealed record ResourceAuthorityEnvelope(IInferenceResourceAccess? Access, IInferenceResourceReadHandle? Read, IInferenceResourceWriteHandle? Write);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ResourceAuthorityEnvelope))]
internal sealed partial class ResourceAuthorityJsonContext : JsonSerializerContext;
