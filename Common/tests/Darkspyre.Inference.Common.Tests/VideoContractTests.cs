using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;

namespace Darkspyre.Inference.Common.Tests;

public sealed class VideoContractTests
{
    private const string Scope = "{\"Owner\":\"selected-endpoint\",\"Identity\":\"native-account\"}";
    private const string Job = "{\"ProviderJobId\":\"actual-job\",\"Scope\":" + Scope + "}";
    private const string Resource = "{\"type\":\"resource\",\"Resource\":{\"WorkspaceId\":\"w\",\"Id\":\"original\"}}";
    private const string Output = "{\"Scope\":" + Scope + ",\"ProviderState\":{\"url\":\"https://example.invalid/?secret=exact\"},\"Duration\":\"00:00:00\",\"ContentType\":null,\"ExpiresAt\":null,\"FileName\":null,\"File\":{\"FileId\":\"native\"},\"Resource\":{\"Reference\":{\"WorkspaceId\":\"w\",\"Id\":\"result\"},\"SessionId\":\"s\",\"Bytes\":0}}";
    private const string Generation = "{\"ModelId\":\"manual-model\",\"Prompt\":\"unaltered prompt\",\"Duration\":\"00:00:03\",\"AspectRatio\":\"custom\",\"ResolutionHeight\":720,\"FirstFrame\":{\"type\":\"binary\",\"ContentType\":null,\"Data\":\"AAH/\"},\"LastFrame\":{\"type\":\"url\",\"Url\":\"https://example.invalid/end\"},\"Keyframes\":[{\"Image\":" + Resource + ",\"Timestamp\":\"00:00:00\"}],\"ReferenceImages\":[{\"type\":\"file\",\"File\":{\"FileId\":\"native\"}}],\"ReferenceAudio\":[{\"type\":\"voice\",\"VoiceId\":\"preset\"},{\"type\":\"audio\",\"Source\":" + Resource + "}],\"GenerateAudio\":false,\"ProviderOptions\":null}";

    public static TheoryData<string, string> Shapes => new()
    {
        { "VideoProviderOptions", "null" },
        { "VideoRequest", "{\"type\":\"generation\",\"ModelId\":\"m\"}" },
        { "VideoRequest", "{\"type\":\"edit\",\"ModelId\":\"m\",\"Prompt\":\"edit\",\"SourceVideo\":" + Resource + "}" },
        { "VideoRequest", "{\"type\":\"extension\",\"ModelId\":\"m\",\"SourceVideo\":" + Resource + ",\"ExtensionDuration\":\"00:00:02\"}" },
        { "VideoGenerationRequest", Generation },
        { "VideoGenerationRequest", "{\"ModelId\":\"m\",\"Keyframes\":[],\"ReferenceImages\":[],\"ReferenceAudio\":[],\"GenerateAudio\":null}" },
        { "VideoEditRequest", "{\"ModelId\":\"m\",\"Prompt\":\"\",\"SourceVideo\":" + Resource + "}" },
        { "VideoExtensionRequest", "{\"ModelId\":\"m\",\"SourceVideo\":" + Resource + ",\"Prompt\":null,\"ExtensionDuration\":null}" },
        { "VideoKeyframe", "{\"Image\":" + Resource + ",\"Timestamp\":\"00:00:00\"}" },
        { "VideoReferenceAudio", "{\"type\":\"voice\",\"VoiceId\":\"preset\"}" },
        { "VideoReferenceAudio", "{\"type\":\"audio\",\"Source\":" + Resource + "}" },
        { "VideoVoiceReference", "{\"VoiceId\":\"exact-preset\"}" },
        { "VideoAudioReference", "{\"Source\":" + Resource + "}" },
        { "VideoJobState", "0" }, { "VideoJobState", "1" }, { "VideoJobState", "2" }, { "VideoJobState", "3" },
        { "VideoOperation", "0" }, { "VideoOperation", "1" }, { "VideoOperation", "2" },
        { "VideoJob", Job }, { "VideoOutput", Output },
        { "VideoJobSnapshot", "{\"Job\":" + Job + ",\"State\":0,\"ProgressPercent\":0,\"Output\":null,\"Error\":null}" },
        { "VideoJobSnapshot", "{\"Job\":" + Job + ",\"State\":1,\"ProgressPercent\":null,\"Output\":" + Output + "}" },
        { "VideoJobSnapshot", "{\"Job\":" + Job + ",\"State\":3,\"Error\":{\"Code\":10,\"ProviderCode\":\"reported\",\"Message\":\"safe\"}}" },
        { "VideoCapabilities", "{}" },
        { "VideoCapabilities", "{\"Operations\":[0],\"SourceKinds\":[3],\"MinimumGenerationDuration\":\"00:00:00\",\"MaximumGenerationDuration\":\"00:00:04\",\"MinimumExtensionDuration\":null,\"MaximumExtensionDuration\":\"00:00:02\",\"ResolutionHeights\":[1080,720],\"MaximumKeyframes\":0,\"MaximumReferenceImages\":null,\"MaximumReferenceAudio\":1,\"SupportsProgress\":true,\"SupportsLastFrame\":true,\"SupportsAudioControl\":true,\"SupportsPresetVoiceReferences\":true,\"SupportsSuppliedAudioReferences\":false}" }
    };

