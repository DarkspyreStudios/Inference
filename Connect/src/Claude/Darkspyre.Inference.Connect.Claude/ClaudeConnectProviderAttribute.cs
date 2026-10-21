using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

[assembly: ClaudeConnectProvider]

namespace Darkspyre.Inference.Connect.LLM;

internal sealed class ClaudeConnectProviderAttribute : LlmConnectProviderAttribute
{
    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(ClaudeDefinition.Value);
        services.AddInferenceHttpClient<ClaudeCliLlmClient>();
        services.AddSingleton<ClaudeCliExits>();
        services.AddSingleton<ClaudeRateLimitLog>();
        services.AddSingleton<IClaudeCliRunner, ClaudeCliProcessRunner>();
        services.AddSingleton<IClaudeCliSessionProcessFactory, ClaudeCliSessionProcessFactory>();
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<ClaudeCliLlmClient>());
        services.AddTransient<ClaudeAgentClient>();
        services.AddTransient<ILlmAgentClient>(provider => provider.GetRequiredService<ClaudeAgentClient>());
        services.AddInferenceHttpClient<ClaudeAuthenticator>();
        services.AddTransient<ILlmAuthenticator>(provider => provider.GetRequiredService<ClaudeAuthenticator>());
    }
}
