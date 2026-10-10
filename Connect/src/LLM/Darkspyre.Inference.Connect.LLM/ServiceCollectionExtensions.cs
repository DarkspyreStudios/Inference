using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM;

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
    /// Registers transport clients and the provider facade. The host must separately register an
    /// <see cref="ILlmConnectionSource"/> implementation.
    /// </summary>
    public static IServiceCollection AddLlmConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(ProviderDefinitions.ClaudeSubscription);
        services.AddSingleton(ProviderDefinitions.ChatGptSubscription);
        services.AddSingleton<LlmProviderCatalog>();
        // What reported counts and refusals taught about each model, shared by every session on that model.
        services.AddSingleton<LearnedModelContext>();
        services.AddInferenceHttpClient<ClaudeCliLlmClient>();
        services.AddInferenceHttpClient<OpenAiEmbeddingClient>();
        services.AddLogging();
        // The container's disposal waits for every Claude CLI still finishing in the background.
        services.AddSingleton<ClaudeCliExits>();
        services.AddSingleton<IClaudeCliRunner, ClaudeCliProcessRunner>();
        services.AddSingleton<IClaudeCliSessionProcessFactory, ClaudeCliSessionProcessFactory>();
        services.AddTransient<ILlmClient>(provider =>
            provider.GetRequiredService<ClaudeCliLlmClient>());
        services.AddInferenceHttpClient<OpenAiCodexLlmClient>();
        services.AddTransient<ILlmClient>(provider =>
            provider.GetRequiredService<OpenAiCodexLlmClient>());
        services.AddTransient<ManagedCodexAgentClient>();
        services.AddTransient<ILlmAgentClient>(provider =>
            provider.GetRequiredService<ManagedCodexAgentClient>());
        services.AddTransient<ClaudeAgentClient>();
        services.AddTransient<ILlmAgentClient>(provider =>
            provider.GetRequiredService<ClaudeAgentClient>());
        services.AddInferenceHttpClient<CodexAuthenticator>();
        services.AddInferenceHttpClient<ClaudeAuthenticator>();
        services.AddTransient<ILlmAuthenticator>(provider =>
            provider.GetRequiredService<CodexAuthenticator>());
        services.AddTransient<ILlmAuthenticator>(provider =>
            provider.GetRequiredService<ClaudeAuthenticator>());
        services.AddTransient<ILlmEmbeddingClient>(provider =>
            provider.GetRequiredService<OpenAiEmbeddingClient>());
        services.AddSingleton<LlmModelListCache>();
        services.AddSingleton<LlmAccountUsageObservations>();
        // Reflection-based activation sees only public constructors, and these take internal adapter interfaces.
        services.AddScoped(provider => new LlmConnectionAccess(
            provider.GetRequiredService<ILlmConnectionSource>(), provider.GetServices<ILlmAuthenticator>(),
            provider.GetServices<ILlmClient>()));
        services.AddScoped(provider => new LlmProviderService(
            provider.GetRequiredService<ILlmConnectionSource>(), provider.GetServices<ILlmClient>(),
            provider.GetServices<ILlmAuthenticator>(), provider.GetServices<ILlmSetupEnvironment>(),
            provider.GetRequiredService<LlmModelListCache>(), provider.GetRequiredService<LlmAccountUsageObservations>(),
            provider.GetRequiredService<LlmConnectionAccess>()));
        services.AddScoped(provider => new LlmAgentProviderService(
            provider.GetRequiredService<ILlmConnectionSource>(), provider.GetServices<ILlmAgentClient>(),
            provider.GetRequiredService<LlmAccountUsageObservations>()));
        services.AddScoped(provider => new LlmEmbeddingProviderService(
            provider.GetRequiredService<ILlmConnectionSource>(), provider.GetServices<ILlmEmbeddingClient>(),
            provider.GetServices<ILlmAuthenticator>(), provider.GetRequiredService<LlmConnectionAccess>()));
        return services;
    }
}
