using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Codex registration for the provider-neutral live engine.</summary>
internal sealed class ManagedCodexAgentClient(OpenAiCodexLlmClient client, LlmProviderService providers)
    : ILlmAgentClient
{
    public LlmTransport Transport => LlmTransport.OpenAiCodex;

    public async Task<ILlmAgentSession> StartSessionAsync(
        string connectionId, LlmConnection connection, LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        options = ClientTools.Snapshot(options);
        if (options.Context is not null) { ManagedAgentContext.ValidateRuntimeResources(options.Context); }
        HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, client.GetCapabilities(connection, options.ModelId).HostedTools);
        ClientTools.ValidateManagedOpening(options, client.GetCapabilities(connection, options.ModelId));
        // The capability checks below and the session's per-model lookups read the Codex model catalog.
        connection = await providers.AttachCatalogAsync(connectionId, connection, client, cancellationToken);
        var capabilities = client.GetCapabilities(connection, options.ModelId);
        if (!capabilities.SupportsAgentSessions || (options.Tools.Any(tool => tool is LlmAgentToolDefinition) && !capabilities.SupportsAgentToolUse))
        {
            throw new NotSupportedException("The selected model/connection disables the requested agent operation.");
        }
        // Metadata is optional, never invented. Discovery and generation share the existing OAuth path.
        var models = await providers.ListModelsAsync(connectionId, cancellationToken);
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return new ManagedAgentSession(connection, options, models, client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model));
    }
}
