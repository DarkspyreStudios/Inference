using System.Text;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// Estimates what attachments cost Claude, so a request that carries images or documents still has a size. Each part is
/// counted at no less than Claude's published cost, so a history is compacted before it overflows rather than after.
/// </summary>
internal static class ClaudeAttachmentTokens
{
    // Claude scales an image whose long edge exceeds 1,568 pixels, or that would cost more than about 1,600 tokens,
    // down to that size: https://platform.claude.com/docs/en/build-with-claude/vision#evaluate-image-size
    private const long ImageTokens = 1_600;

    // A PDF page costs its text, typically 1,500 to 3,000 tokens, plus its page image:
    // https://platform.claude.com/docs/en/build-with-claude/pdf-support#estimate-your-costs
    private const long PdfPageTokens = 3_000 + ImageTokens;

    // A document whose content cannot be read counts one token per three bytes.
    private const long BytesPerToken = 3;

    /// <summary>The estimated cost of one attachment or tool-output part of <paramref name="mediaType"/>.</summary>
    public static long Estimate(LlmBinaryKind kind, string? mediaType, ReadOnlySpan<byte> data)
    {
        if (kind == LlmBinaryKind.Image || mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) return ImageTokens;
        if (mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true)
            return LocalTokenCount.Count(Encoding.UTF8.GetString(data));
        if (string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            return Math.Max(1, PdfPages(data)) * PdfPageTokens;
        return Math.Max(1, data.Length / BytesPerToken);
    }

    // Counts "/Type /Page" objects, not "/Type /Pages" tree nodes; a PDF that hides its pages in compressed object
    // streams counts as one page.
    private static long PdfPages(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> marker = "/Type"u8;
        long pages = 0;
        for (int index = data.IndexOf(marker); index >= 0; )
        {
            int at = index + marker.Length;
            while (at < data.Length && data[at] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') at++;
            if (data[at..].StartsWith("/Page"u8) && (at + 5 >= data.Length || data[at + 5] != (byte)'s')) pages++;
            int next = data[at..].IndexOf(marker);
            index = next < 0 ? -1 : at + next;
        }
        return pages;
    }
}
