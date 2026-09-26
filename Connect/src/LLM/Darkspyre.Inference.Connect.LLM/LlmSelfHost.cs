using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>One engine the host runs in its own process, as a SelfHost connection names it.</summary>
public sealed record LlmSelfHostEngine(string Id, string DisplayName);

/// <summary>
/// The host's in-process engines. A <see cref="LlmTransport.SelfHost"/> connection stores an engine ID in
/// its endpoint; several connections can name one engine. The host owns each engine's lifetime.
/// </summary>
public interface ILlmSelfHost
{
    /// <summary>Lists the engines a SelfHost connection can name.</summary>
    Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the loaded engine. Throws <see cref="Common.InferenceException"/> with
    /// <see cref="Common.InferenceFailureCode.ModelUnavailable"/> when the engine is unknown or not loaded.
    /// </summary>
    Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The capabilities the loaded engine declared for <paramref name="modelId"/>, which the host captures at
    /// load; null when the engine is not loaded. This reads the host's own records and does no I/O.
    /// </summary>
    LlmCapabilities? GetCapabilities(string engineId, string? modelId);
}
