using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class FileResourceContractTests
{
    private const string FileJson = "{\"Id\":\"file\",\"FileName\":\"original.txt\",\"Bytes\":0,\"CreatedAt\":\"1970-01-01T00:00:00+00:00\",\"PublicUrl\":\"https://example.invalid/?token=secret\",\"PublicUrlExpiresAt\":null}";
    private const string SkillJson = "{\"Id\":\"skill\",\"CreatedAt\":\"1970-01-01T00:00:00+00:00\",\"Name\":\"n\",\"Description\":\"\",\"DefaultVersion\":\"v-next\",\"LatestVersion\":\"v-other\"}";

    public static TheoryData<string, string> Shapes => new()
    {
        { "LlmFileOrder", "1" }, { "LlmFileSort", "2" }, { "LlmFileContentFormat", "1" },
        { "LlmProviderFile", FileJson },
        { "LlmProviderFile", "{\"Id\":\"native-id\",\"FileName\":\"created.json\",\"Bytes\":23,\"CreatedAt\":\"1970-01-01T00:00:00+00:00\",\"Resource\":{\"WorkspaceId\":\"workspace\",\"Id\":\"logical-id\"}}" },
        { "LlmFileUploadOptions", "{\"ExpiresAfter\":\"01:00:00\"}" },
        { "LlmFileUploadOptions", "{\"ExpiresAfter\":null}" },
        { "LlmFileListRequest", "{\"Limit\":1,\"PaginationToken\":\"opaque+/=\",\"Order\":0,\"Sort\":1,\"Filter\":\"opaque provider expression\"}" },
        { "LlmFilePage", "{\"Items\":[" + FileJson + "],\"PaginationToken\":\"preserved-even-at-end\",\"IsKnownLastPage\":true}" },
        { "LlmFilePage", "{\"Items\":[],\"PaginationToken\":null,\"IsKnownLastPage\":false}" },
        { "LlmFileDeletion", "{\"Id\":\"f\",\"Deleted\":false}" },
        { "LlmFileDownloadOptions", "{\"Format\":null}" },
        { "ResourceDownloadReceipt", "{\"BytesWritten\":0,\"ResponseContentType\":null,\"DeclaredContentLength\":null}" },
        { "ResourceDownloadReceipt", "{\"BytesWritten\":7,\"ResponseContentType\":\"text/plain\",\"DeclaredContentLength\":15}" },
        { "XaiPublicUrlOptions", "{\"ExpiresAfter\":\"30.00:00:00\"}" },
        { "XaiPublicUrl", "{\"PublicUrl\":\"https://example.invalid/a%2fb?token=exact\",\"ExpiresAt\":null}" },
        { "XaiPublicUrlRevocation", "{\"Id\":\"f\",\"Revoked\":false,\"PublicUrl\":null}" },
        { "LlmFileCapabilities", "{}" }, { "XaiSkillCapabilities", "{}" },
        { "XaiSkill", SkillJson },
        { "XaiSkillDeletion", "{\"Id\":\"s\",\"Deleted\":false}" },
        { "XaiSkillListRequest", "{\"Limit\":100,\"After\":\"actual-last-id\",\"Order\":1}" },
        { "XaiSkillPage", "{\"Items\":[" + SkillJson + "],\"FirstId\":\"skill\",\"LastId\":\"skill\",\"HasMore\":true}" },
        { "XaiSkillPage", "{\"Items\":[],\"FirstId\":null,\"LastId\":\"\",\"HasMore\":false}" }
    };

    public static TheoryData<string, string, string> RequiredFacts => new()
    {
        { "LlmProviderFile", FileJson, "Id,FileName,Bytes,CreatedAt" },
        { "LlmFilePage", "{\"Items\":[],\"IsKnownLastPage\":false}", "Items,IsKnownLastPage" },
        { "LlmFileDeletion", "{\"Id\":\"f\",\"Deleted\":false}", "Id,Deleted" },
        { "ResourceDownloadReceipt", "{\"BytesWritten\":0}", "BytesWritten" },
        { "XaiPublicUrl", "{\"PublicUrl\":\"exact\"}", "PublicUrl" },
        { "XaiPublicUrlRevocation", "{\"Id\":\"f\",\"Revoked\":false}", "Id,Revoked" },
        { "XaiSkill", SkillJson, "Id,CreatedAt,Name,Description,DefaultVersion,LatestVersion" },
        { "XaiSkillDeletion", "{\"Id\":\"s\",\"Deleted\":false}", "Id,Deleted" },
        { "XaiSkillPage", "{\"Items\":[],\"HasMore\":false}", "Items,HasMore" }
    };

    [Theory, MemberData(nameof(Shapes))]
    public void ExactOwnedRootsPreserveActualFacts(string name, string json) => RoundTrip(json, Metadata(typeof(ConnectLlmJson), name));

    [Theory, MemberData(nameof(RequiredFacts))]
    public void RequiredObservedFactsRefuseMissingAndNull(string name, string json, string properties)
    {
        JsonTypeInfo metadata = Metadata(typeof(ConnectLlmJson), name);
        RoundTrip(json, metadata);
        foreach (string property in properties.Split(','))
        {
            JsonObject missing = JsonNode.Parse(json)!.AsObject(); missing.Remove(property);
            Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(missing.ToJsonString(), metadata)));
            JsonObject explicitNull = JsonNode.Parse(json)!.AsObject(); explicitNull[property] = null;
            Assert.NotNull(Record.Exception(() => JsonSerializer.Deserialize(explicitNull.ToJsonString(), metadata)));
        }
    }

    [Theory]
    [InlineData("LlmFileOrder")]
    [InlineData("LlmFileSort")]
    [InlineData("LlmFileContentFormat")]
    public void NewEnumsRejectUndefinedReadAndWrite(string name)
    {
        JsonTypeInfo metadata = Metadata(typeof(ConnectLlmJson), name);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("99", metadata));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"Ascending\"", metadata));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Enum.ToObject(metadata.Type, 99), metadata));
    }

    [Fact]
    public void LimitsAndWholeSecondExpiryDoNotRoundOrInventDefaults()
    {
        Assert.Equal(100, new LlmFileListRequest().Limit); Assert.Equal(100, new XaiSkillListRequest().Limit);
        Assert.Null(new LlmFileListRequest().Order); Assert.Null(new LlmFileListRequest().Sort);
        Assert.Null(new LlmFileUploadOptions().ExpiresAfter); Assert.Null(new XaiPublicUrlOptions().ExpiresAfter);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmFileListRequest { Limit = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new XaiSkillListRequest { Limit = 101 });
        foreach (TimeSpan invalid in new[] { TimeSpan.Zero, TimeSpan.FromHours(1) - TimeSpan.FromTicks(1), TimeSpan.FromHours(1) + TimeSpan.FromTicks(1), TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1) })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LlmFileUploadOptions(invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => new XaiPublicUrlOptions() with { ExpiresAfter = invalid });
        }
        Assert.Equal(TimeSpan.FromDays(30), new LlmFileUploadOptions(TimeSpan.FromDays(30)).ExpiresAfter);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceDownloadReceipt(-1, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceDownloadReceipt(0, null, null) with { DeclaredContentLength = -1 });
    }

    [Fact]
    public void OrderedPageAndUploadCollectionsCopyWithoutInvokingBorrowedStreams()
    {
        var file = new LlmProviderFile("id", "private/path", 0, DateTimeOffset.UnixEpoch, null) { PublicUrl = "secret-url" };
        List<LlmProviderFile> files = [file]; var page = new LlmFilePage(files, "token", false); files.Clear();
        Assert.Same(file, Assert.Single(page.Items)); Assert.Equal("token", page.PaginationToken);
        var changed = page with { Items = files }; files.Add(file); Assert.Empty(changed.Items);
        Assert.Throws<ArgumentNullException>(() => file with { FileName = null! });
        Assert.Throws<ArgumentOutOfRangeException>(() => file with { Bytes = -1 });
        Assert.Throws<ArgumentException>(() => page with { Items = new LlmProviderFile[] { null! } });
        using var stream = new UncallableStream();
        var entry = new XaiSkillDirectoryFile("raw/path.txt", stream);
        List<XaiSkillDirectoryFile> entries = [entry]; var upload = new XaiSkillDirectoryUpload(entries); entries.Clear();
        Assert.Same(entry, Assert.Single(upload.Files)); Assert.Same(stream, entry.Content);
        var rewritten = upload with { Files = entries }; entries.Add(entry); Assert.Empty(rewritten.Files);
        Assert.Equal("XaiSkillDirectoryUpload { <borrowed> }", upload.ToString());
        Assert.Equal("XaiSkillDirectoryFile { <borrowed> }", entry.ToString());
        Assert.Equal("LlmProviderFile { <metadata> }", file.ToString());
        Assert.DoesNotContain("secret", new XaiPublicUrl("secret", null).ToString(), StringComparison.Ordinal);
        Assert.Equal(0, stream.Calls);
    }

    [Fact]
    public void RuntimeInputSerializationRefusesBeforeStreamInspection()
    {
        using var stream = new UncallableStream();
        var entry = new XaiSkillDirectoryFile("private/path", stream);
        object[] inputs = [new XaiSkillZipUpload("private.zip", stream), entry, new XaiSkillDirectoryUpload([entry])];
        JsonSerializerOptions options = RuntimeMetadata().Options;
        foreach (object input in inputs)
        {
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(input, input.GetType(), options));
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{}", input.GetType(), options));
        }
        var envelope = new FileRuntimeEnvelope { Upload = (XaiSkillUpload)inputs[0] };
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(envelope, RuntimeMetadata()));
        foreach (string field in new[] { "Upload", "Zip", "DirectoryFile", "Directory", "Files", "PublicUrls", "Skills" })
        {
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"" + field + "\":{}}", RuntimeMetadata()));
            Assert.NotNull(JsonSerializer.Deserialize("{\"" + field + "\":null}", RuntimeMetadata()));
        }
        Assert.Equal(0, stream.Calls);
        Assert.Null(typeof(ConnectLlmJson).GetProperty(nameof(XaiSkillUpload)));
    }

    public static JsonTypeInfo<FileRuntimeEnvelope> RuntimeMetadata()
    {
        var options = new JsonSerializerOptions(ConnectLlmJson.LlmProviderFile.Options) { TypeInfoResolver = FileRuntimeJsonContext.Default };
        options.MakeReadOnly();
        return options.GetTypeInfo<FileRuntimeEnvelope>();
    }

    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignConnectCommonAndCallerMetadataRetires(string name, string json)
    {
        WeakReference weak = ExerciseForeign(name, json);
        for (int i = 0; weak.IsAlive && i < 20; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(string name, string json)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(ConnectLlmJson).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            var originals = new[] { typeof(ConnectLlmJson).Assembly, typeof(InferenceJson).Assembly, typeof(FileResourceContractTests).Assembly };
            Assembly[] loaded = originals.Select(original => generation.LoadFromAssemblyPath(original.Location)).ToArray();
            for (int i = 0; i < loaded.Length; i++)
            { Assert.NotSame(originals[i], loaded[i]); Assert.Equal(originals[i].ManifestModule.ModuleVersionId, loaded[i].ManifestModule.ModuleVersionId); Assert.Same(generation, AssemblyLoadContext.GetLoadContext(loaded[i])); }
            JsonTypeInfo metadata = Metadata(loaded[0].GetType(typeof(ConnectLlmJson).FullName!, true)!, name);
            Assert.Same(loaded[0], metadata.Type.Assembly);
            object result = RoundTrip(json, metadata);
            Assert.Same(loaded[0], result.GetType().Assembly);
            if (name == nameof(LlmProviderFile) && result.GetType().GetProperty("Resource")!.GetValue(result) is { } resource)
            {
                Assert.Same(loaded[1], resource.GetType().Assembly);
                Assert.Same(loaded[1], metadata.Options.GetTypeInfo(resource.GetType()).Type.Assembly);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(resource.GetType().Assembly));
            }
            Type fixture = loaded[2].GetType(typeof(FileResourceContractTests).FullName!, true)!;
            var runtime = (JsonTypeInfo)fixture.GetMethod(nameof(RuntimeMetadata))!.Invoke(null, null)!;
            Assert.Same(loaded[2], runtime.Type.Assembly);
            Assert.Same(loaded[0], runtime.Options.GetConverter(loaded[0].GetType(typeof(XaiSkillUpload).FullName!, true)!).GetType().Assembly);
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Upload\":{}}", runtime));
            Assert.NotNull(JsonSerializer.Deserialize("{\"Upload\":null}", runtime));
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static JsonTypeInfo Metadata(Type owner, string name) => (JsonTypeInfo)owner.GetProperty(name)!.GetValue(null)!;
    private static object RoundTrip(string json, JsonTypeInfo metadata)
    {
        object value = JsonSerializer.Deserialize(json, metadata)!;
        string written = JsonSerializer.Serialize(value, metadata);
        using JsonDocument supplied = JsonDocument.Parse(json); using JsonDocument actual = JsonDocument.Parse(written);
        Preserve(supplied.RootElement, actual.RootElement);
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
    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll"); return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
    private sealed class UncallableStream : Stream
    {
        public int Calls { get; private set; }
        private InvalidOperationException Called() { Calls++; return new("Stream was inspected."); }
        public override string ToString() => throw Called();
        public override bool CanRead => throw Called(); public override bool CanSeek => throw Called(); public override bool CanWrite => throw Called();
        public override long Length => throw Called(); public override long Position { get => throw Called(); set => throw Called(); }
        public override void Flush() => throw Called(); public override int Read(byte[] buffer, int offset, int count) => throw Called();
        public override long Seek(long offset, SeekOrigin origin) => throw Called(); public override void SetLength(long value) => throw Called();
        public override void Write(byte[] buffer, int offset, int count) => throw Called();
    }
}

public sealed record FileRuntimeEnvelope
{
    public XaiSkillUpload? Upload { get; init; }
    public XaiSkillZipUpload? Zip { get; init; }
    public XaiSkillDirectoryFile? DirectoryFile { get; init; }
    public XaiSkillDirectoryUpload? Directory { get; init; }
    public ILlmFileClient? Files { get; init; }
    public IXaiFilePublicUrlClient? PublicUrls { get; init; }
    public IXaiSkillClient? Skills { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(FileRuntimeEnvelope))]
internal sealed partial class FileRuntimeJsonContext : JsonSerializerContext;
