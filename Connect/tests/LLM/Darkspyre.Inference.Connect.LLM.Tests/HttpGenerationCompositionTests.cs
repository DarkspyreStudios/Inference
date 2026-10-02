using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HttpGenerationCompositionTests
{
    private static readonly string[] CreationModes = ["SdkGenerationOwned", "BorrowHostFactory"];
    [Fact]
    public void ExactApprovedPublicConfigurationSurfaceExists()
    {
        Assembly owner = typeof(LlmProviderService).Assembly;
        Type mode = owner.GetType("Darkspyre.Inference.Connect.LLM.InferenceHttpCreationMode", true)!;
        Assert.Equal(CreationModes, Enum.GetNames(mode));
        Assert.Equal(0, Convert.ToInt32(Enum.Parse(mode, "SdkGenerationOwned"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(Enum.Parse(mode, "BorrowHostFactory"), System.Globalization.CultureInfo.InvariantCulture));
        Type options = owner.GetType("Darkspyre.Inference.Connect.LLM.InferenceHttpPipelineOptions", true)!;
        Assert.Equal(mode, options.GetProperty("CreationMode")!.PropertyType);
        Type builder = owner.GetType("Darkspyre.Inference.Connect.LLM.InferenceHttpLoggingBuilder", true)!;
        Assert.NotNull(options.GetMethod("ConfigureLogging", [typeof(string), typeof(Action<>).MakeGenericType(builder)]));
        Assert.NotNull(typeof(ServiceCollectionExtensions).GetMethod("AddLlmConnect", [typeof(IServiceCollection), typeof(Action<>).MakeGenericType(options)]));
        Assert.NotNull(typeof(ServiceCollectionExtensions).GetMethod("ConfigureInferenceHttpPipelines", [typeof(IServiceCollection), typeof(Action<>).MakeGenericType(options)]));
    }

    [Fact]
    public void ActualDefaultAddLlmConnectRootRetiresWithoutFactoryTimerRoots()
    {
        WeakReference[] roots = ExerciseGeneration();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 30; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10);
        }
        Assert.All(roots, root => Assert.False(root.IsAlive, "Actual disposed AddLlmConnect still roots its foreign generation."));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseGeneration()
    {
        var generation = new HttpGeneration();
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(HttpGenerationCompositionTests).Assembly.Location);
            Assembly core = generation.LoadFromAssemblyPath(typeof(LlmProviderService).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(core));
            Assert.Equal(typeof(LlmProviderService).Assembly.ManifestModule.ModuleVersionId, core.ManifestModule.ModuleVersionId);
            Type caller = fixture.GetType(typeof(ForeignCaller).FullName!, true)!;
            MethodInfo method = caller.GetMethod(nameof(ForeignCaller.Run))!;
            var result = (string)method.Invoke(null, null)!;
            Assert.Contains("Messages", result, StringComparison.Ordinal);
            return [new(generation), new(fixture), new(core), new(caller), new(method)];
        }
        finally { generation.Unload(); }
    }

    private sealed class HttpGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        private readonly string _directory = Path.GetDirectoryName(typeof(HttpGenerationCompositionTests).Assembly.Location)!;
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name!.StartsWith("Darkspyre.Inference", StringComparison.Ordinal))
            {
                string path = Path.Combine(_directory, name.Name + ".dll");
                if (File.Exists(path)) return LoadFromAssemblyPath(path);
            }
            return Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
        }
    }

    public static class ForeignCaller
    {
        public static string Run()
        {
            var services = new ServiceCollection(); services.AddSingleton<ILlmConnectionSource, EmptySource>(); services.AddLlmConnect();
            using ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope scope = provider.CreateScope();
            Assert.Same(scope.ServiceProvider.GetRequiredService<LlmProviderService>(), scope.ServiceProvider.GetRequiredService<ILlmFileClient>());
            Assert.Same(scope.ServiceProvider.GetRequiredService<LlmProviderService>(), scope.ServiceProvider.GetRequiredService<IDocumentSearchClient>());
            return JsonSerializer.Serialize(new LlmChatRequest { ModelId = "manual", Messages = [new("user", "actual")] }, ConnectLlmJson.ChatRequest);
        }
    }

    private sealed class EmptySource : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(null);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
