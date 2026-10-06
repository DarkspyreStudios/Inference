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
        cancellationToken.ThrowIfCancellationRequested();
        options = ClientTools.Snapshot(options);
        ProviderControlPolicy.Validate(connection, new() { ModelId = options.ModelId });
        if (options.Context is { } context)
        {
            _ = ManagedAgentContext.ValidatePortable(context);
            if (options.CallerServices?.Resources is null) ManagedAgentContext.ValidateRuntimeResources(context);
        }
        var capabilities = client.GetCapabilities(connection, options.ModelId);
        HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, capabilities.HostedTools);
        ClientTools.ValidateManagedOpening(options, capabilities,
            supportsResources: client.CanPrepareResources && capabilities.SupportsPromptDocuments);
        if (!capabilities.SupportsAgentSessions || (options.Tools.Any(tool => tool is LlmAgentToolDefinition) && !capabilities.SupportsAgentToolUse))
        {
            throw new NotSupportedException("The selected model/connection disables the requested agent operation.");
        }
        var models = await providers.ListModelsAsync(connectionId, cancellationToken);
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return await ManagedAgentSession.CreateAsync(connection, options, models, client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model), cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
