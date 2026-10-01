using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

/// <summary>Bedrock Converse registration for the provider-neutral live engine.</summary>
internal sealed class ManagedBedrockAgentClient(BedrockLlmClient client, LlmProviderService providers)
    : ILlmAgentClient
{
    public LlmTransport Transport => LlmTransport.AwsBedrockConverse;

    public async Task<ILlmAgentSession> StartSessionAsync(
        string connectionId, LlmConnection connection, LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, client.GetCapabilities(connection, options.ModelId).HostedTools);
        var capabilities = client.GetCapabilities(connection, options.ModelId);
        if (!capabilities.SupportsAgentSessions || (options.Tools.Count > 0 && !capabilities.SupportsAgentToolUse))
        {
            throw new NotSupportedException("The selected model/connection disables the requested agent operation.");
        }
        var models = await providers.ListModelsAsync(connectionId, cancellationToken);
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return new ManagedAgentSession(connection, options, models, client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model));
    }
}
