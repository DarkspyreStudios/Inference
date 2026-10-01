using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Video.Tests;

public sealed class VideoJsonTests
{
    private const string Scope = "{\"Owner\":\"endpoint\",\"Identity\":\"account\"}";
    private const string Resource = "{\"type\":\"resource\",\"Resource\":{\"WorkspaceId\":\"w\",\"Id\":\"original\"}}";
    private const string Job = "{\"ProviderJobId\":\"actual-job\",\"Scope\":" + Scope + "}";
    private const string Output = "{\"Scope\":" + Scope + ",\"ProviderState\":{\"signed\":\"secret\"},\"ContentType\":null,\"Duration\":\"00:00:00\"}";
    private const string Connection = "{\"Transport\":0,\"Endpoint\":\"https://api.x.ai/v1\",\"Xai\":{\"ConnectionId\":\"c\"},\"Authorization\":null}";
    private const string Configuration = "{\"Connection\":" + Connection + ",\"ModelId\":\"manual-model\"}";
    private const string Options = "{\"Storage\":{\"FileName\":\"result\",\"ExpiresAfter\":\"00:00:00\",\"PublicUrl\":{\"type\":\"flag\",\"Enabled\":false}},\"Output\":{\"UploadUrl\":\"https://upload.example.invalid/put?signature=secret\"},\"User\":\"opaque\"}";
    public static TheoryData<string, string> Shapes => new()
    {
        { "VideoTransport", "0" }, { "VideoConnection", Connection },
        { "XaiVideoOptions", Options }, { "XaiVideoUploadDestination", "{\"UploadUrl\":\"https://example.invalid/upload\"}" },
        { "VideoProviderOptions", "{\"type\":\"xai-video\",\"Storage\":{\"FileName\":\"out\",\"PublicUrl\":{\"type\":\"configuration\",\"Options\":{}}}}" },
        { "VideoSetupConfiguration", Configuration },
        { "VideoSetupHeaderChanges", "{\"Set\":{\"Protected\":\"value\"},\"Remove\":[\"other\"]}" },
        { "VideoSetupChanges", "{\"Identity\":{\"Intent\":0,\"Id\":\"entry\",\"ExpectedVersion\":null},\"Configuration\":" + Configuration + ",\"Headers\":{\"Set\":{},\"Remove\":[]}}" },
        { "VideoRequest", "{\"type\":\"generation\",\"ModelId\":\"m\",\"ProviderOptions\":{\"type\":\"xai-video\",\"User\":\"u\"}}" },
        { "VideoRequest", "{\"type\":\"edit\",\"ModelId\":\"m\",\"Prompt\":\"edit\",\"SourceVideo\":" + Resource + "}" },
        { "VideoRequest", "{\"type\":\"extension\",\"ModelId\":\"m\",\"SourceVideo\":" + Resource + "}" },
        { "VideoGenerationRequest", "{\"ModelId\":\"m\",\"FirstFrame\":{\"type\":\"binary\",\"Data\":\"AQID\",\"ContentType\":null},\"LastFrame\":{\"type\":\"url\",\"Url\":\"https://example.invalid/frame\"},\"ReferenceImages\":[{\"type\":\"file\",\"File\":{\"FileId\":\"native\"}}],\"ReferenceAudio\":[{\"type\":\"voice\",\"VoiceId\":\"preset\"},{\"type\":\"audio\",\"Source\":" + Resource + "}],\"ProviderOptions\":{\"type\":\"xai-video\",\"Output\":{\"UploadUrl\":\"https://example.invalid/put\"}}}" },
        { "VideoEditRequest", "{\"ModelId\":\"m\",\"Prompt\":\"\",\"SourceVideo\":" + Resource + "}" },
        { "VideoExtensionRequest", "{\"ModelId\":\"m\",\"SourceVideo\":" + Resource + "}" },
        { "VideoCapabilities", "{\"Operations\":[0,1,2],\"SourceKinds\":[0,1,2,3],\"MaximumReferenceAudio\":0}" },
        { "VideoJob", Job }, { "VideoJobState", "2" },
        { "VideoJobSnapshot", "{\"Job\":" + Job + ",\"State\":1,\"ProgressPercent\":null,\"Output\":" + Output + "}" },
        { "VideoOutput", Output }, { "VideoOperation", "2" },
        { "VideoKeyframe", "{\"Image\":" + Resource + ",\"Timestamp\":\"00:00:00\"}" },
        { "VideoReferenceAudio", "{\"type\":\"voice\",\"VoiceId\":\"preset\"}" },
        { "VideoReferenceAudio", "{\"type\":\"audio\",\"Source\":" + Resource + "}" },
        { "VideoVoiceReference", "{\"VoiceId\":\"preset\"}" },
        { "VideoAudioReference", "{\"Source\":" + Resource + "}" }
    };

