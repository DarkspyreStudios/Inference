using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Realtime;

namespace Darkspyre.Inference.Common.Tests;

public sealed class RealtimeContractTests
{
    private const string Conversation = """{"ConversationId":"issued","Scope":{"Owner":"endpoint","Identity":"account"}}""";
    private const string Audio = """{"Audio":"AAH/","Transcript":"actual"}""";
    private const string Message = """{"Role":0,"Content":[{"type":"text","Text":"exact"},{"type":"audio","Audio":"AAH/"}],"Id":"actual-item"}""";
    private const string Output = """{"Parts":[{"type":"text","Text":"tool text"},{"type":"workspace-resource","Resource":{"WorkspaceId":"workspace","Id":"committed"}}],"StructuredContent":{"value":false},"IsError":true}""";
    private const string Config = """{"ModelId":"manual-model","Instructions":"","Voice":"actual","ReasoningEffort":0,"Tools":[],"HostedTools":[{"type":"web-search"}],"TurnDetection":{"type":"server-vad","Threshold":0,"SilenceDuration":"00:00:00"},"InputAudio":{"Encoding":0,"SampleRate":24000,"Transport":0},"OutputAudio":{"Encoding":3,"SampleRate":null,"Transport":1},"TranscriptionLanguage":"en","TranscriptionKeyTerms":["exact"],"OutputSpeed":0,"Resumption":{"Enabled":false},"ProviderOptions":null}""";

