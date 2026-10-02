using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Realtime.Tests;

public sealed class ConnectRealtimeJsonTests
{
    private const string Conversation = """{"ConversationId":"issued","Scope":{"Owner":"endpoint","Identity":"account"}}""";
    private const string Output = """{"Parts":[{"type":"text","Text":"tool text"},{"type":"workspace-resource","Resource":{"WorkspaceId":"workspace","Id":"committed"}}],"StructuredContent":{"value":false},"IsError":true}""";
    private const string Config = """{"ModelId":"manual","Instructions":"","Voice":"actual","ReasoningEffort":0,"Tools":[{"Name":"actual","InputSchema":{"type":"object","properties":{"actual":{"type":"string"}}},"Strict":false}],"HostedTools":[{"type":"xai-x-search","AllowedHandles":["actual"]}],"TurnDetection":{"type":"manual"},"InputAudio":{"Encoding":0,"SampleRate":24000,"Transport":0},"OutputAudio":{"Encoding":3,"Transport":1},"TranscriptionLanguage":"en","TranscriptionKeyTerms":[],"OutputSpeed":0,"Resumption":{"Enabled":false,"Conversation":{"ConversationId":"issued","Scope":{"Owner":"endpoint","Identity":"account"}}},"ProviderOptions":{"type":"xai-realtime","Replace":{"spoken":"replacement"}}}""";
    private const string Connection = """{"Transport":0,"Endpoint":"wss://api.x.ai/v1","Authorization":null,"Xai":{"ConnectionId":"selected"},"AdditionalHeaders":{"x-extra":"secret"}}""";
    private const string Setup = "{\"Connection\":" + Connection + ",\"Session\":" + Config + ",\"StreamLimits\":{\"MaximumBufferedEvents\":1,\"MaximumBufferedPayloadBytes\":2,\"MaximumMessageBytes\":3}}";

