using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Realtime;

/// <summary>Routes explicit realtime operations to registered, borrowed provider adapters.</summary>
public sealed class RealtimeProviderService
{
    private readonly IReadOnlyDictionary<RealtimeTransport, IRealtimeProvider> _providers;
    public RealtimeProviderService(IEnumerable<IRealtimeProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(provider => RealtimeData.Defined(provider.Transport));
    }
    public IReadOnlyList<InferenceSetupDefinition> SetupDefinitions => Array.AsReadOnly(_providers.Values.Select(provider => provider.SetupDefinition).ToArray());
    public IRealtimeProvider GetRequiredProvider(RealtimeTransport transport)
    {
        RealtimeData.Defined(transport);
        return _providers.TryGetValue(transport, out IRealtimeProvider? provider) ? provider
            : throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The selected realtime transport is not registered.");
    }
    public async Task<RealtimeCapabilities> GetCapabilitiesAsync(RealtimeConnection connection, string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection); cancellationToken.ThrowIfCancellationRequested();
        RealtimeCapabilities? capabilities = await GetRequiredProvider(connection.Transport).GetCapabilitiesAsync(connection, modelId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return capabilities ?? throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The realtime provider returned no capability facts.");
    }
    public async Task<IRealtimeVoiceSession> OpenSessionAsync(RealtimeConnection connection, RealtimeSessionConfig config,
        InferenceStreamLimits limits, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(config); ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        IRealtimeProvider provider = GetRequiredProvider(connection.Transport);
        RealtimeSessionConfig snapshot = RealtimeRequestData.Snapshot(config);
        IRealtimeVoiceSession? session = await provider.OpenSessionAsync(connection, snapshot, limits, xai, resources, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The realtime provider returned no session.");
        }
        if (cancellationToken.IsCancellationRequested)
        {
            var cancellation = new OperationCanceledException(cancellationToken);
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(cancellation, cleanup); }
            throw cancellation;
        }
        return session;
    }
    public RealtimeSettingsManager CreateSettingsManager(ISetupCommitTarget<RealtimeSetupChanges> target) => new(this, target);
}

internal static class RealtimeRequestData
{
    internal static RealtimeSessionConfig Snapshot(RealtimeSessionConfig config) => config with
    {
        Tools = config.Tools is null ? null : config.Tools.Select(tool => tool with { InputSchema = tool.InputSchema.Clone() }).ToArray(),
        HostedTools = config.HostedTools is null ? null : HostedTools.Snapshot(config.HostedTools),
        ProviderOptions = config.ProviderOptions switch
        {
            null => null,
            XaiRealtimeOptions options => options,
            _ => throw new NotSupportedException("This realtime provider-options variant is not supported.")
        }
    };
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRealtimeConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddTransient<RealtimeProviderService>();
        return services;
    }
}
