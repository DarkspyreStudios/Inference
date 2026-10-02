using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.Audio.Tests;

public sealed class AudioJsonTests
{
    [Theory]
    [InlineData(nameof(AudioTransport))]
    [InlineData(nameof(TangoFluxEndpointOptions))]
    [InlineData(nameof(AudioConnection))]
    [InlineData(nameof(AudioGenerationRequest))]
    [InlineData(nameof(AudioGenerationCapabilities))]
    [InlineData(nameof(AudioGenerationResult))]
    [InlineData(nameof(AudioSetupConfiguration))]
    [InlineData(nameof(AudioSetupHeaderChanges))]
    [InlineData(nameof(AudioSetupChanges))]
    public void ExactPublicGetterOwnsExistingDataRoot(string name)
    {
        Assembly assembly = typeof(AudioConnection).Assembly;
        Type? owner = assembly.GetType("Darkspyre.Inference.Connect.Audio.ConnectAudioJson");
        Assert.NotNull(owner);
        PropertyInfo? property = owner.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(property);
        JsonTypeInfo metadata = Assert.IsAssignableFrom<JsonTypeInfo>(property.GetValue(null));
        Assert.Equal("Darkspyre.Inference.Connect.Audio." + name, metadata.Type.FullName);
        Assert.Same(assembly, metadata.Options.TypeInfoResolver!.GetType().Assembly);
    }

    private const string Connection = """{"Transport":0,"Endpoint":"http://127.0.0.1:8769/","RequestTimeout":"00:00:12","Headers":{"X-Exact":"a"},"TangoFlux":{"GeneratePath":"/generate"}}""";
    private const string Capabilities = """{"MinimumDuration":"00:00:01","MaximumDuration":"00:00:30","MinimumSteps":10,"MaximumSteps":100,"MinimumGuidanceScale":0.1,"MaximumGuidanceScale":20,"SupportsSeed":false,"SupportsCachePreference":true,"SupportsModelListing":false,"SupportsHealthCheck":false,"ContentTypes":["audio/wav","audio/wav"]}""";
    private const string Result = """{"ContentType":"audio/wav","Audio":"AAEC","MeasuredDuration":null,"Provenance":{"ImplementationId":"tangoflux","ModelId":"model","ModelRevision":null,"Runtime":null,"RuntimeVersion":null,"Backend":null,"Elapsed":null}}""";
    private const string HeaderChanges = """{"Set":{"X-Exact":"value"},"Remove":["X-Old","X-Old"]}""";

