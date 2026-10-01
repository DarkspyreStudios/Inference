using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Reassembles Vertex typed argument paths without exposing them as JSON-text deltas.</summary>
internal sealed class GeminiArgumentStream
{
    private JsonObject? _part;
    private JsonNode? _arguments;
    private readonly HashSet<string> _strings = new(StringComparer.Ordinal);
    public bool IsOpen => _part is not null;

    public (bool Started, JsonObject Part, LlmToolCall? Completed) Observe(JsonObject incoming, Func<string> createId, CancellationToken cancellationToken)
    {
        JsonObject function = incoming["functionCall"]!.AsObject();
        if (function.Any(field => field.Key is not ("id" or "name" or "partialArgs" or "willContinue"))) { throw Failure(); }
        bool started = false;
        if (_part is null)
        {
            string? name = (string?)function["name"];
            if (string.IsNullOrWhiteSpace(name) || (bool?)function["willContinue"] != true && !function.ContainsKey("partialArgs")) { throw Failure(); }
            string id = (string?)function["id"] ?? createId();
            if (string.IsNullOrWhiteSpace(id)) { throw Failure(); }
            var whole = new JsonObject { ["name"] = name };
            if (function["id"] is { } wireId) { whole["id"] = wireId.DeepClone(); }
            _part = new JsonObject { ["functionCall"] = whole, ["connectCallId"] = id };
            _arguments = new JsonObject();
            started = true;
        }
        else if (function.ContainsKey("name") || function.ContainsKey("id")) { throw Failure(); }
        JsonObject part = _part;
        if (incoming["thoughtSignature"] is { } signature)
        {
            _ = signature.GetValue<string>();
            if (part["thoughtSignature"] is { } previous && !JsonNode.DeepEquals(previous, signature)) { throw Failure(); }
            part["thoughtSignature"] = signature.DeepClone();
        }
        if (function["partialArgs"] is { } fragments)
        {
            foreach (JsonNode? fragment in fragments.AsArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fragment is not JsonObject item) { throw Failure(); }
                Apply(item, cancellationToken);
            }
        }
        if ((bool?)function["willContinue"] == true) { return (started, part, null); }
        if (_arguments is not JsonObject arguments || _strings.Count > 0) { throw Failure(); }
        part["functionCall"]!["args"] = arguments;
        var call = new LlmToolCall((string)part["connectCallId"]!, (string)part["functionCall"]!["name"]!, arguments.ToJsonString());
        _part = null;
        _arguments = null;
        return (started, part, call);
    }

    private void Apply(JsonObject fragment, CancellationToken cancellationToken)
    {
        if (fragment.Any(field => field.Key is not ("jsonPath" or "stringValue" or "numberValue" or "boolValue" or "nullValue" or "willContinue"))) { throw Failure(); }
        string path = (string?)fragment["jsonPath"] ?? throw Failure();
        List<object> segments = ParsePath(path);
        string key = JsonSerializer.Serialize(segments);
        string[] values = fragment.Select(field => field.Key).Where(name => name.EndsWith("Value", StringComparison.Ordinal)).ToArray();
        bool continues = (bool?)fragment["willContinue"] == true;
        if (values.Length == 0)
        {
            if (continues || !_strings.Remove(key)) { throw Failure(); }
            return;
        }
        if (values.Length != 1) { throw Failure(); }
        string kind = values[0];
        JsonNode? value = kind switch
        {
            "stringValue" when fragment[kind] is JsonValue text && text.TryGetValue<string>(out string? stringValue) => JsonValue.Create(stringValue),
            "numberValue" when fragment[kind] is JsonValue number && number.GetValueKind() == JsonValueKind.Number => number.DeepClone(),
            "boolValue" when fragment[kind] is JsonValue boolean && boolean.TryGetValue<bool>(out bool boolValue) => JsonValue.Create(boolValue),
            "nullValue" when fragment[kind] is null => null,
            _ => throw Failure()
        };
        if (continues && kind != "stringValue") { throw Failure(); }
        bool append = _strings.Contains(key);
        if (append && kind != "stringValue") { throw Failure(); }
        Set(segments, value, append, cancellationToken);
        if (continues) { _strings.Add(key); } else { _strings.Remove(key); }
    }

    private void Set(List<object> segments, JsonNode? value, bool append, CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            _arguments = Append(_arguments, value, append);
            return;
        }
        JsonNode current = _arguments ?? throw Failure();
        for (int position = 0; position < segments.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object segment = segments[position];
            bool last = position == segments.Count - 1;
            JsonNode? existing;
            if (segment is string name && current is JsonObject obj)
            {
                existing = obj[name];
                if (last) { obj[name] = Append(existing, value, append); return; }
                if (existing is null) { obj[name] = existing = segments[position + 1] is int ? new JsonArray() : new JsonObject(); }
            }
            else if (segment is int index && current is JsonArray array)
            {
                // Sparse-array expansion cannot exceed the continuation payload's existing wire-size bound.
                if (index >= GeminiContinuation.MaxPayloadLength / 5) { throw Failure(); }
                while (array.Count <= index) { cancellationToken.ThrowIfCancellationRequested(); array.Add(null); }
                existing = array[index];
                if (last) { array[index] = Append(existing, value, append); return; }
                if (existing is null) { array[index] = existing = segments[position + 1] is int ? new JsonArray() : new JsonObject(); }
            }
            else { throw Failure(); }
            current = existing;
        }
    }

    private static JsonNode? Append(JsonNode? existing, JsonNode? value, bool append) => append
        ? JsonValue.Create((existing?.GetValue<string>() ?? throw Failure()) + value!.GetValue<string>()) : value;

    private static List<object> ParsePath(string path)
    {
        if (path.Length == 0 || path[0] != '$') { throw PathFailure(); }
        var segments = new List<object>();
        int position = 1;
        while (position < path.Length)
        {
            // The reconstructed object uses the JSON serializer's default maximum depth of 64.
            if (segments.Count >= 63) { throw PathFailure(); }
            if (path[position] == '.')
            {
                int start = ++position;
                if (position == path.Length || !NameStart(path[position])) { throw PathFailure(); }
                while (position < path.Length && (NameStart(path[position]) || char.IsAsciiDigit(path[position]))) { position++; }
                segments.Add(path[start..position]);
            }
            else if (path[position] == '[')
            {
                position++;
                if (position < path.Length && path[position] == '\'')
                {
                    position++;
                    var json = new StringBuilder("\"");
                    bool closed = false;
                    while (position < path.Length)
                    {
                        char character = path[position++];
                        if (character == '\'') { closed = true; break; }
                        if (character == '\\')
                        {
                            if (position == path.Length) { throw PathFailure(); }
                            char escaped = path[position++];
                            if (escaped == '\'') { json.Append('\''); } else { json.Append('\\').Append(escaped); }
                        }
                        else if (character == '"') { json.Append("\\\""); }
                        else { json.Append(character); }
                    }
                    if (!closed) { throw PathFailure(); }
                    json.Append('"');
                    try { segments.Add(JsonSerializer.Deserialize<string>(json.ToString())!); }
                    catch (JsonException) { throw PathFailure(); }
                }
                else
                {
                    int start = position;
                    while (position < path.Length && char.IsAsciiDigit(path[position])) { position++; }
                    if (position == start || position - start > 1 && path[start] == '0'
                        || !int.TryParse(path.AsSpan(start, position - start), NumberStyles.None, CultureInfo.InvariantCulture, out int index)) { throw PathFailure(); }
                    segments.Add(index);
                }
                if (position == path.Length || path[position++] != ']') { throw PathFailure(); }
            }
            else { throw PathFailure(); }
        }
        return segments;
    }

    private static bool NameStart(char value) => char.IsAsciiLetter(value) || value == '_' || value >= 128;
    private static LlmProviderException Failure() => new("Vertex returned invalid streamed function arguments.", LlmTransport.GoogleVertexAi);
    private static LlmProviderException PathFailure() => new("Vertex returned an unsupported argument path.", LlmTransport.GoogleVertexAi);
}
