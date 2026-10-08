using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.Audio;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.Realtime;
using Darkspyre.Inference.Connect.Resources;
using Darkspyre.Inference.Connect.STT;
using Darkspyre.Inference.Connect.TTS;
using Darkspyre.Inference.Connect.Video;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CollectibleSerializationTests
{
    private static readonly Type[] CommonConnectMetadataOwners =
    [
        typeof(InferenceJson), typeof(ConnectLlmJson), typeof(ConnectImageJson), typeof(ConnectVideoJson),
        typeof(ConnectTtsJson), typeof(ConnectSttJson), typeof(ConnectRealtimeJson), typeof(ConnectResourcesJson), typeof(ConnectAudioJson)
    ];
    private static readonly string[] GenerationAssemblies = CommonConnectMetadataOwners
        .Select(owner => owner.Assembly.GetName().Name!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static readonly string[] ResourcesAssemblies =
    [
        "Darkspyre.Inference.Common", "Darkspyre.Inference.Connect.Image", "Darkspyre.Inference.Connect.LLM",
        "Darkspyre.Inference.Connect.Resources", "Darkspyre.Inference.Connect.Video"
    ];

    [Fact]
    public void SchemaGuidanceQuotesNestedPropertyNamesWithoutReflection()
    {
        var node = new AppleSchemaNode("object")
        {
            Properties = [new("part\"\\\n\u00e9", true, new("string") { Description = "leaf" })]
        };

        Assert.Equal("$[\"part\\u0022\\\\\\n\\u00E9\"]: leaf", AppleSchema.Guidance(node));
    }

    [Fact]
    public void OwnedResultMetadataDoesNotKeepItsCollectibleGenerationAlive()
    {
        string json = JsonSerializer.Serialize(new LlmChatResult
        {
            Text = "done",
            Status = LlmCompletionStatus.Completed,
            ModelId = "reported-model",
            Usage = new(7, 3, 10),
            ToolCalls = [new LlmShellCall("call-1", ["pwd"], null, null)],
            Diagnostics = new(200, null, null)
        }, ConnectLlmJson.ChatResult);
        WeakReference[] generation = RoundTripInCollectibleGeneration(json);

        AssertCollected(generation);
    }

    [Fact]
    public void ResourcesMetadataAndNestedCommitReceiptsDoNotKeepTheirCollectibleGenerationAlive()
    {
        WeakReference[] generation = RoundTripInCollectibleGeneration(ResourcesResultJson(), resources: true);

        AssertCollected(generation);
    }

    [Fact]
    public void AllCommonConnectMetadataOwnersReleaseTheirCollectibleGeneration()
    {
        WeakReference[] generation = RoundTripInCollectibleGeneration(ResourcesResultJson(), resources: true, allOwners: true);

        AssertCollected(generation);
    }

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
            Assembly fixtures = context.LoadFromAssemblyPath(typeof(AgentFacadeRetirementTests).Assembly.Location);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(fixtures));
            Assert.Equal(typeof(AgentFacadeRetirementTests).Assembly.ManifestModule.ModuleVersionId, fixtures.ManifestModule.ModuleVersionId);
            object source = Activator.CreateInstance(fixtures.GetType(
                typeof(AgentFacadeRetirementTests).FullName + "+Source", throwOnError: true)!, nonPublic: true)!;
            Type sourceContract = assembly.GetType(typeof(ILlmConnectionSource).FullName!, throwOnError: true)!;
            Assert.IsAssignableFrom(sourceContract, source);
            IServiceCollection services = new ServiceCollection();
            services.Add(ServiceDescriptor.Singleton(sourceContract, source));
            register.Invoke(null, [services]);
            container = services.BuildServiceProvider();
            Type clientType = assembly.GetType(typeof(ILlmClient).FullName!, throwOnError: true)!;
            var clients = (System.Collections.IEnumerable)container.GetRequiredService(typeof(IEnumerable<>).MakeGenericType(clientType));
            object[] resolved = clients.Cast<object>().ToArray();
            Assert.Equal(9, resolved.Length);
            Assert.Contains(resolved, client => client.GetType().Name == "XaiResponsesLlmClient");
            Assert.All(resolved, client => Assert.Same(assembly, client.GetType().Assembly));
            object pool = container.GetRequiredService(assembly.GetType(
                "Darkspyre.Inference.Connect.LLM.Internal.SdkHttpGenerationPool", throwOnError: true)!);
            Assert.Same(assembly, pool.GetType().Assembly);
            object chat = container.GetRequiredService(assembly.GetType(typeof(LlmProviderService).FullName!, throwOnError: true)!);
            Assert.Same(chat, container.GetRequiredService(assembly.GetType(typeof(ILlmFileClient).FullName!, throwOnError: true)!));
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

    private static string ResourcesResultJson()
    {
        var resource = new InferenceResourceReference("workspace", "generated");
        var result = new LlmChatResult
        {
            Text = "done",
            Status = LlmCompletionStatus.Completed,
            Diagnostics = new(200, null, null),
            Usage = new(7, 3, 10),
            ToolCalls = [new LlmShellCall("call-1", ["pwd"], null, null)],
            GeneratedResources = [new(new(resource, "session", "output.txt", "text/plain", 4),
                new("workspace", "session", 1, [resource]))]
        };
        return JsonSerializer.Serialize(new LlmBatchResultPage([new LlmBatchChatResult("row-1", result)], "cursor"),
            ConnectResourcesJson.LlmBatchResultPage);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectingAnUnknownCollectibleTypeDoesNotKeepItAlive(bool credential)
    {
        WeakReference type = AttemptUnsupportedSerialization(credential);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(type.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RoundTripInCollectibleGeneration(string json, bool resources = false, bool allOwners = false)
    {
        var context = new SerializationGeneration(Path.GetDirectoryName(typeof(ConnectLlmJson).Assembly.Location)!);
        try
        {
            Type owner = resources ? typeof(ConnectResourcesJson) : typeof(ConnectLlmJson);
            Assembly assembly = context.LoadFromAssemblyPath(owner.Assembly.Location);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
            Type metadataOwner = assembly.GetType(owner.FullName!, throwOnError: true)!;
            var references = new List<WeakReference> { new(context), new(assembly) };
            if (allOwners)
            {
                int roots = 0;
                foreach (Type sharedOwner in CommonConnectMetadataOwners)
                {
                    Assembly privateAssembly = context.LoadFromAssemblyPath(sharedOwner.Assembly.Location);
                    Assert.Same(context, AssemblyLoadContext.GetLoadContext(privateAssembly));
                    roots += ExerciseMetadata(privateAssembly.GetType(sharedOwner.FullName!, throwOnError: true)!, context, references);
                }
                Assert.Equal(504, roots);
            }
            else ExerciseMetadata(metadataOwner, context, references);
            string rootName = resources ? nameof(ConnectResourcesJson.LlmBatchResultPage) : nameof(ConnectLlmJson.ChatResult);
            var metadata = (JsonTypeInfo)metadataOwner.GetProperty(rootName)!.GetValue(null)!;
            Assert.Same(assembly, metadata.Type.Assembly);

            object value = JsonSerializer.Deserialize(json, metadata)!;
            Assert.Same(assembly, value.GetType().Assembly);
            Assert.Equal(json, JsonSerializer.Serialize(value, metadata));
            object result = value;
            if (resources)
            {
                var items = (System.Collections.IEnumerable)value.GetType().GetProperty(nameof(LlmBatchResultPage.Items))!.GetValue(value)!;
                object row = Assert.Single(items.Cast<object>());
                Assert.Same(assembly, row.GetType().Assembly);
                result = row.GetType().GetProperty(nameof(LlmBatchChatResult.Result))!.GetValue(row)!;
                Assert.Same(context, AssemblyLoadContext.GetLoadContext(result.GetType().Assembly));
                Assert.Equal("Darkspyre.Inference.Connect.LLM", result.GetType().Assembly.GetName().Name);
                references.Add(new(row));
                var receipts = (System.Collections.IEnumerable)result.GetType().GetProperty(nameof(LlmChatResult.GeneratedResources))!.GetValue(result)!;
                object receipt = Assert.Single(receipts.Cast<object>());
                object descriptor = receipt.GetType().GetProperty(nameof(InferenceResourceCommitResult.Resource))!.GetValue(receipt)!;
                object selection = receipt.GetType().GetProperty(nameof(InferenceResourceCommitResult.Selection))!.GetValue(receipt)!;
                foreach (object nested in new[] { receipt, descriptor, selection })
                {
                    Assert.Same(context, AssemblyLoadContext.GetLoadContext(nested.GetType().Assembly));
                    references.Add(new(nested));
                }
            }
            object usage = result.GetType().GetProperty(nameof(LlmChatResult.Usage))!.GetValue(result)!;
            Assembly common = usage.GetType().Assembly;
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(common));
            Assert.Equal("Darkspyre.Inference.Common", common.GetName().Name);
            var tools = (System.Collections.IEnumerable)result.GetType().GetProperty(nameof(LlmChatResult.ToolCalls))!.GetValue(result)!;
            object tool = Assert.Single(tools.Cast<object>());
            Assert.Same(common, tool.GetType().Assembly);

            references.Add(new(metadata));
            references.Add(new(value));
            references.Add(new(result));
            references.Add(new(common));
            references.Add(new(usage));
            references.Add(new(tool));
            if (resources)
                Assert.Equal(allOwners ? GenerationAssemblies : ResourcesAssemblies,
                    context.Assemblies.Select(loaded => loaded.GetName().Name).Order(StringComparer.Ordinal));
            foreach (Assembly loaded in context.Assemblies) references.Add(new(loaded));
            return references.ToArray();
        }
        finally
        {
            context.Unload();
        }
    }

    private static int ExerciseMetadata(Type metadataOwner, AssemblyLoadContext context, List<WeakReference> references)
    {
        PropertyInfo[] roots = metadataOwner.GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => typeof(JsonTypeInfo).IsAssignableFrom(property.PropertyType)).ToArray();
        Assert.NotEmpty(roots);
        foreach (PropertyInfo root in roots)
        {
            var typeInfo = (JsonTypeInfo)root.GetValue(null)!;
            if (typeInfo.Type.Assembly.GetName().Name!.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal))
                Assert.Same(context, AssemblyLoadContext.GetLoadContext(typeInfo.Type.Assembly));
            typeInfo.MakeReadOnly();
            _ = typeInfo.Properties.Count;
            Assert.Same(typeInfo, typeInfo.Options.GetTypeInfo(typeInfo.Type));
            references.Add(new(typeInfo));
            references.Add(new(typeInfo.Options));
        }
        return roots.Length;
    }

    private sealed class SerializationGeneration(string directory) : AssemblyLoadContext("ConnectJsonGeneration", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (!GenerationAssemblies.Contains(name.Name, StringComparer.Ordinal)) return null;
            return LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttemptUnsupportedSerialization(bool credential)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("UnknownContract"), AssemblyBuilderAccess.RunAndCollect);
        TypeBuilder builder = assembly.DefineDynamicModule("Contracts").DefineType("UnknownContract", TypeAttributes.Public);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        Type type = builder.CreateType()!;
        object value = Activator.CreateInstance(type)!;

        JsonSerializerOptions options = credential ? ConnectLlmJson.Credential.Options : ConnectLlmJson.ChatRequest.Options;
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(value, type, options));

        return new WeakReference(type);
    }
}
