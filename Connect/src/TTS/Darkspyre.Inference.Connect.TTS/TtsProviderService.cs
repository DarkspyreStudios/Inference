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
    public ITtsClient Bind(TtsConnection connection, XaiFamilyBinding? xai = null,
        IInferenceResourceAccess? resources = null)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        ArgumentNullException.ThrowIfNull(connection);
        ITtsProvider provider = RequiredProvider(_providers!, connection.Transport);
        ValidateBinding(connection, xai);
        return provider.Bind(connection, xai, resources);
    }
    internal static ITtsProvider RequiredProvider(IReadOnlyDictionary<TtsTransport, ITtsProvider> providers, TtsTransport transport)
    {
        if (!Enum.IsDefined(transport)) throw new InferenceException(InferenceFailureCode.InvalidRequest, "Unknown TTS transport.");
        return providers.TryGetValue(transport, out ITtsProvider? provider) ? provider
            : throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The selected TTS transport is not registered.");
    }
    /// <summary>Creates the settings manager that opens, acts on and saves TTS settings through <paramref name="target"/>.</summary>
    public TtsSettingsManager CreateSettingsManager(ISetupCommitTarget<TtsSetupChanges> target,
        Func<XaiConnectionSelection, XaiFamilyBinding>? xaiBindings = null,
        IInferenceResourceAccess? resources = null)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        return new(this, target, xaiBindings, resources);
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

    internal static void ValidateBinding(TtsConnection connection, XaiFamilyBinding? xai)
    {
        ValidateConnection(connection);
        if (connection.Transport == TtsTransport.PocketTtsHttp)
        {
            if (xai is not null || connection.Xai is not null || connection.Authorization is not null)
                throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "PocketTTS does not implement borrowed provider authorization or xAI binding.");
            return;
        }
        if (connection.Xai is null || xai is null || connection.Xai != xai.Selection
            || connection.Authorization is not null && !ReferenceEquals(connection.Authorization, xai.Authorization))
            throw new ArgumentException("The selected TTS connection requires its matching borrowed xAI binding.");
    }

    internal static void ValidateConnection(TtsConnection connection)
    {
        if (connection.Transport == TtsTransport.PocketTtsHttp) { PocketTtsHttpProvider.ValidateConnection(connection); return; }
        if (connection.Transport != TtsTransport.XaiTts || connection.Endpoint is not { IsAbsoluteUri: true, Scheme: "https" }
            || connection.Endpoint.UserInfo.Length != 0 || connection.Endpoint.Query.Length != 0 || connection.Endpoint.Fragment.Length != 0
            || connection.RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentException("xAI TTS requires an explicit HTTPS endpoint and positive timeout.");
        ProviderHttp.ValidateAdditionalHeaders(connection.Headers);
        if (connection.Headers.ContainsKey("api-key"))
            throw new ArgumentException("Static authentication headers cannot replace the selected xAI authorization.");
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
