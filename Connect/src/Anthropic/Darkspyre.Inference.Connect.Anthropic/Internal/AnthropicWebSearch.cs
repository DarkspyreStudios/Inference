using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// Anthropic's hosted web search on the neutral contract: which models offer it, the checks Anthropic adds, the
/// <c>web_search_20250305</c> tool entry, and the started, completed and citation events of a reply. The search runs on
/// Anthropic's side; the blocks it returns stay in the native continuation unchanged.
/// </summary>
/// <remarks>
/// The basic tool version calls search directly on every model that supports it. Later versions route searches through
/// code execution by default and return code-execution blocks this mapping does not observe.
/// </remarks>
internal sealed class AnthropicWebSearch
{
    internal const string Name = "web_search";
    private const string ToolType = "web_search_20250305";
    private readonly Dictionary<string, JsonElement?> inputs = new(StringComparer.Ordinal);
    private readonly List<LlmCitation> citations = [];

    /// <summary>The citations observed so far, in order.</summary>
    internal IReadOnlyList<LlmCitation> Citations => this.citations;

    /// <summary>
    /// Whether the Models API entry reports <c>capabilities.server_tools.web_search.supported</c> as true, Anthropic's
    /// documented rule for a model that accepts web search. A missing value does not offer it.
    /// </summary>
    internal static bool Offered(JsonElement? entry) =>
        entry is { ValueKind: JsonValueKind.Object } model
        && Member(model, "capabilities") is { } capabilities
        && Member(capabilities, "server_tools") is { } tools
        && Member(tools, Name) is { } search
        && search.TryGetProperty("supported", out JsonElement supported) && supported.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The Anthropic-only checks, after the shared hosted-tool validation: a raw <c>web_search</c> server tool, allowed
    /// and excluded domains together, and image search or image understanding are refused.
    /// </summary>
    internal static void Validate(LlmChatRequest request, AnthropicTools tools)
    {
        if (tools.NativeNames.Contains(Name)) throw new NotSupportedException("Web search uses the hosted web-search tool.");
        foreach (LlmWebSearchTool web in request.HostedTools.OfType<LlmWebSearchTool>())
        {
            if (web.AllowedDomains is { Count: > 0 } && web.ExcludedDomains is { Count: > 0 })
                throw new NotSupportedException("Anthropic web search accepts allowed or excluded domains, not both.");
            if (web.IncludeImageSearch || web.IncludeImageUnderstanding)
                throw new NotSupportedException("Anthropic web search has no image search or image understanding.");
        }
    }

    /// <summary>
    /// The tool entry for the request's web search, or null when it asks for none. <c>max_uses</c> is not sent, so
    /// Anthropic's default applies.
    /// </summary>
    internal static JsonObject? Definition(LlmChatRequest request)
    {
        if (request.HostedTools.OfType<LlmWebSearchTool>().FirstOrDefault() is not { } web) return null;
        var tool = new JsonObject { ["type"] = ToolType, ["name"] = Name };
        if (web.AllowedDomains is { Count: > 0 } allowed) tool["allowed_domains"] = Strings(allowed);
        if (web.ExcludedDomains is { Count: > 0 } excluded) tool["blocked_domains"] = Strings(excluded);
        if (web.Location is { } location)
        {
            var place = new JsonObject { ["type"] = "approximate" };
            if (location.City is { Length: > 0 } city) place["city"] = city;
            if (location.Region is { Length: > 0 } region) place["region"] = region;
            if (location.Country is { Length: > 0 } country) place["country"] = country;
            if (location.Timezone is { Length: > 0 } timezone) place["timezone"] = timezone;
            // Anthropic needs at least one field; a location that sets none is not sent.
            if (place.Count > 1) tool["user_location"] = place;
        }
        return tool;
    }

    /// <summary>
    /// The started event for a <c>server_tool_use</c> block named <c>web_search</c>, or null for any other block or one
    /// without a usable id. The block's own input is kept for a completion the stream gives no assembled input.
    /// </summary>
    internal LlmStreamEvent? Start(JsonElement block)
    {
        if (LlmJson.GetString(block, "type") != "server_tool_use" || LlmJson.GetString(block, "name") != Name
            || LlmJson.GetString(block, "id") is not { Length: > 0 } id || string.IsNullOrWhiteSpace(id)) return null;
        this.inputs[id] = block.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.Object
            ? input.Clone() : null;
        return new LlmHostedToolCallStartedEvent(id, LlmHostedToolKind.WebSearch, Name);
    }

    /// <summary>The final input a stream assembled for a started call; another call's input is not kept.</summary>
    internal void Record(string id, JsonElement? input)
    {
        if (this.inputs.ContainsKey(id)) this.inputs[id] = input;
    }

    /// <summary>
    /// The completed event for a <c>web_search_tool_result</c> block, or null for any other block. A list of results
    /// gives their sources and no status; an error gives the provider's <c>error_code</c> as the status and no output.
    /// The input is the matching call's, which may have started before a pause.
    /// </summary>
    internal LlmStreamEvent? Complete(JsonElement block)
    {
        if (LlmJson.GetString(block, "type") != "web_search_tool_result") return null;
        string? id = LlmJson.GetString(block, "tool_use_id") is { Length: > 0 } known && !string.IsNullOrWhiteSpace(known) ? known : null;
        JsonElement? input = id is not null && this.inputs.TryGetValue(id, out JsonElement? started) ? started : null;
        string? status = null;
        LlmHostedSearchOutput? output = null;
        if (block.TryGetProperty("content", out JsonElement content))
        {
            if (content.ValueKind == JsonValueKind.Array)
                output = new LlmHostedSearchOutput([.. content.EnumerateArray()
                    .Where(result => result.ValueKind == JsonValueKind.Object && LlmJson.GetString(result, "type") == "web_search_result")
                    .Select(result => new LlmHostedSearchSource(LlmJson.GetString(result, "url"), LlmJson.GetString(result, "title")))]);
            else if (content.ValueKind == JsonValueKind.Object && LlmJson.GetString(content, "type") == "web_search_tool_result_error")
                status = LlmJson.GetString(content, "error_code");
        }
        return new LlmHostedToolCallCompletedEvent(new LlmHostedToolCall(id, LlmHostedToolKind.WebSearch, Name, status, input, output));
    }

    /// <summary>The citation event for a <c>web_search_result_location</c> citation with a URL, or null.</summary>
    internal LlmStreamEvent? Cite(JsonElement citation)
    {
        if (citation.ValueKind != JsonValueKind.Object || LlmJson.GetString(citation, "type") != "web_search_result_location"
            || LlmJson.GetString(citation, "url") is not { Length: > 0 } url) return null;
        var cited = new LlmCitation(url, LlmJson.GetString(citation, "title"));
        this.citations.Add(cited);
        return new LlmCitationEvent(cited);
    }

    /// <summary>Every event of a complete reply's content, in content order.</summary>
    internal IEnumerable<LlmStreamEvent> Observe(JsonElement content)
    {
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (this.Start(block) is { } started) yield return started;
            if (this.Complete(block) is { } completed) yield return completed;
            if (LlmJson.GetString(block, "type") == "text" && block.TryGetProperty("citations", out JsonElement cited)
                && cited.ValueKind == JsonValueKind.Array)
                foreach (JsonElement citation in cited.EnumerateArray())
                    if (this.Cite(citation) is { } observed) yield return observed;
        }
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(value => JsonValue.Create(value))]);

    private static JsonElement? Member(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : null;
}
