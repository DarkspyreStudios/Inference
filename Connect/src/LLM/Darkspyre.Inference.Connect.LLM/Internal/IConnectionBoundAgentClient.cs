namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Internal binding to the existing generation facade; no new public storage seam.</summary>
internal interface IConnectionBoundAgentClient
{
    Task<ILlmAgentSession> StartSessionAsync(
        string connectionId,
        LlmConnection connection,
        LlmAgentSessionOptions options,
        CancellationToken cancellationToken);
}