    public static TheoryData<string, string> Shapes => new()
    {
        { "RealtimeAudioEncoding", "3" }, { "RealtimeAudioTransport", "1" },
        { "RealtimeModality", "0" }, { "RealtimeMessageRole", "2" },
        { "RealtimeAudioConfig", """{"Encoding":0,"SampleRate":0,"Transport":null}""" },
        { "RealtimeTurnDetection", """{"type":"manual"}""" },
        { "RealtimeTurnDetection", """{"type":"server-vad","Threshold":0,"SilenceDuration":"00:00:00","PrefixPadding":null,"IdleTimeout":"00:00:03"}""" },
        { "RealtimeManualTurnDetection", "{}" }, { "RealtimeServerVad", """{"Threshold":null,"IdleTimeout":null}""" },
        { "RealtimeConversationReference", Conversation },
        { "RealtimeResumptionOptions", "{\"Enabled\":true,\"Conversation\":" + Conversation + "}" },
        { "RealtimeResumptionOptions", """{"Enabled":false,"Conversation":null}""" },
        { "RealtimeProviderOptions", "null" }, { "RealtimeSessionConfig", Config },
        { "RealtimeSessionConfig", """{"Tools":null,"HostedTools":[],"TranscriptionKeyTerms":[],"TurnDetection":null}""" },
        { "RealtimeContent", """{"type":"text","Text":""}""" },
        { "RealtimeContent", "{\"type\":\"audio\",\"Audio\":\"\",\"Transcript\":null}" },
        { "RealtimeTextContent", """{"Text":"exact"}""" }, { "RealtimeAudioContent", Audio },
        { "RealtimeConversationItem", "{\"type\":\"message\",\"Role\":0,\"Content\":[]}" },
        { "RealtimeConversationItem", """{"type":"function-call","Name":"caller","ArgumentsJson":"{}","CallId":null}""" },
        { "RealtimeConversationItem", "{\"type\":\"function-output\",\"CallId\":\"actual\",\"Output\":" + Output + "}" },
        { "RealtimeMessageItem", Message },
        { "RealtimeFunctionCallItem", """{"Name":"caller","ArgumentsJson":"{\"key\":true}","CallId":"actual"}""" },
        { "RealtimeFunctionOutputItem", "{\"CallId\":\"actual\",\"Output\":" + Output + "}" },
        { "RealtimeResponseOptions", """{"Instructions":"","Modalities":[0,1],"Metadata":{"key":""}}""" },
        { "RealtimeToolResult", "{\"CallId\":\"actual\",\"Output\":" + Output + "}" },
        { "RealtimeCapabilities", "{}" },
        { "RealtimeCapabilities", """{"InputEncodings":[0],"OutputEncodings":[3],"SupportedHostedToolKinds":[0],"InputSampleRates":[],"OutputSampleRates":null,"SupportsManualTurns":true,"SupportsServerVad":false,"SupportsClientTools":true,"SupportsStructuredToolOutput":true,"SupportsResumption":true,"SupportsBinaryTransport":true,"SupportsPronunciationReplacement":true,"SupportsForcedMessages":true}""" },
        { "RealtimeEventIdentity", """{"EventId":"actual-event","ResponseId":null,"ItemId":"actual-item","OutputIndex":0,"ContentIndex":0}""" },
        { "RealtimeSessionEvent", "{\"Identity\":{},\"Stage\":\"session.updated\",\"SessionId\":null,\"Configuration\":" + Config + "}" },
        { "RealtimeConversationCreatedEvent", "{\"Identity\":{},\"Conversation\":" + Conversation + "}" },
        { "RealtimeInputSpeechEvent", """{"Identity":{},"Stage":"speech.started","Start":"00:00:00","End":null}""" },
        { "RealtimeInputBufferEvent", """{"Identity":{},"Stage":"input.committed","PreviousItemId":null}""" },
        { "RealtimeItemEvent", "{\"Identity\":{},\"Stage\":\"item.done\",\"Item\":{\"type\":\"message\",\"Role\":0,\"Content\":[]},\"PreviousItemId\":null,\"AudioEnd\":\"00:00:00\",\"Transcript\":\"\"}" },
        { "RealtimeContentPartEvent", """{"Identity":{},"Stage":"part.added","Part":{"type":"audio","Audio":"AAH/"}}""" },
        { "RealtimeTranscriptEvent", """{"Identity":{},"Text":"","IsInput":false,"IsSnapshot":false,"IsFinal":false}""" },
        { "RealtimeAudioDeltaEvent", """{"Identity":{},"Audio":"AAH/"}""" },
        { "RealtimeAudioDoneEvent", """{"Identity":{}}""" },
        { "RealtimeTextDeltaEvent", """{"Identity":{},"Text":"exact delta"}""" },
        { "RealtimeFunctionArgumentsEvent", """{"Identity":{},"CallId":null,"Name":null,"Arguments":"","IsFinal":false}""" },
        { "RealtimeHostedStartedEvent", """{"Identity":{},"Call":{"CallId":"actual","Kind":0,"Name":"web_search"}}""" },
        { "RealtimeHostedProgressEvent", """{"Identity":{},"Call":{"CallId":"actual","Phase":0}}""" },
        { "RealtimeHostedCompletedEvent", """{"Identity":{},"Call":{"CallId":"actual","Kind":0,"Name":"web_search","Status":"actual","Input":null,"Output":{"type":"search","Sources":[{"Url":"https://example.invalid/actual","Title":null}]}}}""" },
        { "RealtimeResponseEvent", """{"Identity":{},"Stage":"response.done","Status":null,"Usage":null,"Metadata":{"key":"actual"}}""" },
        { "RealtimeErrorEvent", """{"Identity":{},"Error":{"Code":10,"Message":"safe","ProviderCode":"reported"},"Parameter":null,"RelatedClientEventId":null}""" }
    };

    [Theory, MemberData(nameof(Shapes))]
    public void EveryExactGetterPreservesPopulatedGraphs(string name, string json) => RoundTrip(json, Metadata(typeof(InferenceJson), name));

    [Fact]
    public void AllDeclaredRealtimeRootsHavePopulatedCoverage()
    {
        Assert.Equal(typeof(InferenceJson).GetProperties().Where(p => p.Name.StartsWith("Realtime", StringComparison.Ordinal)).Select(p => p.Name).Order(),
            Shapes.Select(row => (string)row[0]).Distinct().Order());
        Assert.Null(typeof(InferenceJson).GetProperty(nameof(RealtimeEvent)));
        Assert.Null(typeof(InferenceJson).GetProperty(nameof(IRealtimeVoiceSession)));
    }

