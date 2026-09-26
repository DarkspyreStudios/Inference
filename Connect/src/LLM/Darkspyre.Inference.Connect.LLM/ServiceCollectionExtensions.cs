using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM;

public static class ServiceCollectionExtensions
{
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
        services.AddSingleton<LlmProviderCatalog>();
        services.AddHttpClient<OpenAiResponsesLlmClient>();
        services.AddHttpClient<OpenAiCompatibleChatLlmClient>();
        services.AddHttpClient<AnthropicMessagesLlmClient>();
        services.AddHttpClient<ClaudeCliLlmClient>();
        services.AddHttpClient<OpenAiEmbeddingClient>();
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
        services.AddHttpClient<OpenAiCodexLlmClient>();
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
        services.AddHttpClient<CodexAuthenticator>();
        services.AddHttpClient<ClaudeAuthenticator>();
        services.AddTransient<ILlmAuthenticator>(provider =>
            provider.GetRequiredService<CodexAuthenticator>());
        services.AddTransient<ILlmAuthenticator>(provider =>
            provider.GetRequiredService<ClaudeAuthenticator>());
        services.AddTransient<ILlmEmbeddingClient>(provider =>
            provider.GetRequiredService<OpenAiEmbeddingClient>());
        services.AddSingleton<LlmModelListCache>();
        services.AddScoped<LlmProviderService>();
        services.AddScoped<LlmAgentProviderService>();
        services.AddScoped<LlmEmbeddingProviderService>();
        return services;
    }
}
