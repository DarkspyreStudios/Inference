using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.Tests;

public sealed class MediaJsonTests
{
    private const string Reference = "{\"WorkspaceId\":\"workspace\",\"Id\":\"content\"}";
    private const string Binary = "{\"ContentType\":null,\"Data\":\"AAH/\",\"FileName\":null}";
    private const string Url = "{\"Url\":\"https://example.invalid/media\"}";
    private const string File = "{\"File\":{\"FileId\":\"native\"}}";
    private const string Resource = "{\"Resource\":" + Reference + "}";

    public static TheoryData<string, string> DataRoots => new()
    {
        { "InferenceMediaSourceKind", "0" },
        { "InferenceMediaSourceKind", "1" },
        { "InferenceMediaSourceKind", "2" },
        { "InferenceMediaSourceKind", "3" },
        { "InferenceMediaSource", "{\"type\":\"binary\",\"ContentType\":null,\"Data\":\"AAH/\",\"FileName\":null}" },
        { "InferenceMediaSource", "{\"type\":\"url\",\"Url\":\"https://example.invalid/media\"}" },
        { "InferenceMediaSource", "{\"type\":\"file\",\"File\":{\"FileId\":\"native\"}}" },
        { "InferenceMediaSource", "{\"type\":\"resource\",\"Resource\":" + Reference + "}" },
        { "BinaryMediaSource", Binary },
        { "BinaryMediaSource", "{\"ContentType\":\"image/png\",\"Data\":\"\",\"FileName\":\"input.png\"}" },
        { "UrlMediaSource", Url },
        { "ProviderFileReference", "{\"FileId\":\"native\"}" },
        { "FileMediaSource", File },
        { "ResourceMediaSource", Resource },
        { "InferenceResourceScope", "{\"Owner\":\"endpoint/route\",\"Identity\":\"account\"}" },
        { "InferenceResourceFailure", "{\"Code\":0,\"ProviderCode\":null,\"Message\":\"failure\",\"Resource\":null}" },
        { "InferenceResourceFailure", "{\"Code\":0,\"ProviderCode\":\"native_code\",\"Message\":\"failure\",\"Resource\":{\"Phase\":4,\"Reference\":" + Reference + ",\"CommitOutcome\":3,\"BytesTransferred\":0,\"SelectionOutcome\":3,\"SelectionVersion\":{\"WorkspaceId\":\"workspace\",\"SessionId\":\"session\",\"Revision\":0}}}" }
    };

    [Theory]
    [MemberData(nameof(DataRoots))]
    public void ExactPublicMediaMetadataOwnsCompleteRoundTrip(string name, string json)
    {
        JsonTypeInfo metadata = PublicMetadata(typeof(InferenceJson), name);
        Assert.Equal("Darkspyre.Inference.Common.Media." + name, metadata.Type.FullName);
        Assert.Same(typeof(InferenceJson).Assembly, metadata.Type.Assembly);
        object value = JsonSerializer.Deserialize(json, metadata)!;
        Assert.Equal(json, JsonSerializer.Serialize(value, metadata));
    }

    [Fact]
    public void BinaryConstructionBorrowsMemoryWhileJsonDecodingOwnsItsSnapshot()
    {
        byte[] input = [0, 1, 255];
        var source = new BinaryMediaSource(null, input);
        Assert.True(MemoryMarshal.TryGetArray(source.Data, out ArraySegment<byte> borrowed));
        Assert.Same(input, borrowed.Array);
        string json = JsonSerializer.Serialize(source, InferenceJson.BinaryMediaSource);
        Assert.Equal(Binary, json);
        Assert.Equal(new byte[] { 0, 1, 255 }, input);
        var decoded = JsonSerializer.Deserialize(json, InferenceJson.BinaryMediaSource)!;
        Assert.True(MemoryMarshal.TryGetArray(decoded.Data, out ArraySegment<byte> owned));
        Assert.NotSame(input, owned.Array);
        input[0] = 42;
        Assert.Equal(42, source.Data.Span[0]);
        Assert.Equal(0, decoded.Data.Span[0]);
    }