    [Theory, MemberData(nameof(Shapes))]
    public void RequiredFactsCannotBecomeMissingDefaultsOrNull(string name, string json)
    {
        JsonTypeInfo metadata = Metadata(typeof(InferenceJson), name);
        if (JsonNode.Parse(json) is not JsonObject original) return;
        foreach (JsonPropertyInfo property in metadata.Properties.Where(property => property.IsRequired))
        {
            Assert.True(original.ContainsKey(property.Name), name + "." + property.Name);
            JsonObject missing = original.DeepClone().AsObject(); missing.Remove(property.Name);
            Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(missing.ToJsonString(), metadata)));
            JsonObject invalid = original.DeepClone().AsObject(); invalid[property.Name] = null;
            Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(invalid.ToJsonString(), metadata)));
        }
    }

    [Fact]
    public void EveryConcreteEventRequiresItsActualIdentity()
    {
        foreach (var row in Shapes.Where(row => ((string)row[0]).EndsWith("Event", StringComparison.Ordinal)))
        {
            JsonTypeInfo metadata = Metadata(typeof(InferenceJson), (string)row[0]);
            Assert.True(metadata.Properties.Single(p => p.Name == "Identity").IsRequired);
            var original = JsonNode.Parse((string)row[1])!.AsObject(); original.Remove("Identity");
            Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(original.ToJsonString(), metadata)));
            original["Identity"] = null;
            Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(original.ToJsonString(), metadata)));
        }
        RealtimeEvent value = new RealtimeTextDeltaEvent(new(), "text");
        Assert.Throws<ArgumentNullException>(() => value with { Identity = null! });
        Assert.Throws<ArgumentNullException>(() => new RealtimeAudioDoneEvent(null!));
    }

    [Fact]
    public void ClosedUnionsAndEnumsRefuseUnadmittedValues()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"other\"}", InferenceJson.RealtimeTurnDetection));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"other\"}", InferenceJson.RealtimeContent));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"other\"}", InferenceJson.RealtimeConversationItem));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"manual\",\"Threshold\":0}", InferenceJson.RealtimeTurnDetection));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ForeignItem(), InferenceJson.RealtimeConversationItem));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new RealtimeSessionConfig { ProviderOptions = new ForeignOptions() }, InferenceJson.RealtimeSessionConfig));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"ProviderOptions\":{}}", InferenceJson.RealtimeSessionConfig));
        foreach (string name in new[] { "RealtimeAudioEncoding", "RealtimeAudioTransport", "RealtimeModality", "RealtimeMessageRole" })
        {
            JsonTypeInfo metadata = Metadata(typeof(InferenceJson), name);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", metadata));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"0\"", metadata));
            Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Enum.ToObject(metadata.Type, 99), metadata));
        }
    }

    [Fact]
    public void DataCopiesCollectionsAndBytesWithoutInventingObservations()
    {
        byte[] bytes = [0, 1, 255];
        var audio = new RealtimeAudioContent(bytes);
        var delta = new RealtimeAudioDeltaEvent(new(), bytes);
        bytes[0] = 9;
        Assert.Equal(new byte[] { 0, 1, 255 }, audio.Audio.ToArray());
        Assert.Equal(new byte[] { 0, 1, 255 }, delta.Audio.ToArray());
        var replacement = audio with { Audio = bytes }; bytes[1] = 8;
        Assert.Equal(new byte[] { 9, 1, 255 }, replacement.Audio.ToArray());
        List<RealtimeContent> content = [audio]; var item = new RealtimeMessageItem(RealtimeMessageRole.User, content);
        content.Clear(); Assert.Single(item.Content);
        Assert.Throws<ArgumentException>(() => item with { Content = new RealtimeContent[] { null! } });
        List<string> terms = ["unaltered"];
        List<LlmHostedTool> tools = [new LlmWebSearchTool()];
        var config = new RealtimeSessionConfig { TranscriptionKeyTerms = terms, HostedTools = tools, Tools = [] };
        terms.Clear(); tools.Clear(); Assert.Single(config.TranscriptionKeyTerms!); Assert.Single(config.HostedTools!); Assert.Empty(config.Tools!);
        Assert.Null(new RealtimeSessionConfig().Tools);
        var updated = config with { TranscriptionKeyTerms = terms }; terms.Add("later"); Assert.Empty(updated.TranscriptionKeyTerms!);
        Dictionary<string, string> map = new() { ["key"] = "actual" };
        List<RealtimeModality> modes = [RealtimeModality.Text];
        var options = new RealtimeResponseOptions("", modes, map);
        var response = new RealtimeResponseEvent(new(), "native.stage", null, null, map);
        map.Clear(); modes.Clear(); Assert.Equal("actual", options.Metadata!["key"]); Assert.Single(options.Modalities!); Assert.Equal("actual", response.Metadata!["key"]);
        HashSet<RealtimeAudioEncoding> encodings = [RealtimeAudioEncoding.Opus]; List<int> rates = [0];
        var capabilities = new RealtimeCapabilities { InputEncodings = encodings, InputSampleRates = rates };
        encodings.Clear(); rates.Clear(); Assert.Single(capabilities.InputEncodings); Assert.Equal(0, Assert.Single(capabilities.InputSampleRates!));
        Assert.Empty(capabilities.OutputEncodings); Assert.Null(capabilities.OutputSampleRates); Assert.False(capabilities.SupportsResumption);
        Assert.Throws<ArgumentOutOfRangeException>(() => capabilities with { InputEncodings = new HashSet<RealtimeAudioEncoding> { (RealtimeAudioEncoding)99 } });
        Assert.Null(response.Status); Assert.Null(response.Usage); Assert.Null(new RealtimeEventIdentity().OutputIndex);
        Assert.Null(new RealtimeAudioConfig(RealtimeAudioEncoding.Opus).SampleRate);
        Assert.DoesNotContain("unaltered", config.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("actual", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredAudioBytesUseOnlyTheOwnedTargetedPolicy()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Audio\":null}", InferenceJson.RealtimeAudioContent));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Identity\":{},\"Audio\":null}", InferenceJson.RealtimeAudioDeltaEvent));
        Assert.True(JsonSerializer.Deserialize("{\"Audio\":\"\"}", InferenceJson.RealtimeAudioContent)!.Audio.IsEmpty);
        Assert.True(JsonSerializer.Deserialize("{\"Identity\":{},\"Audio\":\"\"}", InferenceJson.RealtimeAudioDeltaEvent)!.Audio.IsEmpty);
        Assert.Same(InferenceJson.BinaryMediaSource.Properties.Single(p => p.Name == "Data").CustomConverter,
            InferenceJson.RealtimeAudioContent.Properties.Single(p => p.Name == "Audio").CustomConverter);
        Assert.True(JsonSerializer.Deserialize("null", InferenceJson.RealtimeAudioContent.Options.GetTypeInfo<ReadOnlyMemory<byte>>()).IsEmpty);
    }

    [Fact]
    public void RuntimeSessionRefusesPortableAuthorityWithoutCallingIt()
    {
        var session = new UncallableSession();
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new RealtimeRuntimeEnvelope { Session = session }, RuntimeMetadata()));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Session\":{}}", RuntimeMetadata()));
        Assert.Null(JsonSerializer.Deserialize("{\"Session\":null}", RuntimeMetadata())!.Session);
        Assert.Equal(0, session.Calls);
        Assert.Equal(7, (int)InferenceTask.RealtimeVoice); Assert.Equal(InferenceCapability.Audio, InferenceTask.RealtimeVoice.GetCapability());
    }

    public static JsonTypeInfo<RealtimeRuntimeEnvelope> RuntimeMetadata()
    {
        var options = new JsonSerializerOptions(InferenceJson.RealtimeSessionConfig.Options) { TypeInfoResolver = RealtimeRuntimeJsonContext.Default };
        options.MakeReadOnly(); return options.GetTypeInfo<RealtimeRuntimeEnvelope>();
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignMetadataAndNestedGraphsRetire(string name, string json)
    {
        WeakReference weak = ExerciseForeign(name, json);
        for (int i = 0; weak.IsAlive && i < 20; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(string name, string json)
    {
        var generation = new ForeignGeneration(Path.GetDirectoryName(typeof(InferenceJson).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assert.NotSame(typeof(InferenceJson).Assembly, common);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            Type facade = common.GetType(typeof(InferenceJson).FullName!, true)!;
            JsonTypeInfo metadata = Metadata(facade, name);
            Assert.Same(common, metadata.Type.Assembly); Assert.Same(common, metadata.Options.TypeInfoResolver!.GetType().Assembly);
            object? value = RoundTrip(json, metadata); if (value is not null) AssertOwned(value, common);
            if (name is "RealtimeAudioContent" or "RealtimeAudioDeltaEvent")
            {
                Assert.Same(common, metadata.Properties.Single(p => p.Name == "Audio").CustomConverter!.GetType().Assembly);
                var invalid = JsonNode.Parse(json)!.AsObject(); invalid["Audio"] = null;
                Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(invalid.ToJsonString(), metadata));
            }
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(RealtimeContractTests).Assembly.Location);
            Assert.NotSame(typeof(RealtimeContractTests).Assembly, fixture);
            Assert.Equal(typeof(RealtimeContractTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            var runtime = (JsonTypeInfo)fixture.GetType(typeof(RealtimeContractTests).FullName!, true)!.GetMethod(nameof(RuntimeMetadata))!.Invoke(null, null)!;
            Assert.Same(fixture, runtime.Type.Assembly);
            Assert.Same(common, runtime.Options.GetConverter(common.GetType(typeof(IRealtimeVoiceSession).FullName!, true)!).GetType().Assembly);
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Session\":{}}", runtime));
            Assert.NotNull(JsonSerializer.Deserialize("{\"Session\":null}", runtime));
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static void AssertOwned(object value, Assembly owner)
    {
        Assert.Same(owner, value.GetType().Assembly);
        foreach (PropertyInfo property in value.GetType().GetProperties())
        {
            object? nested = property.GetValue(value);
            if (nested is System.Collections.IEnumerable items && nested is not string)
            {
                foreach (object? item in items)
                    if (item?.GetType().Namespace?.StartsWith("Darkspyre.Inference.Common", StringComparison.Ordinal) == true) AssertOwned(item, owner);
            }
            else if (nested?.GetType().Namespace?.StartsWith("Darkspyre.Inference.Common", StringComparison.Ordinal) == true) AssertOwned(nested, owner);
        }
    }

    private static JsonTypeInfo Metadata(Type owner, string name) => (JsonTypeInfo)owner.GetProperty(name)!.GetValue(null)!;
    private static object? RoundTrip(string json, JsonTypeInfo metadata)
    {
        object? value = JsonSerializer.Deserialize(json, metadata);
        string written = JsonSerializer.Serialize(value, metadata);
        using JsonDocument expected = JsonDocument.Parse(json); using JsonDocument actual = JsonDocument.Parse(written);
        Preserve(expected.RootElement, actual.RootElement);
        Assert.Equal(written, JsonSerializer.Serialize(JsonSerializer.Deserialize(written, metadata), metadata));
        return value;
    }
    private static void Preserve(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in expected.EnumerateObject()) { Assert.True(actual.TryGetProperty(property.Name, out var item)); Preserve(property.Value, item); }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (int i = 0; i < expected.GetArrayLength(); i++) Preserve(expected[i], actual[i]); }
        else Assert.True(JsonElement.DeepEquals(expected, actual), expected.GetRawText());
    }
    private sealed class ForeignGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll"); return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
    private sealed record ForeignOptions : RealtimeProviderOptions;
    private sealed record ForeignItem : RealtimeConversationItem;
    private sealed class UncallableSession : IRealtimeVoiceSession
    {
        public int Calls { get; private set; }
        private InvalidOperationException Called() { Calls++; return new("Session was invoked."); }
        public Task UpdateSessionAsync(RealtimeSessionConfig config, CancellationToken cancellationToken = default) => throw Called();
        public Task AppendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default) => throw Called();
        public Task CommitAudioAsync(CancellationToken cancellationToken = default) => throw Called();
        public Task ClearAudioAsync(CancellationToken cancellationToken = default) => throw Called();
        public Task AddItemAsync(RealtimeConversationItem item, string? previousItemId = null, CancellationToken cancellationToken = default) => throw Called();
        public Task DeleteItemAsync(string itemId, CancellationToken cancellationToken = default) => throw Called();
        public Task TruncateItemAsync(string itemId, int contentIndex, TimeSpan audioEnd, CancellationToken cancellationToken = default) => throw Called();
        public Task CreateResponseAsync(RealtimeResponseOptions? options = null, CancellationToken cancellationToken = default) => throw Called();
        public Task CancelResponseAsync(string? responseId = null, CancellationToken cancellationToken = default) => throw Called();
        public Task SubmitToolResultAsync(RealtimeToolResult result, CancellationToken cancellationToken = default) => throw Called();
        public IAsyncEnumerable<RealtimeEvent> Events => throw Called();
        public ValueTask DisposeAsync() => throw Called();
        public override string ToString() => throw Called();
    }
}

public sealed record RealtimeRuntimeEnvelope
{
    public IRealtimeVoiceSession? Session { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RealtimeRuntimeEnvelope))]
internal sealed partial class RealtimeRuntimeJsonContext : JsonSerializerContext;
