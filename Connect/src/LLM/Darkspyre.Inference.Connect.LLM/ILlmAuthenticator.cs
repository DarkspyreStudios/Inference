namespace Darkspyre.Inference.Connect.LLM;

public interface ILlmAuthenticator
{
    LlmTransport Transport { get; }

    Task<LlmCredential> EnsureFreshAsync(
        LlmCredential credential,
        CancellationToken cancellationToken = default);
}

public interface ICodexDeviceLogin : ILlmAuthenticator
{
    Task<LlmDeviceCode> StartAsync(CancellationToken cancellationToken = default);

    Task<LlmCredential> CompleteAsync(
        string deviceAuthId,
        string userCode,
        CancellationToken cancellationToken = default);
}

public interface IClaudePkceLogin : ILlmAuthenticator
{
    LlmAuthorizationSession Start(string redirectUri);

    Task<LlmCredential> CompleteAsync(
        LlmAuthorizationSession session,
        string code,
        string state,
        CancellationToken cancellationToken = default);
}

/// <summary>Grok's RFC 8628 device grant. Completion performs one token poll.</summary>
public interface IXaiDeviceLogin : ILlmAuthenticator
{
    Task<LlmDeviceCode> StartAsync(CancellationToken cancellationToken = default);

    Task<LlmCredential> CompleteAsync(
        string deviceAuthId,
        string userCode,
        CancellationToken cancellationToken = default);
}
