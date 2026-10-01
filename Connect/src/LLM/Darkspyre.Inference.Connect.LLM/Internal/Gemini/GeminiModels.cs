namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class GeminiModels
{
    private static readonly string[] NonTextMarkers = ["-tts", "-image", "-live", "native-audio", "embedding"];

    internal static bool IsTextChat(string modelId) => modelId.StartsWith("gemini-", StringComparison.Ordinal)
        && !NonTextMarkers.Any(marker => modelId.Contains(marker, StringComparison.Ordinal));

    // Vertex's Gemini 3 Flash model card states these limits. Unknown families retain unknown limits.
    internal static (long? Context, int? Output) VertexLimits(string modelId) =>
        modelId.StartsWith("gemini-3-flash", StringComparison.Ordinal) ? (1_048_576, 65_536) : (null, null);
}
