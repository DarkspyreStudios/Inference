using Darkspyre.Inference.Common.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Audio;

public sealed class AudioProviderService
{
    private readonly IReadOnlyDictionary<AudioTransport, IAudioProvider> _providers;
    public AudioProviderService(IEnumerable<IAudioProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(provider => provider.Transport);
    }
    public IReadOnlyList<InferenceSetupDefinition> SetupDefinitions => _providers.Values.Select(provider => provider.SetupDefinition).ToArray();
    public IAudioGenerationClient Bind(AudioConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return _providers[connection.Transport].Bind(connection);
    }
    public AudioSetupSession CreateSetupSession(AudioTransport transport, SetupConfigurationIdentity identity,
        ISetupCommitTarget<AudioSetupChanges> target, AudioSetupConfiguration? existing = null) =>
        new(_providers[transport], identity, target, existing);
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAudioConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient<TangoFluxHttpProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<IAudioProvider>(provider => provider.GetRequiredService<TangoFluxHttpProvider>());
        services.AddTransient<AudioProviderService>();
        return services;
    }
}
