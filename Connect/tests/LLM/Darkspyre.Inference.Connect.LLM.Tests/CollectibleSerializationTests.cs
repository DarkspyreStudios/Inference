using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CollectibleSerializationTests
{
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
    private static WeakReference[] RoundTripInCollectibleGeneration(string json)
    {
        var context = new SerializationGeneration(Path.GetDirectoryName(typeof(ConnectLlmJson).Assembly.Location)!);
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
            Type metadataOwner = assembly.GetType(typeof(ConnectLlmJson).FullName!, throwOnError: true)!;
            var references = new List<WeakReference> { new(context), new(assembly) };
            PropertyInfo[] roots = metadataOwner.GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(property => typeof(JsonTypeInfo).IsAssignableFrom(property.PropertyType)).ToArray();
            Assert.NotEmpty(roots);
            foreach (PropertyInfo root in roots)
            {
                var typeInfo = (JsonTypeInfo)root.GetValue(null)!;
                if (typeInfo.Type.Assembly.GetName().Name is "Darkspyre.Inference.Common" or "Darkspyre.Inference.Connect.LLM")
                    Assert.Same(context, AssemblyLoadContext.GetLoadContext(typeInfo.Type.Assembly));
                typeInfo.MakeReadOnly();
                _ = typeInfo.Properties.Count;
                Assert.Same(typeInfo, typeInfo.Options.GetTypeInfo(typeInfo.Type));
                references.Add(new(typeInfo));
                references.Add(new(typeInfo.Options));
            }
            var metadata = (JsonTypeInfo)metadataOwner.GetProperty(nameof(ConnectLlmJson.ChatResult))!.GetValue(null)!;
            Assert.Same(assembly, metadata.Type.Assembly);

            object value = JsonSerializer.Deserialize(json, metadata)!;
            Assert.Same(assembly, value.GetType().Assembly);
            Assert.Equal(json, JsonSerializer.Serialize(value, metadata));
            object usage = value.GetType().GetProperty(nameof(LlmChatResult.Usage))!.GetValue(value)!;
            Assembly common = usage.GetType().Assembly;
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(common));
            Assert.Equal("Darkspyre.Inference.Common", common.GetName().Name);
            var tools = (System.Collections.IEnumerable)value.GetType().GetProperty(nameof(LlmChatResult.ToolCalls))!.GetValue(value)!;
            object tool = Assert.Single(tools.Cast<object>());
            Assert.Same(common, tool.GetType().Assembly);

            references.Add(new(metadata));
            references.Add(new(value));
            references.Add(new(common));
            references.Add(new(usage));
            references.Add(new(tool));
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
            if (name.Name is not ("Darkspyre.Inference.Common" or "Darkspyre.Inference.Connect.LLM")) return null;
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
