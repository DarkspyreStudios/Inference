using System.Globalization;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>The Grok Build CLI's thinking-channel repetition check and reminder-only resampling.</summary>
internal static class XaiRepetitionCheck
{
    internal const string Reminder = "<system_reminder>Your messages have been flagged as looping. Your response has been flagged as repeating the same text pattern. Avoid excessive repetition. If you are having trouble ask the user for guidance.</system_reminder>";

    internal static void Apply(HttpRequestMessage message)
    {
        message.Headers.TryAddWithoutValidation("x-grok-doom-loop-check", "1024");
        message.Headers.TryAddWithoutValidation("x-grok-exact-repetition-check", "64");
    }

    internal static bool IsConfident(JsonElement holder)
    {
        if (LlmJson.GetObject(holder, "doom_loop_check") is not { } check
            || !check.TryGetProperty("triggers", out JsonElement triggers) || triggers.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement trigger in triggers.EnumerateArray())
        {
            if (trigger.ValueKind != JsonValueKind.String) continue;
            string label = trigger.GetString()!;
            const string prefix = "tail_repetition:";
            const string suffix = "@thinking";
            if (label.StartsWith(prefix, StringComparison.Ordinal) && label.EndsWith(suffix, StringComparison.Ordinal)
                && int.TryParse(label.AsSpan(prefix.Length, label.Length - prefix.Length - suffix.Length),
                    NumberStyles.None, CultureInfo.InvariantCulture, out int threshold) && threshold <= 64) return true;
        }
        return false;
    }
}
