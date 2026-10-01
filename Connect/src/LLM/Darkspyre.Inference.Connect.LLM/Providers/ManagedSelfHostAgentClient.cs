using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>SelfHost registration for the provider-neutral live engine: Connect runs the tool loop.</summary>
internal sealed class ManagedSelfHostAgentClient(SelfHostLlmClient client, LlmProviderService providers)
    : ILlmAgentClient
{
    public LlmTransport Transport => LlmTransport.SelfHost;

    public async Task<ILlmAgentSession> StartSessionAsync(
        string connectionId, LlmConnection connection, LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        options = ClientTools.Snapshot(options);
        if (options.Context is not null) { ManagedAgentContext.ValidateRuntimeResources(options.Context); }
        HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, client.GetCapabilities(connection, options.ModelId).HostedTools);
        ClientTools.ValidateManagedOpening(options, client.GetCapabilities(connection, options.ModelId));
        // Listing first surfaces an engine that is not loaded as ModelUnavailable before any turn.
        var models = await providers.ListModelsAsync(connectionId, cancellationToken);
        var capabilities = client.GetCapabilities(connection, options.ModelId);
        if (!capabilities.SupportsAgentSessions || (options.Tools.Any(tool => tool is LlmAgentToolDefinition) && !capabilities.SupportsAgentToolUse))
        {
            throw new NotSupportedException("The selected engine or model disables the requested agent operation.");
        }
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return new ManagedAgentSession(connection, options, models, client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model));
    }
}
