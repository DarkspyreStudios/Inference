using System.Collections.ObjectModel;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.STT;

/// <summary>Resolves only registered hosted-STT adapters. Registration never implies OAuth qualification.</summary>
public sealed class SttProviderService : IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private IReadOnlyDictionary<SttTransport, ISttProvider>? _providers;
    public SttProviderService(IEnumerable<ISttProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ISttProvider[] registered = providers.ToArray();
        if (registered.Any(provider => provider is null)) throw new ArgumentException("Providers cannot contain null.", nameof(providers));
        foreach (ISttProvider provider in registered) SttData.Transport(provider.Transport);
        _providers = new ReadOnlyDictionary<SttTransport, ISttProvider>(registered.ToDictionary(provider => provider.Transport));
    }
    public IReadOnlyList<InferenceSetupDefinition> SetupDefinitions
    {
        get
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
            return Array.AsReadOnly(Providers.Values.Select(provider => provider.SetupDefinition).ToArray());
        }
    }
    public ISttProvider GetRequiredProvider(SttTransport transport)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        return GetProvider(transport);
    }
    private IReadOnlyDictionary<SttTransport, ISttProvider> Providers => _providers ?? throw new ObjectDisposedException(nameof(SttProviderService));
    private ISttProvider GetProvider(SttTransport transport)
    {
        SttData.Transport(transport);
        return Providers.TryGetValue(transport, out ISttProvider? provider) ? provider
            : throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The selected hosted-STT transport is not registered.");
    }
    public ISpeechTranscriptionClient Bind(SttConnection connection, XaiFamilyBinding? xai = null,
        IInferenceResourceAccess? resources = null)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        ArgumentNullException.ThrowIfNull(connection);
        ISttProvider provider = GetProvider(connection.Transport);
        ValidateBinding(connection, xai);
        return provider.Bind(connection, xai, resources);
    }
    public SttSettingsManager CreateSettingsManager(ISetupCommitTarget<SttSetupChanges> target,
        Func<XaiConnectionSelection, XaiFamilyBinding>? xaiBindings = null,
        IInferenceResourceAccess? resources = null)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        return new(this, target, xaiBindings, resources);
    }
    private Task RetireAsync() => _lifetime.RetireAsync(() =>
    {
        _providers = null;
        return Task.CompletedTask;
    });
    /// <summary>Fences new facade calls and drains admitted callbacks without disposing borrowed adapters or returned clients.</summary>
    public ValueTask DisposeAsync() => new(RetireAsync());
    public void Dispose() => RetireAsync().GetAwaiter().GetResult();
    internal static void ValidateBinding(SttConnection connection, XaiFamilyBinding? xai)
    {
        ValidateConnection(connection);
        if (connection.Xai is null || xai is null || connection.Xai != xai.Selection
            || connection.Authorization is not null && !ReferenceEquals(connection.Authorization, xai.Authorization))
            throw new ArgumentException("The hosted-STT connection requires its matching borrowed xAI binding.");
    }
    internal static void ValidateConnection(SttConnection connection)
    {
        if (!connection.Endpoint.IsAbsoluteUri || connection.RequestTimeout <= TimeSpan.Zero
            || !Enum.GetValues<XaiRegion>().Any(region => string.Equals(connection.Endpoint.AbsoluteUri.TrimEnd('/'),
                new XaiConnectionOptions { Region = region }.GetEndpoint(), StringComparison.Ordinal)))
            throw new ArgumentException("Use the fixed xAI regional API endpoint and a positive request timeout.");
        if (connection.AdditionalHeaders.ContainsKey("api-key"))
            throw new ArgumentException("Static authentication headers cannot override the borrowed xAI authorization.");
        ProviderHttp.ValidateAdditionalHeaders(connection.AdditionalHeaders);
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the hosted-STT facade only. No xAI adapter is registered.</summary>
    public static IServiceCollection AddSttConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddTransient<SttProviderService>();
        return services;
    }
}
