using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common;
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
        return RequiredProvider(_providers, connection.Transport).Bind(connection);
    }
    internal static ITtsProvider RequiredProvider(IReadOnlyDictionary<TtsTransport, ITtsProvider> providers, TtsTransport transport)
    {
        if (!Enum.IsDefined(transport)) throw new InferenceException(InferenceFailureCode.InvalidRequest, "Unknown TTS transport.");
        return providers.TryGetValue(transport, out ITtsProvider? provider) ? provider
            : throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The selected TTS transport is not registered.");
    }
    /// <summary>Creates the settings manager that opens, acts on and saves TTS settings through <paramref name="target"/>.</summary>
    public TtsSettingsManager CreateSettingsManager(ISetupCommitTarget<TtsSetupChanges> target) => new(_providers, target);
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
