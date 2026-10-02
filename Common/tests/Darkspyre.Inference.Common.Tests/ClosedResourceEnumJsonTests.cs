using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.Tests;

public sealed class ClosedResourceEnumJsonTests
{
    public static TheoryData<string, int> InvalidValues
    {
        get
        {
            var rows = new TheoryData<string, int>();
            foreach (string name in new[] { "InferenceMediaSourceKind", "InferenceResourceOperationPhase", "InferenceResourceCommitOutcome" })
                foreach (int value in new[] { -1, 99, int.MinValue, int.MaxValue }) rows.Add(name, value);
            return rows;
        }
    }

    [Theory, MemberData(nameof(InvalidValues))]
    public void ActualGetterReadRefusesUndefined(string name, int number) => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize(number.ToString(CultureInfo.InvariantCulture), Metadata(name)));

    [Theory, MemberData(nameof(InvalidValues))]
    public void ActualGetterWriteRefusesUndefined(string name, int number)
    {
        JsonTypeInfo metadata = Metadata(name);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Enum.ToObject(metadata.Type, number), metadata));
    }

    [Theory]
    [InlineData("Phase")]
    [InlineData("CommitOutcome")]
    [InlineData("SelectionOutcome")]
    public void NestedFailureReadRefusesUndefined(string property) => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize("{\"Phase\":0,\"CommitOutcome\":0,\"SelectionOutcome\":0}".Replace("\"" + property + "\":0", "\"" + property + "\":99", StringComparison.Ordinal),
            InferenceJson.InferenceResourceOperationFailure));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NestedFailureWriteRefusesUndefined(int field)
    {
        var failure = new InferenceResourceOperationFailure(field == 0 ? (InferenceResourceOperationPhase)99 : InferenceResourceOperationPhase.Read,
            null, field == 1 ? (InferenceResourceCommitOutcome)99 : InferenceResourceCommitOutcome.NotAttempted)
        { SelectionOutcome = field == 2 ? (InferenceResourceCommitOutcome)99 : InferenceResourceCommitOutcome.Unknown };
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(failure, InferenceJson.InferenceResourceOperationFailure));
    }

    [Fact]
    public void NestedCapabilityReadRefusesUndefined() => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize("{\"SupportedSizes\":[],\"SupportedReferenceKinds\":[99]}", InferenceJson.ImageCapabilities));

    [Fact]
    public void NestedCapabilityWriteRefusesUndefined() => Assert.Throws<JsonException>(() =>
        JsonSerializer.Serialize(new ImageCapabilities { SupportedSizes = [], SupportedReferenceKinds = new HashSet<InferenceMediaSourceKind> { (InferenceMediaSourceKind)99 } },
            InferenceJson.ImageCapabilities));

    [Fact]
    public void DefinedNumericValuesAndObservedUnknownOrZeroRemainExact()
    {
        foreach (string name in new[] { "InferenceMediaSourceKind", "InferenceResourceOperationPhase", "InferenceResourceCommitOutcome" })
        {
            JsonTypeInfo metadata = Metadata(name);
            foreach (object value in Enum.GetValues(metadata.Type))
            {
                string json = Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                Assert.Equal(json, JsonSerializer.Serialize(value, metadata));
                Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize(json, metadata), metadata));
            }
        }
        var failure = new InferenceResourceOperationFailure(InferenceResourceOperationPhase.Commit, null, InferenceResourceCommitOutcome.Unknown, 0)
        { SelectionOutcome = InferenceResourceCommitOutcome.NotAttempted };
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(failure, InferenceJson.InferenceResourceOperationFailure), InferenceJson.InferenceResourceOperationFailure)!;
        Assert.Equal(failure, restored); Assert.Equal(0, restored.BytesTransferred); Assert.Null(restored.Reference);
    }

    public static int ExerciseClosedPolicy()
    {
        var test = new ClosedResourceEnumJsonTests();
        foreach (string name in new[] { "InferenceMediaSourceKind", "InferenceResourceOperationPhase", "InferenceResourceCommitOutcome" })
        {
            foreach (int number in new[] { -1, 99, int.MinValue, int.MaxValue })
            {
                test.ActualGetterReadRefusesUndefined(name, number);
                test.ActualGetterWriteRefusesUndefined(name, number);
            }
        }
        foreach (string property in new[] { "Phase", "CommitOutcome", "SelectionOutcome" }) test.NestedFailureReadRefusesUndefined(property);
        foreach (int field in new[] { 0, 1, 2 }) test.NestedFailureWriteRefusesUndefined(field);
        test.NestedCapabilityReadRefusesUndefined(); test.NestedCapabilityWriteRefusesUndefined();
        test.DefinedNumericValuesAndObservedUnknownOrZeroRemainExact();
        return 33;
    }

    [Fact]
    public void ActualForeignGettersAndConvertersExecuteAndRetire()
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
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(ClosedResourceEnumJsonTests).Assembly.Location);
            foreach ((Assembly source, Assembly actual) in new[] { (typeof(InferenceJson).Assembly, common), (typeof(ClosedResourceEnumJsonTests).Assembly, fixture) })
            {
                Assert.NotSame(source, actual); Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual)); roots.Add(new(actual));
            }
            Type facade = common.GetType(typeof(InferenceJson).FullName!, true)!; roots.Add(new(facade));
            Type proofType = fixture.GetType(typeof(ClosedResourceEnumJsonTests).FullName!, true)!;
            MethodInfo proof = proofType.GetMethod(nameof(ExerciseClosedPolicy))!;
            Assert.Same(fixture, proof.Module.Assembly); roots.Add(new(proofType)); roots.Add(new(proof));
            foreach (string name in new[] { "InferenceMediaSourceKind", "InferenceResourceOperationPhase", "InferenceResourceCommitOutcome" })
            {
                PropertyInfo getter = facade.GetProperty(name)!; Assert.Same(common, getter.GetMethod!.Module.Assembly);
                var metadata = (JsonTypeInfo)getter.GetValue(null)!; var converter = metadata.Options.GetConverter(metadata.Type);
                Assert.Same(common, metadata.Type.Assembly); Assert.Same(common, converter.GetType().Assembly);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(converter.GetType().Assembly));
                roots.Add(new(metadata)); roots.Add(new(metadata.Type)); roots.Add(new(converter)); roots.Add(new(metadata.Options.TypeInfoResolver!));
            }
            Assert.Equal(33, proof.Invoke(null, null));
        }
        finally { generation.Unload(); }
        return roots.ToArray();
    }

    private static JsonTypeInfo Metadata(string name) => (JsonTypeInfo)typeof(InferenceJson).GetProperty(name)!.GetValue(null)!;
    private sealed class Generation() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()))
            ?? Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }
}
