using System.Security.Cryptography;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

/// <summary>The request images of one loaded model, as files TensorSharp reads by path.</summary>
internal sealed class QwenImageFiles(string directory) : IDisposable
{
    /// <summary>Creates a per-load image folder under the instance's host-owned scratch directory.</summary>
    public static QwenImageFiles Create(string scratchDirectory)
    {
        string directory = Path.Combine(scratchDirectory, "qwen-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new(directory);
    }

    /// <summary>Writes a PNG or JPEG once under its content hash and returns the path.</summary>
    public string Write(LlmAttachment image)
    {
        ReadOnlySpan<byte> bytes = image.Data.Span;
        string extension = bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }) ? ".png"
            : bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }) ? ".jpg"
            : throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Qwen reads PNG and JPEG images only.");
        // Attachment names and media types are opaque. Only the byte hash and fixed extension enter a path (L14).
        string path = Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(bytes)) + extension);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Deletes the per-load folder; cleanup failure does not fail model teardown.</summary>
    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception) { /* Best-effort scratch cleanup, matching CliInfrastructure.DeleteWorkingDirectory. */ }
    }
}
