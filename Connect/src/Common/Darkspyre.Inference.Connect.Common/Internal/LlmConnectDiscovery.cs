using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class LlmConnectDiscovery
{
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Provider packages and source-mode targets root their assemblies and registration dependencies for discovery.")]
    internal static void RegisterServices(IServiceCollection services)
    {
        Assembly owner = typeof(LlmConnectProviderAttribute).Assembly;
        if (string.IsNullOrEmpty(owner.Location))
            throw new InvalidOperationException("Connect discovery requires its libraries to be deployed as assembly files.");

        AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(owner)!;
        foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(owner.Location)!,
            "Darkspyre.Inference.Connect.*.dll").Order(StringComparer.Ordinal))
        {
            Assembly assembly = context.LoadFromAssemblyPath(path);
            assembly.GetCustomAttribute<LlmConnectProviderAttribute>()?.RegisterServices(services);
        }
    }
}