    public static IEnumerable<object[]> Shapes()
    {
        yield return [nameof(AudioTransport), "0"];
        yield return [nameof(TangoFluxEndpointOptions), """{"GeneratePath":"/generate?opaque=x"}"""];
        yield return [nameof(AudioConnection), Connection];
        yield return [nameof(AudioGenerationRequest), """{"Prompt":"line\n<opaque>","Duration":"00:00:05","Steps":null,"GuidanceScale":null,"Seed":0,"UseCache":false}"""];
        yield return [nameof(AudioGenerationCapabilities), Capabilities];
        yield return [nameof(AudioGenerationResult), Result];
        yield return [nameof(AudioSetupConfiguration), $$"""{"Connection":{{Connection}},"Duration":"00:00:05","Steps":null,"GuidanceScale":null,"Seed":0,"UseCache":false}"""];
        yield return [nameof(AudioSetupHeaderChanges), HeaderChanges];
        yield return [nameof(AudioSetupChanges), $$"""{"Identity":{"Intent":1,"Id":"saved","ExpectedVersion":"v1"},"Configuration":{"Connection":{{Connection}},"Seed":0,"UseCache":false},"Headers":{{HeaderChanges}}}"""];
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ExactGetterRoundTripsPopulatedGraph(string name, string json) =>
        RoundTrip(GetMetadata(typeof(ConnectAudioJson), name), json);

    [Theory]
    [InlineData("Endpoint", "null")]
    [InlineData("Headers", "null")]
    [InlineData("TangoFlux", "null")]
    [InlineData("Headers", "{\"X-Test\":null}")]
    public void ConnectionExplicitNullFactsRefuse(string name, string json)
    {
        JsonObjectPropertyRefuses(nameof(AudioConnection), Connection, name, json);
    }

    [Theory]
    [InlineData("ContentType")]
    [InlineData("Audio")]
    [InlineData("Provenance")]
    public void ResultMissingAndNullFactsRefuse(string name)
    {
        JsonObjectPropertyRefuses(nameof(AudioGenerationResult), Result, name, "null");
        JsonObjectPropertyRefuses(nameof(AudioGenerationResult), Result, name, null);
    }

    [Theory]
    [InlineData("MinimumDuration")]
    [InlineData("MaximumDuration")]
    [InlineData("MinimumSteps")]
    [InlineData("MaximumSteps")]
    [InlineData("MinimumGuidanceScale")]
    [InlineData("MaximumGuidanceScale")]
    [InlineData("SupportsSeed")]
    [InlineData("SupportsCachePreference")]
    [InlineData("SupportsModelListing")]
    [InlineData("SupportsHealthCheck")]
    [InlineData("ContentTypes")]
    public void CapabilitiesCannotInventMissingFacts(string name) =>
        JsonObjectPropertyRefuses(nameof(AudioGenerationCapabilities), Capabilities, name, null);

    [Theory]
    [InlineData("AudioGenerationRequest", "{}")]
    [InlineData("AudioGenerationRequest", "{\"Prompt\":null,\"Duration\":\"00:00:05\"}")]
    [InlineData("AudioGenerationCapabilities", "{}")]
    [InlineData("AudioGenerationCapabilities", "{\"ContentTypes\":null}")]
    [InlineData("AudioSetupConfiguration", "{}")]
    [InlineData("AudioSetupConfiguration", "{\"Connection\":null}")]
    [InlineData("AudioSetupHeaderChanges", "{}")]
    [InlineData("AudioSetupHeaderChanges", "{\"Set\":{},\"Remove\":[null]}")]
    [InlineData("AudioSetupHeaderChanges", "{\"Set\":{\"X-Test\":null},\"Remove\":[]}")]
    [InlineData("AudioSetupChanges", "{}")]
    [InlineData("AudioConnection", "{\"Endpoint\":\"http://localhost/\",\"Unknown\":0}")]
    [InlineData("AudioTransport", "1")]
    [InlineData("AudioTransport", "\"TangoFluxHttp\"")]
    public void MalformedOrUnknownOwnedShapesRefuse(string name, string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, GetMetadata(typeof(ConnectAudioJson), name)));

