using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Decision.Laya;
using Darkspyre.Inference.Tensor.Embedding;
using Darkspyre.Inference.Tensor.Image.Supra;
using Darkspyre.Inference.Tensor.LLM.Qwen;
using Darkspyre.Inference.Tensor.STT.Whisper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.Registry;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every engine this package ships and the <see cref="TensorEngineRegistry"/> over all
    /// registered <see cref="ITensorEngineProvider"/> services. Repeated calls add nothing. A provider
    /// registered separately joins the same registry; one whose implementation ID or store key
    /// collides with another registration fails when the registry is resolved.
    /// </summary>
    public static IServiceCollection AddTensorEngines(this IServiceCollection services, TensorRuntimePlan? ggmlRuntimePlan = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (ggmlRuntimePlan is not null)
        {
            var snapshot = new TensorRuntimePlan(ggmlRuntimePlan.Build, ggmlRuntimePlan.TargetRid, ggmlRuntimePlan.Candidates);
            TensorRuntimePlan? registered = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(TensorRuntimePlan))?.ImplementationInstance as TensorRuntimePlan;
            if (registered is not null && !GgmlRuntimeOwner.SamePlan(registered, snapshot))
                throw new ArgumentException("The registry already declares a different runtime plan.", nameof(ggmlRuntimePlan));
            GgmlRuntimeOwner.ValidateGenerationPlan(snapshot);
            if (services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(GgmlRuntimeOwner))?.ImplementationInstance is GgmlRuntimeOwner borrowed
                && !GgmlRuntimeOwner.SamePlan(borrowed.Plan, snapshot))
                throw new ArgumentException("The caller-supplied runtime owner has a different plan.", nameof(ggmlRuntimePlan));
            services.TryAddSingleton(snapshot);
        }
        services.TryAddSingleton(provider => GgmlRuntimeOwner.GetOrCreate(provider.GetService<TensorRuntimePlan>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITensorEngineProvider, QwenEngineProvider>(
            provider => new(provider.GetRequiredService<GgmlRuntimeOwner>())));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITensorEngineProvider, SupraEngineProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITensorEngineProvider, LayaEngineProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITensorEngineProvider, WhisperEngineProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITensorEngineProvider, EmbeddingEngineProvider>());
        services.TryAddSingleton<TensorEngineRegistry>();
        return services;
    }
}
