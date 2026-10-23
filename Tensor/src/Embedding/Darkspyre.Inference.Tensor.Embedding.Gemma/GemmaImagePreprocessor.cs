using StbImageSharp;

namespace Darkspyre.Inference.Tensor.Embedding.Gemma;

/// <summary>
/// EmbeddingGemma's image preprocessing: decode PNG or JPEG bytes to RGBA8 with StbImageSharp, pick the reference
/// canvas for 16-pixel patches, 3x3 pooling and a 280 soft-token budget, composite over white, and resize with
/// Pillow's RGB bicubic filter into channel-first floats scaled to [0, 1]. Ported from the fork's
/// Gemma4ImageProcessor (reference sizing) and ImageProcessorUtils (commits 64532dc6, 59f8dc58).
/// </summary>
internal static class GemmaImagePreprocessor
{
    private const int PatchSize = 16;
    private const int Pooling = 3;
    private const int SoftTokens = 280;
    private const double BicubicSupport = 2.0;
    // Pillow RGB uses 22 fractional bits, leaving room for byte values and cubic overshoot.
    private const int BicubicPrecisionBits = 22;

    /// <summary>Throws <see cref="InvalidDataException"/> when the bytes are not a decodable PNG or JPEG.</summary>
    internal static (float[] Pixels, int Width, int Height) Process(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!IsPng(image) && !IsJpeg(image))
            throw new InvalidDataException("EmbeddingGemma reads PNG and JPEG images only.");
        ImageResult decoded;
        try { decoded = ImageResult.FromMemory(image, ColorComponents.RedGreenBlueAlpha); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { throw new InvalidDataException($"The image could not be decoded: {error.Message}", error); }
        CanvasSize(decoded.Width, decoded.Height, out int width, out int height);
        return (Resize(decoded.Data, decoded.Width, decoded.Height, width, height), width, height);
    }

    private static bool IsPng(byte[] bytes) =>
        bytes is [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..];

    private static bool IsJpeg(byte[] bytes) => bytes is [0xFF, 0xD8, ..];

    // The largest canvas within the soft-token budget with both sides a multiple of patch*pooling, each side floored
    // independently; the reference processor's get_aspect_ratio_preserving_size.
    private static void CanvasSize(int width, int height, out int targetW, out int targetH)
    {
        const int sideMult = PatchSize * Pooling;
        if (width <= 0 || height <= 0)
        {
            targetW = sideMult;
            targetH = sideMult;
            return;
        }

        long maxPatches = (long)SoftTokens * Pooling * Pooling;
        double targetPx = (double)maxPatches * PatchSize * PatchSize;
        double factor = Math.Sqrt(targetPx / ((double)width * height));

        targetH = (int)Math.Floor(factor * height / sideMult) * sideMult;
        targetW = (int)Math.Floor(factor * width / sideMult) * sideMult;

        // Either side can floor to zero for an extreme aspect ratio; the reference clamps the degenerate side to one
        // unit and caps the other so the patch budget still holds.
        int maxSide = (int)(maxPatches / (Pooling * Pooling)) * sideMult;
        if (targetH == 0 && targetW == 0)
        {
            targetH = sideMult;
            targetW = sideMult;
        }
        else if (targetH == 0)
        {
            targetH = sideMult;
            targetW = Math.Min(Math.Max(1, (int)Math.Floor((double)width / height)) * sideMult, maxSide);
        }
        else if (targetW == 0)
        {
            targetW = sideMult;
            targetH = Math.Min(Math.Max(1, (int)Math.Floor((double)height / width)) * sideMult, maxSide);
        }
    }

