using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Registers the LLM connection services in a dependency injection container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers LLM connections with an explicit HTTP creation and logging policy.</summary>
    public static IServiceCollection AddLlmConnect(this IServiceCollection services, Action<InferenceHttpPipelineOptions> configureHttp)
    {
        services.ConfigureInferenceHttpPipelines(configureHttp);
        return services.AddLlmConnect();
    }

    /// <summary>Configures the finite SDK HTTP pipelines before building the service provider.</summary>
    public static IServiceCollection ConfigureInferenceHttpPipelines(this IServiceCollection services, Action<InferenceHttpPipelineOptions> configureHttp)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureHttp);
        SdkHttpConfiguration configuration = SdkHttpRegistration.Register(services);
        var options = new InferenceHttpPipelineOptions();
        configureHttp(options);
        options.Apply(services, configuration);
        return services;
    }

    /// <summary>
    /// Discovers deployed provider libraries and registers their clients and the shared facades. The caller supplies an
    /// <see cref="ILlmConnectionSource"/> implementation.
    /// </summary>
    /// <gotcha>Call once per service collection. SelfHost requires caller-owned ILlmSelfHost when included.</gotcha>
    public static IServiceCollection AddLlmConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<LlmProviderCatalog>();
        services.AddSingleton<LearnedModelContext>();
        services.AddSingleton<ILlmLearnedModelFacts>(provider => provider.GetRequiredService<LearnedModelContext>());
        services.AddLogging();
        services.AddSingleton<LlmModelListCache>();
        services.AddSingleton<LlmAccountUsageObservations>();
        // Reflection-based activation sees only public constructors, and these take internal adapter interfaces.
        services.AddScoped(provider => new LlmConnectionAccess(
            provider.GetRequiredService<ILlmConnectionSource>(), provider.GetServices<ILlmAuthenticator>(),
            provider.GetServices<ILlmClient>()));
        // AWS Bedrock, Google Vertex AI and Azure OpenAI use the ILlmSetupEnvironment services; they are out of the build
        // (Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Bedrock, .LLM.Vertex and .LLM.Azure, outside the solution). It
        // moves into their own libraries when they return to the build.
        services.AddScoped(provider => new LlmProviderService(
            provider.GetRequiredService<ILlmConnectionSource>(), provider.GetServices<ILlmClient>(),
            provider.GetServices<ILlmAuthenticator>(), provider.GetServices<ILlmSetupEnvironment>(),
            provider.GetRequiredService<LlmModelListCache>(), provider.GetRequiredService<LlmAccountUsageObservations>(),
            provider.GetRequiredService<LlmConnectionAccess>()));
        services.AddScoped(provider => new LlmAgentProviderService(
            provider.GetRequiredService<ILlmConnectionSource>(), provider.GetServices<ILlmAgentClient>(),
            provider.GetRequiredService<LlmProviderService>(), provider.GetRequiredService<LlmAccountUsageObservations>()));
        services.AddScoped(provider => new LlmEmbeddingProviderService(
            provider.GetRequiredService<ILlmConnectionSource>(), provider.GetServices<ILlmEmbeddingClient>(),
            provider.GetServices<ILlmAuthenticator>(), provider.GetRequiredService<LlmConnectionAccess>()));
        LlmConnectDiscovery.RegisterServices(services);
        return services;
    }

}
