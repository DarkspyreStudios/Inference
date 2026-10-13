using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Declares a Connect library's provider registration entry point.</summary>
/// <gotcha>Discovery invokes this attribute during composition. Registration must not start provider operations.</gotcha>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public abstract class LlmConnectProviderAttribute : Attribute
{
    /// <summary>Registers this library's provider services in the caller's container.</summary>
    public abstract void RegisterServices(IServiceCollection services);
}
