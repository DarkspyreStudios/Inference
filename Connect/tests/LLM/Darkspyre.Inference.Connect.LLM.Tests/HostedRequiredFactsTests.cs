using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedRequiredFactsTests
{
    public static IEnumerable<object[]> MissingCases()
    {
        foreach ((string root, string[] fields) in new (string, string[])[]
        {
            ("LlmAgentHostedToolCallStartedEvent", ["ThreadId", "TurnId", "RequestId", "CallId", "Kind", "Name"]),
            ("LlmAgentHostedToolCallProgressEvent", ["ThreadId", "TurnId", "RequestId", "CallId", "Phase"]),
            ("LlmAgentHostedToolCallCompletedEvent", ["ThreadId", "TurnId", "RequestId", "Call"]),
            ("LlmAgentCitationEvent", ["ThreadId", "TurnId", "RequestId", "Citation"]),
            ("LlmAgentCompactedEvent", ["ThreadId", "TurnId", "RequestId", "ContextRevision"])
        })
            foreach (string field in fields) yield return [root, field];
    }

    [Theory]
    [MemberData(nameof(MissingCases))]
    public void MissingRequiredFactsRefuseAtOwnedMetadata(string root, string field)
    {
        var data = JsonNode.Parse(HostedContractTests.ForeignCases.Single(row => (string)row[0] == root)[1].ToString()!)!.AsObject();
        Assert.True(data.Remove(field));
        var metadata = (JsonTypeInfo)typeof(ConnectLlmJson).GetProperty(root)!.GetValue(null)!;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(data.ToJsonString(), metadata));
    }

    public static IEnumerable<object[]> NullCases() => MissingCases().Where(row => (string)row[1] is not ("Kind" or "Phase" or "ContextRevision"));

    [Theory]
    [MemberData(nameof(NullCases))]
    public void NullRequiredReferencesRefuseAtOwnedMetadata(string root, string field)
    {
        var data = JsonNode.Parse(HostedContractTests.ForeignCases.Single(row => (string)row[0] == root)[1].ToString()!)!.AsObject();
        data[field] = null;
        var metadata = (JsonTypeInfo)typeof(ConnectLlmJson).GetProperty(root)!.GetValue(null)!;
        Assert.Throws<ArgumentNullException>(() => JsonSerializer.Deserialize(data.ToJsonString(), metadata));
    }

    [Fact]
    public void ConnectAndConsumerGraphsUseTheActualCommonAndEventPolicies() => ExercisePolicies();

    public static int ExercisePolicies()
    {
        var tests = new HostedRequiredFactsTests();
        foreach (object[] row in MissingCases()) tests.MissingRequiredFactsRefuseAtOwnedMetadata((string)row[0], (string)row[1]);
        foreach (object[] row in NullCases()) tests.NullRequiredReferencesRefuseAtOwnedMetadata((string)row[0], (string)row[1]);
        const string image = "{\"type\":\"image\",\"Data\":null,\"MediaType\":\"image/png\"}";
        const string scope = "\"ThreadId\":\"thread\",\"TurnId\":\"turn\",\"RequestId\":\"request\"";
        string completion = "{" + scope + ",\"Call\":{\"CallId\":\"call\",\"Kind\":3,\"Name\":\"image\",\"Status\":\"actual\",\"Output\":" + image + "}}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(completion, ConnectLlmJson.LlmAgentHostedToolCallCompletedEvent));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ModelId\":\"model\",\"HostedTools\":[{\"type\":\"image-generation\",\"Action\":99}]}", ConnectLlmJson.ChatRequest));
        Assert.Throws<ArgumentNullException>(() => JsonSerializer.Deserialize("{\"Text\":\"actual\",\"Status\":0,\"Diagnostics\":{\"HttpStatusCode\":200},\"Citations\":[{\"Url\":null}]}", ConnectLlmJson.ChatResult));
        var options = new JsonSerializerOptions(ConnectLlmJson.ChatRequest.Options)
        { TypeInfoResolver = JsonTypeInfoResolver.Combine(ConnectLlmJson.ChatRequest.Options.TypeInfoResolver, HostedConnectConsumerContext.Default) };
        options.MakeReadOnly();
        var envelope = options.GetTypeInfo<HostedConnectConsumerEnvelope>();
        Assert.Same(InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter,
            options.GetTypeInfo<LlmHostedImageOutput>().Properties.Single(property => property.Name == "Data").CustomConverter);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Completed\":" + completion + "}", envelope));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Request\":{\"ModelId\":\"model\",\"HostedTools\":[{\"type\":\"image-generation\",\"Action\":99}]}}", envelope));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Progress\":{" + scope + ",\"CallId\":\"call\",\"Phase\":99}}", envelope));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Progress\":{\"ThreadId\":\"thread\",\"RequestId\":\"request\",\"CallId\":\"call\",\"Phase\":0}}", envelope));
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize("{\"Progress\":{" + scope + ",\"CallId\":\" \",\"Phase\":0}}", envelope));
        Assert.Throws<ArgumentException>(() => JsonSerializer.Serialize(new HostedConnectConsumerEnvelope(null, null,
            new LlmAgentHostedToolCallProgressEvent("thread", "turn", "request", "call", LlmHostedToolPhase.Searching) with { TurnId = "" }), envelope));
        var value = new HostedConnectConsumerEnvelope(new LlmChatRequest { ModelId = "model", HostedTools = [new LlmImageGenerationTool()] },
            new LlmAgentHostedToolCallCompletedEvent("thread", "turn", "request", new LlmHostedToolCall("call", LlmHostedToolKind.ImageGeneration,
                "image", "actual-status", null, new LlmHostedImageOutput(new byte[] { 0, 1, 2 }, "image/png"))),
            new LlmAgentHostedToolCallProgressEvent("thread", "turn", "request", "call", LlmHostedToolPhase.Generating));
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(value, envelope), envelope)!;
        Assert.Equal(new byte[] { 0, 1, 2 }, Assert.IsType<LlmHostedImageOutput>(restored.Completed!.Call.Output).Data.ToArray());
        Assert.Equal(LlmImageGenerationAction.Auto, Assert.IsType<LlmImageGenerationTool>(Assert.Single(restored.Request!.HostedTools)).Action);
        Assert.Equal(LlmHostedToolPhase.Generating, restored.Progress!.Phase);
        return 9;
    }

    [Fact]
    public void ActualForeignCommonConnectAndConsumerPoliciesExecuteThenRetire()
    {
        WeakReference generation = ExerciseForeignPolicies();
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(generation.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeignPolicies()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(ConnectLlmJson).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            var common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            var connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            var tests = generation.LoadFromAssemblyPath(typeof(HostedRequiredFactsTests).Assembly.Location);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(ConnectLlmJson).Assembly.ManifestModule.ModuleVersionId, connect.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(HostedRequiredFactsTests).Assembly.ManifestModule.ModuleVersionId, tests.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(connect));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(tests));
            Assert.Equal(9, tests.GetType(typeof(HostedRequiredFactsTests).FullName!)!.GetMethod(nameof(ExercisePolicies))!.Invoke(null, null));
        }
        finally { generation.Unload(); }
        return weak;
    }

    [Fact]
    public void NewObservationConstructorsRejectMalformedProducerFacts()
    {
        Assert.Throws<ArgumentException>(() => new LlmAgentHostedToolCallStartedEvent("thread", "turn", "request", "", LlmHostedToolKind.WebSearch, "actual"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmAgentHostedToolCallStartedEvent("thread", "turn", "request", "call", (LlmHostedToolKind)99, "actual"));
        Assert.Throws<ArgumentException>(() => new LlmAgentHostedToolCallProgressEvent("thread", "turn", "request", "call", LlmHostedToolPhase.Searching) with { CallId = " " });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmAgentHostedToolCallProgressEvent("thread", "turn", "request", "call", LlmHostedToolPhase.Searching) with { Phase = (LlmHostedToolPhase)99 });
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}

internal sealed record HostedConnectConsumerEnvelope(LlmChatRequest? Request, LlmAgentHostedToolCallCompletedEvent? Completed, LlmAgentHostedToolCallProgressEvent? Progress);

[JsonSerializable(typeof(HostedConnectConsumerEnvelope))]
internal sealed partial class HostedConnectConsumerContext : JsonSerializerContext;