    [Fact]
    public void ExistingOptionalDefaultsAndUnknownObservationsRemainDistinct()
    {
        var connection = JsonSerializer.Deserialize("{\"Endpoint\":\"http://localhost/\"}", ConnectAudioJson.AudioConnection)!;
        Assert.Equal(AudioTransport.TangoFluxHttp, connection.Transport);
        Assert.Equal(TimeSpan.FromSeconds(12), connection.RequestTimeout);
        Assert.Equal("/generate", connection.TangoFlux.GeneratePath);
        Assert.Empty(connection.Headers);
        var request = JsonSerializer.Deserialize("{\"Prompt\":\"text\",\"Duration\":\"00:00:05\"}", ConnectAudioJson.AudioGenerationRequest)!;
        Assert.Null(request.Seed); Assert.Null(request.UseCache); Assert.Null(request.Steps); Assert.Null(request.GuidanceScale);
        var result = JsonSerializer.Deserialize(Result, ConnectAudioJson.AudioGenerationResult)!;
        Assert.Null(result.MeasuredDuration);
        Assert.Equal(new byte[] { 0, 1, 2 }, result.Audio.ToArray());
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((AudioTransport)123, ConnectAudioJson.AudioTransport));
    }

    [Fact]
    public void InvalidPublicDataCannotSerializeAsValidFacts()
    {
        var result = new AudioGenerationResult(null!, ReadOnlyMemory<byte>.Empty, null, new() { ImplementationId = "a", ModelId = "b" });
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(result, ConnectAudioJson.AudioGenerationResult));
        var changes = new AudioSetupHeaderChanges(new Dictionary<string, string>(), [null!]);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(changes, ConnectAudioJson.AudioSetupHeaderChanges));
    }

    [Fact]
    public void LiveAuthoritiesRefuseWithoutCallbacks() => ExerciseRuntimeRefusal();

    private static void ExerciseRuntimeRefusal()
    {
        var collaborator = new BorrowedRuntime();
        using var stream = new MemoryStream([1, 2, 3]);
        var service = new AudioProviderService([collaborator]);
        JsonTypeInfo<AudioRuntimeEnvelope> metadata = RuntimeMetadata();
        foreach (var envelope in new[] { new AudioRuntimeEnvelope { Client = collaborator }, new() { Provider = collaborator },
            new() { Target = collaborator }, new() { Authorization = collaborator }, new() { Stream = stream }, new() { Service = service } })
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(envelope, metadata));
        foreach (string property in new[] { "Client", "Provider", "Target", "Authorization", "Stream", "Service", "Settings", "Manager" })
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"" + property + "\":{}}", metadata));
        string json = JsonSerializer.Serialize(new AudioRuntimeEnvelope(), metadata);
        Assert.NotNull(JsonSerializer.Deserialize(json, metadata));
        Assert.Equal(0, collaborator.Calls);
        Assert.Equal(0, stream.Position);
        Assert.True(stream.CanRead);
    }

    public static JsonTypeInfo<AudioRuntimeEnvelope> RuntimeMetadata()
    {
        var options = new JsonSerializerOptions(ConnectAudioJson.AudioConnection.Options) { TypeInfoResolver = AudioRuntimeContext.Default };
        options.MakeReadOnly(); return options.GetTypeInfo<AudioRuntimeEnvelope>();
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignGetterGraphAndRuntimePolicyRetire(string name, string json)
    {
        WeakReference weak = ExerciseForeign(name, json);
        for (int attempt = 0; weak.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(string name, string json)
    {
        var generation = new ForeignGeneration(Path.GetDirectoryName(typeof(AudioJsonTests).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly[] actual = [typeof(InferenceJson).Assembly, typeof(ConnectAudioJson).Assembly, typeof(AudioJsonTests).Assembly];
            Assembly[] loaded = actual.Select(assembly => generation.LoadFromAssemblyPath(assembly.Location)).ToArray();
            for (int index = 0; index < actual.Length; index++)
            {
                Assert.NotSame(actual[index], loaded[index]);
                Assert.Equal(actual[index].ManifestModule.ModuleVersionId, loaded[index].ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(loaded[index]));
            }
            Type owner = loaded[1].GetType(typeof(ConnectAudioJson).FullName!, true)!;
            JsonTypeInfo metadata = GetMetadata(owner, name);
            Assert.Same(loaded[1], metadata.Type.Assembly);
            Assert.Same(loaded[1], metadata.Options.TypeInfoResolver!.GetType().Assembly);
            object? value = RoundTrip(metadata, json);
            Assert.NotNull(value);
            Assert.Same(loaded[1], value.GetType().Assembly);
            if (name == nameof(AudioGenerationResult))
            {
                object provenance = value.GetType().GetProperty("Provenance")!.GetValue(value)!;
                Assert.Same(loaded[0], provenance.GetType().Assembly);
                Assert.Same(loaded[0], metadata.Properties.Single(property => property.Name == "Provenance").PropertyType.Assembly);
                Assert.Same(loaded[0], metadata.Properties.Single(property => property.Name == "Audio").CustomConverter!.GetType().Assembly);
            }
            if (name == nameof(AudioSetupChanges))
                Assert.Same(loaded[0], value.GetType().GetProperty("Identity")!.GetValue(value)!.GetType().Assembly);
            Type authority = loaded[0].GetType(typeof(IProviderAuthorization).FullName!, true)!;
            Assert.Same(loaded[0], metadata.Options.Converters.Single(converter => converter.CanConvert(authority)).GetType().Assembly);
            Type fixture = loaded[2].GetType(typeof(AudioJsonTests).FullName!, true)!;
            fixture.GetMethod(nameof(ExerciseRuntimeRefusal), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            JsonTypeInfo runtime = (JsonTypeInfo)fixture.GetMethod(nameof(RuntimeMetadata))!.Invoke(null, null)!;
            Assert.Same(loaded[2], runtime.Type.Assembly);
            Type client = loaded[1].GetType(typeof(IAudioGenerationClient).FullName!, true)!;
            Assert.Same(loaded[1], runtime.Options.Converters.Single(converter => converter.CanConvert(client)).GetType().Assembly);
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static JsonTypeInfo GetMetadata(Type owner, string name) => (JsonTypeInfo)owner.GetProperty(name)!.GetValue(null)!;

    private static object? RoundTrip(JsonTypeInfo metadata, string json)
    {
        object? value = JsonSerializer.Deserialize(json, metadata);
        string serialized = JsonSerializer.Serialize(value, metadata);
        using JsonDocument expected = JsonDocument.Parse(json); using JsonDocument actual = JsonDocument.Parse(serialized);
        Preserve(expected.RootElement, actual.RootElement);
        Assert.Equal(serialized, JsonSerializer.Serialize(JsonSerializer.Deserialize(serialized, metadata), metadata));
        return value;
    }

    private static void Preserve(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in expected.EnumerateObject())
            { Assert.True(actual.TryGetProperty(property.Name, out JsonElement child)); Preserve(property.Value, child); }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (int index = 0; index < expected.GetArrayLength(); index++) Preserve(expected[index], actual[index]); }
        else Assert.True(JsonElement.DeepEquals(expected, actual));
    }

    private static void JsonObjectPropertyRefuses(string name, string source, string property, string? replacement)
    {
        var node = JsonSerializer.Deserialize(source, AudioTestContext.Default.DictionaryStringJsonElement)!;
        if (replacement is null) node.Remove(property);
        else { using JsonDocument value = JsonDocument.Parse(replacement); node[property] = value.RootElement.Clone(); }
        string json = JsonSerializer.Serialize(node, AudioTestContext.Default.DictionaryStringJsonElement);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, GetMetadata(typeof(ConnectAudioJson), name)));
    }

    private sealed class ForeignGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class BorrowedRuntime : IAudioGenerationClient, IAudioProvider, ISetupCommitTarget<AudioSetupChanges>, IProviderAuthorization
    {
        public int Calls { get; private set; }
        private InvalidOperationException Effect() { Calls++; return new InvalidOperationException("No runtime callback is allowed."); }
        public AudioTransport Transport => AudioTransport.TangoFluxHttp;
        public InferenceImplementationDescriptor Descriptor => throw Effect();
        public InferenceSetupDefinition SetupDefinition => throw Effect();
        public IAudioGenerationClient Bind(AudioConnection connection) => throw Effect();
        public Task<AudioGenerationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => throw Effect();
        public Task<AudioGenerationResult> GenerateAsync(AudioGenerationRequest request, CancellationToken cancellationToken = default) => throw Effect();
        public Task<SetupCommitResult> CommitAsync(AudioSetupChanges changes, CancellationToken cancellationToken = default) => throw Effect();
        public ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default) => throw Effect();
        public override string ToString() => throw Effect();
    }
}

public sealed class AudioRuntimeEnvelope
{
    public IAudioGenerationClient? Client { get; init; }
    public IAudioProvider? Provider { get; init; }
    public ISetupCommitTarget<AudioSetupChanges>? Target { get; init; }
    public IProviderAuthorization? Authorization { get; init; }
    public Stream? Stream { get; init; }
    public AudioProviderService? Service { get; init; }
    public AudioSettings? Settings { get; init; }
    public AudioSettingsManager? Manager { get; init; }
}

[JsonSerializable(typeof(AudioRuntimeEnvelope))]
internal sealed partial class AudioRuntimeContext : JsonSerializerContext;

[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal sealed partial class AudioTestContext : JsonSerializerContext;
