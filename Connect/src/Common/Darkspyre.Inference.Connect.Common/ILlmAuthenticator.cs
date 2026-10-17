namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Refreshes stored OAuth credentials for one LLM transport.</summary>
public interface ILlmAuthenticator
{
    /// <summary>The transport whose credentials this authenticator refreshes.</summary>
    LlmTransport Transport { get; }

    /// <summary>Returns the credential unchanged when its OAuth tokens are still fresh, or refreshes them with the refresh token. Throws when the tokens have expired and no refresh token exists.</summary>
    Task<LlmCredential> EnsureFreshAsync(
        LlmCredential credential,
        CancellationToken cancellationToken = default);
}

/// <summary>Signs in to a subscription with the OAuth device-code flow.</summary>
/// <remarks><see cref="LlmProviderService"/> finds the device login for a transport by its <see cref="ILlmAuthenticator.Transport"/>.</remarks>
public interface ILlmDeviceLogin : ILlmAuthenticator
{
    /// <summary>Requests a device login code and returns the verification URL and user code to show the user.</summary>
    Task<LlmDeviceCode> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Exchanges an approved device login for a credential. Throws <see cref="LlmAuthPendingException"/> while the browser approval is still pending, with <see cref="LlmAuthPendingException.SlowDown"/> set when the provider asks for slower polling.</summary>
    Task<LlmCredential> CompleteAsync(
        string deviceAuthId,
        string userCode,
        CancellationToken cancellationToken = default);
}

/// <summary>Signs in to Claude with the OAuth authorization-code flow and PKCE.</summary>
public interface IClaudePkceLogin : ILlmAuthenticator
{
    /// <summary>Creates an authorization session with a fresh state and code verifier for the given redirect URI.</summary>
    LlmAuthorizationSession Start(string redirectUri);

    /// <summary>Exchanges the authorization code for a credential. Throws when <paramref name="state"/> does not match the session state.</summary>
    Task<LlmCredential> CompleteAsync(
        LlmAuthorizationSession session,
        string code,
        string state,
        CancellationToken cancellationToken = default);
}
