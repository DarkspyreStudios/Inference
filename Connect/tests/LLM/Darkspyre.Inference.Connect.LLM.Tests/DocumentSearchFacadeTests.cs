using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class DocumentSearchFacadeTests
{
    private static DocumentSearchRequest Request => new("query", ["collection"]);

    [Fact]
    public void FacadeExposesTheExactExistingPublicSignature()
    {
        Assert.Contains(typeof(IDocumentSearchClient), typeof(LlmProviderService).GetInterfaces());
        MethodInfo method = SearchMethod();
        Assert.Equal(typeof(Task<DocumentSearchResult>), method.ReturnType);
        Assert.Equal(["connectionId", "request", "cancellationToken"], method.GetParameters().Select(parameter => parameter.Name));
        Assert.True(method.GetParameters()[2].HasDefaultValue);
    }

    [Fact]
    public async Task DependencyInjectionAliasUsesTheSameScopedFacadeWithoutEffects()
    {
        var source = new UnusedSource();
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(source);
        services.AddLlmConnect();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<LlmProviderService>();
        Assert.Same(facade, scope.ServiceProvider.GetRequiredService<IDocumentSearchClient>());
        await using AsyncServiceScope other = provider.CreateAsyncScope();
        Assert.NotSame(facade, other.ServiceProvider.GetRequiredService<IDocumentSearchClient>());
        Assert.Equal(0, source.Calls);
        Assert.False(source.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableSearchPreservesAdmissionCancellationAndRetirement(bool direct)
    {
        var source = new UnusedSource();
        var authenticator = new UnusedAuthenticator();
        var facade = new LlmProviderService(source, [], [authenticator]);
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(facade, direct));
            using var caller = new CancellationTokenSource();
            caller.Cancel();
            OperationCanceledException cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Invoke(facade, direct, caller.Token));
            Assert.Equal(caller.Token, cancellation.CancellationToken);
            Task first = facade.DisposeAsync().AsTask();
            Task second = facade.DisposeAsync().AsTask();
            Assert.Same(first, second);
            await first;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Invoke(facade, direct));
            Assert.Equal(0, source.Calls);
            Assert.Equal(0, authenticator.Calls);
            Assert.False(source.Disposed);
            Assert.False(authenticator.Disposed);
        }
        finally { await facade.DisposeAsync(); }
    }

    [Fact]
    public void OwnedOptionsRefuseLiveSearchClientsWithoutInspectingThem()
    {
        var client = new UnusedSearchClient();
        JsonTypeInfo<DocumentSearchRuntimeEnvelope> metadata = RuntimeMetadata();
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new DocumentSearchRuntimeEnvelope { Client = client }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Client\":{}}", metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Client\":{\"authority\":true}}", metadata));
        Assert.Null(JsonSerializer.Deserialize("{\"Client\":null}", metadata)!.Client);
        var empty = new DocumentSearchRuntimeEnvelope { Request = Request };
        DocumentSearchRuntimeEnvelope restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(empty, metadata), metadata)!;
        Assert.Null(restored.Client);
        Assert.Equal("query", restored.Request!.Query);
        Assert.Equal(["collection"], restored.Request.CollectionIds);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public void ActualForeignFacadeCallerAndOwnedMetadataCollectAfterExplicitRetirement()
    {
        WeakReference weak = ExerciseForeign();
        for (int attempt = 0; weak.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(DocumentSearchFacadeTests).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            foreach (Assembly original in new[] { typeof(InferenceJson).Assembly, typeof(ConnectLlmJson).Assembly, typeof(DocumentSearchFacadeTests).Assembly })
            {
                Assembly foreign = generation.LoadFromAssemblyPath(original.Location);
                Assert.NotSame(original, foreign);
                Assert.Equal(original.ManifestModule.ModuleVersionId, foreign.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(foreign));
            }
            Type fixture = generation.Assemblies.Single(assembly => assembly.GetName().Name == typeof(DocumentSearchFacadeTests).Assembly.GetName().Name)
                .GetType(typeof(DocumentSearchFacadeTests).FullName!, true)!;
            Task operation = (Task)fixture.GetMethod(nameof(ForeignCallerAsync), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            operation.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static async Task ForeignCallerAsync()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(DocumentSearchFacadeTests).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmProviderService).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(InferenceJson).Assembly));
        Assert.Same(typeof(ConnectLlmJson).Assembly, ConnectLlmJson.DocumentSearchRequest.Type.Assembly);
        Assert.Same(typeof(ConnectLlmJson).Assembly, ConnectLlmJson.DocumentSearchResult.Type.Assembly);
        Assert.Same(typeof(ConnectLlmJson).Assembly, RuntimeMetadata().Properties.Single(property => property.Name == "Client").PropertyType.Assembly);
        Assert.Same(typeof(DocumentSearchFacadeTests).Assembly, RuntimeMetadata().Type.Assembly);
        var fixture = new DocumentSearchFacadeTests();
        fixture.FacadeExposesTheExactExistingPublicSignature();
        // Full AddLlmConnect DI retirement is a separate handler-timer qualification gate.
        await fixture.UnavailableSearchPreservesAdmissionCancellationAndRetirement(false);
        await fixture.UnavailableSearchPreservesAdmissionCancellationAndRetirement(true);
        fixture.OwnedOptionsRefuseLiveSearchClientsWithoutInspectingThem();
    }

    private static MethodInfo SearchMethod() => Assert.IsAssignableFrom<MethodInfo>(
        typeof(LlmProviderService).GetMethod("SearchAsync", [typeof(string), typeof(DocumentSearchRequest), typeof(CancellationToken)]));

    private static Task<DocumentSearchResult> Invoke(LlmProviderService facade, bool direct, CancellationToken cancellationToken = default)
    {
        if (!direct) return ((IDocumentSearchClient)facade).SearchAsync("connection", Request, cancellationToken);
        return facade.SearchAsync("connection", Request, cancellationToken);
    }

    private static JsonTypeInfo<DocumentSearchRuntimeEnvelope> RuntimeMetadata()
    {
        var options = new JsonSerializerOptions(ConnectLlmJson.DocumentSearchRequest.Options)
        { TypeInfoResolver = DocumentSearchRuntimeContext.Default };
        options.MakeReadOnly();
        return options.GetTypeInfo<DocumentSearchRuntimeEnvelope>();
    }

    private sealed class UnusedSource : ILlmConnectionSource, IDisposable
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Unavailable search cannot resolve a connection."); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Unavailable search cannot save a credential."); }
        public void Dispose() => Disposed = true;
    }

    private sealed class UnusedAuthenticator : ILlmAuthenticator, IDisposable
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Unavailable search cannot refresh a credential."); }
        public void Dispose() => Disposed = true;
    }

    private sealed class UnusedSearchClient : IDocumentSearchClient
    {
        internal int Calls { get; private set; }
        public Task<DocumentSearchResult> SearchAsync(string connectionId, DocumentSearchRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Serialization cannot perform a search."); }
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (!name.Name!.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal)) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}

public sealed record DocumentSearchRuntimeEnvelope
{
    public IDocumentSearchClient? Client { get; init; }
    public DocumentSearchRequest? Request { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DocumentSearchRuntimeEnvelope))]
internal sealed partial class DocumentSearchRuntimeContext : JsonSerializerContext;
