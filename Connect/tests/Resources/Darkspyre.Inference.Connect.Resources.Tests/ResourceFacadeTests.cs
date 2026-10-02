using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.Video;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Resources.Tests;

public sealed class ResourceFacadeTests
{
    [Fact]
    public void ConstructorRequiresOnlyTheBorrowedConnectionAccess()
    {
        ConstructorInfo constructor = Assert.Single(typeof(LlmResourceProviderService).GetConstructors());
        ParameterInfo parameter = Assert.Single(constructor.GetParameters());
        Assert.Equal(typeof(LlmConnectionAccess), parameter.ParameterType);
        Assert.Equal("connectionAccess", parameter.Name);
        Assert.Throws<ArgumentNullException>(() => new LlmResourceProviderService(null!));
        Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddLlmResources(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task EveryOperationRefusesBeforeAnyBorrowedEffect(int operation)
    {
        using var source = new Source();
        using var authenticator = new Authenticator();
        using var resources = new Resources();
        await using var access = new LlmConnectionAccess(source, [authenticator]);
        await using var facade = new LlmResourceProviderService(access);
        await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(facade, operation, resources));
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, authenticator.Calls);
        Assert.Equal(0, resources.Calls);
        Assert.False(source.Disposed);
        Assert.False(authenticator.Disposed);
        Assert.False(resources.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task EveryOperationPreservesCanceledAndRetiredEntryPrecedence(int operation)
    {
        using var source = new Source();
        using var resources = new Resources();
        await using var access = new LlmConnectionAccess(source, []);
        var facade = new LlmResourceProviderService(access);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        OperationCanceledException before = await Assert.ThrowsAsync<OperationCanceledException>(
            () => Invoke(facade, operation, resources, canceled.Token));
        Assert.Equal(canceled.Token, before.CancellationToken);
        await facade.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Invoke(facade, operation, resources));
        OperationCanceledException after = await Assert.ThrowsAsync<OperationCanceledException>(
            () => Invoke(facade, operation, resources, canceled.Token));
        Assert.Equal(canceled.Token, after.CancellationToken);
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, resources.Calls);
    }

    [Fact]
    public async Task RetirementIsSharedAndBorrowedAccessSurvives()
    {
        using var source = new Source();
        using var authenticator = new Authenticator();
        await using var access = new LlmConnectionAccess(source, [authenticator]);
        var facade = new LlmResourceProviderService(access);
        var retirements = new Task[16];
        Parallel.For(0, retirements.Length, index => retirements[index] = facade.DisposeAsync().AsTask());
        Task first = retirements[0];
        Assert.All(retirements, retirement => Assert.Same(first, retirement));
        await Task.WhenAll(retirements);
        facade.Dispose();
        Assert.Same(first, facade.DisposeAsync().AsTask());
        source.AllowRead = true;
        Assert.Equal("v1", (await access.AcquireAsync("connection")).Version);
        Assert.Equal(1, source.Calls);
        Assert.Equal(0, authenticator.Calls);
        Assert.False(source.Disposed);
        Assert.False(authenticator.Disposed);
    }

    [Fact]
    public async Task ScopedAliasUsesTheExistingAccessAndNoImplicitRegistrations() => await ExerciseCompositionAsync();

