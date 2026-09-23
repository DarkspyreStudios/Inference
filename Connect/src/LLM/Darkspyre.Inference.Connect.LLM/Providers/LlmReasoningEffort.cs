using System.Collections.Frozen;
using Darkspyre.Inference.Common;
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

    /// <summary>Rejects a level the resolved model does not offer, before any provider call.</summary>
    public static void Validate(LlmReasoningLevel? level, LlmProviderCapabilities capabilities) =>
        Validate(level, capabilities.ReasoningLevels);

    /// <summary>Rejects a level outside <paramref name="offered"/>, before any provider call.</summary>
    public static void Validate(LlmReasoningLevel? level, IReadOnlySet<LlmReasoningLevel> offered)
    {
        if (level is { } requested && !offered.Contains(requested))
        {
            throw new InferenceException(
                InferenceFailureCode.InvalidRequest,
                offered.Count == 0
                    ? $"The selected model accepts no reasoning level; '{requested}' was requested."
                    : $"The selected model does not accept reasoning level '{requested}'. It accepts: "
                      + string.Join(", ", offered.Order()) + ".");
        }
    }
}
