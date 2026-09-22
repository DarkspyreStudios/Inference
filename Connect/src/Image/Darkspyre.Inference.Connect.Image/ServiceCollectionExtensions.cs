using Darkspyre.Inference.Connect.Image.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Darkspyre.Inference.Connect.Image;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddImageConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IImageTransport, ComfyUiImageTransport>());
        services.TryAddSingleton<ImageProviderService>();
        return services;
    }
}