    public static TheoryData<string, string> Shapes => new()
    {
        { "RealtimeAudioEncoding", "3" }, { "RealtimeAudioTransport", "1" }, { "RealtimeModality", "0" }, { "RealtimeMessageRole", "2" },
        { "RealtimeAudioConfig", """{"Encoding":0,"SampleRate":0,"Transport":null}""" },
        { "RealtimeTurnDetection", """{"type":"manual"}""" }, { "RealtimeManualTurnDetection", "{}" },
        { "RealtimeServerVad", """{"Threshold":0,"SilenceDuration":"00:00:00","PrefixPadding":null,"IdleTimeout":"00:00:03"}""" },
        { "RealtimeConversationReference", Conversation }, { "RealtimeResumptionOptions", "{\"Enabled\":true,\"Conversation\":" + Conversation + "}" },
        { "RealtimeProviderOptions", """{"type":"xai-realtime","Replace":{}}""" }, { "RealtimeSessionConfig", Config },
        { "RealtimeContent", """{"type":"audio","Audio":"AAH/"}""" }, { "RealtimeTextContent", """{"Text":"exact"}""" },
        { "RealtimeAudioContent", """{"Audio":"AAH/","Transcript":"actual"}""" },
        { "RealtimeConversationItem", """{"type":"xai-force-message","Text":"exact","Interruptible":false,"Id":null}""" },
        { "RealtimeMessageItem", """{"Role":0,"Content":[{"type":"text","Text":"exact"},{"type":"audio","Audio":"AAH/"}],"Id":"actual"}""" },
        { "RealtimeFunctionCallItem", """{"Name":"tool","ArgumentsJson":"{}","CallId":null}""" },
        { "RealtimeFunctionOutputItem", "{\"CallId\":\"actual\",\"Output\":" + Output + "}" },
        { "RealtimeResponseOptions", """{"Instructions":"","Modalities":[0,1],"Metadata":{"key":"actual"}}""" },
        { "RealtimeToolResult", "{\"CallId\":\"actual\",\"Output\":" + Output + "}" },
        { "RealtimeCapabilities", """{"InputEncodings":[0,3],"OutputEncodings":[1],"SupportedHostedToolKinds":[0],"InputSampleRates":[24000],"OutputSampleRates":null,"SupportsManualTurns":true,"SupportsServerVad":true,"SupportsClientTools":true,"SupportsStructuredToolOutput":true,"SupportsResumption":true,"SupportsBinaryTransport":true,"SupportsPronunciationReplacement":true,"SupportsForcedMessages":true}""" },
        { "RealtimeEventIdentity", """{"EventId":"actual","ResponseId":null,"ItemId":"actual","OutputIndex":0,"ContentIndex":0}""" },
        { "RealtimeSessionEvent", "{\"Identity\":{},\"Stage\":\"session.updated\",\"SessionId\":null,\"Configuration\":" + Config + "}" },
        { "RealtimeConversationCreatedEvent", "{\"Identity\":{},\"Conversation\":" + Conversation + "}" },
        { "RealtimeInputSpeechEvent", """{"Identity":{},"Stage":"speech.started","Start":"00:00:00","End":null}""" },
        { "RealtimeInputBufferEvent", """{"Identity":{},"Stage":"input.committed","PreviousItemId":null}""" },
        { "RealtimeItemEvent", """{"Identity":{},"Stage":"item.done","Item":{"type":"xai-force-message","Text":"exact","Interruptible":true},"PreviousItemId":null,"AudioEnd":"00:00:00","Transcript":""}""" },
        { "RealtimeContentPartEvent", """{"Identity":{},"Stage":"part.added","Part":{"type":"audio","Audio":"AAH/"}}""" },
        { "RealtimeTranscriptEvent", """{"Identity":{},"Text":"","IsInput":false,"IsSnapshot":false,"IsFinal":false}""" },
        { "RealtimeAudioDeltaEvent", """{"Identity":{},"Audio":"AAH/"}""" }, { "RealtimeAudioDoneEvent", """{"Identity":{}}""" },
        { "RealtimeTextDeltaEvent", """{"Identity":{},"Text":"exact"}""" },
        { "RealtimeFunctionArgumentsEvent", """{"Identity":{},"CallId":null,"Name":null,"Arguments":"","IsFinal":false}""" },
        { "RealtimeHostedStartedEvent", """{"Identity":{},"Call":{"CallId":"actual","Kind":0,"Name":"web_search"}}""" },
        { "RealtimeHostedProgressEvent", """{"Identity":{},"Call":{"CallId":"actual","Phase":0}}""" },
        { "RealtimeHostedCompletedEvent", """{"Identity":{},"Call":{"CallId":"actual","Kind":0,"Name":"web_search","Status":"actual","Output":{"type":"search","Sources":[{"Url":"https://example.invalid/actual","Title":null}]}}}""" },
        { "RealtimeResponseEvent", """{"Identity":{},"Stage":"response.done","Status":null,"Usage":null,"Metadata":{"key":"actual"}}""" },
        { "RealtimeErrorEvent", """{"Identity":{},"Error":{"Code":10,"Message":"safe","ProviderCode":"reported"},"Parameter":null,"RelatedClientEventId":null}""" },
        { "RealtimeConnection", Connection }, { "RealtimeTransport", "0" },
        { "XaiRealtimeOptions", """{"Replace":{"spoken":"replacement"}}""" },
        { "XaiRealtimeForceMessageItem", """{"Text":"exact","Interruptible":false}""" },
        { "RealtimeSetupConfiguration", Setup }, { "RealtimeSetupHeaderChanges", """{"Set":{"x-new":"secret"},"Remove":["x-old"]}""" },
        { "RealtimeSetupChanges", "{\"Identity\":{\"Intent\":0,\"Id\":\"one\",\"ExpectedVersion\":null},\"Configuration\":" + Setup + ",\"Headers\":{\"Set\":{},\"Remove\":[]}}" }
    };

