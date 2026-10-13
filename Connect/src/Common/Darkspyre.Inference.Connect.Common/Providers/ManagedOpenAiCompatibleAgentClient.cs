using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Configured compatible Chat registration for the provider-neutral managed engine.</summary>
internal sealed class ManagedOpenAiCompatibleAgentClient(OpenAiCompatibleChatLlmClient client, LlmProviderService providers)
    : ILlmAgentClient
{
    public LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;

    public async Task<ILlmAgentSession> StartSessionAsync(
        string connectionId, LlmConnection connection, LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        options = ClientTools.Snapshot(options);
        ProviderControlPolicy.Validate(connection, new() { ModelId = options.ModelId });
        if (options.Context is not null) { ManagedAgentContext.ValidateRuntimeResources(options.Context); }
        HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, client.GetCapabilities(connection, options.ModelId).HostedTools);
        ClientTools.ValidateManagedOpening(options, client.GetCapabilities(connection, options.ModelId));
        connection = await client.PrepareAsync(connection, cancellationToken).ConfigureAwait(false);
        connection = await providers.AttachCatalogAsync(connectionId, connection, client, cancellationToken).ConfigureAwait(false);
        var capabilities = client.GetCapabilities(connection, options.ModelId);
        if ((capabilities.SupportsAgentSessions == InferenceSupport.Unsupported) || (options.Tools.Any(tool => tool is LlmAgentToolDefinition) && (capabilities.SupportsAgentToolUse == InferenceSupport.Unsupported)))
        {
            throw new NotSupportedException("The selected model/connection disables the requested agent operation.");
        }
        var models = await providers.ListModelsAsync(connectionId, cancellationToken).ConfigureAwait(false);
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return await ManagedAgentSession.CreateAsync(connection, options, models, client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model), cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