    [Theory, MemberData(nameof(Shapes))]
    public void EveryOwnedGetterPreservesActualFacts(string getter, string json) => RoundTrip(Metadata(typeof(ConnectVideoJson), getter), json);

    [Fact]
    public void NewOwnedShapesRejectMissingNullAndUnknownFacts()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Endpoint\":\"https://api.x.ai/v1\"}", ConnectVideoJson.VideoConnection));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Transport\":0}", ConnectVideoJson.VideoConnection));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{}", ConnectVideoJson.XaiVideoUploadDestination));
        Assert.Throws<ArgumentNullException>(() => JsonSerializer.Deserialize("{\"UploadUrl\":null}", ConnectVideoJson.XaiVideoUploadDestination));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", ConnectVideoJson.VideoTransport));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", ConnectVideoJson.VideoJobState));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"unknown\"}", ConnectVideoJson.VideoProviderOptions));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize<VideoProviderOptions>(new UnknownOptions(), ConnectVideoJson.VideoProviderOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ModelId\":\"m\",\"FirstFrame\":{\"type\":\"binary\",\"Data\":null}}", ConnectVideoJson.VideoGenerationRequest));
        Assert.True(Assert.IsType<BinaryMediaSource>(JsonSerializer.Deserialize("{\"ModelId\":\"m\",\"FirstFrame\":{\"type\":\"binary\",\"Data\":\"\"}}", ConnectVideoJson.VideoGenerationRequest)!.FirstFrame).Data.IsEmpty);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new VideoGenerationRequest { ModelId = "m", ProviderOptions = new XaiVideoOptions() }, InferenceJson.VideoGenerationRequest));
    }

    [Fact]
    public void WithAssignmentCopiesMapsAndRetainsRequiredPresenceAndSafeDiagnostics()
    {
        var map = new Dictionary<string, string> { ["protected"] = "secret" };
        var connection = new VideoConnection { Transport = VideoTransport.XaiImagine, Endpoint = "https://api.x.ai/v1", AdditionalHeaders = map };
        map.Clear(); Assert.Single(connection.AdditionalHeaders);
        var next = connection with { AdditionalHeaders = map }; map["new"] = "value"; Assert.Empty(next.AdditionalHeaders);
        var set = new Dictionary<string, string> { ["one"] = "secret" }; var remove = new List<string> { "two" };
        var changes = new VideoSetupHeaderChanges(set, remove); set.Clear(); remove.Clear(); Assert.Single(changes.Set); Assert.Single(changes.Remove);
        Assert.Throws<ArgumentNullException>(() => changes with { Set = null! });
        Assert.Throws<ArgumentNullException>(() => new XaiVideoUploadDestination(new("https://example.invalid/?secret")) with { UploadUrl = null! });
        Assert.DoesNotContain("secret", connection.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", new XaiVideoOptions(new("secret"), new(new("https://example.invalid/?secret")), "secret").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CallerGeneratedEnvelopeUsesActualFixedAuthorityRefusalWithoutCallbacks()
    {
        var authorization = new Authorization();
        JsonTypeInfo<VideoRuntimeEnvelope> metadata = RuntimeMetadata();
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new VideoRuntimeEnvelope { Authorization = authorization }, metadata));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Authorization\":{}}", metadata));
        Assert.NotNull(JsonSerializer.Deserialize("{\"Authorization\":null,\"Binding\":null,\"Resources\":null,\"Inference\":null}", metadata));
        Assert.Equal(0, authorization.Calls);
        Assert.Null(typeof(ConnectVideoJson).GetProperty(nameof(XaiFamilyBinding)));
    }

    public static JsonTypeInfo<VideoRuntimeEnvelope> RuntimeMetadata()
    {
        var options = new JsonSerializerOptions(ConnectVideoJson.VideoConnection.Options) { TypeInfoResolver = VideoRuntimeContext.Default };
        options.MakeReadOnly(); return options.GetTypeInfo<VideoRuntimeEnvelope>();
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignOwnedMetadataAndNestedGraphRetire(string getter, string json)
    {
        WeakReference generation = ExerciseForeign(getter, json);
        for (int i = 0; generation.IsAlive && i < 20; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(generation.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(string getter, string json)
    {
        var generation = new ForeignGeneration(Path.GetDirectoryName(typeof(ConnectVideoJson).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly video = generation.LoadFromAssemblyPath(typeof(ConnectVideoJson).Assembly.Location);
            Assert.NotSame(typeof(ConnectVideoJson).Assembly, video);
            Assert.Equal(typeof(ConnectVideoJson).Assembly.ManifestModule.ModuleVersionId, video.ManifestModule.ModuleVersionId);
            JsonTypeInfo metadata = Metadata(video.GetType(typeof(ConnectVideoJson).FullName!, true)!, getter);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
            Assert.Same(video, metadata.Options.TypeInfoResolver!.GetType().Assembly);
            object? value = RoundTrip(metadata, json);
            if (value is not null) AssertNestedProvenance(value, generation);
            Assembly common = generation.Assemblies.Single(assembly => assembly.GetName().Name == typeof(InferenceJson).Assembly.GetName().Name);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            Type binary = common.GetType(typeof(BinaryMediaSource).FullName!, true)!;
            JsonTypeInfo binaryMetadata = metadata.Options.GetTypeInfo(binary);
            Assert.Same(common, binaryMetadata.Properties.Single(property => property.Name == "Data").CustomConverter!.GetType().Assembly);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(VideoJsonTests).Assembly.Location);
            Assert.Equal(typeof(VideoJsonTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            var runtime = (JsonTypeInfo)fixture.GetType(typeof(VideoJsonTests).FullName!, true)!.GetMethod(nameof(RuntimeMetadata))!.Invoke(null, null)!;
            Assert.Same(fixture, runtime.Type.Assembly);
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Binding\":{}}", runtime));
            Assert.NotNull(JsonSerializer.Deserialize("{\"Binding\":null}", runtime));
        }
        finally { generation.Unload(); }
        return weak;
    }
    private static void AssertNestedProvenance(object value, AssemblyLoadContext generation)
    {
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(value.GetType().Assembly));
        foreach (PropertyInfo property in value.GetType().GetProperties())
        {
            object? nested = property.GetValue(value);
            if (nested is System.Collections.IEnumerable sequence && nested is not string)
            {
                foreach (object? item in sequence)
                    if (item?.GetType().Namespace?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) == true) AssertNestedProvenance(item, generation);
            }
            else if (nested?.GetType().Namespace?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) == true) AssertNestedProvenance(nested, generation);
        }
    }
    private static JsonTypeInfo Metadata(Type owner, string getter) => (JsonTypeInfo)owner.GetProperty(getter)!.GetValue(null)!;
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
            foreach (JsonProperty property in expected.EnumerateObject()) { Assert.True(actual.TryGetProperty(property.Name, out JsonElement child)); Preserve(property.Value, child); }
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
    private sealed record UnknownOptions : VideoProviderOptions;
    private sealed class Authorization : IProviderAuthorization
    {
        public int Calls { get; private set; }
        public ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default) { Calls++; throw new InvalidOperationException(); }
    }
}

public sealed record VideoRuntimeEnvelope
{
    public IProviderAuthorization? Authorization { get; init; }
    public XaiFamilyBinding? Binding { get; init; }
    public IInferenceResourceAccess? Resources { get; init; }
    public IVideoInference? Inference { get; init; }
}
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(VideoRuntimeEnvelope))]
internal sealed partial class VideoRuntimeContext : JsonSerializerContext;
