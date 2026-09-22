namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Host seam that resolves metadata plus a short-lived protected credential.</summary>
public interface ILlmConnectionSource
{
    Task<LlmConnection?> GetConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces only the credential when the connection still matches
    /// <paramref name="expectedVersion"/>. The host must reject missing, replaced, or concurrently
    /// changed connections; this operation must never upsert.
    /// </summary>
    Task SaveCredentialAsync(
        string connectionId,
        string credentialJson,
        string expectedVersion,
        CancellationToken cancellationToken = default);
}
