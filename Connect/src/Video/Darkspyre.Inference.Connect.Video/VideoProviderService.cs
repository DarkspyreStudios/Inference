using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.Video.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Darkspyre.Inference.Connect.Video;

public sealed class VideoProviderService : IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private IReadOnlyDictionary<VideoTransport, IVideoTransport>? _transports;

    public VideoProviderService(IEnumerable<IVideoTransport> transports)
    {
        ArgumentNullException.ThrowIfNull(transports);
        IVideoTransport[] registered = transports.ToArray();
        foreach (IVideoTransport transport in registered)
        {
            ArgumentNullException.ThrowIfNull(transport);
            VideoContractData.Defined(transport.Transport);
        }
        _transports = registered.ToDictionary(transport => transport.Transport);
    }

    public IReadOnlyList<InferenceSetupDefinition> SetupDefinitions
    {
        get
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
            return Transports.Values.Select(transport => transport.SetupDefinition).ToArray();
        }
    }
    public IVideoTransport GetRequiredTransport(VideoTransport transport)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        return GetTransport(transport);
    }
    private IReadOnlyDictionary<VideoTransport, IVideoTransport> Transports => _transports ?? throw new ObjectDisposedException(nameof(VideoProviderService));
    private IVideoTransport GetTransport(VideoTransport transport)
    {
        VideoContractData.Defined(transport);
        return Transports.TryGetValue(transport, out IVideoTransport? registered) ? registered
            : throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The selected video transport is not registered.");
    }

    public IVideoInference Bind(VideoConnection connection, XaiFamilyBinding? xai = null,
        IInferenceResourceAccess? resources = null)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        ArgumentNullException.ThrowIfNull(connection);
        return new ConnectVideoInference(GetTransport(connection.Transport), connection, xai, resources);
    }

    public VideoSettingsManager CreateSettingsManager(ISetupCommitTarget<VideoSetupChanges> target,
        Func<XaiConnectionSelection, XaiFamilyBinding>? xaiBindings = null)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        return new(this, target, xaiBindings);
    }
    private Task RetireAsync() => _lifetime.RetireAsync(() =>
    {
        _transports = null;
        return Task.CompletedTask;
    });
    /// <summary>Fences new facade calls and drains admitted callbacks without disposing borrowed transports or handed-off inference.</summary>
    public ValueTask DisposeAsync() => new(RetireAsync());
    public void Dispose() => RetireAsync().GetAwaiter().GetResult();
}

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the family service and the selected-connection xAI video transport.</summary>
    public static IServiceCollection AddVideoConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IVideoTransport, XaiImagineVideoTransport>());
        services.AddTransient<VideoProviderService>();
        return services;
    }
}
