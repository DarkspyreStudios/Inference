using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Darkspyre.Inference.Tensor.Embedding.Gemma;

namespace Darkspyre.Inference.Tensor.Embedding.Tests;

public sealed class GemmaImagePreprocessorTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "GemmaImages");

    // expected.json holds the canvas and the SHA-256 of the float32 pixels the fork produced for each image.
    [Theory]
    [InlineData("rgb.png")]
    [InlineData("rgba.png")]
    [InlineData("grey.png")]
    [InlineData("photo.jpg")]
    public void ProducesTheForkPixelsForEachImage(string name)
    {
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "expected.json")));
        JsonElement image = expected.RootElement.GetProperty("images").GetProperty(name);

        (float[] pixels, int width, int height) = GemmaImagePreprocessor.Process(File.ReadAllBytes(Path.Combine(Folder, name)));

        Assert.Equal((image.GetProperty("width").GetInt32(), image.GetProperty("height").GetInt32(), image.GetProperty("length").GetInt32()),
            (width, height, pixels.Length));
        Assert.True(BitConverter.IsLittleEndian);
        Assert.Equal(image.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan()))));
    }

    [Fact]
    public void RefusesBytesThatAreNotPngOrJpeg() =>
        Assert.Throws<InvalidDataException>(() => GemmaImagePreprocessor.Process("GIF89a"u8.ToArray()));
}