    [Theory, MemberData(nameof(Shapes))]
    public void EveryExactGetterPreservesPopulatedGraphAndRequiredFacts(string name, string json)
    {
        JsonTypeInfo metadata = Metadata(typeof(ConnectRealtimeJson), name);
        RoundTrip(json, metadata);
        if (JsonNode.Parse(json) is not JsonObject original) return;
        foreach (JsonPropertyInfo property in metadata.Properties.Where(property => property.IsRequired))
        {
            Assert.True(original.ContainsKey(property.Name), name + "." + property.Name);
            JsonObject invalid = original.DeepClone().AsObject(); invalid.Remove(property.Name);
            Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(invalid.ToJsonString(), metadata)));
            invalid[property.Name] = null;
            Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(invalid.ToJsonString(), metadata)));
        }
    }

    [Fact]
    public void AllExactGettersHavePopulatedCoverageWithoutRuntimeRoots()
    {
        Assert.Equal(46, typeof(ConnectRealtimeJson).GetProperties().Length);
        Assert.Equal(typeof(ConnectRealtimeJson).GetProperties().Select(property => property.Name).Order(), Shapes.Select(row => (string)row[0]).Order());
        foreach (string runtime in new[] { nameof(IRealtimeProvider), nameof(RealtimeProviderService), nameof(RealtimeSettings), nameof(RealtimeSettingsManager), nameof(XaiFamilyBinding), nameof(IRealtimeVoiceSession) })
            Assert.Null(typeof(ConnectRealtimeJson).GetProperty(runtime));
    }

    [Fact]
    public void FamilyExtensionsAreClosedAndRemainOutsideCommon()
    {
        var config = new RealtimeSessionConfig { ProviderOptions = new XaiRealtimeOptions(), HostedTools = [new XaiXSearchTool()] };
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(config, InferenceJson.RealtimeSessionConfig));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new XaiRealtimeForceMessageItem("exact"), InferenceJson.RealtimeConversationItem));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new RealtimeSessionConfig { ProviderOptions = new ForeignOptions() }, ConnectRealtimeJson.RealtimeSessionConfig));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new ForeignItem(), ConnectRealtimeJson.RealtimeConversationItem));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"ProviderOptions":{"type":"unknown"}}""", ConnectRealtimeJson.RealtimeSessionConfig));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"type":"xai-force-message","Text":"exact","Other":false}""", ConnectRealtimeJson.RealtimeConversationItem));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new XaiRealtimeForceMessageItem("exact") { Id = "unmapped" }, ConnectRealtimeJson.RealtimeConversationItem));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("""{"Identity":{},"Stage":"actual","Item":{"type":"xai-force-message","Text":"exact","Id":"unmapped"},"PreviousItemId":null}""", ConnectRealtimeJson.RealtimeItemEvent));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", ConnectRealtimeJson.RealtimeTransport));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"0\"", ConnectRealtimeJson.RealtimeTransport));
    }

    [Fact]
    public void BytesReuseCommonTargetedPolicyAndNestedHostedImageData()
    {
        Assert.Same(InferenceJson.RealtimeAudioContent.Properties.Single(property => property.Name == "Audio").CustomConverter,
            ConnectRealtimeJson.RealtimeAudioContent.Properties.Single(property => property.Name == "Audio").CustomConverter);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"Identity":{},"Audio":null}""", ConnectRealtimeJson.RealtimeAudioDeltaEvent));
        Assert.True(JsonSerializer.Deserialize("""{"Audio":""}""", ConnectRealtimeJson.RealtimeAudioContent)!.Audio.IsEmpty);
        JsonTypeInfo hosted = ConnectRealtimeJson.RealtimeSessionConfig.Options.GetTypeInfo<LlmHostedImageOutput>();
        Assert.Same(InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter,
            hosted.Properties.Single(property => property.Name == "Data").CustomConverter);
        Assert.True(JsonSerializer.Deserialize("null", ConnectRealtimeJson.RealtimeSessionConfig.Options.GetTypeInfo<ReadOnlyMemory<byte>>()).IsEmpty);
    }

    [Fact]
    public void DictionariesAndHeaderChangesFreezeConstructorInitAndWithValues()
    {
        Dictionary<string, string> map = new() { ["spoken"] = "replacement" };
        var options = new XaiRealtimeOptions(map) with { Replace = map };
        var connection = new RealtimeConnection { Transport = RealtimeTransport.XaiRealtime, Endpoint = new("wss://api.x.ai/v1"), AdditionalHeaders = map };
        List<string> remove = ["old"];
        var changes = new RealtimeSetupHeaderChanges(map, remove) with { Set = map, Remove = remove };
        map.Clear(); remove.Clear();
        Assert.Equal("replacement", options.Replace!["spoken"]); Assert.Equal("replacement", connection.AdditionalHeaders["SPOKEN"]);
        Assert.Single(changes.Set); Assert.Equal("old", Assert.Single(changes.Remove));
        Assert.Null(new XaiRealtimeOptions().Replace); Assert.Empty(new XaiRealtimeOptions(new Dictionary<string, string>()).Replace!);
        Assert.Throws<ArgumentNullException>(() => changes with { Set = null! });
        Assert.Throws<ArgumentNullException>(() => connection with { AdditionalHeaders = null! });
        Assert.DoesNotContain("replacement", options.ToString(), StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignCommonLlmRealtimeFixtureGraphRetires(string name, string json)
    {
        WeakReference weak = ExerciseForeign(name, json);
        for (int i = 0; weak.IsAlive && i < 20; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }

    internal static void RuntimeRefusalProof()
    {
        var authority = new Authority();
        var provider = new ControlledRealtimeProvider();
        var service = new RealtimeProviderService([provider]);
        using var manager = service.CreateSettingsManager(new ControlledCommitTarget());
        RealtimeSettings settings = manager.OpenAsync(RealtimeTransport.XaiRealtime, new(SetupChangeIntent.Create, "entry", null)).GetAwaiter().GetResult();
        var values = new RealtimeRuntimeEnvelope[]
        {
            new() { Authorization = authority }, new() { Provider = provider }, new() { Service = service },
            new() { Settings = settings }, new() { Manager = manager }, new() { Session = new ControlledRealtimeSession() }
        };
        JsonTypeInfo<RealtimeRuntimeEnvelope> metadata = RuntimeMetadata();
        Assert.Same(typeof(InferenceJson).Assembly, metadata.Options.GetConverter(typeof(IProviderAuthorization)).GetType().Assembly);
        Assert.Same(typeof(InferenceJson).Assembly, metadata.Options.GetConverter(typeof(IInferenceResourceAccess)).GetType().Assembly);
        Assert.Same(typeof(ConnectLlmJson).Assembly, metadata.Options.GetConverter(typeof(XaiFamilyBinding)).GetType().Assembly);
        foreach (RealtimeRuntimeEnvelope value in values) Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(value, metadata));
        foreach (string property in new[] { "Authorization", "Provider", "Service", "Settings", "Manager", "Session", "Binding", "Resources", "Read", "Write" })
        {
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"" + property + "\":{}}", metadata));
            Assert.NotNull(JsonSerializer.Deserialize("{\"" + property + "\":null}", metadata));
        }
        Assert.Equal(0, authority.Calls); Assert.Equal(0, provider.Calls);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new RealtimeConnection
        { Transport = RealtimeTransport.XaiRealtime, Endpoint = new("wss://api.x.ai/v1"), Authorization = authority }, ConnectRealtimeJson.RealtimeConnection));
    }

    [Fact] public void LiveRuntimeAuthorityRefusesWithoutEffects() => RuntimeRefusalProof();

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Provider")]
    [InlineData("Service")]
    [InlineData("Settings")]
    [InlineData("Manager")]
    [InlineData("Session")]
    [InlineData("Binding")]
    [InlineData("Resources")]
    [InlineData("Read")]
    [InlineData("Write")]
    public void RuntimeNullRoundTripsWithoutAPortableRoot(string property)
    {
        Type type = typeof(RealtimeRuntimeEnvelope).GetProperty(property)!.PropertyType;
        JsonSerializerOptions options = ConnectRealtimeJson.RealtimeSessionConfig.Options;
        Assert.Equal("null", JsonSerializer.Serialize((object?)null, type, options));
        Assert.Null(JsonSerializer.Deserialize("null", type, options));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{}", type, options));
    }

    public static JsonTypeInfo<RealtimeRuntimeEnvelope> RuntimeMetadata()
    {
        var options = new JsonSerializerOptions(ConnectRealtimeJson.RealtimeSessionConfig.Options) { TypeInfoResolver = RealtimeConsumerJsonContext.Default };
        options.MakeReadOnly(); return options.GetTypeInfo<RealtimeRuntimeEnvelope>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(string name, string json)
    {
        var generation = new ForeignGeneration(Path.GetDirectoryName(typeof(ConnectRealtimeJsonTests).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly realtime = generation.LoadFromAssemblyPath(typeof(ConnectRealtimeJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assembly llm = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(ConnectRealtimeJsonTests).Assembly.Location);
            foreach ((Assembly local, Assembly foreign) in new[] { (typeof(ConnectRealtimeJson).Assembly, realtime), (typeof(InferenceJson).Assembly, common), (typeof(ConnectLlmJson).Assembly, llm), (typeof(ConnectRealtimeJsonTests).Assembly, fixture) })
            {
                Assert.NotSame(local, foreign); Assert.Equal(local.ManifestModule.ModuleVersionId, foreign.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(foreign));
            }
            JsonTypeInfo metadata = Metadata(realtime.GetType(typeof(ConnectRealtimeJson).FullName!, true)!, name);
            Assert.Same(realtime, metadata.Options.TypeInfoResolver!.GetType().Assembly);
            object? value = RoundTrip(json, metadata);
            Assert.NotNull(value); Assert.Same(generation, AssemblyLoadContext.GetLoadContext(value.GetType().Assembly));
            AssertNestedOwned(value, generation);
            MethodInfo proof = fixture.GetType(typeof(ConnectRealtimeJsonTests).FullName!, true)!.GetMethod(nameof(RuntimeRefusalProof), BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.Same(fixture, proof.DeclaringType!.Assembly); proof.Invoke(null, null);
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static void AssertNestedOwned(object value, AssemblyLoadContext generation)
    {
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(value.GetType().Assembly));
        foreach (PropertyInfo property in value.GetType().GetProperties())
        {
            object? nested = property.GetValue(value);
            if (nested is System.Collections.IEnumerable items && nested is not string)
            {
                foreach (object? item in items)
                    if (item?.GetType().Namespace?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) == true) AssertNestedOwned(item, generation);
            }
            else if (nested?.GetType().Namespace?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) == true) AssertNestedOwned(nested, generation);
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
    private sealed class Authority : IProviderAuthorization
    {
        public int Calls { get; private set; }
        public ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Serialization cannot invoke runtime authority."); }
        public override string ToString() => throw new InvalidOperationException("Runtime diagnostics cannot run.");
    }
}

public sealed record RealtimeRuntimeEnvelope
{
    public IProviderAuthorization? Authorization { get; init; }
    public IRealtimeProvider? Provider { get; init; }
    public RealtimeProviderService? Service { get; init; }
    public RealtimeSettings? Settings { get; init; }
    public RealtimeSettingsManager? Manager { get; init; }
    public IRealtimeVoiceSession? Session { get; init; }
    public XaiFamilyBinding? Binding { get; init; }
    public IInferenceResourceAccess? Resources { get; init; }
    public IInferenceResourceReadHandle? Read { get; init; }
    public IInferenceResourceWriteHandle? Write { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RealtimeRuntimeEnvelope))]
internal sealed partial class RealtimeConsumerJsonContext : JsonSerializerContext;
