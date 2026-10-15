using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Main-loop usage for one CLI turn, keyed by API message identity.</summary>
internal sealed class ClaudeStreamUsage(bool enabled, ISet<string>? accountedCalls = null)
{
    private readonly Dictionary<string, Call> _calls = new(StringComparer.Ordinal);
    private readonly UsageDeltas _deltas = new(enabled);
    private Call? _current;
    private bool _ignoreCurrent;
    private bool _finished;

    public LlmUsageDeltaEvent? Observe(JsonElement root)
    {
        if (_finished || LlmJson.GetString(root, "type") != "stream_event"
            || LlmJson.GetString(root, "parent_tool_use_id") is not null
            || root.TryGetProperty("isReplay", out var replay) && replay.ValueKind == JsonValueKind.True
            || LlmJson.GetObject(root, "event") is not { } item)
        {
            return null;
        }

        switch (LlmJson.GetString(item, "type"))
        {
            case "message_start":
                JsonElement message = LlmJson.GetObject(item, "message")
                    ?? throw new JsonException("Claude usage has no message identity.");
                string id = LlmJson.GetString(message, "id")
                    ?? throw new JsonException("Claude usage has no message identity.");
                if (string.IsNullOrWhiteSpace(id)) { throw new JsonException("Claude usage has an empty message identity."); }
                if (_calls.TryGetValue(id, out _current))
                {
                    _ignoreCurrent = _current.Complete;
                    return null;
                }
                _ignoreCurrent = accountedCalls is not null && !accountedCalls.Add(id);
                if (_ignoreCurrent) { return null; }
                _current = new();
                _calls.Add(id, _current);
                if (LlmJson.GetObject(message, "usage") is { } initial) { _current.Report(initial, start: true); }
                break;
            case "message_delta":
                if (_ignoreCurrent || LlmJson.GetObject(item, "usage") is not { } usage) { return null; }
                if (_current is null) { throw new JsonException("Claude usage arrived before its message start."); }
                if (_current.Complete) { return null; }
                _current.Report(usage, start: false);
                break;
            case "message_stop":
                if (_current is not null) { _current.Complete = true; }
                return null;
            default:
                return null;
        }

        LlmUsage? total = null;
        bool first = true;
        foreach (Call call in _calls.Values)
        {
            total = UsageTotals.Add(total, call.Usage, first);
            first = false;
        }
        return _deltas.Next(total);
    }

    public void Finish() => _finished = true;

    /// <summary>Usage for the next CLI turn, with the same settings and the same record of accounted calls.</summary>
    public ClaudeStreamUsage Next() => new(enabled, accountedCalls);

    /// <summary>The usage reported for the latest model message; null until one reports.</summary>
    public LlmUsage? Last => _current?.Usage;

    public static LlmUsage? ReadFinal(JsonElement root) => LlmJson.GetObject(root, "usage") is { } usage
        ? AnthropicUsage.Create(LlmJson.GetInt(usage, "input_tokens"), LlmJson.GetInt(usage, "output_tokens"),
            LlmJson.GetInt(usage, "cache_read_input_tokens"), LlmJson.GetInt(usage, "cache_creation_input_tokens"))
        : null;

    private sealed class Call
    {
        private int? _input, _output, _cached, _written;
        public bool Complete { get; set; }
        public LlmUsage? Usage => AnthropicUsage.Create(_input, _output, _cached, _written);

        public void Report(JsonElement usage, bool start)
        {
            _input = LlmJson.GetInt(usage, "input_tokens") ?? _input;
            _cached = LlmJson.GetInt(usage, "cache_read_input_tokens") ?? _cached;
            _written = LlmJson.GetInt(usage, "cache_creation_input_tokens") ?? _written;
            // message_start carries a placeholder output count, not generated output.
            if (!start) { _output = LlmJson.GetInt(usage, "output_tokens") ?? _output; }
        }
    }
}
