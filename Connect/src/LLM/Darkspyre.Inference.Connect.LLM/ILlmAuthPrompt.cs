namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Host-supplied interaction for <see cref="LlmProviderService.SignInAsync"/>. The library never
/// creates UI; it reports URLs and codes through this interface and accepts the user's result.
/// </summary>
public interface ILlmAuthPrompt
{
    /// <summary>Displays a Codex or Grok device code and verification URL.</summary>
    Task ShowDeviceCodeAsync(
        LlmDeviceCode code,
        CancellationToken cancellationToken = default);
}
