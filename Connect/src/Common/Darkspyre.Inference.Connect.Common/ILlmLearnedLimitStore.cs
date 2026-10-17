namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Saves the context limits too-long refusals teach, so a later process starts with them. The host that owns
/// connections implements it; <see cref="LlmConnection.LearnedContextLimits"/> carries the saved limits back.
/// </summary>
/// <gotcha>Without a registered store, learned limits last only for the process.</gotcha>
public interface ILlmLearnedLimitStore
{
    /// <summary>Saves <paramref name="tokens"/> as the context limit of <paramref name="modelId"/> on the connection.</summary>
    Task SaveContextLimitAsync(string connectionId, string modelId, long tokens, CancellationToken cancellationToken = default);
}
