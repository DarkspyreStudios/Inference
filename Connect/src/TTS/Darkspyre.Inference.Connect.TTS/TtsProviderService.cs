using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.TTS;

public sealed class TtsProviderService : IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private IReadOnlyDictionary<TtsTransport, ITtsProvider>? _providers;
    public TtsProviderService(IEnumerable<ITtsProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(provider => provider.Transport);
    }
    public IReadOnlyList<InferenceSetupDefinition> SetupDefinitions
    {
        get
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
            return _providers!.Values.Select(provider => provider.SetupDefinition).ToArray();
        }
    }
    public ITtsClient Bind(TtsConnection connection,
        IInferenceResourceAccess? resources = null)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        ArgumentNullException.ThrowIfNull(connection);
        ITtsProvider provider = RequiredProvider(_providers!, connection.Transport);
        ValidateBinding(connection);
        return provider.Bind(connection, resources);
    }
    internal static ITtsProvider RequiredProvider(IReadOnlyDictionary<TtsTransport, ITtsProvider> providers, TtsTransport transport)
    {
        if (!Enum.IsDefined(transport)) throw new InferenceException(InferenceFailureCode.InvalidRequest, "Unknown TTS transport.");
        return providers.TryGetValue(transport, out ITtsProvider? provider) ? provider
            : throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The selected TTS transport is not registered.");
    }
    /// <summary>Creates the settings manager that opens, acts on and saves TTS settings through <paramref name="target"/>.</summary>
    public TtsSettingsManager CreateSettingsManager(ISetupCommitTarget<TtsSetupChanges> target,
        IInferenceResourceAccess? resources = null)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        return new(this, target, resources);
    }

    internal ITtsProvider Provider(TtsTransport transport)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        return RequiredProvider(_providers!, transport);
    }

    public void Dispose() => RetireAsync().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(RetireAsync());

    private Task RetireAsync() => _lifetime.RetireAsync(() =>
    {
        _providers = null;
        return Task.CompletedTask;
    });

    internal static void ValidateBinding(TtsConnection connection)
    {
        ValidateConnection(connection);
        if (connection.Transport == TtsTransport.PocketTtsHttp && connection.Authorization is not null)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "PocketTTS does not implement borrowed provider authorization.");
    }

    internal static void ValidateConnection(TtsConnection connection)
    {
        if (connection.Transport == TtsTransport.PocketTtsHttp) PocketTtsHttpProvider.ValidateConnection(connection);
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTtsConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddInferenceHttpClient<PocketTtsHttpProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<ITtsProvider>(provider => provider.GetRequiredService<PocketTtsHttpProvider>());
        services.AddTransient<TtsProviderService>();
        return services;
    }
}
