using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Video;

public sealed class VideoProviderService
{
    private readonly IReadOnlyDictionary<VideoTransport, IVideoTransport> _transports;

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

    public IReadOnlyList<InferenceSetupDefinition> SetupDefinitions => _transports.Values.Select(transport => transport.SetupDefinition).ToArray();
    public IVideoTransport GetRequiredTransport(VideoTransport transport)
    {
        VideoContractData.Defined(transport);
        return _transports.TryGetValue(transport, out IVideoTransport? registered) ? registered
            : throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The selected video transport is not registered.");
    }

    public IVideoInference Bind(VideoConnection connection, XaiFamilyBinding? xai = null,
        IInferenceResourceAccess? resources = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ConnectVideoInference(GetRequiredTransport(connection.Transport), connection, xai, resources);
    }

    public VideoSettingsManager CreateSettingsManager(ISetupCommitTarget<VideoSetupChanges> target,
        Func<XaiConnectionSelection, XaiFamilyBinding>? xaiBindings = null) => new(this, target, xaiBindings);
}

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the family service. No unimplemented video transport is activated.</summary>
    public static IServiceCollection AddVideoConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddTransient<VideoProviderService>();
        return services;
    }
}
