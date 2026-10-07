using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.Resources;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CollectibleSerializationTests
{
    private static readonly string[] GenerationAssemblies =
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
        string json = JsonSerializer.Serialize(new LlmBatchResultPage([new LlmBatchChatResult("row-1", result)], "cursor"),
            ConnectResourcesJson.LlmBatchResultPage);
        WeakReference[] generation = RoundTripInCollectibleGeneration(json, resources: true);

        AssertCollected(generation);
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
    private static WeakReference[] RoundTripInCollectibleGeneration(string json, bool resources = false)
    {
        var context = new SerializationGeneration(Path.GetDirectoryName(typeof(ConnectLlmJson).Assembly.Location)!);
        try
        {
            Type owner = resources ? typeof(ConnectResourcesJson) : typeof(ConnectLlmJson);
            Assembly assembly = context.LoadFromAssemblyPath(owner.Assembly.Location);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
            Type metadataOwner = assembly.GetType(owner.FullName!, throwOnError: true)!;
            var references = new List<WeakReference> { new(context), new(assembly) };
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
                Assert.Equal(GenerationAssemblies, context.Assemblies.Select(loaded => loaded.GetName().Name).Order(StringComparer.Ordinal));
            foreach (Assembly loaded in context.Assemblies) references.Add(new(loaded));
            return references.ToArray();
        }
        finally
        {
            context.Unload();
        }
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
