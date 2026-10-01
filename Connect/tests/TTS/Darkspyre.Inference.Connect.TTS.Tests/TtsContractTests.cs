using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class TtsContractTests
{
    private const string Provenance = "{\"ImplementationId\":\"i\",\"ModelId\":\"m\"}";
    public static TheoryData<string, string> Shapes => new()
    {
        { "TtsTransport", "1" }, { "PocketTtsProtocol", "1" }, { "TtsAudioFormat", "4" },
        { "PocketTtsEndpointOptions", "{}" },
        { "TtsConnection", "{\"Endpoint\":\"https://example.invalid/\"}" },
        { "TtsRequest", "{\"Text\":\"<&>\",\"Language\":\"en\",\"SampleRate\":24000,\"BitRate\":128000,\"IncludeTimestamps\":false,\"Xai\":{\"OptimizeStreamingLatency\":2,\"TextNormalization\":false,\"Replace\":{\"A\":\"B\"}}}" },
        { "TtsVoice", "{\"Id\":\"voice\",\"DisplayName\":\"name\",\"Locale\":null}" },
        { "TtsCapabilities", "{\"SupportsVoiceListing\":false,\"SupportsSpeed\":false,\"Formats\":[0,1],\"SampleRates\":null,\"BitRates\":[]}" },
        { "TtsResult", "{\"ContentType\":\"audio/wav\",\"Audio\":\"AQI=\",\"Provenance\":" + Provenance + ",\"Duration\":null,\"Timestamps\":[{\"Text\":\" a \",\"Start\":\"00:00:01\",\"End\":\"00:00:02\"}]}" },
        { "TtsSetupConfiguration", "{\"Connection\":{\"Endpoint\":\"https://example.invalid/\"}}" },
        { "TtsSetupHeaderChanges", "{\"Set\":{},\"Remove\":[]}" },
        { "TtsSetupChanges", "{\"Identity\":{\"Id\":\"id\",\"Intent\":0},\"Configuration\":{\"Connection\":{\"Endpoint\":\"https://example.invalid/\"}},\"Headers\":{\"Set\":{},\"Remove\":[]}}" },
        { "TtsTimestamp", "{\"Text\":\" 🦊 \",\"Start\":\"00:00:00\",\"End\":\"00:00:00\"}" },
        { "XaiTtsOptions", "{\"OptimizeStreamingLatency\":null,\"TextNormalization\":false,\"Replace\":{}}" },
        { "TtsStreamOptions", "{\"VoiceId\":null,\"Format\":3,\"Xai\":{\"Replace\":null}}" },
        { "TtsAudioChunk", "{\"Audio\":\"\",\"ContentType\":null,\"Duration\":null,\"Timestamps\":[]}" },
        { "TtsUtteranceCompleted", "{\"ProviderTraceId\":null}" },
        { "TtsStreamError", "{\"Error\":{\"Code\":6,\"Message\":\"sanitized\",\"ProviderCode\":null}}" }
    };

    [Theory, MemberData(nameof(Shapes))]
    public void ExactOwnedRootsPreserveData(string name, string json) => RoundTrip(json, Metadata(typeof(ConnectTtsJson), name));

    [Theory]
    [InlineData("Language")]
    [InlineData("SampleRate")]
    [InlineData("BitRate")]
    [InlineData("IncludeTimestampsFalse")]
    [InlineData("XaiNormalizationFalse")]
    [InlineData("XaiReplacementEmpty")]
    [InlineData("XaiLatency")]
    [InlineData("XaiEmpty")]
    public async Task PocketRejectsEveryExplicitNewControlBeforeHttp(string control)
    {
        using var handler = new NoHttp(); using var http = new HttpClient(handler);
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(new() { Endpoint = new("https://example.invalid") });
        TtsRequest request = control switch
        {
            "Language" => new() { Text = "text", Language = "en" },
            "SampleRate" => new() { Text = "text", SampleRate = 24000 },
            "BitRate" => new() { Text = "text", BitRate = 128000 },
            "IncludeTimestampsFalse" => new() { Text = "text", IncludeTimestamps = false },
            "XaiNormalizationFalse" => new() { Text = "text", Xai = new(TextNormalization: false) },
            "XaiReplacementEmpty" => new() { Text = "text", Xai = new(Replace: new Dictionary<string, string>()) },
            "XaiLatency" => new() { Text = "text", Xai = new(OptimizeStreamingLatency: 0) },
            _ => new() { Text = "text", Xai = new() }
        };
        InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() => client.SpeakAsync(request));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, failure.Code);
        Assert.Equal(0, handler.Calls);
        Assert.False((await client.GetCapabilitiesAsync()).SupportsStreaming);
        Assert.False(client is ITtsStreamingClient);
    }

    [Fact]
    public void ListsDictionariesAndBytesAreCopiedByConstructorsAndWith()
    {
        byte[] bytes = [1, 2]; List<TtsTimestamp> times = [new("literal", TimeSpan.Zero, TimeSpan.FromSeconds(1))];
        var chunk = new TtsAudioChunk(bytes, Timestamps: times); bytes[0] = 9; times.Clear();
        Assert.Equal(1, chunk.Audio.Span[0]); Assert.Single(chunk.Timestamps!);
        var result = new TtsResult("audio/wav", bytes, new() { ImplementationId = "i", ModelId = "m" }) { Timestamps = times };
        var changed = result with { Audio = bytes, Timestamps = times }; bytes[0] = 8; times.Add(new("x", TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal(9, changed.Audio.Span[0]); Assert.Empty(changed.Timestamps!);
        Dictionary<string, string> replace = new(StringComparer.Ordinal) { ["a"] = "b" }; var options = new XaiTtsOptions(Replace: replace); replace.Clear();
        Assert.Equal("b", options.Replace!["a"]); var rewritten = options with { Replace = replace }; replace["x"] = "y"; Assert.Empty(rewritten.Replace!);
        List<TtsAudioFormat> formats = [TtsAudioFormat.Wav]; var capabilities = new TtsCapabilities(true, false, formats); formats.Clear(); Assert.Single(capabilities.Formats);
        Assert.Throws<ArgumentException>(() => chunk with { Timestamps = new TtsTimestamp[] { null! } });
        Assert.Equal("TtsAudioChunk { <data> }", chunk.ToString());
        (string text, TimeSpan start, TimeSpan end) = times[0]; Assert.Equal("x", text); Assert.Equal(start, end);
    }

    [Fact]
    public void ExplicitBudgetFactsCannotBeMissingNullZeroOrNegative()
    {
        const string json = "{\"MaximumBufferedEvents\":1,\"MaximumBufferedPayloadBytes\":9223372036854775807,\"MaximumMessageBytes\":1}";
        RoundTrip(json, InferenceJson.InferenceStreamLimits);
        foreach (string name in new[] { "MaximumBufferedEvents", "MaximumBufferedPayloadBytes", "MaximumMessageBytes" })
        {
            foreach (JsonNode? value in new JsonNode?[] { null, JsonValue.Create(0), JsonValue.Create(-1) })
            {
                JsonObject malformed = JsonNode.Parse(json)!.AsObject(); malformed[name] = value;
                Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(malformed.ToJsonString(), InferenceJson.InferenceStreamLimits)));
            }
            JsonObject missing = JsonNode.Parse(json)!.AsObject(); missing.Remove(name);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(missing.ToJsonString(), InferenceJson.InferenceStreamLimits));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new InferenceStreamLimits(1, 1, 1) with { MaximumMessageBytes = 0 });
    }

    [Theory]
    [InlineData("TtsAudioChunk", "{\"Audio\":\"\"}", "Audio")]
    [InlineData("TtsTimestamp", "{\"Text\":\"\",\"Start\":\"00:00:00\",\"End\":\"00:00:00\"}", "Text,Start,End")]
    [InlineData("TtsCapabilities", "{\"SupportsVoiceListing\":false,\"SupportsSpeed\":false,\"Formats\":[]}", "SupportsVoiceListing,SupportsSpeed,Formats")]
    public void MissingAndNullFactsRefuse(string name, string json, string properties)
    {
        JsonTypeInfo metadata = Metadata(typeof(ConnectTtsJson), name);
        foreach (string property in properties.Split(','))
        {
            JsonObject missing = JsonNode.Parse(json)!.AsObject(); missing.Remove(property);
            Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(missing.ToJsonString(), metadata)));
            JsonObject nullValue = JsonNode.Parse(json)!.AsObject(); nullValue[property] = null;
            Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(nullValue.ToJsonString(), metadata)));
        }
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignOwnedGraphsRetire(string name, string json)
    {
        WeakReference weak = ExerciseForeign(name, json);
        for (int i = 0; weak.IsAlive && i < 20; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(string name, string json)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(ConnectTtsJson).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly tts = generation.LoadFromAssemblyPath(typeof(ConnectTtsJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            foreach ((Assembly actual, Assembly original) in new[] { (tts, typeof(ConnectTtsJson).Assembly), (common, typeof(InferenceJson).Assembly) })
            { Assert.NotSame(original, actual); Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual)); Assert.Equal(original.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId); }
            JsonTypeInfo metadata = Metadata(tts.GetType(typeof(ConnectTtsJson).FullName!, true)!, name);
            object value = RoundTrip(json, metadata); Assert.Same(tts, value.GetType().Assembly);
            if (name == "TtsResult" || name == "TtsStreamError")
            {
                string field = name == "TtsResult" ? "Provenance" : "Error";
                object nested = value.GetType().GetProperty(field)!.GetValue(value)!;
                Assert.Same(common, nested.GetType().Assembly);
                Assert.Same(common, metadata.Properties.Single(property => property.Name == field).PropertyType.Assembly);
            }
            JsonTypeInfo limits = Metadata(common.GetType(typeof(InferenceJson).FullName!, true)!, nameof(InferenceStreamLimits));
            Assert.Same(common, limits.Type.Assembly); RoundTrip("{\"MaximumBufferedEvents\":1,\"MaximumBufferedPayloadBytes\":2,\"MaximumMessageBytes\":3}", limits);
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
        Preserve(expected.RootElement, actual.RootElement);
        return value;
    }
    private static void Preserve(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in expected.EnumerateObject()) { Assert.True(actual.TryGetProperty(property.Name, out var nested)); Preserve(property.Value, nested); }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (int i = 0; i < expected.GetArrayLength(); i++) Preserve(expected[i], actual[i]); }
        else Assert.True(JsonElement.DeepEquals(expected, actual), expected.GetRawText());
    }
    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll"); return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
    private sealed class NoHttp : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
        }
    }
}
