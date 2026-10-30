using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>The Responses output items a Grok connection retains and replays.</summary>
internal static class XaiReplay
{
    /// <summary>
    /// Grok replays its output verbatim and in order, including the encrypted state of hosted calls and the compaction
    /// item from responses/compact. The payload is opaque: the library never runs or logs its fields.
    /// </summary>
    internal static ResponsesReplayRules Rules { get; } = new(new[]
        {
            "message", "reasoning", "function_call", "compaction", "web_search_call", "x_search_call",
            "code_interpreter_call", "code_execution_call", "file_search_call", "mcp_call", "image_generation_call",
            "view_image_call", "view_x_video_call", "custom_tool_call", "tool_search_call", "tool_search_output"
        }.ToDictionary(type => type, _ => (string[]?)null, StringComparer.Ordinal))
    {
        ValidateItem = ValidateHostedItem
    };

    /// <summary>The codec every Grok client returns.</summary>
    internal static ILlmContinuationCodec Codec { get; } = new ResponsesContinuationCodec(Rules);

    /// <summary>A custom tool call or a tool search item needs its string identity and string fields.</summary>
    private static void ValidateHostedItem(JsonObject item, string type)
    {
        if (type is not ("custom_tool_call" or "tool_search_call" or "tool_search_output")) return;
        if (item["id"] is not JsonValue identity || !identity.TryGetValue<string>(out _))
            throw new ArgumentException("Hosted continuation requires its item identity.");
        foreach (string field in new[] { "call_id", "status", "name", "input", "execution" })
            if (item[field] is { } value && (value is not JsonValue text || !text.TryGetValue<string>(out _)))
                throw new ArgumentException("Hosted continuation contains an invalid string field.");
        if (type == "custom_tool_call" && (item["call_id"] is null || item["name"] is null))
            throw new ArgumentException("Custom continuation requires its call identity and name.");
        if (type == "tool_search_output" && item["tools"] is { } tools && tools is not JsonArray)
            throw new ArgumentException("Tool-search output requires ordered definitions.");
    }
}
