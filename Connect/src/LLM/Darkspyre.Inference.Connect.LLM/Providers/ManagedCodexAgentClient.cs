using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Codex registration for the provider-neutral live engine.</summary>
internal sealed class ManagedCodexAgentClient(OpenAiCodexLlmClient client, LlmProviderService providers,
    LearnedModelContext learned)
    : ILlmAgentClient
{
    public LlmTransport Transport => LlmTransport.OpenAiCodex;

    public async Task<ILlmAgentSession> StartSessionAsync(
        string connectionId, LlmConnection connection, LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var opening = providers.EnterManagedOpening(cancellationToken);
        void Fence() => providers.FenceManagedOpening(opening.Token);
        Fence();
        options = ClientTools.Snapshot(options);
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
        }
        connection = await providers.PrepareManagedConnectionAsync(connectionId, connection, client, Preflight, opening.Token).ConfigureAwait(false);
        Fence();
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return await ManagedAgentSession.CreateAsync(connection, options, connection.Catalog ?? [], client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model), learned, openingFence: Fence,
            cancellationToken: opening.Token).ConfigureAwait(false);
    }
}
