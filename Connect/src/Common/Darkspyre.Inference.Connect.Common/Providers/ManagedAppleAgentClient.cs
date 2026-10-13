using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class ManagedAppleAgentClient(AppleFoundationModelsClient client, LlmProviderService providers) : ILlmAgentClient
{
    public LlmTransport Transport => LlmTransport.AppleFoundationModels;

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
        AppleProtocol.Ready(await client.ProbeAsync(connection, cancellationToken).ConfigureAwait(false));
        var models = await providers.ListModelsAsync(connectionId, cancellationToken).ConfigureAwait(false);
        var capabilities = client.GetCapabilities(connection, options.ModelId);
        if ((capabilities.SupportsAgentSessions == InferenceSupport.Unsupported) || (options.Tools.Any(tool => tool is LlmAgentToolDefinition) && (capabilities.SupportsAgentToolUse == InferenceSupport.Unsupported)))
            throw new NotSupportedException("The Apple connection disables this agent operation.");
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return await ManagedAgentSession.CreateAsync(connection, options, models, client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model), cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
