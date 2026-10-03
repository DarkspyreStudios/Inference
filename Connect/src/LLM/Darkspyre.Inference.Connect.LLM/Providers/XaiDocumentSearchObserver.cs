using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class XaiDocumentSearchObserver(Func<Exception> malformed) : ResponsesHostedToolObserver
{
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private bool _completed;

    internal override IEnumerable<LlmStreamEvent> ObserveAdded(JsonElement root)
    {
        if (!root.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object
            || LlmJson.GetString(item, "type") != "file_search_call") yield break;
        string? id = OptionalString(item, "id");
        if (!string.IsNullOrWhiteSpace(id) && _started.Add(id))
            yield return new LlmHostedToolCallStartedEvent(id, LlmHostedToolKind.DocumentSearch, "file_search");
    }

    internal override IEnumerable<LlmStreamEvent> Complete(JsonElement root)
    {
        if (_completed) yield break;
        _completed = true;
        JsonElement response = root.TryGetProperty("response", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.Object ? nested : root;
        if (!response.TryGetProperty("output", out JsonElement output)) yield break;
        if (output.ValueKind != JsonValueKind.Array) throw malformed();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || LlmJson.GetString(item, "type") != "file_search_call") continue;
            string? id = OptionalString(item, "id");
            string? status = OptionalString(item, "status");
            JsonElement? queries = null;
            if (item.TryGetProperty("queries", out JsonElement observedQueries))
            {
                if (observedQueries.ValueKind != JsonValueKind.Array
                    || observedQueries.EnumerateArray().Any(query => query.ValueKind != JsonValueKind.String)) throw malformed();
                queries = observedQueries;
            }
            IReadOnlyList<LlmHostedDocumentMatch>? results = ReadResults(item);
            // Equality of id-less payloads is not evidence that they are the same call.
            if (!string.IsNullOrWhiteSpace(id) && !completed.Add(id)) continue;
            yield return new LlmHostedToolCallCompletedEvent(new(id, LlmHostedToolKind.DocumentSearch,
                "file_search", status, queries, new LlmHostedDocumentSearchOutput(results)));
        }
    }

    private List<LlmHostedDocumentMatch>? ReadResults(JsonElement item)
    {
        if (!item.TryGetProperty("results", out JsonElement results)) return null;
        if (results.ValueKind != JsonValueKind.Array) throw malformed();
        var matches = new List<LlmHostedDocumentMatch>();
        foreach (JsonElement result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object || OptionalString(result, "file_id") is not { } fileId)
                throw malformed();
            double? score = null;
            if (result.TryGetProperty("score", out JsonElement observedScore))
            {
                if (observedScore.ValueKind != JsonValueKind.Number || !observedScore.TryGetDouble(out double number)
                    || !double.IsFinite(number)) throw malformed();
                score = number;
            }
            matches.Add(new(fileId, OptionalString(result, "filename"), score, OptionalString(result, "text")));
        }
        return matches;
    }

    private string? OptionalString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw malformed();
    }
}
