using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

[assembly: SelfHostConnectProvider]

namespace Darkspyre.Inference.Connect.LLM;

internal sealed class SelfHostConnectProviderAttribute : LlmConnectProviderAttribute
{
    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(SelfHostDefinition.Value);
        services.AddTransient<SelfHostLlmClient>();
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<SelfHostLlmClient>());
        services.AddTransient<ILlmAgentClient>(provider => new ManagedLlmAgentClient<SelfHostLlmClient>(
            provider.GetRequiredService<SelfHostLlmClient>(), provider.GetRequiredService<LlmProviderService>(),
            provider.GetRequiredService<LearnedModelContext>()));
        services.AddTransient<ILlmEmbeddingClient, SelfHostEmbeddingClient>();
    }
}
