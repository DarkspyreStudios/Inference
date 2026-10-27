using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

[assembly: AntigravityConnectProvider]

namespace Darkspyre.Inference.Connect.LLM;

internal sealed class AntigravityConnectProviderAttribute : LlmConnectProviderAttribute
{
    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(AntigravityDefinition.Value);
        services.AddSingleton<IAntigravityCliSessionProcessFactory, AntigravityCliSessionProcessFactory>();
        services.AddSingleton<IAntigravityCliRunner, AntigravityCliProcessRunner>();
        services.AddSingleton<AntigravityCliLlmClient>();
        services.AddTransient<ILlmClient>(provider => provider.GetRequiredService<AntigravityCliLlmClient>());
        services.AddTransient<AntigravityAgentClient>();
        services.AddTransient<ILlmAgentClient>(provider => provider.GetRequiredService<AntigravityAgentClient>());
        services.AddSingleton<ILlmDataFolderUser, AntigravityDataFolderUser>();
    }
}
