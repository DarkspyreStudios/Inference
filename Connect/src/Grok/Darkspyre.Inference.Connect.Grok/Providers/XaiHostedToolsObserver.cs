using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>The Responses hosted-call observer with Grok's X-search items: x_search_call, and custom_tool_call items
/// that carry an xs_ call id or an X-search tool name.</summary>
internal sealed class XaiHostedToolsObserver(Func<Exception> malformed) : ResponsesHostedToolsObserver(malformed)
{
    /// <inheritdoc />
    protected override (LlmHostedToolKind Kind, string Name)? Identify(JsonElement item) => LlmJson.GetString(item, "type") switch
    {
        "x_search_call" => (LlmHostedToolKind.SocialSearch, "x_search"),
        // https://docs.x.ai/developers/tools/tool-usage-details identifies Grok's X search by its call id or tool name.
        "custom_tool_call" when LlmJson.GetString(item, "call_id")?.StartsWith("xs_", StringComparison.Ordinal) == true
            || LlmJson.GetString(item, "name") is "x_keyword_search" or "x_semantic_search" or "x_user_search" or "x_thread_fetch"
            => (LlmHostedToolKind.SocialSearch, RequiredString(item, "name")),
        _ => base.Identify(item)
    };

    /// <summary>The call's identity: call_id for a custom X-search call, the shared reading for every other item.</summary>
    protected override string? CallIdentity(JsonElement item, LlmHostedToolKind kind) =>
        IsCustomSearch(item, kind) ? OptionalString(item, "call_id") : base.CallIdentity(item, kind);

    /// <summary>A custom X-search call's input is its string <c>input</c>; it carries no output.</summary>
    protected override bool TryReadItem(JsonElement item, LlmHostedToolKind kind, out JsonElement? input,
        out LlmHostedToolOutput? output)
    {
        output = null;
        if (!IsCustomSearch(item, kind))
        {
            input = null;
            return false;
        }
        input = OptionalValue(item, "input");
        if (input is { ValueKind: not JsonValueKind.String }) throw Malformed();
        return true;
    }

    private static bool IsCustomSearch(JsonElement item, LlmHostedToolKind kind) =>
        kind == LlmHostedToolKind.SocialSearch && LlmJson.GetString(item, "type") == "custom_tool_call";
}
