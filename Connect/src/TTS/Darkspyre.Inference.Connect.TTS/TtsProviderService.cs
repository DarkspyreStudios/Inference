using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.TTS;

/// <summary>Routes text-to-speech connections to the registered provider for their transport.</summary>
public sealed class TtsProviderService : IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private IReadOnlyDictionary<TtsTransport, ITtsProvider>? _providers;
    /// <summary>Creates the service over the registered providers, one per transport.</summary>
    public TtsProviderService(IEnumerable<ITtsProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(provider => provider.Transport);
    }
    /// <summary>The setup definition of each registered provider.</summary>
    public IReadOnlyList<InferenceSetupDefinition> SetupDefinitions
    {
        get
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
            return _providers!.Values.Select(provider => provider.SetupDefinition).ToArray();
        }
    }
    /// <summary>Validates a connection and binds it to a client from the provider for its transport. Throws <see cref="InferenceException"/> when the transport is unknown or not registered.</summary>
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

    /// <summary>Cancels running operations, waits for them to end and releases the providers.</summary>
    public void Dispose() => RetireAsync().GetAwaiter().GetResult();

    /// <summary>Cancels running operations, waits for them to end and releases the providers.</summary>
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

/// <summary>Registers the text-to-speech services with dependency injection.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Pocket TTS provider and <see cref="TtsProviderService"/>.</summary>
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
