using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Binds OAuth authorization to one connection and borrows its credential-access owner.</summary>
public sealed class LlmConnectionAuthorization : IProviderAuthorization
{
    private readonly string _connectionId;
    private readonly LlmConnectionAccess _access;

    public LlmConnectionAuthorization(string connectionId, LlmConnectionAccess access)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        _connectionId = connectionId;
        _access = access ?? throw new ArgumentNullException(nameof(access));
    }

    public async ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default)
    {
        LlmConnection connection = await _access.AcquireAsync(_connectionId, cancellationToken);
        string? token = LlmCredential.Parse(connection.Credential)?.OAuth?.AccessToken;
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("The connection requires an OAuth sign-in.");
        return new("Bearer", token);
    }
}
