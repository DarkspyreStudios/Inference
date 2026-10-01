using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.STT;

namespace Darkspyre.Inference.Common.Tests;

public sealed class SpeechContractTests
{
    private const string Word = "{\"Text\":\" \\u00e9 \",\"Start\":\"00:00:00\",\"End\":\"00:00:01\",\"Confidence\":null,\"Speaker\":null}";
    private const string Resource = "{\"WorkspaceId\":\"workspace\",\"Id\":\"actual-file\"}";
    private const string Provenance = "{\"ImplementationId\":\"hosted\",\"ModelId\":\"manual\"}";

    public static TheoryData<string, string> Shapes => new()
    {
        { "SpeechAudioEncoding", "0" }, { "SpeechAudioEncoding", "3" },
        { "SpeechTranscriptionInput", "{\"type\":\"binary\",\"ContentType\":\"audio/wav\",\"Data\":\"AAH/\",\"FileName\":null}" },
        { "SpeechTranscriptionInput", "{\"type\":\"url\",\"Url\":\"https://example.invalid/a?secret=exact\"}" },
        { "SpeechTranscriptionInput", "{\"type\":\"resource\",\"Resource\":" + Resource + "}" },
        { "BinarySpeechInput", "{\"ContentType\":\"audio/raw\",\"Data\":\"\",\"FileName\":\"original/path.raw\"}" },
        { "UrlSpeechInput", "{\"Url\":\"https://example.invalid/a%2fb\"}" },
        { "ResourceSpeechInput", "{\"Resource\":" + Resource + "}" },
        { "SpeechTranscriptionOptions", "{\"ModelId\":\"manual\",\"Language\":\"en\",\"RawAudioFormat\":1,\"SampleRate\":24000,\"Diarization\":false,\"Multichannel\":true,\"Channels\":2,\"KeyTerms\":[\"exact term\",\"exact term\"],\"IncludeFillerWords\":false,\"VadThreshold\":0.4,\"TextNormalization\":false}" },
        { "SpeechTranscriptionOptions", "{\"KeyTerms\":null,\"RawAudioFormat\":null}" },
        { "SpeechStreamOptions", "{\"ModelId\":\"m\",\"Language\":null,\"Encoding\":3,\"SampleRate\":48000,\"Diarization\":true,\"Multichannel\":false,\"Channels\":1,\"KeyTerms\":[],\"IncludeFillerWords\":false,\"VadThreshold\":0,\"TextNormalization\":true,\"InterimResults\":false,\"Endpointing\":\"00:00:00.5000000\",\"SmartTurnThreshold\":0.5,\"SmartTurnTimeout\":\"00:00:02\"}" },
        { "SpeechWord", Word },
        { "SpeechWord", "{\"Text\":\"\",\"Start\":\"00:00:00\",\"End\":\"00:00:00\",\"Confidence\":0,\"Speaker\":0}" },
        { "SpeechChannelTranscript", "{\"Index\":0,\"Text\":\"full channel\",\"Language\":null,\"Words\":[" + Word + "]}" },
        { "SpeechTranscriptionResult", "{\"Text\":\"snapshot\",\"Provenance\":" + Provenance + ",\"Language\":null,\"Duration\":null,\"Words\":null,\"Channels\":[]}" },
        { "SpeechTranscriptionResult", "{\"Text\":\"two channels\",\"Provenance\":" + Provenance + ",\"Duration\":\"00:00:00\",\"Words\":[" + Word + "],\"Channels\":[{\"Index\":0,\"Text\":\"first\",\"Words\":[]},{\"Index\":1,\"Text\":\"second\",\"Words\":null}]}" },
        { "SpeechTranscriptCreatedEvent", "{\"SessionId\":null}" },
        { "SpeechTranscriptPartialEvent", "{\"Text\":\"snapshot\",\"Start\":null,\"Duration\":null,\"IsFinal\":true,\"SpeechFinal\":false,\"ChannelIndex\":0,\"Words\":[" + Word + "],\"EndOfTurnConfidence\":0}" },
        { "SpeechTranscriptPartialEvent", "{\"Text\":\"\",\"IsFinal\":null,\"SpeechFinal\":null,\"ChannelIndex\":null,\"Words\":[]}" },
        { "SpeechTranscriptDoneEvent", "{\"Text\":\"done\",\"Duration\":null,\"ChannelIndex\":null,\"Words\":null}" },
        { "SpeechTranscriptDoneEvent", "{\"Text\":\"\",\"Duration\":\"00:00:00\",\"ChannelIndex\":1,\"Words\":[]}" },
        { "SpeechTranscriptErrorEvent", "{\"Error\":{\"Code\":10,\"ProviderCode\":null,\"Message\":\"safe\",\"Resource\":{\"Reference\":" + Resource + ",\"Phase\":0,\"CommitOutcome\":0}}}" },
        { "SpeechTranscriptionCapabilities", "{}" },
        { "SpeechTranscriptionCapabilities", "{\"Transcription\":{\"Available\":false,\"InputKinds\":[0,1,3]},\"Streaming\":{\"Available\":false,\"Encodings\":[0,3]}}" },
        { "SpeechTranscriptionModeCapabilities", "{\"Available\":false,\"InputKinds\":[],\"Encodings\":[],\"SampleRates\":null,\"Languages\":[],\"MaximumChannels\":null}" },
        { "SpeechTranscriptionModeCapabilities", "{\"Available\":false,\"InputKinds\":[0],\"Encodings\":[0],\"SampleRates\":[16000,24000],\"Languages\":[\"en\",\"es\"],\"MaximumChannels\":2,\"SupportsLanguage\":true,\"SupportsMultichannel\":false,\"SupportsDiarization\":true,\"SupportsKeyTerms\":true,\"SupportsFillerWords\":false,\"SupportsVadThreshold\":false,\"SupportsTextNormalization\":true,\"SupportsInterimResults\":true,\"SupportsEndpointing\":true,\"SupportsSmartTurn\":true,\"SupportsFinalize\":true}" }
    };

