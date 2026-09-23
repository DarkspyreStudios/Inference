using Darkspyre.Inference.Common.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.TTS;

public sealed class TtsProviderService
{
    private readonly IReadOnlyDictionary<TtsTransport, ITtsProvider> _providers;
    public TtsProviderService(IEnumerable<ITtsProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(provider => provider.Transport);
    }
    public IReadOnlyList<InferenceSetupDefinition> SetupDefinitions => _providers.Values.Select(provider => provider.SetupDefinition).ToArray();
    public ITtsClient Bind(TtsConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return _providers[connection.Transport].Bind(connection);
    }
    public TtsSetupSession CreateSetupSession(TtsTransport transport, SetupConfigurationIdentity identity,
        ISetupCommitTarget<TtsSetupChanges> target, TtsSetupConfiguration? existing = null) =>
        new(_providers[transport], identity, target, existing);
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTtsConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient<PocketTtsHttpProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<ITtsProvider>(provider => provider.GetRequiredService<PocketTtsHttpProvider>());
        services.AddTransient<TtsProviderService>();
        return services;
    }
}