    [Fact]
    public void SourceConstructionAndMetadataHaveNoResolutionOrAuthorityEffects()
    {
        InferenceMediaSource[] sources =
        [new UrlMediaSource(new Uri("https://example.invalid/media")),
            new FileMediaSource(new("caller-native")),
            new ResourceMediaSource(new("unopened-workspace", "unread-content"))];
        foreach (InferenceMediaSource source in sources)
        {
            string json = JsonSerializer.Serialize(source, InferenceJson.InferenceMediaSource);
            Assert.DoesNotContain("Data", json, StringComparison.Ordinal);
            Assert.Equal(source, JsonSerializer.Deserialize(json, InferenceJson.InferenceMediaSource));
        }
        Assert.DoesNotContain(InferenceJson.InferenceMediaSource.Options.Converters,
            converter => converter.CanConvert(typeof(Stream)));
    }

    [Theory]
    [InlineData("{\"type\":\"custom\"}")]
    [InlineData("{\"type\":\"binary\",\"Data\":\"\",\"LegacyUrl\":\"hidden\"}")]
    [InlineData("{\"type\":\"url\"}")]
    [InlineData("{\"type\":\"file\"}")]
    [InlineData("{\"type\":\"resource\"}")]
    public void SourceCodecRefusesUnknownVariantsMembersAndMissingFacts(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, InferenceJson.InferenceMediaSource));

    [Theory]
    [InlineData("BinaryMediaSource", "{}")]
    [InlineData("ProviderFileReference", "{}")]
    [InlineData("InferenceResourceScope", "{\"Owner\":\"endpoint\"}")]
    [InlineData("InferenceResourceFailure", "{\"Message\":\"missing code\"}")]
    public void DataRootsRefuseMissingRequiredFacts(string name, string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, PublicMetadata(typeof(InferenceJson), name)));

    [Fact]
    public void ExternalSubtypeDoesNotExtendTheOwnedSourceUnion()
        => Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize<InferenceMediaSource>(
            new ExternalMediaSource(), InferenceJson.InferenceMediaSource));

    [Theory]
    [MemberData(nameof(DataRoots))]
    public void ActualForeignMediaMetadataAndNestedValuesRetire(string name, string json)
    {
        WeakReference weak = ExerciseForeignGeneration(name, json);
        for (int attempt = 0; weak.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(weak.IsAlive, "The actual foreign Common media generation remains rooted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeignGeneration(string name, string json)
    {
        var generation = new AssemblyLoadContext("Common media metadata", isCollectible: true);
        var weak = new WeakReference(generation);
        try
        {
            Assembly assembly = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assert.NotSame(typeof(InferenceJson).Assembly, assembly);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
            JsonTypeInfo metadata = PublicMetadata(assembly.GetType(typeof(InferenceJson).FullName!, true)!, name);
            Assert.Same(assembly, metadata.Type.Assembly);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
            Assert.Same(assembly, metadata.Options.TypeInfoResolver!.GetType().Assembly);
            object value = JsonSerializer.Deserialize(json, metadata)!;
            Assert.Same(assembly, value.GetType().Assembly);
            AssertForeignNestedValues(value, assembly);
            Assert.Equal(json, JsonSerializer.Serialize(value, metadata));
        }
        finally
        {
            generation.Unload();
        }
        return weak;
    }

    private static void AssertForeignNestedValues(object value, Assembly owner)
    {
        foreach (string propertyName in new[] { "File", "Resource", "Reference", "SelectionVersion" })
        {
            object? nested = value.GetType().GetProperty(propertyName)?.GetValue(value);
            if (nested is null)
                continue;
            Assert.Same(owner, nested.GetType().Assembly);
            AssertForeignNestedValues(nested, owner);
        }
    }

    private static JsonTypeInfo PublicMetadata(Type owner, string name)
        => Assert.IsAssignableFrom<JsonTypeInfo>(owner.GetProperty(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null));

    private sealed record ExternalMediaSource : InferenceMediaSource;
}
