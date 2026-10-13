using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

[assembly: OpenAiApiConnectProvider]

namespace Darkspyre.Inference.Connect.LLM;

internal sealed class OpenAiApiConnectProviderAttribute : LlmConnectProviderAttribute
{
    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(OpenAiApiDefinition.Value);
        services.AddInferenceHttpClient<OpenAiResponsesLlmClient>()
            .ConfigureHttpClient(client => client.Timeout = ProviderHttp.GenerationTimeout);
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<OpenAiResponsesLlmClient>());
        services.AddTransient<ILlmAgentClient>(provider => new ManagedLlmAgentClient<OpenAiResponsesLlmClient>(
            provider.GetRequiredService<OpenAiResponsesLlmClient>(), provider.GetRequiredService<LlmProviderService>(),
            provider.GetRequiredService<LearnedModelContext>()));
        services.AddInferenceHttpClient<OpenAiEmbeddingClient>();
        services.AddTransient<ILlmEmbeddingClient>(provider => provider.GetRequiredService<OpenAiEmbeddingClient>());
    }
}
