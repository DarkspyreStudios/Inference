namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>The request values a service refused as unsupported, per connection and model.</summary>
[Flags]
internal enum LlmRefusedSettings
{
    /// <summary>The refusal named no value the provider can leave out.</summary>
    None = 0,

    /// <summary>The temperature.</summary>
    Temperature = 1,

    /// <summary>The reasoning object.</summary>
    Reasoning = 2
}

/// <summary>Reads an "unsupported" refusal: OpenAI's code and param, or a message naming the value (OpenRouter, inferred).</summary>
internal static class LlmUnsupportedSetting
{
    /// <summary>The values an HTTP 400 refusal names as unsupported; None for any other refusal.</summary>
    internal static LlmRefusedSettings Read(string? code, string? param, string? message)
    {
        if (code is "unsupported_parameter" or "unsupported_value")
        {
            return param switch
            {
                "temperature" => LlmRefusedSettings.Temperature,
                not null when param.StartsWith("reasoning", StringComparison.Ordinal) => LlmRefusedSettings.Reasoning,
                _ => LlmRefusedSettings.None
            };
        }
        if (message is null
            || !(message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
                || message.Contains("not supported", StringComparison.OrdinalIgnoreCase)))
        {
            return LlmRefusedSettings.None;
        }
        LlmRefusedSettings named = LlmRefusedSettings.None;
        if (message.Contains("temperature", StringComparison.OrdinalIgnoreCase)) named |= LlmRefusedSettings.Temperature;
        if (message.Contains("reasoning", StringComparison.OrdinalIgnoreCase)) named |= LlmRefusedSettings.Reasoning;
        return named;
    }
}
