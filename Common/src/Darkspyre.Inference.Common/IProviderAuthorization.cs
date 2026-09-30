namespace Darkspyre.Inference.Common;

/// <summary>Acquires current authorization for one provider operation without exposing credential storage.</summary>
public interface IProviderAuthorization
{
    /// <summary>Returns the current header. Its value is a secret, not diagnostic or persistent data.</summary>
    ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>An operation's authorization scheme and secret value.</summary>
public sealed record ProviderAuthorizationHeader(string Scheme, string Value)
{
    /// <summary>Returns a diagnostic description without either header field.</summary>
    public override string ToString() => "ProviderAuthorizationHeader { Redacted }";
}
