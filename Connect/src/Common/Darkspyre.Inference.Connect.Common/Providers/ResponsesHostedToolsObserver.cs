using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Reports the hosted tool calls and citations of a Responses reply: image generation, web search, X search,
/// file search, code interpreter and remote MCP calls, url_citation annotations and a top-level citations list.</summary>
internal sealed class ResponsesHostedToolsObserver(Func<Exception> malformed) : ResponsesHostedToolObserver
{
    private const string ImageName = "image_generation";
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _citationKeys = new(StringComparer.Ordinal);
    private readonly List<LlmCitation> _citations = [];
    private bool _streamedItems;

    /// <summary>A started event for a hosted item on output_item.added, and a completed event with its output and the
    /// citations of a message item on output_item.done. Throws malformed() for an image item without an id, an image
    /// item done without a base64 result, and a hosted field of the wrong JSON kind.</summary>
    internal override IEnumerable<LlmStreamEvent> Observe(JsonElement root)
    {
        string? type = LlmJson.GetString(root, "type");
        if (type is not ("response.output_item.added" or "response.output_item.done")
            || !root.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
            yield break;
        if (type == "response.output_item.done")
        {
            _streamedItems = true;
            foreach (LlmStreamEvent citation in ObserveCitations(item, LlmJson.GetInt(root, "output_index")))
                yield return citation;
        }
        if (Identify(item) is not { } tool) yield break;
        string? id = CallIdentity(item, tool.Kind);
        if (type == "response.output_item.added")
        {
            if (tool.Kind == LlmHostedToolKind.ImageGeneration && string.IsNullOrWhiteSpace(id)) throw malformed();
            if (!string.IsNullOrWhiteSpace(id) && _started.Add(id))
                yield return new LlmHostedToolCallStartedEvent(id, tool.Kind, tool.Name);
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(id)) _completed.Add(id);
        yield return Complete(item, id, tool);
    }

    /// <summary>The completed events for hosted items found only in the terminal output, the citations not yet
    /// observed, and the URLs of a top-level citations list that no annotation already cited.</summary>
    internal override Task<IReadOnlyList<LlmStreamEvent>> CompleteAsync(JsonElement root, CancellationToken token)
    {
        JsonElement response = root.TryGetProperty("response", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.Object ? nested : root;
        var events = new List<LlmStreamEvent>();
        if (response.TryGetProperty("output", out JsonElement output) && output.ValueKind != JsonValueKind.Null)
        {
            if (output.ValueKind != JsonValueKind.Array) throw malformed();
            int index = 0;
            foreach (JsonElement item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw malformed();
                events.AddRange(ObserveCitations(item, index++));
                if (Identify(item) is not { } tool) continue;
                string? id = CallIdentity(item, tool.Kind);
                // A streamed item was completed on output_item.done; an id-less one cannot be matched, so only a reply
                // that streamed no items reports it here.
                if (string.IsNullOrWhiteSpace(id) ? _streamedItems : !_completed.Add(id)) continue;
                events.Add(Complete(item, id, tool));
            }
        }
        if (response.TryGetProperty("citations", out JsonElement citations) && citations.ValueKind != JsonValueKind.Null)
        {
            if (citations.ValueKind != JsonValueKind.Array) throw malformed();
            foreach (JsonElement citation in citations.EnumerateArray())
            {
                if (citation.ValueKind != JsonValueKind.String) throw malformed();
                string url = citation.GetString()!;
                // Top-level URLs summarize the annotations, so a URL an annotation already cited is not added again.
                if (_citations.Any(observed => observed.Url == url) || !_citationKeys.Add("top:" + url)) continue;
                var observed = new LlmCitation(url);
                _citations.Add(observed);
                events.Add(new LlmCitationEvent(observed));
            }
        }
        return Task.FromResult<IReadOnlyList<LlmStreamEvent>>(events);
    }

    /// <summary>The result with every citation the reply carried, each once.</summary>
    internal override LlmChatResult Decorate(LlmChatResult result) => result with { Citations = _citations.ToArray() };

    private LlmHostedToolCallCompletedEvent Complete(JsonElement item, string? id, (LlmHostedToolKind Kind, string Name) tool)
    {
        string? status = LlmJson.GetString(item, "status");
        JsonElement? input = null;
        LlmHostedToolOutput? output = null;
        switch (tool.Kind)
        {
            case LlmHostedToolKind.ImageGeneration:
                return CompleteImage(item, id, status);
            case LlmHostedToolKind.WebSearch:
                input = OptionalValue(item, "action");
                if (input is { ValueKind: not JsonValueKind.Object }) throw malformed();
                output = new LlmHostedSearchOutput(input is { } action ? ReadSources(action) : null);
                break;
            case LlmHostedToolKind.SocialSearch when LlmJson.GetString(item, "type") == "custom_tool_call":
                input = OptionalValue(item, "input");
                if (input is { ValueKind: not JsonValueKind.String }) throw malformed();
                break;
            case LlmHostedToolKind.DocumentSearch:
                input = OptionalArray(item, "queries", strings: true);
                output = new LlmHostedDocumentSearchOutput(ReadDocuments(item));
                break;
            case LlmHostedToolKind.CodeExecution:
                output = ReadCode(item);
                break;
            case LlmHostedToolKind.RemoteMcp:
                input = OptionalValue(item, "arguments");
                output = new LlmHostedMcpOutput(input, OptionalValue(item, "output"), OptionalValue(item, "error"));
                break;
        }
        return new LlmHostedToolCallCompletedEvent(new(id, tool.Kind, tool.Name, status, input, output));
    }

    /// <summary>The image call with its decoded bytes; the media type comes from output_format, png when absent.</summary>
    private LlmHostedToolCallCompletedEvent CompleteImage(JsonElement item, string? id, string? status)
    {
        if (string.IsNullOrWhiteSpace(id)) throw malformed();
        string result = LlmJson.GetString(item, "result") is { Length: > 0 } encoded ? encoded : throw malformed();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(result); }
        catch (FormatException) { throw malformed(); }
        string format = LlmJson.GetString(item, "output_format") is { Length: > 0 } reported ? reported : "png";
        return new LlmHostedToolCallCompletedEvent(new(id, LlmHostedToolKind.ImageGeneration, ImageName,
            status, null, new LlmHostedImageOutput(bytes, "image/" + format)));
    }

    private (LlmHostedToolKind Kind, string Name)? Identify(JsonElement item) => LlmJson.GetString(item, "type") switch
    {
        "image_generation_call" => (LlmHostedToolKind.ImageGeneration, ImageName),
        "web_search_call" => (LlmHostedToolKind.WebSearch, "web_search"),
        "x_search_call" => (LlmHostedToolKind.SocialSearch, "x_search"),
        "file_search_call" => (LlmHostedToolKind.DocumentSearch, "file_search"),
        "code_interpreter_call" => (LlmHostedToolKind.CodeExecution, "code_interpreter"),
        "mcp_call" => (LlmHostedToolKind.RemoteMcp, RequiredString(item, "name")),
        // https://docs.x.ai/developers/tools/tool-usage-details identifies Grok's X search by its call id or tool name.
        "custom_tool_call" when LlmJson.GetString(item, "call_id")?.StartsWith("xs_", StringComparison.Ordinal) == true
            || LlmJson.GetString(item, "name") is "x_keyword_search" or "x_semantic_search" or "x_user_search" or "x_thread_fetch"
            => (LlmHostedToolKind.SocialSearch, RequiredString(item, "name")),
        _ => null
    };

    /// <summary>The call's identity: call_id for Grok's custom X-search calls, id for every other item.</summary>
    private string? CallIdentity(JsonElement item, LlmHostedToolKind kind) => OptionalString(item,
        kind == LlmHostedToolKind.SocialSearch && LlmJson.GetString(item, "type") == "custom_tool_call" ? "call_id" : "id");

    /// <summary>A citation event for each url_citation annotation of a message item not already observed, keyed by the
    /// item's id (or output position), the content part and the annotation.</summary>
    private IEnumerable<LlmStreamEvent> ObserveCitations(JsonElement item, int? index)
    {
        if (LlmJson.GetString(item, "type") != "message"
            || !item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array) yield break;
        string? id = OptionalString(item, "id");
        if (id is null && index is null) yield break;
        string identity = id is not null ? $"id:{id}" : $"index:{index}";
        int partIndex = 0;
        foreach (JsonElement part in content.EnumerateArray())
        {
            int currentPart = partIndex++;
            if (LlmJson.GetString(part, "type") != "output_text" || !part.TryGetProperty("annotations", out JsonElement annotations)
                || annotations.ValueKind == JsonValueKind.Null) continue;
            if (annotations.ValueKind != JsonValueKind.Array) throw malformed();
            int annotationIndex = 0;
            foreach (JsonElement annotation in annotations.EnumerateArray())
            {
                int currentAnnotation = annotationIndex++;
                if (LlmJson.GetString(annotation, "type") != "url_citation"
                    || !_citationKeys.Add($"{identity}:{currentPart}:{currentAnnotation}")) continue;
                var citation = new LlmCitation(RequiredString(annotation, "url"), OptionalString(annotation, "title"));
                _citations.Add(citation);
                yield return new LlmCitationEvent(citation);
            }
        }
    }

    private LlmHostedSearchSource[]? ReadSources(JsonElement action) =>
        OptionalArray(action, "sources") is { } sources
            ? [.. sources.EnumerateArray().Select(source => new LlmHostedSearchSource(OptionalString(source, "url"), OptionalString(source, "title")))]
            : null;

    private List<LlmHostedDocumentMatch>? ReadDocuments(JsonElement item)
    {
        if (OptionalArray(item, "results") is not { } results) return null;
        var matches = new List<LlmHostedDocumentMatch>();
        foreach (JsonElement result in results.EnumerateArray())
        {
            double? score = null;
            if (result.TryGetProperty("score", out JsonElement observed) && observed.ValueKind != JsonValueKind.Null)
            {
                if (observed.ValueKind != JsonValueKind.Number || !observed.TryGetDouble(out double value) || !double.IsFinite(value)) throw malformed();
                score = value;
            }
            matches.Add(new(RequiredString(result, "file_id"), OptionalString(result, "filename"), score, OptionalString(result, "text")));
        }
        return matches;
    }

    private LlmHostedCodeOutput ReadCode(JsonElement item)
    {
        List<string>? logs = null;
        List<string>? images = null;
        if (OptionalArray(item, "outputs") is { } outputs)
        {
            logs = []; images = [];
            foreach (JsonElement output in outputs.EnumerateArray())
                switch (LlmJson.GetString(output, "type"))
                {
                    case "logs": logs.Add(RequiredString(output, "logs")); break;
                    case "image": images.Add(RequiredString(output, "url")); break;
                    default: throw malformed();
                }
        }
        return new(OptionalString(item, "code"), logs, images);
    }

    private static JsonElement? OptionalValue(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value)
        && value.ValueKind != JsonValueKind.Null ? value.Clone() : null;

    private JsonElement? OptionalArray(JsonElement item, string name, bool strings = false)
    {
        JsonElement? value = OptionalValue(item, name);
        if (value is { } array && (array.ValueKind != JsonValueKind.Array
            || strings && array.EnumerateArray().Any(entry => entry.ValueKind != JsonValueKind.String))) throw malformed();
        return value;
    }

    private string RequiredString(JsonElement item, string name) => OptionalString(item, name) ?? throw malformed();

    private string? OptionalString(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object) throw malformed();
        if (!item.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw malformed();
    }
}
