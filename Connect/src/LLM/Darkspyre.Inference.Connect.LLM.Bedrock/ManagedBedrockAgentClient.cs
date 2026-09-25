using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

/// <summary>Bedrock Converse registration for the provider-neutral live engine.</summary>
internal sealed class ManagedBedrockAgentClient(BedrockLlmClient client, LlmProviderService providers)
    : ILlmAgentClient, IConnectionBoundAgentClient
{
    public LlmTransport Transport => LlmTransport.AwsBedrockConverse;

    public Task<ILlmAgentSession> StartSessionAsync(
        LlmConnection connection, LlmAgentSessionOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Open managed sessions through LlmAgentProviderService so each model step can resolve and refresh the host connection.");

    public async Task<ILlmAgentSession> StartSessionAsync(
        string connectionId, LlmConnection connection, LlmAgentSessionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var capabilities = client.GetCapabilities(connection, options.ModelId);
        if (!capabilities.SupportsAgentSessions || (options.Tools.Count > 0 && !capabilities.SupportsAgentToolUse))
        {
            throw new NotSupportedException("The selected model/connection disables the requested agent operation.");
        }
        var models = await providers.ListModelsAsync(connectionId, cancellationToken);
        string scope = ProviderResponseStateCodec.Scope(connection);
        return new ManagedAgentSession(connection, options, models, client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model));
    }

    public Task<ILlmAgentSession> ResumeSessionAsync(
        LlmConnection connection, string threadId, LlmAgentResumeOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Managed sessions reopen with StartSessionAsync and host-owned Context/ThreadId, not a provider resume handle.");

    public Task<ILlmAgentSession> ForkSessionAsync(
        LlmConnection connection, string threadId, string? lastTurnId = null,
        LlmAgentResumeOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Fork a complete host context prefix and start it with a new ThreadId.");
}
