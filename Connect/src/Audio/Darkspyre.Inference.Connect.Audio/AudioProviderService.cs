using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Audio;

/// <summary>Routes audio connections to the registered audio provider for their transport.</summary>
public sealed class AudioProviderService
{
    private readonly IReadOnlyDictionary<AudioTransport, IAudioProvider> _providers;
    /// <summary>Indexes the given providers by transport. Throws when two providers share a transport.</summary>
    public AudioProviderService(IEnumerable<IAudioProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(provider => provider.Transport);
    }
    /// <summary>The setup definition of every registered provider.</summary>
    public IReadOnlyList<InferenceSetupDefinition> SetupDefinitions => _providers.Values.Select(provider => provider.SetupDefinition).ToArray();
    /// <summary>Creates a client for the connection through the provider for its transport. Throws when no provider serves that transport.</summary>
    public IAudioGenerationClient Bind(AudioConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return _providers[connection.Transport].Bind(connection);
    }
    /// <summary>Creates the settings manager that opens, acts on and saves audio settings through <paramref name="target"/>.</summary>
    public AudioSettingsManager CreateSettingsManager(ISetupCommitTarget<AudioSetupChanges> target) => new(_providers, target);
}

/// <summary>Registers the audio Connect services with dependency injection.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the TangoFlux HTTP provider, with redirects disabled and no client timeout, and <see cref="AudioProviderService"/>.</summary>
    public static IServiceCollection AddAudioConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddInferenceHttpClient<TangoFluxHttpProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<IAudioProvider>(provider => provider.GetRequiredService<TangoFluxHttpProvider>());
        services.AddTransient<AudioProviderService>();
        return services;
    }
}
