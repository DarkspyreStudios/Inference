using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>The published context window and maximum output of one Claude model. A null value is unknown.</summary>
internal sealed record ClaudeModelLimit(long? ContextTokens, long? OutputTokens);

/// <summary>
/// Context and output limits Anthropic publishes for Claude models, keyed by Anthropic model id. Every route to Claude
/// that reports no sizes of its own reads them here: Bedrock and the Claude CLI. A dated id shares its model's entry, a
/// <c>[1m]</c> alias has the 1M-token window, and a model that is not listed has unknown limits.
/// </summary>
internal static partial class ClaudeModelLimits
{
    // Sources:
    // [B] Claude on Amazon Bedrock, "Context window":
    //     https://platform.claude.com/docs/en/build-with-claude/claude-on-amazon-bedrock-legacy#context-window
    //     Fable 5.1, Fable 5, Opus 5.5, Opus 5, Opus 4.8, Opus 4.7, Opus 4.6, Sonnet 5.5, Sonnet 5 and Sonnet 4.6
    //     have a 1M-token context window on Amazon Bedrock; other Claude models, including Sonnet 4.5 and
    //     Sonnet 4, have 200k.
    // [C] Context windows, "Context window sizes by model":
    //     https://platform.claude.com/docs/en/build-with-claude/context-windows#context-window-sizes-by-model
    //     Every 1M-token model, Mythos 5.1, Mythos 5 and Mythos Preview included, generates up to 128k output tokens.
    // [M] Models overview: https://platform.claude.com/docs/en/models/overview (Haiku 4.5: 200K, 64K output).
    // [O] Claude Opus 4.5: https://platform.claude.com/docs/en/models/opus-4-5/overview (200K, 64K output).
    // [S] Claude Sonnet 4.5: https://platform.claude.com/docs/en/models/sonnet-4-5/overview (200K, 64K output).
    private static readonly FrozenDictionary<string, ClaudeModelLimit> Models = new Dictionary<string, ClaudeModelLimit>
    {
        ["claude-fable-5-1"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-fable-5"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-mythos-5-1"] = new(1_000_000, 128_000), // [C]
        ["claude-mythos-5"] = new(1_000_000, 128_000), // [C]
        ["claude-mythos-preview"] = new(1_000_000, 128_000), // [C]
        ["claude-opus-5-5"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-opus-5"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-opus-4-8"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-opus-4-7"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-opus-4-6"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-sonnet-5-5"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-sonnet-5"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-sonnet-4-6"] = new(1_000_000, 128_000), // [B] [C]
        ["claude-opus-4-5"] = new(200_000, 64_000), // [B] [O]
        ["claude-sonnet-4-5"] = new(200_000, 64_000), // [B] [S]
        ["claude-haiku-4-5"] = new(200_000, 64_000), // [B] [M]
        ["claude-sonnet-4"] = new(200_000, null), // [B]; no current Anthropic page states its output limit.
        ["claude-3-5-haiku"] = new(200_000, null) // [B]; no current Anthropic page states its output limit.
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly ClaudeModelLimit Unknown = new(null, null);

    /// <summary>The limits of an Anthropic model id, a dated id or a <c>[1m]</c> alias. An unlisted model is unknown.</summary>
    public static ClaudeModelLimit Get(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        bool million = modelId.EndsWith("[1m]", StringComparison.Ordinal);
        string key = DatedSuffix().Replace(million ? modelId[..^4] : modelId, string.Empty);
        if (!Models.TryGetValue(key, out ClaudeModelLimit? limit)) return million ? new(1_000_000, null) : Unknown;
        return million ? limit with { ContextTokens = 1_000_000 } : limit;
    }

    [GeneratedRegex(@"-\d{8}$")]
    private static partial Regex DatedSuffix();
}
