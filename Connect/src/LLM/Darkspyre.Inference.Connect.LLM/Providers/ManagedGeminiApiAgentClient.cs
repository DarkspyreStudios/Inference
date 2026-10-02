using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Runs Gemini API agent sessions on the managed agent loop. The Gemini API has no native session resume.</summary>
internal sealed class ManagedGeminiApiAgentClient(GeminiApiLlmClient client, LlmProviderService providers) : ILlmAgentClient
{
    public LlmTransport Transport => LlmTransport.GoogleGeminiApi;

    public async Task<ILlmAgentSession> StartSessionAsync(string connectionId, LlmConnection connection,
        LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        options = ClientTools.Snapshot(options);
        ProviderControlPolicy.Validate(connection, new() { ModelId = options.ModelId });
        if (options.Context is not null) { ManagedAgentContext.ValidateRuntimeResources(options.Context); }
        HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, client.GetCapabilities(connection, options.ModelId).HostedTools);
        ClientTools.ValidateManagedOpening(options, client.GetCapabilities(connection, options.ModelId));
        connection = await client.PrepareAsync(connection, cancellationToken).ConfigureAwait(false);
        // The capability checks below and the session's per-model lookups read the provider catalog.
        connection = await providers.AttachCatalogAsync(connectionId, connection, client, cancellationToken).ConfigureAwait(false);
        var capabilities = client.GetCapabilities(connection, options.ModelId);
        if (!capabilities.SupportsAgentSessions || options.Tools.Any(tool => tool is LlmAgentToolDefinition) && !capabilities.SupportsAgentToolUse)
        { throw new NotSupportedException("The selected Gemini API model disables the requested agent operation."); }
        var models = await providers.ListModelsAsync(connectionId, cancellationToken).ConfigureAwait(false);
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return await ManagedAgentSession.CreateAsync(connection, options, models, client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model), cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
