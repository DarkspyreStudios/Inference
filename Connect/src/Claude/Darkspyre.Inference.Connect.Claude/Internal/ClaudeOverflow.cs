namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Recognizes the Claude CLI's report that the input is longer than the model's context.</summary>
internal static class ClaudeOverflow
{
    /// <summary>True when a turn error's detail says the prompt is too long.</summary>
    internal static bool IsContextOverflow(string? detail) =>
        detail?.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase) == true;
}
