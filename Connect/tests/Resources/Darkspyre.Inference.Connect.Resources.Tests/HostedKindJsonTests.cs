using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.Video;

namespace Darkspyre.Inference.Connect.Resources.Tests;

public sealed class HostedKindJsonTests
{
    [Fact]
    public void UndefinedNestedUsageKeyReadRefuses() => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
        "{\"CustomId\":\"actual\",\"Result\":{\"Text\":\"actual\",\"Status\":0,\"Diagnostics\":{\"HttpStatusCode\":200},\"Usage\":{\"InputTokens\":0,\"OutputTokens\":0,\"TotalTokens\":0,\"HostedToolCalls\":{\"99\":0}}}}", ConnectResourcesJson.LlmBatchChatResult));

    [Fact]
    public void UndefinedNestedUsageKeyWriteRefuses() => Assert.Throws<JsonException>(() => JsonSerializer.Serialize(
        Result((LlmHostedToolKind)99), ConnectResourcesJson.LlmBatchChatResult));

    private static LlmBatchChatResult Result(LlmHostedToolKind kind) => new("actual", new()
    {
        Text = "actual",
        Status = LlmCompletionStatus.Completed,
        Diagnostics = new(200, null, null),
        Usage = new(0, 0, 0) { HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [kind] = 0 } }
    });

    [Fact]
    public void AllKindsPreserveNamedKeysZeroAndUnknownFacts()
    {
        foreach (LlmHostedToolKind kind in Enum.GetValues<LlmHostedToolKind>())
        {
            string json = JsonSerializer.Serialize(Result(kind), ConnectResourcesJson.LlmBatchChatResult);
            var result = JsonSerializer.Deserialize(json, ConnectResourcesJson.LlmBatchChatResult)!;
            Assert.Equal(0, result.Result.Usage!.HostedToolCalls![kind]);
            Assert.Null(result.Result.Usage.HostedToolCallCount);
            using JsonDocument data = JsonDocument.Parse(json);
            Assert.Equal(0, data.RootElement.GetProperty("Result").GetProperty("Usage").GetProperty("HostedToolCalls").GetProperty(kind.ToString()).GetInt32());
        }
        Assert.Same(InferenceJson.LlmHostedToolKind.Converter, ConnectResourcesJson.LlmBatchChatResult.Options.GetConverter(typeof(LlmHostedToolKind)));
    }

    public static int ExerciseClosedPolicy()
    {
        var tests = new HostedKindJsonTests();
        tests.UndefinedNestedUsageKeyReadRefuses(); tests.UndefinedNestedUsageKeyWriteRefuses();
        tests.AllKindsPreserveNamedKeysZeroAndUnknownFacts();
        return 3;
    }

    [Fact]
    public void ActualForeignFamilyPolicyExecutesAndRetires()
    {
        WeakReference[] roots = ExerciseForeign();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        foreach (WeakReference root in roots) Assert.False(root.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign()
    {
        var generation = new AssemblyLoadContext("hosted-kind-resources", isCollectible: true);
        var roots = new List<WeakReference> { new(generation) };
        try
        {
            foreach (Assembly local in new[] { typeof(InferenceJson).Assembly, typeof(ConnectLlmJson).Assembly, typeof(ConnectImageJson).Assembly, typeof(ConnectVideoJson).Assembly, typeof(ConnectResourcesJson).Assembly, typeof(HostedKindJsonTests).Assembly })
            {
                Assembly actual = generation.LoadFromAssemblyPath(local.Location);
                roots.Add(new(actual));
                Assert.NotSame(local, actual);
                Assert.Equal(local.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
            }
            Assembly fixture = generation.Assemblies.Single(assembly => assembly.GetName().Name == typeof(HostedKindJsonTests).Assembly.GetName().Name);
            MethodInfo proof = fixture.GetType(typeof(HostedKindJsonTests).FullName!, true)!.GetMethod(nameof(ExerciseClosedPolicy))!;
            Assert.Same(fixture, proof.DeclaringType!.Assembly);
            roots.Add(new(proof.DeclaringType));
            Assembly owner = generation.Assemblies.Single(assembly => assembly.GetName().Name == typeof(ConnectResourcesJson).Assembly.GetName().Name);
            Type facade = owner.GetType(typeof(ConnectResourcesJson).FullName!, true)!;
            var metadata = (JsonTypeInfo)facade.GetProperty(nameof(ConnectResourcesJson.LlmBatchChatResult))!.GetValue(null)!;
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
            Assert.Equal(3, proof.Invoke(null, null));
        }
        finally { generation.Unload(); }
        return roots.ToArray();
    }
}