    public static TheoryData<string, string, string> RequiredFacts => new()
    {
        { "VideoGenerationRequest", "{\"ModelId\":\"m\"}", "ModelId" },
        { "VideoEditRequest", "{\"ModelId\":\"m\",\"Prompt\":\"edit\",\"SourceVideo\":" + Resource + "}", "ModelId,Prompt,SourceVideo" },
        { "VideoExtensionRequest", "{\"ModelId\":\"m\",\"SourceVideo\":" + Resource + "}", "ModelId,SourceVideo" },
        { "VideoKeyframe", "{\"Image\":" + Resource + ",\"Timestamp\":\"00:00:00\"}", "Image,Timestamp" },
        { "VideoVoiceReference", "{\"VoiceId\":\"v\"}", "VoiceId" },
        { "VideoAudioReference", "{\"Source\":" + Resource + "}", "Source" },
        { "VideoJob", Job, "ProviderJobId,Scope" },
        { "VideoOutput", Output, "Scope,ProviderState" },
        { "VideoJobSnapshot", "{\"Job\":" + Job + ",\"State\":0}", "Job,State" }
    };

    [Theory, MemberData(nameof(Shapes))]
    public void ExactOwnedRootsPreserveActualValues(string name, string json) => RoundTrip(json, Metadata(typeof(InferenceJson), name));

