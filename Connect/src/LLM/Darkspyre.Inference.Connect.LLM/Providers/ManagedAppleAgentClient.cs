using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class ManagedAppleAgentClient(AppleFoundationModelsClient client, LlmProviderService providers) : ILlmAgentClient
{
    public LlmTransport Transport => LlmTransport.AppleFoundationModels;

    public async Task<ILlmAgentSession> StartSessionAsync(string connectionId, LlmConnection connection,
        LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, client.GetCapabilities(connection, options.ModelId).HostedTools);
        AppleProtocol.Ready(await client.ProbeAsync(connection, cancellationToken).ConfigureAwait(false));
        var models = await providers.ListModelsAsync(connectionId, cancellationToken).ConfigureAwait(false);
        var capabilities = client.GetCapabilities(connection, options.ModelId);
        if (!capabilities.SupportsAgentSessions || (options.Tools.Count > 0 && !capabilities.SupportsAgentToolUse))
            throw new NotSupportedException("The Apple connection disables this agent operation.");
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return new ManagedAgentSession(connection, options, models, client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model));
    }
}
