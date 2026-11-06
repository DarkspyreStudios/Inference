using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

[assembly: AnthropicConnectProvider]

namespace Darkspyre.Inference.Connect.LLM;

internal sealed class AnthropicConnectProviderAttribute : LlmConnectProviderAttribute
{
    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(AnthropicDefinition.Value);
        services.AddInferenceHttpClient<AnthropicMessagesLlmClient>()
            .ConfigureHttpClient(client => client.Timeout = ProviderHttp.GenerationTimeout);
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<AnthropicMessagesLlmClient>());
        services.AddTransient<ILlmAgentClient>(provider => new ManagedLlmAgentClient<AnthropicMessagesLlmClient>(
            provider.GetRequiredService<AnthropicMessagesLlmClient>(), provider.GetRequiredService<LlmProviderService>(),
            provider.GetRequiredService<LearnedModelContext>()));
    }
}
