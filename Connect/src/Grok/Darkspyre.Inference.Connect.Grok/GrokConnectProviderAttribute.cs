using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

[assembly: GrokConnectProvider]

namespace Darkspyre.Inference.Connect.LLM;

internal sealed class GrokConnectProviderAttribute : LlmConnectProviderAttribute
{
    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(GrokDefinition.Value);
        services.AddInferenceHttpClient<GrokResponsesLlmClient>()
            .ConfigureHttpClient(client => client.Timeout = ProviderHttp.GenerationTimeout);
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<GrokResponsesLlmClient>());
        services.AddTransient<ILlmAgentClient>(provider => new ManagedLlmAgentClient<GrokResponsesLlmClient>(
            provider.GetRequiredService<GrokResponsesLlmClient>(), provider.GetRequiredService<LlmProviderService>(),
            provider.GetRequiredService<LearnedModelContext>()));
        services.AddInferenceHttpClient<GrokAuthenticator>();
        services.AddTransient<ILlmAuthenticator>(provider => provider.GetRequiredService<GrokAuthenticator>());
    }
}
