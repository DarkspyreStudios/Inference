namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Whether a provider that signs in outside Darkspyre.Inference.Connect.LLM has its tool and an active sign-in.</summary>
internal enum ExternalAuthenticationState
{
    /// <summary>The provider's tool could not be found or started.</summary>
    NotInstalled,
    /// <summary>The tool runs, but it has no usable sign-in.</summary>
    SignedOut,
    /// <summary>The tool runs and reports a usable sign-in.</summary>
    SignedIn
}

/// <summary>
/// Reports whether authentication managed outside Darkspyre.Inference.Connect.LLM is ready enough to attempt a request.
/// </summary>
internal interface IExternalLlmAuthenticationProbe
{
    /// <summary>Checks the provider's tool and sign-in for <paramref name="connection"/>.</summary>
    Task<ExternalAuthenticationState> CheckAuthenticationAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default);
}
