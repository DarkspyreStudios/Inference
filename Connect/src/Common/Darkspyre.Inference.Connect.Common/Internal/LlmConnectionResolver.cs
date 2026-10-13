namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class LlmConnectionResolver
{
    public static async Task<LlmConnection> ResolveAsync(
        ILlmConnectionSource connections,
        string connectionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("A connection id is required.", nameof(connectionId));
        }

        LlmConnection? connection = await connections.GetConnectionAsync(
            connectionId,
            cancellationToken);
        if (connection is null)
        {
            throw new InvalidOperationException($"No LLM connection found for '{connectionId}'.");
        }

        if (string.IsNullOrWhiteSpace(connection.Version))
        {
            throw new InvalidOperationException(
                $"LLM connection '{connectionId}' did not supply a concurrency version.");
        }

        return connection;
    }
}
