using Darkspyre.Inference.Connect.Image.Providers;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.Image;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddImageConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        SdkHttpRegistration.Register(services);
        Func<IServiceProvider, ComfyUiImageTransport> factory = provider => new(
            provider.GetRequiredService<SdkHttpGenerationPool>(),
            provider.GetRequiredService<ILogger<ComfyUiImageTransport>>());
        services.TryAddEnumerable(new ServiceDescriptor(typeof(IImageTransport), factory, ServiceLifetime.Singleton));
        services.TryAddSingleton<ImageProviderService>();
        return services;
    }
}
