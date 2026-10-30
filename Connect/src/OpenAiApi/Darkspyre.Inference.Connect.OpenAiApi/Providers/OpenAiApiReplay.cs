using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>The Responses output items an OpenAI API connection retains and replays.</summary>
internal static class OpenAiApiReplay
{
    /// <summary>The projected base items and the hosted calls. Hosted tool calls are retained like the image tool's, so a
    /// replayed reasoning item keeps the call after it.</summary>
    internal static ResponsesReplayRules Rules { get; } = new(
        new Dictionary<string, string[]?>(ResponsesReplayRules.ProjectedBase, StringComparer.Ordinal)
        {
            ["image_generation_call"] = ["type", "id", "status", "result"],
            ["web_search_call"] = ["type", "id", "status", "action"],
            ["file_search_call"] = ["type", "id", "status", "queries", "results"],
            ["code_interpreter_call"] = ["type", "id", "status", "code", "container_id", "outputs"],
            ["mcp_call"] = ["type", "id", "status", "server_label", "name", "arguments", "output", "error"]
        });

    /// <summary>The codec every OpenAI API client returns.</summary>
    internal static ILlmContinuationCodec Codec { get; } = new ResponsesContinuationCodec(Rules);
}
