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
    /// Registers the SelfHost transport. The host registers its own <see cref="ILlmSelfHost"/>, which
    /// resolves each engine a SelfHost connection names.
    /// </summary>
    public static IServiceCollection AddLlmConnectSelfHost(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddTransient<SelfHostLlmClient>();
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<SelfHostLlmClient>());
        services.AddTransient<ManagedSelfHostAgentClient>();
        services.AddTransient<ILlmAgentClient>(provider => provider.GetRequiredService<ManagedSelfHostAgentClient>());
        return services;
    }

    /// <summary>
    /// Registers transport clients and the provider facade. The host must separately register an
    /// <see cref="ILlmConnectionSource"/> implementation.
    /// </summary>
    public static IServiceCollection AddLlmConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(ProviderDefinitions.OpenAiApiKey);
        services.AddSingleton(ProviderDefinitions.OpenAiCompatible);
        services.AddSingleton(ProviderDefinitions.AnthropicApiKey);
        services.AddSingleton(ProviderDefinitions.ClaudeSubscription);
        services.AddSingleton(ProviderDefinitions.ChatGptSubscription);
        services.AddSingleton(ProviderDefinitions.GeminiSubscription);
        services.AddSingleton(ProviderDefinitions.GeminiApi);
        services.AddSingleton(ProviderDefinitions.AppleFoundationModels);
        services.AddSingleton<IAppleProcessRunner, AppleProcessRunner>();
        services.AddInferenceHttpClient<AppleFoundationModelsClient>();
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<AppleFoundationModelsClient>());
        services.AddTransient<ManagedAppleAgentClient>();
        services.AddTransient<ILlmAgentClient>(provider => provider.GetRequiredService<ManagedAppleAgentClient>());
        services.AddSingleton<LlmProviderCatalog>();
        services.AddInferenceHttpClient<GeminiApiLlmClient>()
            .ConfigureHttpClient(client => client.Timeout = ProviderHttp.GenerationTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<GeminiApiLlmClient>());
        services.AddTransient<ILlmAgentClient, ManagedGeminiApiAgentClient>();
        services.AddInferenceHttpClient<OpenAiResponsesLlmClient>()
            .ConfigureHttpClient(client => client.Timeout = ProviderHttp.GenerationTimeout);
        services.AddInferenceHttpClient<OpenAiCompatibleChatLlmClient>()
            .ConfigureHttpClient(client => client.Timeout = ProviderHttp.GenerationTimeout);
        services.AddInferenceHttpClient<AnthropicMessagesLlmClient>()
            .ConfigureHttpClient(client => client.Timeout = ProviderHttp.GenerationTimeout);
        services.AddInferenceHttpClient<ClaudeCliLlmClient>();
        services.AddInferenceHttpClient<OpenAiEmbeddingClient>();
        services.AddSingleton<IAntigravityCliSessionProcessFactory, AntigravityCliSessionProcessFactory>();
        services.AddSingleton<IAntigravityCliRunner, AntigravityCliProcessRunner>();
        services.AddSingleton<IClaudeCliRunner, ClaudeCliProcessRunner>();
        services.AddSingleton<IClaudeCliSessionProcessFactory, ClaudeCliSessionProcessFactory>();
        services.AddTransient<ILlmClient>(provider =>
            provider.GetRequiredService<OpenAiResponsesLlmClient>());
        services.AddTransient<ILlmClient>(provider =>
            provider.GetRequiredService<OpenAiCompatibleChatLlmClient>());
        services.AddTransient<ILlmClient>(provider =>
            provider.GetRequiredService<AnthropicMessagesLlmClient>());
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
        services.AddTransient<AntigravityCliLlmClient>();
        services.AddTransient<ILlmClient>(provider =>
            provider.GetRequiredService<AntigravityCliLlmClient>());
        services.AddTransient<AntigravityAgentClient>();
        services.AddTransient<ILlmAgentClient>(provider =>
            provider.GetRequiredService<AntigravityAgentClient>());
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
        services.AddScoped<LlmConnectionAccess>();
        services.AddScoped<LlmProviderService>();
        services.AddScoped<ILlmFileClient>(provider => provider.GetRequiredService<LlmProviderService>());
        services.AddScoped<IXaiFilePublicUrlClient>(provider => provider.GetRequiredService<LlmProviderService>());
        services.AddScoped<IXaiSkillClient>(provider => provider.GetRequiredService<LlmProviderService>());
        services.AddScoped<IDocumentSearchClient>(provider => provider.GetRequiredService<LlmProviderService>());
        services.AddScoped<LlmAgentProviderService>();
        services.AddScoped(provider => new LlmEmbeddingProviderService(
            provider.GetRequiredService<ILlmConnectionSource>(), provider.GetServices<ILlmEmbeddingClient>(),
            provider.GetServices<ILlmAuthenticator>(), provider.GetRequiredService<LlmConnectionAccess>(),
            provider.GetRequiredService<LlmProviderService>()));
        return services;
    }
}
