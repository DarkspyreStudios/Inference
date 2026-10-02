using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorRuntimeEnumConsumerTests
{
    [Fact]
    public void PublicEnumsDoNotRequireInaccessibleConverterAttributes()
    {
        Assert.Null(typeof(TensorRuntimeState).GetCustomAttribute<JsonConverterAttribute>());
        Assert.Null(typeof(TensorRuntimeShutdownState).GetCustomAttribute<JsonConverterAttribute>());
    }

    [Fact]
    public void ConsumerGeneratedMetadataComposesActualOwnedEnumGuardsWithoutChangingBackendKind()
    {
        TensorEnumConsumerContext context = CreateContext();
        var value = new TensorEnumConsumerObservation(TensorRuntimeState.Ready, TensorRuntimeShutdownState.Busy, (TensorBackendKind)999);
        const string expected = "{\"State\":2,\"ShutdownState\":2,\"Backend\":999}";
        Assert.Equal(expected, JsonSerializer.Serialize(value, context.TensorEnumConsumerObservation));
        Assert.Equal(value, JsonSerializer.Deserialize(expected, context.TensorEnumConsumerObservation));
    }

    [Theory]
    [InlineData("State", "-1")]
    [InlineData("State", "7")]
    [InlineData("State", "\"Ready\"")]
    [InlineData("State", "null")]
    [InlineData("ShutdownState", "-1")]
    [InlineData("ShutdownState", "4")]
    [InlineData("ShutdownState", "\"Busy\"")]
    [InlineData("ShutdownState", "null")]
    public void ConsumerOwnedGraphRejectsUndefinedOrNonnumericRuntimeState(string property, string value)
    {
        TensorEnumConsumerContext context = CreateContext();
        string json = "{\"State\":2,\"ShutdownState\":2,\"Backend\":999}";
        json = json.Replace("\"" + property + "\":2", "\"" + property + "\":" + value, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, context.TensorEnumConsumerObservation));
    }

    [Fact]
    public void ActualForeignConsumerAndTensorMetadataCollectAfterCodecExecution()
    {
        WeakReference generation = ExerciseForeign();
        for (int attempt = 0; attempt < 30 && generation.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign()
    {
        var generation = new ConsumerGeneration(Path.GetDirectoryName(typeof(TensorRuntimeEnumConsumerTests).Assembly.Location)!);
        try
        {
            Assembly consumer = generation.LoadFromAssemblyPath(typeof(TensorRuntimeEnumConsumerTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(consumer));
            Assert.Equal(typeof(TensorRuntimeEnumConsumerTests).Assembly.ManifestModule.ModuleVersionId, consumer.ManifestModule.ModuleVersionId);
            consumer.GetType(typeof(TensorRuntimeEnumConsumerTests).FullName!, true)!
                .GetMethod(nameof(ExecuteForeign), BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            return new(generation);
        }
        finally { generation.Unload(); }
    }

    public static void ExecuteForeign()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(TensorRuntimeEnumConsumerTests).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(TensorRuntimeState).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(TensorRuntimeShutdownState).Assembly));
        var context = CreateContext();
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(context.GetType().Assembly));
        Assert.Equal(typeof(TensorEnumConsumerObservation), context.TensorEnumConsumerObservation.Type);
        var value = new TensorEnumConsumerObservation(TensorRuntimeState.Ready, TensorRuntimeShutdownState.Busy, (TensorBackendKind)999);
        Assert.Equal(value, JsonSerializer.Deserialize(JsonSerializer.Serialize(value, context.TensorEnumConsumerObservation), context.TensorEnumConsumerObservation));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"State\":7,\"ShutdownState\":2,\"Backend\":999}", context.TensorEnumConsumerObservation));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("999", TensorJson.TensorRuntimeState));
    }

    private static TensorEnumConsumerContext CreateContext()
    {
        var options = new JsonSerializerOptions(TensorEnumConsumerContext.Default.Options);
        options.Converters.Add(TensorJson.TensorRuntimeState.Options.Converters.Single(static converter => converter.CanConvert(typeof(TensorRuntimeState))));
        options.Converters.Add(TensorJson.TensorRuntimeShutdownState.Options.Converters.Single(static converter => converter.CanConvert(typeof(TensorRuntimeShutdownState))));
        return new(options);
    }

    private sealed class ConsumerGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) is true)
            {
                string path = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(path)) return LoadFromAssemblyPath(path);
            }
            return Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
        }
    }
}

public sealed record TensorEnumConsumerObservation(TensorRuntimeState State, TensorRuntimeShutdownState ShutdownState,
    TensorBackendKind Backend);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TensorEnumConsumerObservation))]
internal sealed partial class TensorEnumConsumerContext : JsonSerializerContext;
