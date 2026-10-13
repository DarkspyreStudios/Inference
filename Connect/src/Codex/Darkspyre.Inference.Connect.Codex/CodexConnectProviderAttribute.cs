using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

[assembly: CodexConnectProvider]

namespace Darkspyre.Inference.Connect.LLM;

internal sealed class CodexConnectProviderAttribute : LlmConnectProviderAttribute
{
    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(CodexDefinition.Value);
        services.AddInferenceHttpClient<OpenAiCodexLlmClient>();
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<OpenAiCodexLlmClient>());
        services.AddTransient<ILlmAgentClient>(provider => new ManagedLlmAgentClient<OpenAiCodexLlmClient>(
            provider.GetRequiredService<OpenAiCodexLlmClient>(), provider.GetRequiredService<LlmProviderService>(),
            provider.GetRequiredService<LearnedModelContext>()));
        services.AddInferenceHttpClient<CodexAuthenticator>();
        services.AddTransient<ILlmAuthenticator>(provider => provider.GetRequiredService<CodexAuthenticator>());
    }
}
