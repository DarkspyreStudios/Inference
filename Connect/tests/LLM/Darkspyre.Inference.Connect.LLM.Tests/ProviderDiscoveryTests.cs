using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderDiscoveryTests
{
    [Theory]
    [InlineData("Codex", "chatgpt-subscription")]
    [InlineData("Codex,Claude,OpenAiApi,SelfHost", "chatgpt-subscription,claude-subscription,openai-api-key,self-host")]
    public void IncludedLibrariesAreDiscoveredBeforeProviderTypesAreUsed(string providers, string expectedIds)
    {
        string directory = Path.Combine(Path.GetTempPath(), "connect-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.GetDirectoryName(typeof(ServiceCollectionExtensions).Assembly.Location)!;
            string[] names = ["Darkspyre.Inference.Common", "Darkspyre.Inference.Connect.Common",
                .. providers.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(name => "Darkspyre.Inference.Connect." + name)];
            foreach (string name in names)
                File.Copy(Path.Combine(output, name + ".dll"), Path.Combine(directory, name + ".dll"));

            WeakReference generation = Discover(directory, names.Skip(2).ToArray(), expectedIds);
            // Match the existing collectible-context qualification's ten finalizer passes.
            for (int attempt = 0; attempt < 10 && generation.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.False(generation.IsAlive);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Discover(string directory, string[] providers, string expectedIds)
    {
        var context = new Deployment(directory);
        try
        {
            Assembly common = context.LoadFromAssemblyPath(Path.Combine(directory, "Darkspyre.Inference.Connect.Common.dll"));
            Assert.DoesNotContain(context.Assemblies, assembly => providers.Contains(assembly.GetName().Name));
            Type registration = common.GetType(typeof(ServiceCollectionExtensions).FullName!, throwOnError: true)!;
            MethodInfo add = registration.GetMethods().Single(method => method.Name == nameof(ServiceCollectionExtensions.AddLlmConnect)
                && method.GetParameters().Length == 1);
            IServiceCollection services = new ServiceCollection();
            add.Invoke(null, [services]);
            using ServiceProvider container = services.BuildServiceProvider();
            Type catalogType = common.GetType(typeof(LlmProviderCatalog).FullName!, throwOnError: true)!;
            object catalog = container.GetRequiredService(catalogType);
            var definitions = (IEnumerable)catalogType.GetProperty(nameof(LlmProviderCatalog.SupportedProviders))!.GetValue(catalog)!;
            string[] ids = definitions.Cast<object>().Select(item => (string)item.GetType().GetProperty("Id")!.GetValue(item)!).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal), ids);
            Assert.Equal(providers.Length, services.Count(service => service.ServiceType.FullName == typeof(ILlmClient).FullName));
            Assert.Equal(providers.Length, services.Count(service => service.ServiceType.FullName == typeof(ILlmAgentClient).FullName));
            foreach (string name in providers)
                Assert.Single(context.Assemblies, assembly => assembly.GetName().Name == name);
            return new(context);
        }
        finally { context.Unload(); }
    }

    private sealed class Deployment(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            return LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll"));
        }
    }
}
