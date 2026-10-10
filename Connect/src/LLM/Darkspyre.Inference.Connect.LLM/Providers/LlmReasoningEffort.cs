using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// The lower-case effort vocabulary that the OpenAI, Anthropic and CLI providers share. Each adapter
/// still declares which levels it offers for each model; this type only spells them.
/// </summary>
internal static class LlmReasoningEffort
{
    public static IReadOnlySet<LlmReasoningLevel> Levels(params LlmReasoningLevel[] levels) => levels.ToFrozenSet();

    /// <summary>The provider spelling of a level above <see cref="LlmReasoningLevel.None"/>.</summary>
    public static string Word(LlmReasoningLevel level) => level switch
    {
        LlmReasoningLevel.Minimal => "minimal",
        LlmReasoningLevel.Low => "low",
        LlmReasoningLevel.Medium => "medium",
        LlmReasoningLevel.High => "high",
        LlmReasoningLevel.ExtraHigh => "xhigh",
        LlmReasoningLevel.Maximum => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "The level has no effort word.")
    };

    /// <summary>The OpenAI <c>reasoning.effort</c> spelling. Unlike <see cref="Word"/>, it spells None as <c>none</c>.</summary>
    public static string OpenAiWord(LlmReasoningLevel level) => level == LlmReasoningLevel.None ? "none" : Word(level);

    /// <summary>
    /// The offered level closest to requested: the highest offered level at or below it, else the lowest
    /// offered level above it. Returns requested when offered is empty.
    /// </summary>
    public static LlmReasoningLevel? Nearest(LlmReasoningLevel? requested, IReadOnlySet<LlmReasoningLevel> offered)
    {
        if (requested is not { } level || offered.Count == 0) return requested;
        LlmReasoningLevel[] below = [.. offered.Where(candidate => candidate <= level)];
        return below.Length > 0 ? below.Max() : offered.Where(candidate => candidate > level).Min();
    }
}
