namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class GeminiModels
{
    private static readonly string[] NonTextMarkers = ["-tts", "-image", "-live", "native-audio", "embedding"];

    internal static bool IsTextChat(string modelId) => !modelId.StartsWith("gemini-", StringComparison.Ordinal)
        || !NonTextMarkers.Any(marker => modelId.Contains(marker, StringComparison.Ordinal));

    // The model card establishes limits for this native ID, not every same-prefix variant.
    internal static (long? Context, int? Output) VertexLimits(string modelId) =>
        modelId == "gemini-3-flash-preview" ? (1_048_576, 65_536) : (null, null);
}