    public static TheoryData<string, string, string> RequiredFacts => new()
    {
        { "BinarySpeechInput", "{\"ContentType\":\"audio/wav\",\"Data\":\"\"}", "ContentType,Data" },
        { "UrlSpeechInput", "{\"Url\":\"https://example.invalid\"}", "Url" },
        { "ResourceSpeechInput", "{\"Resource\":" + Resource + "}", "Resource" },
        { "SpeechWord", Word, "Text,Start,End" },
        { "SpeechChannelTranscript", "{\"Index\":0,\"Text\":\"\"}", "Index,Text" },
        { "SpeechTranscriptionResult", "{\"Text\":\"\",\"Provenance\":" + Provenance + "}", "Text,Provenance" },
        { "SpeechTranscriptPartialEvent", "{\"Text\":\"\"}", "Text" },
        { "SpeechTranscriptDoneEvent", "{\"Text\":\"\",\"Duration\":null}", "Text" },
        { "SpeechTranscriptErrorEvent", "{\"Error\":{\"Code\":10,\"ProviderCode\":null,\"Message\":\"safe\"}}", "Error" }
    };

    [Theory, MemberData(nameof(Shapes))]
    public void ExactOwnedRootsPreserveActualFacts(string name, string json) => RoundTrip(json, Metadata(typeof(InferenceJson), name));

