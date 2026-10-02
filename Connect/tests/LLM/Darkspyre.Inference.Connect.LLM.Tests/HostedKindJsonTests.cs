using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedKindJsonTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UndefinedNestedReadRefuses(int graph)
    {
        JsonTypeInfo metadata = graph switch
        {
            0 => ConnectLlmJson.LlmAgentCapabilities,
            1 => ConnectLlmJson.ChatResult.Options.GetTypeInfo<LlmUsage>(),
            _ => ConnectLlmJson.ChatResult
        };
        const string usage = "{\"InputTokens\":0,\"OutputTokens\":0,\"TotalTokens\":0,\"HostedToolCalls\":{\"99\":0}}";
        string json = graph switch
        {
            0 => "{\"HostedTools\":[99]}",
            1 => usage,
            _ => "{\"Text\":\"actual\",\"Status\":0,\"Diagnostics\":{\"HttpStatusCode\":200},\"Usage\":" + usage + "}"
        };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, metadata));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UndefinedNestedWriteRefuses(int graph)
    {
        var kinds = new HashSet<LlmHostedToolKind> { (LlmHostedToolKind)99 };
        switch (graph)
        {
            case 0:
                Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new LlmAgentCapabilities { HostedTools = kinds }, ConnectLlmJson.LlmAgentCapabilities));
                break;
            case 1:
                Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new LlmUsage(0, 0, 0) { HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [(LlmHostedToolKind)99] = 0 } }, ConnectLlmJson.ChatResult.Options.GetTypeInfo<LlmUsage>()));
                break;
            default:
                var result = new LlmChatResult { Text = "actual", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null), Usage = new LlmUsage(0, 0, 0) { HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [(LlmHostedToolKind)99] = 0 } } };
                Assert.Throws<JsonException>(() => JsonSerializer.Serialize(result, ConnectLlmJson.ChatResult));
                break;
        }
    }

    [Fact]
    public void ValidKindsAndUnknownOrZeroCountsRemainExact()
    {
        foreach (LlmHostedToolKind kind in Enum.GetValues<LlmHostedToolKind>())
        {
            var result = new LlmChatResult { Text = "actual", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null), Usage = new LlmUsage(0, 0, 0) { HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [kind] = 0 }, HostedToolCallCount = 0 } };
            string json = JsonSerializer.Serialize(result, ConnectLlmJson.ChatResult);
            var restored = JsonSerializer.Deserialize(json, ConnectLlmJson.ChatResult)!;
            Assert.Equal(0, restored.Usage!.HostedToolCalls![kind]);
            Assert.Equal(0, restored.Usage.HostedToolCallCount);
            Assert.Null(restored.Usage.HostedSourcesUsed);
            using JsonDocument data = JsonDocument.Parse(json);
            Assert.Equal(0, data.RootElement.GetProperty("Usage").GetProperty("HostedToolCalls").GetProperty(kind.ToString()).GetInt32());
        }
        var converter = ConnectLlmJson.ChatResult.Options.GetConverter(typeof(LlmHostedToolKind));
        Assert.Same(typeof(InferenceJson).Assembly, converter.GetType().Assembly);
        Assert.Same(InferenceJson.LlmHostedToolKind.Converter, converter);
    }

    public static int ExerciseClosedPolicy()
    {
        var tests = new HostedKindJsonTests();
        for (int graph = 0; graph < 3; graph++) { tests.UndefinedNestedReadRefuses(graph); tests.UndefinedNestedWriteRefuses(graph); }
        tests.ValidKindsAndUnknownOrZeroCountsRemainExact();
        return 7;
    }

    [Fact]
    public void ActualForeignCommonAndConnectPolicyExecutesAndRetires()
    {
        WeakReference generation = ExerciseForeign();
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(generation.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign()
    {
        var generation = new AssemblyLoadContext("hosted-kind-connect", isCollectible: true);
        var weak = new WeakReference(generation);
        try
        {
            foreach (Assembly local in new[] { typeof(InferenceJson).Assembly, typeof(ConnectLlmJson).Assembly, typeof(HostedKindJsonTests).Assembly })
            {
                Assembly actual = generation.LoadFromAssemblyPath(local.Location);
                Assert.NotSame(local, actual);
                Assert.Equal(local.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
            }
            Assembly fixture = generation.Assemblies.Single(assembly => assembly.GetName().Name == typeof(HostedKindJsonTests).Assembly.GetName().Name);
            MethodInfo proof = fixture.GetType(typeof(HostedKindJsonTests).FullName!, true)!.GetMethod(nameof(ExerciseClosedPolicy))!;
            Assert.Same(fixture, proof.DeclaringType!.Assembly);
            Assert.Equal(7, proof.Invoke(null, null));
        }
        finally { generation.Unload(); }
        return weak;
    }
}
