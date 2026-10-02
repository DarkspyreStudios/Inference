using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Realtime;

namespace Darkspyre.Inference.Common.Tests;

public sealed class HostedKindJsonTests
{
    [Fact]
    public void UndefinedRootReadRefuses() => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize("99", InferenceJson.LlmHostedToolKind));

    [Fact]
    public void UndefinedRootWriteRefuses() => Assert.Throws<JsonException>(() =>
        JsonSerializer.Serialize((LlmHostedToolKind)99, InferenceJson.LlmHostedToolKind));

    [Theory]
    [InlineData("-1")]
    [InlineData("8")]
    [InlineData("99")]
    [InlineData("2147483648")]
    [InlineData("UnreportedKind")]
    public void UndefinedOrMalformedDictionaryKeysRefuse(string key) => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize("{\"InputTokens\":0,\"OutputTokens\":0,\"TotalTokens\":0,\"HostedToolCalls\":{\"" + key + "\":0}}", InferenceJson.LlmUsage));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UndefinedNestedReadRefuses(int graph)
    {
        JsonTypeInfo metadata = graph switch
        {
            0 => InferenceJson.LlmCapabilities,
            1 => InferenceJson.RealtimeCapabilities,
            _ => InferenceJson.LlmUsage
        };
        string json = graph switch
        {
            0 => "{\"HostedTools\":[99]}",
            1 => "{\"SupportedHostedToolKinds\":[99]}",
            _ => "{\"InputTokens\":0,\"OutputTokens\":0,\"TotalTokens\":0,\"HostedToolCalls\":{\"99\":0}}"
        };
        if (graph == 1)
        {
            Exception? error = Record.Exception(() => JsonSerializer.Deserialize(json, metadata));
            Assert.True(error is JsonException or ArgumentOutOfRangeException);
        }
        else Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, metadata));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UndefinedNestedWriteRefuses(int graph)
    {
        LlmHostedToolKind invalid = (LlmHostedToolKind)99;
        switch (graph)
        {
            case 0:
                Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new LlmCapabilities { HostedTools = new HashSet<LlmHostedToolKind> { invalid } }, InferenceJson.LlmCapabilities));
                break;
            case 1:
                Assert.Throws<ArgumentOutOfRangeException>(() => new RealtimeCapabilities { SupportedHostedToolKinds = new HashSet<LlmHostedToolKind> { invalid } });
                break;
            default:
                Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new LlmUsage(0, 0, 0) { HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [invalid] = 0 } }, InferenceJson.LlmUsage));
                break;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void DefinedValuesKeepNumericValuesAndNamedOrNumericKeys(int number)
    {
        LlmHostedToolKind kind = (LlmHostedToolKind)number;
        string numeric = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(numeric, JsonSerializer.Serialize(kind, InferenceJson.LlmHostedToolKind));
        Assert.Equal(kind, JsonSerializer.Deserialize(numeric, InferenceJson.LlmHostedToolKind));
        foreach (string key in new[] { kind.ToString(), kind.ToString().ToLowerInvariant(), numeric })
        {
            string json = "{\"InputTokens\":0,\"OutputTokens\":0,\"TotalTokens\":0,\"HostedToolCalls\":{\"" + key + "\":0}}";
            var usage = JsonSerializer.Deserialize(json, InferenceJson.LlmUsage)!;
            Assert.Equal(0, usage.HostedToolCalls![kind]);
            using JsonDocument written = JsonDocument.Parse(JsonSerializer.Serialize(usage, InferenceJson.LlmUsage));
            Assert.Equal(0, written.RootElement.GetProperty("HostedToolCalls").GetProperty(kind.ToString()).GetInt32());
        }
        Assert.Null(JsonSerializer.Deserialize("{\"InputTokens\":0,\"OutputTokens\":0,\"TotalTokens\":0,\"HostedToolCalls\":null}", InferenceJson.LlmUsage)!.HostedToolCalls);
    }

    public static int ExerciseClosedPolicy()
    {
        var tests = new HostedKindJsonTests();
        tests.UndefinedRootReadRefuses(); tests.UndefinedRootWriteRefuses();
        foreach (string key in new[] { "-1", "8", "99", "2147483648", "UnreportedKind" }) tests.UndefinedOrMalformedDictionaryKeysRefuse(key);
        for (int graph = 0; graph < 3; graph++) { tests.UndefinedNestedReadRefuses(graph); tests.UndefinedNestedWriteRefuses(graph); }
        for (int number = 0; number < 8; number++) tests.DefinedValuesKeepNumericValuesAndNamedOrNumericKeys(number);
        Assert.Same(typeof(InferenceJson).Assembly, InferenceJson.LlmHostedToolKind.Converter.GetType().Assembly);
        return 21;
    }

    [Fact]
    public void ActualForeignOwnedPolicyExecutesAndRetires()
    {
        WeakReference[] roots = ExerciseForeign();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        foreach (WeakReference root in roots) Assert.False(root.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign()
    {
        var generation = new AssemblyLoadContext("hosted-kind-common", isCollectible: true);
        var roots = new List<WeakReference> { new(generation) };
        try
        {
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(HostedKindJsonTests).Assembly.Location);
            roots.Add(new(common));
            roots.Add(new(fixture));
            foreach ((Assembly local, Assembly actual) in new[] { (typeof(InferenceJson).Assembly, common), (typeof(HostedKindJsonTests).Assembly, fixture) })
            {
                Assert.NotSame(local, actual);
                Assert.Equal(local.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
            }
            MethodInfo proof = fixture.GetType(typeof(HostedKindJsonTests).FullName!, true)!.GetMethod(nameof(ExerciseClosedPolicy))!;
            Assert.Same(fixture, proof.DeclaringType!.Assembly);
            roots.Add(new(proof.DeclaringType));
            Assembly owner = generation.Assemblies.Single(assembly => assembly.GetName().Name == typeof(InferenceJson).Assembly.GetName().Name);
            Type facade = owner.GetType(typeof(InferenceJson).FullName!, true)!;
            var metadata = (JsonTypeInfo)facade.GetProperty(nameof(InferenceJson.LlmHostedToolKind))!.GetValue(null)!;
            Assert.Same(owner, metadata.Type.Assembly);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
            Assembly commonOwner = generation.Assemblies.Single(assembly => assembly.GetName().Name == typeof(InferenceJson).Assembly.GetName().Name);
            Type kind = commonOwner.GetType(typeof(LlmHostedToolKind).FullName!, true)!;
            var converter = metadata.Options.GetConverter(kind);
            Assert.Same(commonOwner, converter.GetType().Assembly);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(converter.GetType().Assembly));
            roots.Add(new(facade));
            roots.Add(new(metadata));
            roots.Add(new(metadata.Type));
            roots.Add(new(metadata.Options.TypeInfoResolver!));
            roots.Add(new(converter));
            Assert.Equal(21, proof.Invoke(null, null));
        }
        finally { generation.Unload(); }
        return roots.ToArray();
    }
}