    [Theory, MemberData(nameof(RequiredFacts))]
    public void RequiredFactsCannotBeMissingOrNull(string name, string json, string properties)
    {
        JsonTypeInfo metadata = Metadata(typeof(InferenceJson), name);
        RoundTrip(json, metadata);
        foreach (string property in properties.Split(','))
        {
            JsonObject missing = JsonNode.Parse(json)!.AsObject(); missing.Remove(property);
            Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(missing.ToJsonString(), metadata)));
            JsonObject invalid = JsonNode.Parse(json)!.AsObject(); invalid[property] = null;
            Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(invalid.ToJsonString(), metadata)));
        }
    }

    [Fact]
    public void ClosedInputsAndEncodingRefuseUnknownOrMixedValues()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"file\",\"FileId\":\"not-admitted\"}", InferenceJson.SpeechTranscriptionInput));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"binary\",\"ContentType\":\"audio/wav\",\"Data\":\"\",\"Url\":\"https://example.invalid\"}", InferenceJson.SpeechTranscriptionInput));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ContentType\":\"audio/wav\",\"Data\":null}", InferenceJson.BinarySpeechInput));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", InferenceJson.SpeechAudioEncoding));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"Opus\"", InferenceJson.SpeechAudioEncoding));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((SpeechAudioEncoding)99, InferenceJson.SpeechAudioEncoding));
        Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize("{\"Encoding\":99}", InferenceJson.SpeechStreamOptions)));
        Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize("{\"Encodings\":[99]}", InferenceJson.SpeechTranscriptionModeCapabilities)));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new UnadmittedInput(new BinarySpeechInput("audio/wav", ReadOnlyMemory<byte>.Empty)), InferenceJson.SpeechTranscriptionInput));
        Assert.Same(InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter,
            InferenceJson.BinarySpeechInput.Properties.Single(property => property.Name == "Data").CustomConverter);
    }

    [Fact]
    public void ConstructorInitAndWithFreezeEveryMutableInput()
    {
        byte[] bytes = [1, 2]; var input = new BinarySpeechInput(ContentType: "audio/wav", Data: bytes); bytes[0] = 9;
        Assert.Equal(1, input.Data.Span[0]); var changedInput = input with { Data = bytes }; bytes[0] = 8; Assert.Equal(9, changedInput.Data.Span[0]);
        List<string> terms = ["A", "A"]; var options = new SpeechTranscriptionOptions(KeyTerms: terms); terms.Clear(); Assert.Equal(2, options.KeyTerms!.Count);
        var stream = new SpeechStreamOptions(KeyTerms: terms); terms.Add("B"); Assert.Empty(stream.KeyTerms!);
        var revised = stream with { KeyTerms = terms }; terms.Clear(); Assert.Single(revised.KeyTerms!);
        var word = new SpeechWord(Text: "literal", Start: TimeSpan.Zero, End: TimeSpan.FromSeconds(1));
        List<SpeechWord> words = [word]; var channel = new SpeechChannelTranscript(Index: 0, Text: "channel", Words: words); words.Clear(); Assert.Single(channel.Words!);
        List<SpeechChannelTranscript> channels = [channel]; var result = new SpeechTranscriptionResult(Text: "all", Provenance: new() { ImplementationId = "i", ModelId = "m" }, Words: words, Channels: channels);
        channels.Clear(); words.Add(word); Assert.Single(result.Channels!); Assert.Empty(result.Words!);
        var partial = new SpeechTranscriptPartialEvent(Text: "snapshot", Words: words); var done = new SpeechTranscriptDoneEvent(Text: "done", Words: words); words.Clear();
        Assert.Single(partial.Words!); Assert.Single(done.Words!);
        var changedDone = done with { Words = words }; words.Add(word); Assert.Empty(changedDone.Words!);
        HashSet<SpeechAudioEncoding> encodings = [SpeechAudioEncoding.Opus]; HashSet<InferenceMediaSourceKind> kinds = [InferenceMediaSourceKind.Resource];
        List<int> rates = [24000]; List<string> languages = ["en"];
        var mode = new SpeechTranscriptionModeCapabilities { Encodings = encodings, InputKinds = kinds, SampleRates = rates, Languages = languages };
        encodings.Clear(); kinds.Clear(); rates.Clear(); languages.Clear();
        Assert.Single(mode.Encodings); Assert.Single(mode.InputKinds); Assert.Single(mode.SampleRates!); Assert.Single(mode.Languages!);
        Assert.Throws<ArgumentException>(() => options with { KeyTerms = new string[] { null! } });
        Assert.Throws<ArgumentNullException>(() => done with { Text = null! });
        Assert.Throws<ArgumentNullException>(() => mode with { Encodings = null! });
        Assert.Throws<ArgumentOutOfRangeException>(() => mode with { InputKinds = new HashSet<InferenceMediaSourceKind> { (InferenceMediaSourceKind)99 } });
        Assert.Throws<ArgumentOutOfRangeException>(() => word with { Confidence = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => stream with { SmartTurnThreshold = double.PositiveInfinity });
        Assert.DoesNotContain("secret", new UrlSpeechInput(new("https://example.invalid/?secret=token")).ToString(), StringComparison.Ordinal);
        (string text, TimeSpan start, TimeSpan end, double? confidence, int? speaker) = word;
        Assert.Equal("literal", text); Assert.Equal(TimeSpan.Zero, start); Assert.Equal(TimeSpan.FromSeconds(1), end); Assert.Null(confidence); Assert.Null(speaker);
    }

    [Fact]
    public void DefaultCapabilitiesAndOptionalFactsDoNotInventAvailability()
    {
        var capabilities = new SpeechTranscriptionCapabilities();
        Assert.False(capabilities.Transcription.Available); Assert.False(capabilities.Streaming.Available);
        Assert.Empty(capabilities.Transcription.InputKinds); Assert.Empty(capabilities.Streaming.Encodings);
        Assert.Null(capabilities.Transcription.Languages); Assert.Null(capabilities.Streaming.SampleRates); Assert.Null(capabilities.Streaming.MaximumChannels);
        Assert.False(capabilities.Streaming.SupportsFinalize); Assert.False(capabilities.Streaming.SupportsSmartTurn);
        var done = new SpeechTranscriptDoneEvent("done"); Assert.Null(done.Duration); Assert.Null(done.ChannelIndex); Assert.Null(done.Words);
        var partial = new SpeechTranscriptPartialEvent("snapshot", IsFinal: true, SpeechFinal: false);
        Assert.True(partial.IsFinal); Assert.False(partial.SpeechFinal);
        Assert.Equal(4, (int)InferenceTask.SpeechTranscription); Assert.Equal(InferenceCapability.Audio, InferenceTask.SpeechTranscription.GetCapability());
    }

    [Fact]
    public void RuntimeRefusalHasNoCallbacksOrPublicDataHandleRoots()
    {
        Assert.Equal(0, ExerciseRuntimeRefusal());
        Assert.Null(typeof(InferenceJson).GetProperty(nameof(ISpeechTranscriptionClient)));
        Assert.Null(typeof(InferenceJson).GetProperty(nameof(ISpeechTranscriptionSession)));
        Assert.Null(typeof(InferenceJson).GetProperty(nameof(SpeechTranscriptEvent)));
    }

    public static int ExerciseRuntimeRefusal()
    {
        var runtime = new UntouchableRuntime(); JsonSerializerOptions options = InferenceJson.SpeechTranscriptionOptions.Options;
        foreach (Type type in new[] { typeof(ISpeechTranscriptionClient), typeof(ISpeechTranscriptionSession) })
        {
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(runtime, type, options));
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{}", type, options));
            Assert.Equal("null", JsonSerializer.Serialize((object?)null, type, options));
            Assert.Null(JsonSerializer.Deserialize("null", type, options));
        }
        return runtime.Effects;
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignCommonAndFixtureGraphsRetire(string name, string json)
    {
        WeakReference weak = ExerciseForeign(name, json);
        for (int i = 0; weak.IsAlive && i < 20; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(string name, string json)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(InferenceJson).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(SpeechContractTests).Assembly.Location);
            foreach ((Assembly actual, Assembly original) in new[] { (common, typeof(InferenceJson).Assembly), (fixture, typeof(SpeechContractTests).Assembly) })
            { Assert.NotSame(original, actual); Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual)); Assert.Equal(original.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId); }
            JsonTypeInfo metadata = Metadata(common.GetType(typeof(InferenceJson).FullName!, true)!, name);
            object value = RoundTrip(json, metadata);
            Assert.Same(common, metadata.Type.Assembly); Assert.Same(common, value.GetType().Assembly);
            JsonTypeInfo concreteMetadata = metadata.Options.GetTypeInfo(value.GetType());
            foreach (string field in new[] { "Resource", "Provenance", "Error" })
                if (value.GetType().GetProperty(field)?.GetValue(value) is { } nested)
                { Assert.Same(common, nested.GetType().Assembly); Assert.Same(common, concreteMetadata.Properties.Single(property => property.Name == field).PropertyType.Assembly); }
            MethodInfo entry = fixture.GetType(typeof(SpeechContractTests).FullName!, true)!.GetMethod(nameof(ExerciseRuntimeRefusal))!;
            Assert.Same(fixture, entry.Module.Assembly); Assert.Equal(0, entry.Invoke(null, null));
            Type sessionType = common.GetType(typeof(ISpeechTranscriptionSession).FullName!, true)!;
            Assert.Same(common, metadata.Options.GetConverter(sessionType).GetType().Assembly);
            if (name == "BinarySpeechInput") Assert.Same(common, metadata.Properties.Single(property => property.Name == "Data").CustomConverter!.GetType().Assembly);
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static JsonTypeInfo Metadata(Type owner, string name) => (JsonTypeInfo)owner.GetProperty(name)!.GetValue(null)!;
    private static object RoundTrip(string json, JsonTypeInfo metadata)
    {
        object value = JsonSerializer.Deserialize(json, metadata)!;
        string written = JsonSerializer.Serialize(value, metadata);
        Assert.Equal(written, JsonSerializer.Serialize(JsonSerializer.Deserialize(written, metadata), metadata));
        using JsonDocument expected = JsonDocument.Parse(json); using JsonDocument actual = JsonDocument.Parse(written);
        Preserve(expected.RootElement, actual.RootElement); return value;
    }
    private static void Preserve(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in expected.EnumerateObject()) { Assert.True(actual.TryGetProperty(property.Name, out var nested)); Preserve(property.Value, nested); }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (int i = 0; i < expected.GetArrayLength(); i++) Preserve(expected[i], actual[i]); }
        else Assert.True(JsonElement.DeepEquals(expected, actual), expected.GetRawText());
    }
    private sealed record UnadmittedInput : SpeechTranscriptionInput
    {
        public UnadmittedInput(SpeechTranscriptionInput original) : base(original) { }
    }
    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll"); return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
    private sealed class UntouchableRuntime : ISpeechTranscriptionClient, ISpeechTranscriptionSession
    {
        public int Effects { get; private set; }
        private InvalidOperationException Touched() { Effects++; return new("Runtime collaborator was inspected."); }
        public override string ToString() => throw Touched();
        public IAsyncEnumerable<SpeechTranscriptEvent> Events => throw Touched();
        public Task<SpeechTranscriptionCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) => throw Touched();
        public Task<SpeechTranscriptionResult> TranscribeAsync(SpeechTranscriptionInput input, SpeechTranscriptionOptions options, CancellationToken cancellationToken = default) => throw Touched();
        public Task<ISpeechTranscriptionSession> OpenStreamAsync(SpeechStreamOptions options, InferenceStreamLimits limits, CancellationToken cancellationToken = default) => throw Touched();
        public IAsyncEnumerable<SpeechTranscriptEvent> TranscribeStreamAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> audio, SpeechStreamOptions options, InferenceStreamLimits limits, CancellationToken cancellationToken = default) => throw Touched();
        public Task AppendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default) => throw Touched();
        public Task FinalizeAsync(int? channel = null, CancellationToken cancellationToken = default) => throw Touched();
        public Task CompleteAudioAsync(CancellationToken cancellationToken = default) => throw Touched();
        public ValueTask DisposeAsync() => throw Touched();
    }
}
