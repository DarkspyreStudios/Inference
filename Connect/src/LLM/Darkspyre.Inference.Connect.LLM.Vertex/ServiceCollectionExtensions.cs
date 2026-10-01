using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

public static class ServiceCollectionExtensions
{
    /// <summary>Adds the Google Vertex AI Gemini/Claude provider and its managed agent runtime.</summary>
    public static IServiceCollection AddLlmConnectVertex(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(VertexProviderDefinition.Value);
        services.AddSingleton<IVertexAuthentication, VertexAuthentication>();
        services.AddSingleton<ILlmSetupEnvironment, VertexSetupEnvironment>();
        services.AddHttpClient<VertexLlmClient>()
            .ConfigureHttpClient(client => client.Timeout = ProviderHttp.GenerationTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<VertexLlmClient>());
        services.AddTransient<ILlmAgentClient, ManagedVertexAgentClient>();
        return services;
    }
}

internal sealed class ManagedVertexAgentClient(VertexLlmClient client, LlmProviderService providers) : ILlmAgentClient
{
    public LlmTransport Transport => LlmTransport.GoogleVertexAi;
    public async Task<ILlmAgentSession> StartSessionAsync(string connectionId, LlmConnection connection,
        LlmAgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Context is not null) { ManagedAgentContext.ValidateRuntimeResources(options.Context); }
        HostedTools.Validate(options.HostedTools, options.MaxHostedTurns, client.GetCapabilities(connection, options.ModelId).HostedTools);
        ClientTools.ValidateManagedOpening(options, client.GetCapabilities(connection, options.ModelId));
        var capabilities = client.GetCapabilities(connection, options.ModelId);
        if (!capabilities.SupportsAgentSessions || options.Tools.Any(tool => tool is LlmAgentToolDefinition) && !capabilities.SupportsAgentToolUse)
        { throw new NotSupportedException("The selected Vertex model disables the requested agent operation."); }
        connection = await client.PrepareAsync(connection, cancellationToken).ConfigureAwait(false);
        var models = await providers.ListModelsAsync(connectionId, cancellationToken).ConfigureAwait(false);
        string scope = ProviderContinuationStateCodec.Scope(connection);
        return new ManagedAgentSession(connection, options, models, client,
            (request, token) => providers.StreamAgentAsync(connectionId, request, scope, token),
            model => client.GetCapabilities(connection, model));
    }
}
