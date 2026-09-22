namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// Reports whether authentication managed outside Darkspyre.Inference.Connect.LLM is ready enough to attempt a request.
/// </summary>
internal interface IExternalLlmAuthenticationProbe
{
    Task<bool> IsAuthenticatedAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default);
}
