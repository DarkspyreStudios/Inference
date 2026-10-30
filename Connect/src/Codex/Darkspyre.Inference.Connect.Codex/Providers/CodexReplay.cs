using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>The Responses output items a ChatGPT subscription connection retains and replays.</summary>
internal static class CodexReplay
{
    /// <summary>The projected base items, the image tool's call, and the compaction items replayed verbatim.</summary>
    internal static ResponsesReplayRules Rules { get; } = new(
        new Dictionary<string, string[]?>(ResponsesReplayRules.ProjectedBase, StringComparer.Ordinal)
        {
            ["image_generation_call"] = ["type", "id", "status", "result"],
            ["compaction"] = null,
            ["compaction_summary"] = null
        });

    /// <summary>The codec every ChatGPT subscription client returns.</summary>
    internal static ILlmContinuationCodec Codec { get; } = new ResponsesContinuationCodec(Rules);
}
