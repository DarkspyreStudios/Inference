using Darkspyre.Inference.Common;
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
        using var opening = providers.EnterManagedOpening(cancellationToken);
        void Fence() => providers.FenceManagedOpening(opening.Token);
        Fence();
        options = ClientTools.Snapshot(options);
        Fence();
        void Preflight(LlmConnection selected)
        {
            if (options.Context is not null) { ManagedAgentContext.ValidateRuntimeResources(options.Context); }
            var capabilities = client.GetCapabilities(selected, options.ModelId);
            HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, capabilities.HostedTools);
            ClientTools.ValidateManagedOpening(options, capabilities);
            if ((capabilities.SupportsAgentSessions == InferenceSupport.Unsupported) || (options.Tools.Any(tool => tool is LlmAgentToolDefinition) && (capabilities.SupportsAgentToolUse == InferenceSupport.Unsupported)))
                throw new NotSupportedException("The selected model/connection disables the requested agent operation.");
        }
        connection = await providers.PrepareManagedConnectionAsync(connectionId, connection, client, Preflight, opening.Token).ConfigureAwait(false);
        Fence();
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return await ManagedAgentSession.CreateAsync(connection, options, connection.Catalog ?? [], client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model), openingFence: Fence,
            compactor: (request, token) => providers.CompactAgentAsync(connectionId, request, scope, token),
            cancellationToken: opening.Token).ConfigureAwait(false);
    }
}
