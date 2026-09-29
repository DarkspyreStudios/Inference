using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Decision.Laya;
using Darkspyre.Inference.Tensor.Image.Supra;
using Darkspyre.Inference.Tensor.LLM.Qwen;
using Darkspyre.Inference.Tensor.STT.Whisper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Darkspyre.Inference.Tensor.Registry;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every engine this package ships and the <see cref="TensorEngineRegistry"/> over all
    /// registered <see cref="ITensorEngineProvider"/> services. Repeated calls add nothing. A provider
    /// registered separately joins the same registry; one whose implementation ID or store key
    /// collides with another registration fails when the registry is resolved.
    /// </summary>
    public static IServiceCollection AddTensorEngines(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITensorEngineProvider, QwenEngineProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITensorEngineProvider, SupraEngineProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITensorEngineProvider, LayaEngineProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITensorEngineProvider, WhisperEngineProvider>());
        services.TryAddSingleton<TensorEngineRegistry>();
        return services;
    }
}
