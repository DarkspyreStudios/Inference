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
public sealed class RealtimeProviderService : IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private IReadOnlyDictionary<RealtimeTransport, IRealtimeProvider>? _providers;
    public RealtimeProviderService(IEnumerable<IRealtimeProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(provider => RealtimeData.Defined(provider.Transport));
    }
    public IReadOnlyList<InferenceSetupDefinition> SetupDefinitions
    {
        get
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
            return Array.AsReadOnly(Providers.Values.Select(provider => provider.SetupDefinition).ToArray());
        }
    }
    public IRealtimeProvider GetRequiredProvider(RealtimeTransport transport)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        return GetProvider(transport);
    }
    private IReadOnlyDictionary<RealtimeTransport, IRealtimeProvider> Providers => _providers ?? throw new ObjectDisposedException(nameof(RealtimeProviderService));
    private IRealtimeProvider GetProvider(RealtimeTransport transport)
    {
        RealtimeData.Defined(transport);
        return Providers.TryGetValue(transport, out IRealtimeProvider? provider) ? provider
            : throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The selected realtime transport is not registered.");
    }
    public async Task<RealtimeCapabilities> GetCapabilitiesAsync(RealtimeConnection connection, string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection); cancellationToken.ThrowIfCancellationRequested();
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        RealtimeCapabilities? capabilities = await GetProvider(connection.Transport).GetCapabilitiesAsync(connection, modelId, operation.Token).ConfigureAwait(false);
        ThrowIfCancelled(operation.Token);
        return capabilities ?? throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The realtime provider returned no capability facts.");
    }
    public async Task<IRealtimeVoiceSession> OpenSessionAsync(RealtimeConnection connection, RealtimeSessionConfig config,
        InferenceStreamLimits limits, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(config); ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        IRealtimeProvider provider = GetProvider(connection.Transport);
        RealtimeSessionConfig snapshot = RealtimeRequestData.Snapshot(config);
        IRealtimeVoiceSession? session = await provider.OpenSessionAsync(connection, snapshot, limits, xai, resources, operation.Token).ConfigureAwait(false);
        if (session is null)
        {
            ThrowIfCancelled(operation.Token);
            throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The realtime provider returned no session.");
        }
        try { ThrowIfCancelled(operation.Token); }
        catch (OperationCanceledException cancellation)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(cancellation, cleanup); }
            throw;
        }
        return session;
    }
    public RealtimeSettingsManager CreateSettingsManager(ISetupCommitTarget<RealtimeSetupChanges> target)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        return new(this, target);
    }
    private void ThrowIfCancelled(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException) { throw new OperationCanceledException(token); }
    }
    private Task RetireAsync() => _lifetime.RetireAsync(() =>
    {
        _providers = null;
        return Task.CompletedTask;
    });
    public void Dispose() => RetireAsync().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(RetireAsync());
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