    [Fact]
    public void RegistrationRequiresAnExistingAccessOwner()
    {
        var services = new ServiceCollection().AddLlmResources();
        Assert.Equal(2, services.Count);
        Assert.DoesNotContain(services, registration => registration.ServiceType == typeof(LlmConnectionAccess));
        Assert.Throws<AggregateException>(() => services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }));
    }

    internal static async Task ExerciseCompositionAsync()
    {
        using var source = new Source();
        using var authenticator = new Authenticator();
        await using var access = new LlmConnectionAccess(source, [authenticator]);
        var registrations = new ServiceCollection();
        registrations.AddScoped(_ => access);
        Assert.Same(registrations, registrations.AddLlmResources());
        Assert.Equal(3, registrations.Count);
        Assert.All(registrations, registration => Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime));
        await using ServiceProvider provider = registrations.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        LlmResourceProviderService facade = scope.ServiceProvider.GetRequiredService<LlmResourceProviderService>();
        Assert.Same(facade, scope.ServiceProvider.GetRequiredService<ILlmBatchClient>());
        Assert.Same(access, scope.ServiceProvider.GetRequiredService<LlmConnectionAccess>());
        await Assert.ThrowsAsync<NotSupportedException>(() => facade.CreateAsync("connection", "batch"));
        await facade.DisposeAsync();
        source.AllowRead = true;
        Assert.Equal("v1", (await access.AcquireAsync("connection")).Version);
        Assert.Equal(1, source.Calls);
        Assert.Equal(0, authenticator.Calls);
        Assert.False(source.Disposed);
        Assert.False(authenticator.Disposed);
    }

    [Fact]
    public async Task OwnedRuntimeMetadataRefusesConcreteAndInterfaceWithoutEffects() => await ExerciseRuntimeAsync();

    internal static async Task ExerciseRuntimeAsync()
    {
        using var source = new Source();
        await using var access = new LlmConnectionAccess(source, []);
        await using var facade = new LlmResourceProviderService(access);
        var options = new JsonSerializerOptions(ConnectResourcesJson.LlmBatch.Options)
        {
            TypeInfoResolver = ResourceFacadeRuntimeContext.Default
        };
        options.MakeReadOnly();
        JsonTypeInfo<ResourceFacadeRuntimeEnvelope> metadata = options.GetTypeInfo<ResourceFacadeRuntimeEnvelope>();
        foreach (Type type in new[] { typeof(LlmResourceProviderService), typeof(ILlmBatchClient) })
            Assert.Same(typeof(ConnectResourcesJson).Assembly, options.GetConverter(type).GetType().Assembly);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourceFacadeRuntimeEnvelope { Facade = facade }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ResourceFacadeRuntimeEnvelope { Client = facade }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Facade\":{}}", metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Client\":{}}", metadata));
        const string empty = "{\"Facade\":null,\"Client\":null}";
        ResourceFacadeRuntimeEnvelope value = Assert.IsType<ResourceFacadeRuntimeEnvelope>(JsonSerializer.Deserialize(empty, metadata));
        Assert.Equal(empty, JsonSerializer.Serialize(value, metadata));
        Assert.Equal(25, typeof(ConnectResourcesJson).GetProperties(BindingFlags.Public | BindingFlags.Static).Length);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task ActualForeignMinimalCompositionAndOwnedRuntimeCodecRetire()
    {
        WeakReference[] references = await ExerciseForeignAsync();
        for (int attempt = 0; references.Any(reference => reference.IsAlive) && attempt < 20; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(10);
        }
        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> ExerciseForeignAsync()
    {
        var generation = new ForeignGeneration(Path.GetDirectoryName(typeof(ConnectResourcesJson).Assembly.Location)!);
        Assembly[] defaults = [typeof(InferenceJson).Assembly, typeof(ConnectLlmJson).Assembly,
            typeof(ConnectImageJson).Assembly, typeof(ConnectVideoJson).Assembly,
            typeof(ConnectResourcesJson).Assembly, typeof(ResourceFacadeTests).Assembly];
        try
        {
            Assembly[] loaded = defaults.Select(assembly => generation.LoadFromAssemblyPath(assembly.Location)).ToArray();
            for (int index = 0; index < loaded.Length; index++)
            {
                Assert.NotSame(defaults[index], loaded[index]);
                Assert.Equal(defaults[index].ManifestModule.ModuleVersionId, loaded[index].ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(loaded[index]));
            }
            foreach ((int index, string owner, string getter) in new[]
            {
                (0, typeof(InferenceJson).FullName!, "InferenceResourceReference"),
                (1, typeof(ConnectLlmJson).FullName!, "ChatRequest"),
                (2, typeof(ConnectImageJson).FullName!, "ImageConnection"),
                (3, typeof(ConnectVideoJson).FullName!, "VideoConnection"),
                (4, typeof(ConnectResourcesJson).FullName!, "LlmBatch")
            })
            {
                var metadata = (JsonTypeInfo)loaded[index].GetType(owner)!.GetProperty(getter)!.GetValue(null)!;
                Assert.Same(loaded[index], metadata.Type.Assembly);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Options.TypeInfoResolver!.GetType().Assembly));
            }
            Type fixture = loaded[^1].GetType(typeof(ResourceFacadeTests).FullName!)!;
            foreach (string method in new[] { nameof(ExerciseCompositionAsync), nameof(ExerciseRuntimeAsync) })
                await ((Task)fixture.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!).WaitAsync(TimeSpan.FromSeconds(10));
            return [new WeakReference(generation), .. loaded.Select(assembly => new WeakReference(assembly))];
        }
        finally { generation.Unload(); }
    }

    private static Task Invoke(LlmResourceProviderService client, int operation, IInferenceResourceAccess resources,
        CancellationToken cancellationToken = default)
    {
        var scope = new InferenceResourceScope("endpoint", "account");
        var batch = new LlmBatch("batch", "name", scope, new(null, null, null, null, null));
        var page = new LlmResourcePageRequest();
        var request = new LlmChatRequest { ModelId = "model", Resources = resources };
        LlmBatchRequest[] requests = [new("custom", new LlmBatchChatOperation(request))];
        return operation switch
        {
            0 => client.CreateAsync("connection", "name", cancellationToken),
            1 => client.AddRequestsAsync("connection", batch, requests, resources, cancellationToken),
            2 => client.GetAsync("connection", batch, cancellationToken),
            3 => client.ListAsync("connection", page, cancellationToken),
            4 => client.ListRequestsAsync("connection", batch, page, cancellationToken),
            5 => client.GetResultsAsync("connection", batch, page, new(1, 1), resources, cancellationToken),
            6 => client.CancelAsync("connection", batch, cancellationToken),
            7 => client.SubmitDeferredAsync("connection", request, resources, cancellationToken),
            8 => client.GetDeferredAsync("connection", new("deferred", scope), resources, cancellationToken),
            9 => client.CreateFromRequestsAsync("connection", "name", requests, resources, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private sealed class ForeignGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class Source : ILlmConnectionSource, IDisposable
    {
        public int Calls { get; private set; }
        public bool AllowRead { get; set; }
        public bool Disposed { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (!AllowRead) throw new InvalidOperationException("Unavailable operations cannot resolve connections.");
            return Task.FromResult<LlmConnection?>(new() { Version = "v1", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://example.invalid/v1" });
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException(); }
        public void Dispose() => Disposed = true;
    }

    private sealed class Authenticator : ILlmAuthenticator, IDisposable
    {
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException(); }
        public void Dispose() => Disposed = true;
    }

    private sealed class Resources : IInferenceResourceAccess, IDisposable
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        private InvalidOperationException Effect() { Calls++; return new("Unavailable operations cannot access resources."); }
        public string WorkspaceId => throw Effect();
        public string SessionId => throw Effect();
        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw Effect();
        public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw Effect();
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => throw Effect();
        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => throw Effect();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw Effect();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw Effect();
        public void Dispose() => Disposed = true;
        public override string ToString() => throw Effect();
    }
}

public sealed record ResourceFacadeRuntimeEnvelope
{
    public LlmResourceProviderService? Facade { get; init; }
    public ILlmBatchClient? Client { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ResourceFacadeRuntimeEnvelope))]
internal sealed partial class ResourceFacadeRuntimeContext : JsonSerializerContext;
