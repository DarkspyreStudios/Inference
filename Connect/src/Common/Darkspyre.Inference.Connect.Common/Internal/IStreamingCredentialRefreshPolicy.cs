namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// Lets a client declare that its streaming path does not consume the stored OAuth access token.
/// Model discovery continues to use the normal refresh path.
/// </summary>
internal interface IStreamingCredentialRefreshPolicy
{
    bool RequiresFreshOAuthCredentialForStreaming(LlmConnection connection);
}
