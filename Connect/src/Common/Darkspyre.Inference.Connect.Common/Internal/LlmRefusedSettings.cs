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
