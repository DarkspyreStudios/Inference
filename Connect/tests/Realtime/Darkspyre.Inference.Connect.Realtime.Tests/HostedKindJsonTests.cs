using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Realtime.Tests;

public sealed class HostedKindJsonTests
{
    [Fact]
    public void UndefinedCapabilityReadRefuses()
    {
        Exception? error = Record.Exception(() => JsonSerializer.Deserialize("{\"SupportedHostedToolKinds\":[99]}", ConnectRealtimeJson.RealtimeCapabilities));
        Assert.True(error is JsonException or ArgumentOutOfRangeException);
    }

    [Fact]
    public void UndefinedCapabilityProducerRefuses() => Assert.Throws<ArgumentOutOfRangeException>(() =>
        new RealtimeCapabilities { SupportedHostedToolKinds = new HashSet<LlmHostedToolKind> { (LlmHostedToolKind)99 } });

    [Fact]
    public void UndefinedUsageKeyReadRefuses() => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
        "{\"Identity\":{},\"Stage\":\"response.done\",\"Usage\":{\"InputTokens\":0,\"OutputTokens\":0,\"TotalTokens\":0,\"HostedToolCalls\":{\"99\":0}}}", ConnectRealtimeJson.RealtimeResponseEvent));

    [Fact]
    public void UndefinedUsageKeyWriteRefuses() => Assert.Throws<JsonException>(() => JsonSerializer.Serialize(
        new RealtimeResponseEvent(new(), "response.done", null, new LlmUsage(0, 0, 0) { HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [(LlmHostedToolKind)99] = 0 } }, null), ConnectRealtimeJson.RealtimeResponseEvent));

    [Fact]
    public void ValidKindsAndZeroOrUnknownUsageRemainExact()
    {
        var capabilities = new RealtimeCapabilities { SupportedHostedToolKinds = Enum.GetValues<LlmHostedToolKind>().ToHashSet() };
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(capabilities, ConnectRealtimeJson.RealtimeCapabilities), ConnectRealtimeJson.RealtimeCapabilities)!;
        Assert.Equal(8, restored.SupportedHostedToolKinds.Count);
        var observed = new RealtimeResponseEvent(new(), "response.done", null, new LlmUsage(0, 0, 0) { HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [LlmHostedToolKind.WebSearch] = 0 } }, null);
        var result = JsonSerializer.Deserialize(JsonSerializer.Serialize(observed, ConnectRealtimeJson.RealtimeResponseEvent), ConnectRealtimeJson.RealtimeResponseEvent)!;
        Assert.Equal(0, result.Usage!.HostedToolCalls![LlmHostedToolKind.WebSearch]);
        Assert.Null(result.Usage.HostedToolCallCount);
        Assert.Null(JsonSerializer.Deserialize(JsonSerializer.Serialize(observed with { Usage = null }, ConnectRealtimeJson.RealtimeResponseEvent), ConnectRealtimeJson.RealtimeResponseEvent)!.Usage);
        Assert.Same(InferenceJson.LlmHostedToolKind.Converter, ConnectRealtimeJson.RealtimeCapabilities.Options.GetConverter(typeof(LlmHostedToolKind)));
    }

    public static int ExerciseClosedPolicy()
    {
        var tests = new HostedKindJsonTests();
        tests.UndefinedCapabilityReadRefuses(); tests.UndefinedCapabilityProducerRefuses();
        tests.UndefinedUsageKeyReadRefuses(); tests.UndefinedUsageKeyWriteRefuses();
        tests.ValidKindsAndZeroOrUnknownUsageRemainExact();
        return 5;
    }

    [Fact]
    public void ActualForeignFamilyPolicyExecutesAndRetires()
    {
        WeakReference generation = ExerciseForeign();
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(generation.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign()
    {
        var generation = new AssemblyLoadContext("hosted-kind-realtime", isCollectible: true);
        var weak = new WeakReference(generation);
        try
        {
            foreach (Assembly local in new[] { typeof(InferenceJson).Assembly, typeof(ConnectLlmJson).Assembly, typeof(ConnectRealtimeJson).Assembly, typeof(HostedKindJsonTests).Assembly })
            {
                Assembly actual = generation.LoadFromAssemblyPath(local.Location);
                Assert.NotSame(local, actual);
                Assert.Equal(local.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
            }
            Assembly fixture = generation.Assemblies.Single(assembly => assembly.GetName().Name == typeof(HostedKindJsonTests).Assembly.GetName().Name);
            MethodInfo proof = fixture.GetType(typeof(HostedKindJsonTests).FullName!, true)!.GetMethod(nameof(ExerciseClosedPolicy))!;
            Assert.Same(fixture, proof.DeclaringType!.Assembly);
            Assert.Equal(5, proof.Invoke(null, null));
        }
        finally { generation.Unload(); }
        return weak;
    }
}
