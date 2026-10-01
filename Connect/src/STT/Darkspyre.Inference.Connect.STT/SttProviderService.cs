using System.Collections.ObjectModel;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Connect.LLM;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.STT;

/// <summary>Resolves only registered hosted-STT adapters. Registration never implies OAuth qualification.</summary>
public sealed class SttProviderService
{
    private readonly IReadOnlyDictionary<SttTransport, ISttProvider> _providers;
    public SttProviderService(IEnumerable<ISttProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ISttProvider[] registered = providers.ToArray();
        if (registered.Any(provider => provider is null)) throw new ArgumentException("Providers cannot contain null.", nameof(providers));
        foreach (ISttProvider provider in registered) SttData.Transport(provider.Transport);
        _providers = new ReadOnlyDictionary<SttTransport, ISttProvider>(registered.ToDictionary(provider => provider.Transport));
    }
    public IReadOnlyList<InferenceSetupDefinition> SetupDefinitions => Array.AsReadOnly(_providers.Values.Select(provider => provider.SetupDefinition).ToArray());
    public ISttProvider GetRequiredProvider(SttTransport transport)
    {
        SttData.Transport(transport);
        return _providers.TryGetValue(transport, out ISttProvider? provider) ? provider
            : throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The selected hosted-STT transport is not registered.");
    }
    public ISpeechTranscriptionClient Bind(SttConnection connection, XaiFamilyBinding? xai = null,
        IInferenceResourceAccess? resources = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ISttProvider provider = GetRequiredProvider(connection.Transport);
        ValidateBinding(connection, xai);
        return provider.Bind(connection, xai, resources);
    }
    public SttSettingsManager CreateSettingsManager(ISetupCommitTarget<SttSetupChanges> target,
        Func<XaiConnectionSelection, XaiFamilyBinding>? xaiBindings = null,
        IInferenceResourceAccess? resources = null) => new(this, target, xaiBindings, resources);
    internal static void ValidateBinding(SttConnection connection, XaiFamilyBinding? xai)
    {
        if (connection.Xai is null || xai is null || connection.Xai != xai.Selection
            || connection.Authorization is not null && !ReferenceEquals(connection.Authorization, xai.Authorization))
            throw new ArgumentException("The hosted-STT connection requires its matching borrowed xAI binding.");
        if (connection.AdditionalHeaders.ContainsKey("Authorization") || connection.AdditionalHeaders.ContainsKey("api-key"))
            throw new ArgumentException("Static authentication headers cannot override the borrowed xAI authorization.");
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
