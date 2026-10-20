using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// The common conversation opening for managed provider adapters and in-process engines. A client that implements
/// <see cref="ILlmProviderCompaction"/> compacts its sessions on the provider side for the models it reports.
/// </summary>
internal sealed class ManagedLlmAgentClient<TClient>(TClient client, LlmProviderService providers,
    LearnedModelContext learned) : ILlmAgentClient where TClient : ILlmClient, ILlmContextEstimator
{
    public LlmTransport Transport => client.Transport;

    public async Task<ILlmAgentSession> StartSessionAsync(
        string connectionId, LlmConnection connection, LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var opening = providers.EnterManagedOpening(cancellationToken);
        void Fence() => providers.FenceManagedOpening(opening.Token);
        Fence();
        options = AgentAbilityTools.Apply(ClientTools.Snapshot(options));
        Fence();
        void Preflight(LlmConnection selected)
        {
            if (options.Context is not null && options.CallerServices?.Resources is null)
                ManagedAgentContext.ValidateRuntimeResources(options.Context);
            var capabilities = client.GetCapabilities(selected, options.ModelId);
            HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, capabilities.HostedTools);
            ClientTools.ValidateManagedOpening(options, capabilities, supportsResources: true);
            if (capabilities.SupportsAgentSessions == InferenceSupport.Unsupported)
                throw new NotSupportedException(NativeAgentContract.AgentSessionsUnsupported);
            if (options.Tools.Any(tool => tool is LlmAgentToolDefinition) && (capabilities.SupportsAgentToolUse == InferenceSupport.Unsupported))
                throw new NotSupportedException(NativeAgentContract.AgentToolUseUnsupported);
            if (options.FallbackModelIds.Count > 0 && capabilities.Agent.SupportsFallbackModels == InferenceSupport.Unsupported)
                throw new NotSupportedException("The selected model does not support fallback models.");
        }
        connection = await providers.PrepareManagedConnectionAsync(connectionId, connection, client, Preflight, opening.Token).ConfigureAwait(false);
        Fence();
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return await ManagedAgentSession.CreateAsync(connectionId, connection, options, connection.Catalog ?? [], client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model), learned, openingFence: Fence,
            providerCompaction: client as ILlmProviderCompaction, retryPolicy: client as ILlmStepRetryPolicy,
            sessionHostedTools: client as ILlmSessionHostedTools, cancellationToken: opening.Token).ConfigureAwait(false);
    }
}
