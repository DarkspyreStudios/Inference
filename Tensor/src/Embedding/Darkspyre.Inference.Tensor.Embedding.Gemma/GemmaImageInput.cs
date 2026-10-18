namespace Darkspyre.Inference.Tensor.Embedding.Gemma;

/// <summary>Packs shared Gemma preprocessing output into the pinned ONNX vision input layout.</summary>
internal sealed record GemmaImageInput(float[] Patches, long[] Positions, int Width, int Height)
{
    internal const int MaxPatches = 2520;
    internal const int PatchPixels = 768;

    internal static GemmaImageInput Prepare(byte[] bytes)
    {
        var (pixels, width, height) = GemmaImagePreprocessor.Process(bytes);
        int ph = height / 16, pw = width / 16, plane = width * height;
        float[] patches = new float[MaxPatches * PatchPixels];
        long[] positions = Enumerable.Repeat(-1L, MaxPatches * 2).ToArray();
        int index = 0;
        for (int row = 0; row < ph; row++)
            for (int col = 0; col < pw; col++)
            {
                positions[(row * pw + col) * 2] = col;
                positions[(row * pw + col) * 2 + 1] = row;
                for (int dy = 0; dy < 16; dy++)
                    for (int dx = 0; dx < 16; dx++)
                        for (int c = 0; c < 3; c++)
                            patches[index++] = pixels[c * plane + (row * 16 + dy) * width + col * 16 + dx];
            }
        return new(patches, positions, width, height);
    }
}
