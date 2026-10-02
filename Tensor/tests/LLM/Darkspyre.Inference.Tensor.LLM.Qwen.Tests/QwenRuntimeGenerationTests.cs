using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.GGML;
using Xunit.Abstractions;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenRuntimeGenerationTests(ITestOutputHelper output)
{
    [Fact]
    public void ActualPrivateSupplierEmptyPlanCompositionAndMetadataRetireWithoutNativeInitialization()
    {
        (WeakReference[] roots, string evidence) = ExerciseGeneration();
        output.WriteLine(evidence);
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.All(roots, root => Assert.False(root.IsAlive, "The actual inert Qwen/supplier managed generation remains rooted."));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference[] Roots, string Evidence) ExerciseGeneration()
    {
        string directory = Path.GetDirectoryName(typeof(QwenRuntimeGenerationTests).Assembly.Location)!;
        var generation = new SupplierGeneration(directory);
        var roots = new List<WeakReference> { new(generation) };
        string evidence;
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(QwenRuntimeGenerationTests).Assembly.Location);
            Type caller = fixture.GetType(typeof(QwenRuntimeGenerationTests).FullName!, throwOnError: true)!;
            object[] result = (object[])caller.GetMethod(nameof(RunForeignEmptyPlan), BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            evidence = (string)result[0];
            roots.AddRange((WeakReference[])result[1]);
            roots.AddRange(generation.Assemblies.Select(assembly => new WeakReference(assembly)));
        }
        finally { generation.Unload(); }
        return (roots.ToArray(), evidence);
    }

    private static object[] RunForeignEmptyPlan()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(QwenRuntimeGenerationTests).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.NotSame(AssemblyLoadContext.Default, generation);
        string directory = Path.GetDirectoryName(typeof(QwenRuntimeGenerationTests).Assembly.Location)!;
        Assert.Empty(Directory.GetFiles(directory, GgmlNativeLoader.EntryLibraryName, SearchOption.AllDirectories));
        Assert.Equal(GgmlRuntimeState.Unconfigured, GgmlNativeLoader.State);
        Assert.Null(GgmlNativeLoader.Current);
        var build = new TensorSharpBuildIdentity(GgmlNativeLoader.TensorSharpBuild, GgmlNativeLoader.NativeAbi, GgmlNativeLoader.GgmlCommit);
        var plan = new TensorRuntimePlan(build, GgmlNativeLoader.RuntimeIdentifier, []);
        QwenRuntimeOwner owner = QwenRuntimeOwner.GetOrCreate(plan);
        var packs = new UnenteredPacks();
        QwenEngineProvider provider;
        ITensorModelInstance? instance = null;
        TensorSettingsManager manager;
        TensorBackendAvailability availability;
        TensorRuntimeShutdownResult shutdown;
        try
        {
            Assert.Same(owner, QwenRuntimeOwner.GetOrCreate(new(build, plan.TargetRid, [])));
            provider = new(owner);
            instance = provider.CreateInstance(packs, TensorBackendKind.Auto);
            manager = provider.CreateSettingsManager(new UnenteredTarget(), packs, [], instance);
            var request = new TensorBackendProbeRequest(Guid.NewGuid(), provider.Definition.Setup.Implementation.Id,
                TensorBackendKind.Auto, plan.TargetRid);
            Assert.Equal(GgmlRuntimeState.Unconfigured, GgmlNativeLoader.State);
            Assert.Null(GgmlNativeLoader.Current);
            availability = owner.CheckAsync(request).GetAwaiter().GetResult();
            Assert.Equal(TensorBackendAvailabilityState.Unavailable, availability.State);
            Assert.Equal(TensorRuntimeState.Unavailable, owner.State);
            Assert.Null(owner.Current);
            Assert.Equal(GgmlRuntimeState.Unconfigured, GgmlNativeLoader.State);
            Assert.Null(GgmlNativeLoader.Current);
            Assert.Null(instance.GetObservationAsync().GetAwaiter().GetResult().Model);
        }
        finally
        {
            try
            {
                instance?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                shutdown = owner.ShutdownAsync().GetAwaiter().GetResult();
            }
            finally { owner.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        Assert.Equal(TensorRuntimeShutdownState.Completed, shutdown.State);
        Assert.Equal(TensorRuntimeShutdownState.AlreadyStopped, owner.ShutdownAsync().GetAwaiter().GetResult().State);
        Assert.Equal(0, shutdown.OutstandingLeases);
        Assert.False(shutdown.NativeStateMayRemain);
        Assert.Equal(GgmlRuntimeState.Stopped, GgmlNativeLoader.State);
        Assert.Null(GgmlNativeLoader.Current);
        Assert.Equal(0, packs.Calls);

        string planJson = JsonSerializer.Serialize(plan, TensorJson.TensorRuntimePlan);
        Assert.Equal(planJson, JsonSerializer.Serialize(Assert.IsType<TensorRuntimePlan>(JsonSerializer.Deserialize(planJson, TensorJson.TensorRuntimePlan)), TensorJson.TensorRuntimePlan));
        string availabilityJson = JsonSerializer.Serialize(availability, TensorJson.BackendAvailability);
        Assert.Equal(availabilityJson, JsonSerializer.Serialize(Assert.IsType<TensorBackendAvailability>(JsonSerializer.Deserialize(availabilityJson, TensorJson.BackendAvailability)), TensorJson.BackendAvailability));
        string shutdownJson = JsonSerializer.Serialize(shutdown, TensorJson.TensorRuntimeShutdownResult);
        Assert.Equal(shutdownJson, JsonSerializer.Serialize(Assert.IsType<TensorRuntimeShutdownResult>(JsonSerializer.Deserialize(shutdownJson, TensorJson.TensorRuntimeShutdownResult)), TensorJson.TensorRuntimeShutdownResult));

        Assembly[] assemblies = [typeof(QwenRuntimeOwner).Assembly, typeof(TensorJson).Assembly, typeof(TensorSharp.Server.ModelService).Assembly,
            typeof(TensorSharp.Models.ModelBase).Assembly, typeof(GgmlNativeLoader).Assembly, typeof(TensorSharp.Tensor).Assembly,
            typeof(TensorSharp.Runtime.BackendType).Assembly];
        foreach (Assembly assembly in assemblies)
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(TensorJson.TensorRuntimePlan.Type.Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(TensorJson.TensorRuntimePlan.Options.TypeInfoResolver!.GetType().Assembly));
        string evidence = "Managed-only actual private Qwen/Chat/Models/GGML/Core/Runtime composition; explicit empty plan; no native initialization/model load.\n"
            + $"Build={build.Version}; ABI={build.NativeAbi}; upstream={build.GgmlCommit}; RID={plan.TargetRid}\n"
            + string.Join("\n", assemblies.Select(assembly => $"{assembly.GetName().Name}|{assembly.GetName().Version}|{assembly.ManifestModule.ModuleVersionId}|{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location)))}"));
        return [evidence, new WeakReference[] { new(owner), new(instance), new(provider), new(manager),
            new(TensorJson.TensorRuntimePlan), new(TensorJson.TensorRuntimePlan.Options.TypeInfoResolver!) }];
    }

    private sealed class SupplierGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true
                && assemblyName.Name?.StartsWith("TensorSharp.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class UnenteredTarget : ISetupCommitTarget<TensorSetupChanges>
    {
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The inert composition must not invoke the commit target.");
    }

    private sealed class UnenteredPacks : ITensorModelPackManager
    {
        public int Calls { get; private set; }
        private InvalidOperationException Refuse() { Calls++; return new("The inert composition must not invoke model-pack operations."); }
        public Task<IReadOnlyList<TensorModelPackDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken = default) => throw Refuse();
        public Task<TensorModelPackDefinition> RegisterAsync(TensorModelPackDefinition definition, CancellationToken cancellationToken = default) => throw Refuse();
        public Task<TensorModelPackDefinition> GetRequiredDefinitionAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw Refuse();
        public Task<TensorModelPackStatus> GetStatusAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw Refuse();
        public Task InstallAsync(TensorModelKey key, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw Refuse();
        public Task<TensorModelImportResult> ImportAsync(TensorModelImportRequest request, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw Refuse();
        public Task<TensorModelPackStatus> VerifyAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw Refuse();
        public Task<TensorModelPackLease> AcquireAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw Refuse();
        public Task RemoveAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw Refuse();
    }
}
