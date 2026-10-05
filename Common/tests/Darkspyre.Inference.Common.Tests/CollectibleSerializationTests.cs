using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Darkspyre.Inference.Common.Tests;

public sealed class CollectibleSerializationTests
{
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
