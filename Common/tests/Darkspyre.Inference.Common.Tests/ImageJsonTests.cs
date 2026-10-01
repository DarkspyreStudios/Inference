using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;

namespace Darkspyre.Inference.Common.Tests;

public sealed class ImageJsonTests
{
    public static TheoryData<string, string> Roots => new()
    {
        { "ImageSize", "{\"Width\":256,\"Height\":512}" },
        { "ImageCapabilities", "{\"SupportedSizes\":[{\"Width\":256,\"Height\":512}],\"SupportedReferenceKinds\":[0,3],\"MaximumReferences\":1,\"SupportsSteps\":true,\"MaximumImageCount\":4}" },
        { "GeneratedImage", "{\"ContentType\":\"image/png\",\"Data\":\"AQI=\",\"File\":{\"FileId\":\"native\"},\"Resource\":{\"Reference\":{\"WorkspaceId\":\"workspace\",\"Id\":\"output\"},\"Bytes\":2},\"Width\":256,\"Height\":null}" },
        { "GeneratedImage", "{\"ContentType\":null,\"Data\":\"\"}" },
        { "ImageGenerationStage", "4" },
        { "ImageRequest", "{\"ModelId\":\"model\",\"Prompt\":\"draw\",\"Count\":2,\"References\":[{\"type\":\"binary\",\"Data\":\"AQI=\"},{\"type\":\"resource\",\"Resource\":{\"WorkspaceId\":\"workspace\",\"Id\":\"original\"}},{\"type\":\"file\",\"File\":{\"FileId\":\"native\"}},{\"type\":\"url\",\"Url\":\"https://example.invalid/image\"}]}" },
        { "ImageResult", "{\"Images\":[{\"ContentType\":\"image/png\",\"Data\":\"AQI=\",\"File\":{\"FileId\":\"native\"}}],\"Provenance\":{\"ImplementationId\":\"fixture\",\"ModelId\":\"model\",\"Backend\":\"controlled\"}}" },
        { "ImageProgress", "{\"Stage\":2,\"Value\":0,\"Maximum\":null}" }
    };

    [Theory]
    [MemberData(nameof(Roots))]
    public void CompleteImageMetadataPreservesAllPresentFacts(string name, string json)
        => Exercise(typeof(InferenceJson), typeof(InferenceJson).Assembly, name, json);

    [Fact]
    public void ImageDefaultsRetainEmptyReferencesAndUnknownLimits()
    {
        var request = JsonSerializer.Deserialize("{\"ModelId\":\"model\",\"Prompt\":\"draw\"}", InferenceJson.ImageRequest)!;
        Assert.Equal(1, request.Count);
        Assert.Empty(request.References);
        var capabilities = new ImageCapabilities { SupportedSizes = [new(256, 256)] };
        Assert.Empty(capabilities.SupportedReferenceKinds);
        Assert.Null(capabilities.MaximumReferences);
        var decoded = JsonSerializer.Deserialize(JsonSerializer.Serialize(capabilities, InferenceJson.ImageCapabilities),
            InferenceJson.ImageCapabilities)!;
        Assert.Equal(capabilities.SupportedSizes, decoded.SupportedSizes);
        Assert.Null(decoded.MaximumReferences);
    }

    [Fact]
    public void UnknownMimeRemainsNullButItsRequiredFactCannotDisappear()
    {
        var image = JsonSerializer.Deserialize("{\"ContentType\":null,\"Data\":\"\"}", InferenceJson.GeneratedImage)!;
        Assert.Null(image.ContentType);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Data\":\"\"}", InferenceJson.GeneratedImage));
    }

    [Fact]
    public void SuppliedBinaryRequestStillBorrowsTheCallerMemory()
    {
        byte[] bytes = [1, 2];
        var source = new BinaryMediaSource(null, bytes);
        var request = new ImageGenerationRequest { ModelId = "model", Prompt = "draw", References = [source] };
        Assert.Same(source, request.References[0]);
        Assert.Equal(bytes, source.Data.ToArray());
        JsonSerializer.Serialize(request, InferenceJson.ImageRequest);
        Assert.Equal(new byte[] { 1, 2 }, bytes);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void ActualForeignImageAndNestedMetadataRetire(string name, string json)
    {
        WeakReference weak = ExerciseForeign(name, json);
        for (int attempt = 0; weak.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(weak.IsAlive, "The actual foreign Common image generation remains rooted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign(string name, string json)
    {
        var generation = new AssemblyLoadContext("Common image metadata", true);
        var weak = new WeakReference(generation);
        try
        {
            Assembly assembly = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assert.NotSame(typeof(InferenceJson).Assembly, assembly);
            Assert.Equal(typeof(InferenceJson).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
            Exercise(assembly.GetType(typeof(InferenceJson).FullName!, true)!, assembly, name, json);
        }
        finally
        {
            generation.Unload();
        }
        return weak;
    }

    private static void Exercise(Type facade, Assembly owner, string name, string json)
    {
        var metadata = (JsonTypeInfo)facade.GetProperty(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        Assert.Same(owner, metadata.Type.Assembly);
        Assert.Same(owner, metadata.Options.TypeInfoResolver!.GetType().Assembly);
        object value = JsonSerializer.Deserialize(json, metadata)!;
        AssertOwnedGraph(value, owner);
        using JsonDocument expected = JsonDocument.Parse(json);
        using JsonDocument actual = JsonDocument.Parse(JsonSerializer.Serialize(value, metadata));
        AssertPresentFacts(expected.RootElement, actual.RootElement);
    }

    private static void AssertOwnedGraph(object value, Assembly owner)
    {
        Assert.Same(owner, value.GetType().Assembly);
        foreach (string name in new[] { "File", "Resource", "Reference", "Provenance" })
        {
            object? nested = value.GetType().GetProperty(name)?.GetValue(value);
            if (nested is not null)
                AssertOwnedGraph(nested, owner);
        }
        foreach (string name in new[] { "References", "Images", "SupportedSizes", "SupportedReferenceKinds" })
        {
            if (value.GetType().GetProperty(name)?.GetValue(value) is System.Collections.IEnumerable items)
                foreach (object item in items)
                    AssertOwnedGraph(item, owner);
        }
    }

    private static void AssertPresentFacts(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in expected.EnumerateObject())
                AssertPresentFacts(property.Value, actual.GetProperty(property.Name));
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
            for (int i = 0; i < expected.GetArrayLength(); i++)
                AssertPresentFacts(expected[i], actual[i]);
        }
        else
            Assert.True(JsonElement.DeepEquals(expected, actual));
    }
}
