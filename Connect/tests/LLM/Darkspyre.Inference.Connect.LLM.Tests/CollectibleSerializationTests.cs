using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CollectibleSerializationTests
{
    private static readonly Type[] CommonConnectMetadataOwners = [typeof(InferenceJson), typeof(ConnectLlmJson)];
    private static readonly string[] GenerationAssemblies = CommonConnectMetadataOwners
        .Select(owner => owner.Assembly.GetName().Name!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void RegisteredChatFacadeHttpGraphReleasesItsCollectibleGeneration()
    {
        WeakReference[] generation = RetireRegisteredChatGeneration();

        AssertCollected(generation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RetireRegisteredChatGeneration()
    {
        var context = new SerializationGeneration(Path.GetDirectoryName(typeof(ConnectLlmJson).Assembly.Location)!);
        ServiceProvider? container = null;
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
            Assert.Equal(typeof(ConnectLlmJson).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
            Type registration = assembly.GetType(typeof(ServiceCollectionExtensions).FullName!, throwOnError: true)!;
            MethodInfo register = registration.GetMethods().Single(method => method.Name == nameof(ServiceCollectionExtensions.AddLlmConnect)
                && method.GetParameters().Length == 1);
            Assembly fixtures = context.LoadFromAssemblyPath(typeof(CollectibleSerializationTests).Assembly.Location);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(fixtures));
            Assert.Equal(typeof(CollectibleSerializationTests).Assembly.ManifestModule.ModuleVersionId, fixtures.ManifestModule.ModuleVersionId);
            object source = Activator.CreateInstance(fixtures.GetType(
                typeof(CollectibleSerializationTests).FullName + "+Source", throwOnError: true)!, nonPublic: true)!;
            Type sourceContract = assembly.GetType(typeof(ILlmConnectionSource).FullName!, throwOnError: true)!;
            Assert.IsAssignableFrom(sourceContract, source);
            IServiceCollection services = new ServiceCollection();
            services.Add(ServiceDescriptor.Singleton(sourceContract, source));
            Assembly selfHost = context.LoadFromAssemblyPath(typeof(ILlmSelfHost).Assembly.Location);
            Type hostContract = selfHost.GetType(typeof(ILlmSelfHost).FullName!, throwOnError: true)!;
            object host = Activator.CreateInstance(fixtures.GetType(
                typeof(CollectibleSerializationTests).FullName + "+UnusedSelfHost", throwOnError: true)!, nonPublic: true)!;
            services.Add(ServiceDescriptor.Singleton(hostContract, host));
            Type folderContract = assembly.GetType(typeof(ILlmDataFolder).FullName!, throwOnError: true)!;
            object folder = Activator.CreateInstance(fixtures.GetType(
                typeof(CollectibleSerializationTests).FullName + "+UnusedDataFolder", throwOnError: true)!, nonPublic: true)!;
            services.Add(ServiceDescriptor.Singleton(folderContract, folder));
            register.Invoke(null, [services]);
            container = services.BuildServiceProvider();
            Type clientType = assembly.GetType(typeof(ILlmClient).FullName!, throwOnError: true)!;
            var clients = (System.Collections.IEnumerable)container.GetRequiredService(typeof(IEnumerable<>).MakeGenericType(clientType));
            object[] resolved = clients.Cast<object>().ToArray();
            // One chat client per deployed provider library, Anthropic Messages among them.
            Assert.Equal(7, resolved.Length);
            Assert.Contains(resolved, client => client.GetType().FullName == typeof(AnthropicMessagesLlmClient).FullName);
            Assert.All(resolved, client => Assert.Same(context, AssemblyLoadContext.GetLoadContext(client.GetType().Assembly)));
            object pool = container.GetRequiredService(assembly.GetType(
                "Darkspyre.Inference.Connect.LLM.Internal.SdkHttpGenerationPool", throwOnError: true)!);
            Assert.Same(assembly, pool.GetType().Assembly);
            object chat = container.GetRequiredService(assembly.GetType(typeof(LlmProviderService).FullName!, throwOnError: true)!);
            object agents = container.GetRequiredService(assembly.GetType(typeof(LlmAgentProviderService).FullName!, throwOnError: true)!);
            object embeddings = container.GetRequiredService(assembly.GetType(typeof(LlmEmbeddingProviderService).FullName!, throwOnError: true)!);
            Assert.All(new[] { chat, agents, embeddings }, facade => Assert.Same(assembly, facade.GetType().Assembly));
            return [new(context), new(assembly), new(fixtures), new(source), new(container), new(pool),
                new(chat), new(agents), new(embeddings), .. resolved.Select(client => new WeakReference(client))];
        }
        finally
        {
            try { container?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally { context.Unload(); }
        }
    }

    private static void AssertCollected(WeakReference[] generation)
    {
        // The runtime's unload guidance bounds collection at ten finalizer passes.
        for (int attempt = 0; attempt < 10 && generation.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.All(generation, reference => Assert.False(reference.IsAlive));
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new()
            {
                Version = "1",
                Transport = LlmTransport.OpenAiCodex,
                Endpoint = "https://provider.example.test/codex"
            });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedSelfHost : ILlmSelfHost
    {
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Common.Embedding.IEmbeddingInference> GetEmbeddingInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) => throw new NotSupportedException();
    }

    private sealed class UnusedDataFolder : ILlmDataFolder
    {
        public ValueTask<string> ReadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SerializationGeneration(string directory) : AssemblyLoadContext("ConnectJsonGeneration", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (!GenerationAssemblies.Contains(name.Name, StringComparer.Ordinal)) return null;
            return LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll"));
        }
    }
}
