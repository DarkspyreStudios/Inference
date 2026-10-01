using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Azure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers Azure resource OpenAI v1 Responses and Chat Completions with protected API keys.</summary>
    public static IServiceCollection AddLlmConnectAzure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(AzureProviderDefinition.Responses);
        services.AddSingleton(AzureProviderDefinition.Chat);
        services.AddSingleton<ILlmSetupEnvironment>(new AzureSetupEnvironment(LlmTransport.AzureOpenAiResponses));
        services.AddSingleton<ILlmSetupEnvironment>(new AzureSetupEnvironment(LlmTransport.AzureOpenAiChat));
        services.AddHttpClient<AzureResponsesLlmClient>()
            .ConfigureHttpClient(client => client.Timeout = ProviderHttp.GenerationTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<AzureChatLlmClient>()
            .ConfigureHttpClient(client => client.Timeout = ProviderHttp.GenerationTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<AzureResponsesLlmClient>());
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<AzureChatLlmClient>());
        return services;
    }
}