    private static byte[] CompositeOverWhite(byte[] rgba, int width, int height)
    {
        byte[] result = new byte[width * height * 4];
        Parallel.For(0, height, y =>
        {
            int srcRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int pixBase = srcRow + x * 4;
                int a = rgba[pixBase + 3];
                if (a == 255)
                {
                    result[pixBase] = rgba[pixBase];
                    result[pixBase + 1] = rgba[pixBase + 1];
                    result[pixBase + 2] = rgba[pixBase + 2];
                }
                else
                {
                    float alpha = a / 255f;
                    result[pixBase] = (byte)(rgba[pixBase] * alpha + 255 * (1 - alpha));
                    result[pixBase + 1] = (byte)(rgba[pixBase + 1] * alpha + 255 * (1 - alpha));
                    result[pixBase + 2] = (byte)(rgba[pixBase + 2] * alpha + 255 * (1 - alpha));
                }

                result[pixBase + 3] = 255;
            }
        });
        return result;
    }

    // Pillow's bicubic kernel (a = -0.5).
    private static double CubicFilter(double x)
    {
        const double a = -0.5;
        x = Math.Abs(x);
        if (x < 1.0)
            return ((a + 2.0) * x - (a + 3.0)) * x * x + 1.0;
        if (x < 2.0)
            return (((x - 5.0) * x + 8.0) * x - 4.0) * a;
        return 0.0;
    }

    // Each output pixel's source window and Pillow RGB fixed-point weights; the support widens by 1/scale when
    // downscaling, so every source pixel contributes.
    private static void BuildCubicWeights(int srcSize, int dstSize, out int[] bounds, out int[] weights, out int windowSize)
    {
        double scale = (double)srcSize / dstSize;
        double filterScale = Math.Max(1.0, scale);
        double support = BicubicSupport * filterScale;
        windowSize = (int)Math.Ceiling(support) * 2 + 1;

        bounds = new int[dstSize * 2];
        weights = new int[(long)dstSize * windowSize <= int.MaxValue ? dstSize * windowSize : 0];
        if (weights.Length == 0)
            throw new InvalidDataException($"The resize window is too large for {srcSize} to {dstSize} pixels.");
        var kernel = new double[windowSize];
        double inverseFilterScale = 1.0 / filterScale;

        for (int i = 0; i < dstSize; i++)
        {
            double center = (i + 0.5) * scale;
            int xmin = Math.Max(0, (int)(center - support + 0.5));
            int xmax = Math.Min(srcSize, (int)(center + support + 0.5));
            int count = xmax - xmin;

            double sum = 0;
            int wBase = i * windowSize;
            for (int k = 0; k < count; k++)
            {
                double w = CubicFilter((xmin + k - center + 0.5) * inverseFilterScale);
                kernel[k] = w;
                sum += w;
            }
            for (int k = 0; k < count; k++)
            {
                double normalized = sum != 0 ? kernel[k] / sum : kernel[k];
                weights[wBase + k] = (int)Math.Round(normalized * (1 << BicubicPrecisionBits), MidpointRounding.AwayFromZero);
            }

            bounds[i * 2] = xmin;
            bounds[i * 2 + 1] = count;
        }
    }

    // Antialiased bicubic resize composited over white into channel-first [C, H, W] scaled to [0, 1]. Both passes round
    // and clip to bytes, matching Pillow RGB (Pillow 12.1.1 src/libImaging/Resample.c).
    private static float[] Resize(byte[] rgba, int srcW, int srcH, int dstW, int dstH)
    {
        BuildCubicWeights(srcW, dstW, out int[] xBounds, out int[] xWeights, out int xWindow);
        BuildCubicWeights(srcH, dstH, out int[] yBounds, out int[] yWeights, out int yWindow);
        byte[] composited = CompositeOverWhite(rgba, srcW, srcH);

        // The byte intermediate clips overshoot before the vertical filter sees it.
        var horizontal = new byte[3 * srcH * dstW];
        Parallel.For(0, srcH, sy =>
        {
            long rowBase = (long)sy * srcW;
            for (int dx = 0; dx < dstW; dx++)
            {
                int xmin = xBounds[dx * 2], count = xBounds[dx * 2 + 1];
                int wBase = dx * xWindow;
                int r = 1 << (BicubicPrecisionBits - 1);
                int g = r, b = r;
                for (int k = 0; k < count; k++)
                {
                    int w = xWeights[wBase + k];
                    int pixelBase = (int)((rowBase + xmin + k) * 4);
                    r += w * composited[pixelBase];
                    g += w * composited[pixelBase + 1];
                    b += w * composited[pixelBase + 2];
                }
                int idx = sy * dstW + dx;
                horizontal[idx] = Clip(r);
                horizontal[srcH * dstW + idx] = Clip(g);
                horizontal[2 * srcH * dstW + idx] = Clip(b);
            }
        });

        int outPixels = dstW * dstH;
        var result = new float[3 * outPixels];
        Parallel.For(0, dstH, dy =>
        {
            int ymin = yBounds[dy * 2], count = yBounds[dy * 2 + 1];
            int wBase = dy * yWindow;
            for (int dx = 0; dx < dstW; dx++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int plane = c * srcH * dstW;
                    int acc = 1 << (BicubicPrecisionBits - 1);
                    for (int k = 0; k < count; k++)
                        acc += yWeights[wBase + k] * horizontal[plane + (ymin + k) * dstW + dx];
                    result[c * outPixels + dy * dstW + dx] = Clip(acc) / 255f;
                }
            }
        });

        return result;
    }

    private static byte Clip(int accumulated) => (byte)Math.Clamp(accumulated >> BicubicPrecisionBits, 0, 255);
}
