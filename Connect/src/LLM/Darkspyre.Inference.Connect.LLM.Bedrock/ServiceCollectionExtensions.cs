using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlmConnectBedrock(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(BedrockProviderDefinitions.AwsProfile);
        services.AddSingleton(BedrockProviderDefinitions.AgentCoreHarness);
        services.AddSingleton<BedrockClientFactory>();
        services.AddSingleton<BedrockLoggingGuard>();
        services.AddSingleton<IBedrockAgentCoreInvoker, BedrockAgentCoreInvoker>();
        services.AddSingleton<ILlmClient, BedrockLlmClient>();
        services.AddSingleton<BedrockAgentCoreLlmClient>();
        services.AddSingleton<ILlmClient>(provider =>
            provider.GetRequiredService<BedrockAgentCoreLlmClient>());
        services.AddSingleton<ILlmAgentClient>(provider =>
            provider.GetRequiredService<BedrockAgentCoreLlmClient>());
        return services;
    }
}
