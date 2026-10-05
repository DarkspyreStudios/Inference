using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.Tests;

public sealed class CollectibleSerializationTests
{
    [Fact]
    public void OwnedResultGraphDoesNotKeepItsCollectibleGenerationAlive()
    {
        var reference = new InferenceResourceReference("workspace-1", "resource-1");
        string json = JsonSerializer.Serialize(new LlmInferenceResult
        {
            Text = "done",
            Status = LlmCompletionStatus.Completed,
            Usage = new(7, 3, 10),
            ToolCalls = [new LlmShellCall("call-1", ["pwd"], null, null)],
            GeneratedResources = [new(
                new(reference, "session-1", "result.json", "application/json", 2),
                new("workspace-1", "session-1", 1, [reference]))],
            Provenance = new() { ImplementationId = "test-provider", ModelId = "reported-model" }
        }, InferenceJson.LlmResult);
        WeakReference[] generation = RoundTripInCollectibleGeneration(json);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.All(generation, item => Assert.False(item.IsAlive));
    }

    [Fact]
    public void RejectingAnUnknownCollectibleTypeDoesNotKeepItAlive()
    {
        WeakReference type = AttemptUnsupportedSerialization();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(type.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RoundTripInCollectibleGeneration(string json)
    {
        var context = new AssemblyLoadContext("CommonJsonGeneration", isCollectible: true);
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
            Type metadataOwner = assembly.GetType(typeof(InferenceJson).FullName!, throwOnError: true)!;
            var metadata = (JsonTypeInfo)metadataOwner.GetProperty(nameof(InferenceJson.LlmResult))!.GetValue(null)!;
            Assert.Same(assembly, metadata.Type.Assembly);

            object value = JsonSerializer.Deserialize(json, metadata)!;
            Assert.Same(assembly, value.GetType().Assembly);
            Assert.Equal(json, JsonSerializer.Serialize(value, metadata));

            return [new(context), new(assembly), new(metadata), new(value)];
        }
        finally
        {
            context.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttemptUnsupportedSerialization()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("UnknownContract"), AssemblyBuilderAccess.RunAndCollect);
        TypeBuilder builder = assembly.DefineDynamicModule("Contracts").DefineType("UnknownContract", TypeAttributes.Public);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        Type type = builder.CreateType()!;
        object value = Activator.CreateInstance(type)!;

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(value, type, InferenceJson.LlmRequest.Options));

        return new WeakReference(type);
    }
}