    [Theory, MemberData(nameof(RequiredFacts))]
    public void MissingOrNullRequiredFactsRefuse(string name, string json, string properties)
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
    public void CommonRefusesProviderOptionsUnknownVariantsAndUndefinedEnums()
    {
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new VideoGenerationRequest { ModelId = "m", ProviderOptions = new UnadmittedOptions() }, InferenceJson.VideoGenerationRequest));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"ModelId\":\"m\",\"ProviderOptions\":{}}", InferenceJson.VideoGenerationRequest));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"other\"}", InferenceJson.VideoRequest));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"other\"}", InferenceJson.VideoReferenceAudio));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"ModelId\":\"m\",\"Unmapped\":true}", InferenceJson.VideoGenerationRequest));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((VideoJobState)99, InferenceJson.VideoJobState));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", InferenceJson.VideoOperation));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"Pending\"", InferenceJson.VideoJobState));
    }

    [Fact]
    public void NestedBinaryMediaRejectsNullBytesButPreservesActualEmptyBytes()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{\"ModelId\":\"m\",\"FirstFrame\":{\"type\":\"binary\",\"Data\":null}}", InferenceJson.VideoGenerationRequest));
        var request = JsonSerializer.Deserialize(
            "{\"ModelId\":\"m\",\"FirstFrame\":{\"type\":\"binary\",\"Data\":\"\"}}", InferenceJson.VideoGenerationRequest)!;
        Assert.True(Assert.IsType<BinaryMediaSource>(request.FirstFrame).Data.IsEmpty);
        Assert.Same(InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter,
            InferenceJson.BinaryMediaSource.Properties.Single(property => property.Name == "Data").CustomConverter);
        Assert.True(JsonSerializer.Deserialize("null", InferenceJson.VideoOutput.Options.GetTypeInfo<ReadOnlyMemory<byte>>()).IsEmpty);
    }

    [Fact]
    public void CollectionsAndOpaqueStateAreSnapshotsWithoutInventedObservations()
    {
        var source = new ResourceMediaSource(new("workspace", "original"));
        List<VideoKeyframe> frames = [new(source, TimeSpan.Zero)];
        var request = new VideoGenerationRequest { ModelId = "m", Keyframes = frames };
        frames.Clear(); Assert.Single(request.Keyframes!);
        var revised = request with { Keyframes = frames }; frames.Add(new(source, TimeSpan.Zero)); Assert.Empty(revised.Keyframes!);
        Assert.Throws<ArgumentException>(() => request with { ReferenceAudio = new VideoReferenceAudio[] { null! } });
        Assert.Throws<ArgumentException>(() => request with { ModelId = " " });
        HashSet<VideoOperation> operations = [VideoOperation.Generation];
        var capabilities = new VideoCapabilities { Operations = operations }; operations.Clear(); Assert.Single(capabilities.Operations);
        Assert.Empty(new VideoCapabilities().Operations); Assert.Null(new VideoCapabilities().MaximumReferenceImages);
        Assert.False(new VideoCapabilities().SupportsProgress);
        Assert.Throws<ArgumentOutOfRangeException>(() => capabilities with { Operations = new HashSet<VideoOperation> { (VideoOperation)99 } });
        var scope = new InferenceResourceScope("owner", "identity");
        VideoOutput output;
        using (JsonDocument state = JsonDocument.Parse("{\"token\":\"secret\"}")) output = new(scope, state.RootElement);
        VideoOutput replacement;
        using (JsonDocument state = JsonDocument.Parse("{\"replacement\":true}")) replacement = output with { ProviderState = state.RootElement };
        Assert.Equal("secret", output.ProviderState.GetProperty("token").GetString());
        Assert.True(replacement.ProviderState.GetProperty("replacement").GetBoolean());
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.Ordinal);
        Assert.Null(output.Duration); Assert.Null(output.ContentType); Assert.Null(output.ExpiresAt);
        Assert.Throws<ArgumentException>(() => output with { ProviderState = default });
        Assert.Throws<ArgumentNullException>(() => output with { Scope = null! });
        var snapshot = new VideoJobSnapshot(new("job", scope), VideoJobState.Pending, 0);
        Assert.Equal(0, snapshot.ProgressPercent); Assert.Null(snapshot.Output); Assert.Null(snapshot.Error);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot with { ProgressPercent = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot with { State = (VideoJobState)99 });
    }

    [Fact]
    public void VideoTaskAppendsExistingIdentitiesAndDescriptorUsesTheSameMapping()
    {
        Assert.Equal(6, (int)InferenceTask.VideoGeneration); Assert.Equal(4, (int)InferenceCapability.Video);
        Assert.Equal(Enumerable.Range(0, 8), Enum.GetValues<InferenceTask>().Select(value => (int)value));
        Assert.Equal(Enumerable.Range(0, 5), Enum.GetValues<InferenceCapability>().Select(value => (int)value));
        var descriptor = new InferenceImplementationDescriptor("video", "Video", InferenceImplementationKind.Connect, InferenceTask.VideoGeneration);
        Assert.Equal(InferenceCapability.Video, Assert.Single(descriptor.Tasks).GetCapability());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((InferenceTask)99).GetCapability());
    }

    [Fact]
    public void LiveVideoOwnerIsNotPortableAuthority()
    {
        var owner = new UncallableVideo();
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new VideoRuntimeEnvelope { Inference = owner }, RuntimeMetadata()));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Inference\":{}}", RuntimeMetadata()));
        Assert.Null(JsonSerializer.Deserialize("{\"Inference\":null}", RuntimeMetadata())!.Inference);
        Assert.Equal(0, owner.Calls);
        Assert.Null(typeof(InferenceJson).GetProperty(nameof(IVideoInference)));
    }

    public static JsonTypeInfo<VideoRuntimeEnvelope> RuntimeMetadata()
    {
        var options = new JsonSerializerOptions(InferenceJson.VideoOutput.Options) { TypeInfoResolver = VideoRuntimeJsonContext.Default };
        options.MakeReadOnly(); return options.GetTypeInfo<VideoRuntimeEnvelope>();
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
            JsonTypeInfo metadata = Metadata(common.GetType(typeof(InferenceJson).FullName!, true)!, name);
            Assert.Same(common, metadata.Type.Assembly); Assert.Same(common, metadata.Options.TypeInfoResolver!.GetType().Assembly);
            if (name == nameof(VideoGenerationRequest))
            {
                Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
                    "{\"ModelId\":\"m\",\"FirstFrame\":{\"type\":\"binary\",\"Data\":null}}", metadata));
                JsonTypeInfo binary = Metadata(common.GetType(typeof(InferenceJson).FullName!, true)!, nameof(BinaryMediaSource));
                Assert.Same(common, binary.Properties.Single(property => property.Name == "Data").CustomConverter!.GetType().Assembly);
            }
            object? result = RoundTrip(json, metadata);
            if (result is not null) AssertOwned(result, common);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(VideoContractTests).Assembly.Location);
            Assert.NotSame(typeof(VideoContractTests).Assembly, fixture);
            Assert.Equal(typeof(VideoContractTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            var runtime = (JsonTypeInfo)fixture.GetType(typeof(VideoContractTests).FullName!, true)!.GetMethod(nameof(RuntimeMetadata))!.Invoke(null, null)!;
            Assert.Same(fixture, runtime.Type.Assembly);
            Assert.Same(common, runtime.Options.GetConverter(common.GetType(typeof(IVideoInference).FullName!, true)!).GetType().Assembly);
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Inference\":{}}", runtime));
            Assert.NotNull(JsonSerializer.Deserialize("{\"Inference\":null}", runtime));
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
    private sealed record UnadmittedOptions : VideoProviderOptions;
    private sealed class UncallableVideo : IVideoInference
    {
        public int Calls { get; private set; }
        private InvalidOperationException Called() { Calls++; return new("Video owner was invoked."); }
        public Task<VideoCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) => throw Called();
        public Task<VideoJob> SubmitAsync(VideoRequest request, CancellationToken cancellationToken = default) => throw Called();
        public Task<VideoJobSnapshot> GetAsync(VideoJob job, CancellationToken cancellationToken = default) => throw Called();
        public Task<Stream> DownloadAsync(VideoOutput output, CancellationToken cancellationToken = default) => throw Called();
        public Task<InferenceResourceDescriptor> MaterializeAsync(VideoOutput output, CancellationToken cancellationToken = default) => throw Called();
    }
}

public sealed record VideoRuntimeEnvelope
{
    public IVideoInference? Inference { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(VideoRuntimeEnvelope))]
internal sealed partial class VideoRuntimeJsonContext : JsonSerializerContext;
