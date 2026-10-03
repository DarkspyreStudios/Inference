using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Opt-in OAuth Responses registration with provider-owned automatic compaction.</summary>
internal sealed class ManagedXaiAgentClient(XaiResponsesLlmClient client, LlmProviderService providers) : ILlmAgentClient
{
    public LlmTransport Transport => LlmTransport.XaiResponses;

    public async Task<ILlmAgentSession> StartSessionAsync(string connectionId, LlmConnection connection,
        LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var opening = providers.EnterManagedOpening(cancellationToken);
        void Fence() => providers.FenceManagedOpening(opening.Token);
        Fence();
        options = ClientTools.Snapshot(options);
        Fence();
        LlmChatRequest openingRequest = new()
        {
            ModelId = options.ModelId,
            Instructions = options.Instructions,
            Messages = options.Context?.Entries.Select(entry => entry.Message).ToArray() ?? [],
            Tools = ClientTools.Functions(options.Tools).Select(tool => (LlmClientToolDefinition)new LlmToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.InputSchema,
                Strict = tool.Strict
            }).ToArray(),
            HostedTools = options.HostedTools,
            MaxHostedTurns = options.MaxHostedTurns,
            Resources = options.CallerServices?.Resources,
            UnboundContinuationState = options.UnboundContinuationState
        };
        Fence();
        void Preflight(LlmConnection selected)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelId);
            if (options.ThreadId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(options.ThreadId);
            if (options.SessionId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(options.SessionId);
            if (!Enum.IsDefined(options.UnboundContinuationState))
                throw new ArgumentOutOfRangeException(nameof(options), "Select an unbound continuation state policy.");
            ProviderHttp.ValidateAdditionalHeaders(selected.AdditionalHeaders);
            if (options.Runtime is not null)
                throw new NotSupportedException("Managed Grok sessions do not accept provider runtime options.");
            if (options.Tools.Any(tool => tool is LlmAgentToolDefinition { DeferLoading: true }))
                throw new NotSupportedException("Managed Grok sessions do not implement deferred tool loading.");
            if (options.Context is { } context)
            {
                _ = ManagedAgentContext.ValidatePortable(context);
                if (options.CallerServices?.Resources is null)
                    ManagedAgentContext.ValidateRuntimeResources(context);
            }
            LlmProviderCapabilities capabilities = client.GetCapabilities(selected, options.ModelId);
            if (!capabilities.SupportsAgentSessions || (options.Tools.Count > 0 && !capabilities.SupportsAgentToolUse))
                throw new NotSupportedException("The selected model/connection disables the requested agent operation.");
            HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, capabilities.HostedTools);
            ClientTools.ValidateManagedOpening(options, capabilities, supportsResources: capabilities.SupportsPromptDocuments);
            AgentResourceOptions.Validate(capabilities.Agent, allowRuntimeBackgroundWork: false, options.ResourceOutputRetentionBytes);
            ManagedAgentPolicy.Validate(options.ExecutionOptions ?? new(), XaiCatalog.FindModel(selected.Catalog, options.ModelId));
            client.PreflightManagedOpeningRequest(selected, openingRequest);
            if (XaiCatalog.FindModel(selected.Catalog, options.ModelId) is not null)
                client.PreflightResourceRequest(selected, openingRequest);
        }
        connection = await providers.PrepareManagedConnectionAsync(connectionId, connection, client, Preflight, opening.Token).ConfigureAwait(false);
        Fence();
        client.PreflightResourceRequest(connection, openingRequest);
        Fence();
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return await ManagedAgentSession.CreateAsync(connection, options, connection.Catalog ?? [], client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model),
            openingFence: Fence,
            cancellationToken: opening.Token,
            compactor: (request, token) => providers.CompactAgentAsync(connectionId, request, scope, token),
            automaticProviderCompaction: true).ConfigureAwait(false);
    }
}
