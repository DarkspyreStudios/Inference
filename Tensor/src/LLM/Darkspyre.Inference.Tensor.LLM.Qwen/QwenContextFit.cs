using System.Diagnostics;
using System.Globalization;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

/// <summary>
/// Fits a loaded model's context to the memory left after its weights. TensorSharp grows the attention
/// cache on demand up to the declared context; this reports how much of it fits, so a host compacts
/// before memory runs out.
/// </summary>
internal static class QwenContextFit
{
    private const long MinimumReserveBytes = 512L * 1024 * 1024;

    /// <summary>The usable context: the declared context, or fewer tokens when their cache does not fit.</summary>
    public static int Usable(int declaredTokens, long spareBytes, long cacheBytesPerToken)
    {
        if (declaredTokens <= 0) { return 0; }
        if (cacheBytesPerToken <= 0) { return declaredTokens; }
        return (int)Math.Min(declaredTokens, Math.Max(0, spareBytes) / cacheBytesPerToken);
    }

    /// <summary>
    /// Attention-cache bytes one token adds: keys and values for every full-attention layer at two bytes
    /// per element. Recurrent layers keep a fixed state and add nothing per token.
    /// </summary>
    public static long CacheBytesPerToken(IReadOnlyDictionary<string, object> header, string arch)
    {
        int layers = Number(header, $"{arch}.block_count");
        int heads = Number(header, $"{arch}.attention.head_count");
        int kvHeads = Number(header, $"{arch}.attention.head_count_kv");
        int hidden = Number(header, $"{arch}.embedding_length");
        int keyLength = Number(header, $"{arch}.attention.key_length", hidden / heads);
        int valueLength = Number(header, $"{arch}.attention.value_length", keyLength);
        long fullLayers = QwenModelCompatibility.FullAttentionLayers(header, arch, layers);
        return checked(fullLayers * kvHeads * (keyLength + valueLength) * 2L);
    }

    /// <summary>
    /// Memory this process can still use: the GC's available-memory limit, minus the working set, minus a
    /// reserve of the larger of 512 MiB and one sixteenth. TensorSharp estimates host headroom the same way.
    /// </summary>
    public static long SpareBytes()
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        using var process = Process.GetCurrentProcess();
        long reserve = Math.Max(MinimumReserveBytes, available / 16);
        return Math.Max(0, available - Math.Min(available, process.WorkingSet64) - reserve);
    }

    private static int Number(IReadOnlyDictionary<string, object> header, string key, int? defaultValue = null) =>
        header.TryGetValue(key, out object? value) ? checked((int)Convert.ToUInt32(value, CultureInfo.InvariantCulture))
            : defaultValue ?? throw new InvalidDataException($"Missing Qwen metadata '{key}'.");
}
