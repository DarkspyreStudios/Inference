using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Common.Tests;

public sealed class HostedRequiredFactsTests
{
    public static IEnumerable<object[]> MissingCases()
    {
        foreach ((string root, string[] fields) in new (string, string[])[]
        {
            ("LlmHostedToolCall", ["CallId", "Kind", "Name", "Status"]),
            ("LlmHostedToolCallStartedEvent", ["CallId", "Kind", "Name"]),
            ("LlmHostedToolCallProgressEvent", ["CallId", "Phase"]),
            ("LlmHostedToolCallCompletedEvent", ["Call"]),
            ("LlmHostedImageOutput", ["Data", "MediaType"]),
            ("LlmHostedSearchSource", ["Url"]),
            ("LlmHostedDocumentMatch", ["FileId"]),
            ("LlmCitation", ["Url"]),
            ("LlmCitationEvent", ["Citation"]),
            ("LlmDocumentSearchTool", ["CollectionIds"]),
            ("LlmRemoteMcpTool", ["ServerUrl", "ServerLabel"])
        })
            foreach (string field in fields) yield return [root, field];
    }

    [Theory]
    [MemberData(nameof(MissingCases))]
    public void MissingRequiredFactsRefuseAtOwnedMetadata(string root, string field)
    {
        var data = JsonNode.Parse(HostedMetadataTests.Roots.Single(row => (string)row[0] == root)[1].ToString()!)!.AsObject();
        Assert.True(data.Remove(field));
        var metadata = (JsonTypeInfo)typeof(InferenceJson).GetProperty(root)!.GetValue(null)!;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(data.ToJsonString(), metadata));
    }

    public static IEnumerable<object[]> NullCases() => MissingCases().Where(row => (string)row[1] is not ("Kind" or "Phase" or "Data"));

    [Theory]
    [MemberData(nameof(NullCases))]
    public void NullRequiredReferencesRefuseAtOwnedMetadata(string root, string field)
    {
        var data = JsonNode.Parse(HostedMetadataTests.Roots.Single(row => (string)row[0] == root)[1].ToString()!)!.AsObject();
        data[field] = null;
        var metadata = (JsonTypeInfo)typeof(InferenceJson).GetProperty(root)!.GetValue(null)!;
        Assert.Throws<ArgumentNullException>(() => JsonSerializer.Deserialize(data.ToJsonString(), metadata));
    }

    [Fact]
    public void NullImageBytesNeverBecomeAnObservedEmptyImage()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Data\":null,\"MediaType\":\"image/png\"}", InferenceJson.LlmHostedImageOutput));
    }

    [Theory]
    [InlineData("LlmHostedToolPhase")]
    [InlineData("LlmImageGenerationAction")]
    public void NewClosedEnumMetadataRefusesUndefinedValues(string root)
    {
        var metadata = (JsonTypeInfo)typeof(InferenceJson).GetProperty(root)!.GetValue(null)!;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", metadata));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Enum.ToObject(metadata.Type, 99), metadata));
        Assert.Equal("0", JsonSerializer.Serialize(Enum.ToObject(metadata.Type, 0), metadata));
    }

    [Fact]
    public void OptionalRequestControlsKeepTheirDefaultAndNullPolicies()
    {
        Assert.Equal(LlmImageGenerationAction.Auto, JsonSerializer.Deserialize("{}", InferenceJson.LlmImageGenerationTool)!.Action);
        var source = JsonSerializer.Deserialize("{\"Url\":\"actual\"}", InferenceJson.LlmHostedSearchSource)!;
        Assert.Null(source.Title);
        var call = JsonSerializer.Deserialize("{\"CallId\":\"actual\",\"Kind\":0,\"Name\":\"name\",\"Status\":\"future-status\"}", InferenceJson.LlmHostedToolCall)!;
        Assert.Null(call.Input); Assert.Null(call.Output);
    }

    [Fact]
    public void OwnedConcreteUnionNestedAndConsumerMetadataShareTheFinitePolicies() => ExercisePolicies();

    public static int ExercisePolicies()
    {
        var tests = new HostedRequiredFactsTests();
        foreach (object[] row in MissingCases()) tests.MissingRequiredFactsRefuseAtOwnedMetadata((string)row[0], (string)row[1]);
        foreach (object[] row in NullCases()) tests.NullRequiredReferencesRefuseAtOwnedMetadata((string)row[0], (string)row[1]);
        tests.NewClosedEnumMetadataRefusesUndefinedValues("LlmHostedToolPhase");
        tests.NewClosedEnumMetadataRefusesUndefinedValues("LlmImageGenerationAction");
        const string image = "{\"type\":\"image\",\"Data\":null,\"MediaType\":\"image/png\"}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(image, InferenceJson.LlmHostedToolOutput));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Call\":{\"CallId\":\"call\",\"Kind\":3,\"Name\":\"image\",\"Status\":\"actual\",\"Output\":" + image + "}}", InferenceJson.LlmHostedToolCallCompletedEvent));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ModelId\":\"model\",\"HostedTools\":[{\"type\":\"image-generation\",\"Action\":99}]}", InferenceJson.LlmRequest));
        Assert.Throws<ArgumentNullException>(() => JsonSerializer.Deserialize("{\"Text\":\"actual\",\"Status\":0,\"Provenance\":{\"ImplementationId\":\"fixture\",\"ModelId\":\"model\"},\"Citations\":[{\"Url\":null}]}", InferenceJson.LlmResult));

        var options = new JsonSerializerOptions(InferenceJson.LlmHostedImageOutput.Options);
        JsonConverter bytes = InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter!;
        Assert.NotNull(bytes);
        options.TypeInfoResolver = JsonTypeInfoResolver.Combine(InferenceJson.LlmHostedImageOutput.Options.TypeInfoResolver, HostedConsumerContext.Default);
        options.MakeReadOnly();
        Assert.Same(bytes, options.GetTypeInfo<LlmHostedImageOutput>().Properties.Single(property => property.Name == "Data").CustomConverter);
        var envelope = options.GetTypeInfo<HostedConsumerEnvelope>();
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Output\":" + image + "}", envelope));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Tool\":{\"Action\":99}}", envelope));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Progress\":{\"CallId\":\"call\",\"Phase\":99}}", envelope));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Progress\":{\"CallId\":\"call\"}}", envelope));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new HostedConsumerEnvelope(null,
            new LlmImageGenerationTool((LlmImageGenerationAction)99), null), envelope));
        var original = new HostedConsumerEnvelope(new LlmHostedImageOutput(new byte[] { 0, 1, 2 }, "image/png"),
            new LlmImageGenerationTool(), new LlmHostedToolCallProgressEvent("call", LlmHostedToolPhase.Searching));
        string json = JsonSerializer.Serialize(original, envelope);
        var restored = JsonSerializer.Deserialize(json, envelope)!;
        Assert.Equal(new byte[] { 0, 1, 2 }, Assert.IsType<LlmHostedImageOutput>(restored.Output).Data.ToArray());
        Assert.Equal(LlmImageGenerationAction.Auto, restored.Tool!.Action);
        Assert.Equal(LlmHostedToolPhase.Searching, restored.Progress!.Phase);
        Assert.Equal("AAEC", JsonElement.Parse(json).GetProperty("Output").GetProperty("Data").GetString());
        Assert.True(JsonSerializer.Deserialize("{\"UnrelatedData\":null}", envelope)!.UnrelatedData.IsEmpty);
        Assert.Equal("", JsonElement.Parse(JsonSerializer.Serialize(new LlmHostedImageOutput(ReadOnlyMemory<byte>.Empty, "image/png"), InferenceJson.LlmHostedImageOutput)).GetProperty("Data").GetString());
        return 10;
    }

    [Fact]
    public void ActualForeignOwnedAndConsumerPoliciesExecuteThenRetire()
    {
        WeakReference generation = ExerciseForeignPolicies();
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(generation.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeignPolicies()
    {
        var generation = new AssemblyLoadContext("hosted-common-consumer", isCollectible: true);
        var weak = new WeakReference(generation);
        try
        {
            var common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            var tests = generation.LoadFromAssemblyPath(typeof(HostedRequiredFactsTests).Assembly.Location);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(HostedRequiredFactsTests).Assembly.ManifestModule.ModuleVersionId, tests.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(tests));
            Assert.Equal(10, tests.GetType(typeof(HostedRequiredFactsTests).FullName!)!.GetMethod(nameof(ExercisePolicies))!.Invoke(null, null));
        }
        finally { generation.Unload(); }
        return weak;
    }

    [Fact]
    public void RequiredObservationGuardsAlsoRejectMalformedProducerValues()
    {
        Assert.Throws<ArgumentException>(() => new LlmHostedToolCallStartedEvent("", LlmHostedToolKind.WebSearch, "actual"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmHostedToolCallStartedEvent("actual", (LlmHostedToolKind)99, "actual"));
        Assert.Throws<ArgumentException>(() => new LlmHostedToolCallProgressEvent("actual", LlmHostedToolPhase.Searching) with { CallId = " " });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmHostedToolCallProgressEvent("actual", LlmHostedToolPhase.Searching) with { Phase = (LlmHostedToolPhase)99 });
        Assert.Throws<ArgumentNullException>(() => new LlmHostedToolCall("actual", LlmHostedToolKind.WebSearch, "actual", null!, null, null));
    }
}

internal sealed record HostedConsumerEnvelope(LlmHostedToolOutput? Output, LlmImageGenerationTool? Tool, LlmHostedToolCallProgressEvent? Progress)
{
    public ReadOnlyMemory<byte> UnrelatedData { get; init; }
}

[JsonSerializable(typeof(HostedConsumerEnvelope))]
internal sealed partial class HostedConsumerContext : JsonSerializerContext;
