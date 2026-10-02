using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ClosedRuntimePolicyJsonTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ActualGetterReadRefusesUndefined(int number) => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize(number.ToString(CultureInfo.InvariantCulture), ConnectLlmJson.LlmAgentRuntimePolicyKind));

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ActualGetterWriteRefusesUndefined(int number) => Assert.Throws<JsonException>(() =>
        JsonSerializer.Serialize((LlmAgentRuntimePolicyKind)number, ConnectLlmJson.LlmAgentRuntimePolicyKind));

    [Fact]
    public void NestedCapabilityReadRefusesUndefined() => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize("{\"SupportedRuntimePolicies\":[99]}", ConnectLlmJson.LlmAgentCapabilities));

    [Fact]
    public void NestedCapabilityWriteRefusesUndefined() => Assert.Throws<JsonException>(() =>
        JsonSerializer.Serialize(new LlmAgentCapabilities { SupportedRuntimePolicies = new HashSet<LlmAgentRuntimePolicyKind> { (LlmAgentRuntimePolicyKind)99 } }, ConnectLlmJson.LlmAgentCapabilities));

    [Fact]
    public void DefinedNumericValuesAndCapabilitySetsRemainExact()
    {
        foreach (LlmAgentRuntimePolicyKind value in Enum.GetValues<LlmAgentRuntimePolicyKind>())
        {
            string json = ((int)value).ToString(CultureInfo.InvariantCulture);
            Assert.Equal(json, JsonSerializer.Serialize(value, ConnectLlmJson.LlmAgentRuntimePolicyKind));
            Assert.Equal(value, JsonSerializer.Deserialize(json, ConnectLlmJson.LlmAgentRuntimePolicyKind));
        }
        var restored = JsonSerializer.Deserialize("{\"SupportedRuntimePolicies\":[0,1]}", ConnectLlmJson.LlmAgentCapabilities)!;
        Assert.True(restored.SupportedRuntimePolicies.SetEquals(Enum.GetValues<LlmAgentRuntimePolicyKind>()));
    }

    public static int ExerciseClosedPolicy()
    {
        var test = new ClosedRuntimePolicyJsonTests();
        foreach (int number in new[] { -1, 99, int.MinValue, int.MaxValue })
        { test.ActualGetterReadRefusesUndefined(number); test.ActualGetterWriteRefusesUndefined(number); }
        test.NestedCapabilityReadRefusesUndefined(); test.NestedCapabilityWriteRefusesUndefined(); test.DefinedNumericValuesAndCapabilitySetsRemainExact();
        return 11;
    }

    [Fact]
    public void ActualForeignGetterAndConverterExecuteAndRetire()
    {
        WeakReference[] roots = ExerciseForeign();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        foreach (WeakReference root in roots) Assert.False(root.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign()
    {
        var generation = new Generation(); var roots = new List<WeakReference> { new(generation) };
        try
        {
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(ClosedRuntimePolicyJsonTests).Assembly.Location);
            foreach ((Assembly source, Assembly actual) in new[] { (typeof(InferenceJson).Assembly, common), (typeof(ConnectLlmJson).Assembly, connect), (typeof(ClosedRuntimePolicyJsonTests).Assembly, fixture) })
            {
                Assert.NotSame(source, actual); Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual)); roots.Add(new(actual));
            }
            Type facade = connect.GetType(typeof(ConnectLlmJson).FullName!, true)!;
            PropertyInfo getter = facade.GetProperty(nameof(ConnectLlmJson.LlmAgentRuntimePolicyKind))!;
            Assert.Same(connect, getter.GetMethod!.Module.Assembly);
            var metadata = (JsonTypeInfo)getter.GetValue(null)!; var converter = metadata.Options.GetConverter(metadata.Type);
            Assert.Same(connect, metadata.Type.Assembly); Assert.Same(connect, converter.GetType().Assembly);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(converter.GetType().Assembly));
            Type proofType = fixture.GetType(typeof(ClosedRuntimePolicyJsonTests).FullName!, true)!;
            MethodInfo proof = proofType.GetMethod(nameof(ExerciseClosedPolicy))!;
            Assert.Same(fixture, proof.Module.Assembly); Assert.Equal(11, proof.Invoke(null, null));
            roots.AddRange([new(facade), new(proofType), new(proof), new(metadata), new(metadata.Type), new(converter), new(metadata.Options.TypeInfoResolver!)]);
        }
        finally { generation.Unload(); }
        return roots.ToArray();
    }

    private sealed class Generation() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()))
            ?? Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }
}
