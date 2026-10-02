using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Image;

/// <summary>Discovers and resolves the image transports registered by the host.</summary>
public sealed class ImageProviderService : IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private Dictionary<ImageTransport, IImageTransport>? _transports;

    public ImageProviderService(IEnumerable<IImageTransport> transports)
    {
        ArgumentNullException.ThrowIfNull(transports);
        IImageTransport[] registered = transports.ToArray();
        ImageTransport[] duplicates = registered
            .GroupBy(transport => transport.Descriptor.Transport)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate image transports are registered for: {string.Join(", ", duplicates)}.");
        }

        _transports = registered.ToDictionary(transport => transport.Descriptor.Transport);
        AvailableTransports = registered
            .Select(transport => transport.Descriptor)
            .OrderBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ImageTransportDescriptor> AvailableTransports { get; }

    public IReadOnlyList<Common.Setup.InferenceSetupDefinition> SetupDefinitions
    {
        get
        {
            using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
            return AvailableTransports.Select(ImageSettings.DefinitionFor).ToArray();
        }
    }

    /// <summary>
    /// Creates the settings manager that opens, acts on and saves image settings through
    /// <paramref name="target"/>. The host's <paramref name="workflows"/> catalog supplies workflow choices.
    /// </summary>
    public ImageSettingsManager CreateSettingsManager(
        Common.Setup.ISetupCommitTarget<ImageSetupChanges> target,
        IImageWorkflowChoiceSource? workflows = null,
        Func<XaiConnectionSelection, XaiFamilyBinding>? xaiBindings = null)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        return new(this, target, workflows, xaiBindings);
    }

    public async Task<IReadOnlyList<ImageWorkflow>> GetWorkflowsAsync(ImageConnection connection,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        ConnectImageInference.ValidateBinding(connection, xai);
        IReadOnlyList<ImageWorkflow> workflows = await RequiredTransport(connection.Transport)
            .GetWorkflowsAsync(connection, xai, operation.Token).ConfigureAwait(false);
        ThrowIfOperationCanceled(operation.Token);
        return workflows;
    }

    public Darkspyre.Inference.Common.Image.IImageInference Bind(
        ImageConnection connection,
        ImageWorkflow workflow,
        XaiFamilyBinding? xai = null,
        IInferenceResourceAccess? resources = null,
        TimeSpan? pollInterval = null)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(workflow);
        if (connection.Transport != workflow.Transport)
        {
            throw new ArgumentException("The image connection and workflow select different transports.");
        }

        return new ConnectImageInference(
            RequiredTransport(connection.Transport),
            connection,
            workflow,
            xai,
            resources,
            pollInterval ?? TimeSpan.FromMilliseconds(250));
    }

    public IImageTransport GetRequiredTransport(ImageTransport transport)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(default);
        return RequiredTransport(transport);
    }

    private IImageTransport RequiredTransport(ImageTransport transport) =>
        _transports!.TryGetValue(transport, out IImageTransport? implementation)
            ? implementation
            : throw new InvalidOperationException(
                $"No image transport is registered for provider transport '{transport}'.");

    private void ThrowIfOperationCanceled(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error)
        {
            throw new OperationCanceledException("The image facade is retiring.", error, token);
        }
    }

    public void Dispose() => RetireAsync().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(RetireAsync());

    private Task RetireAsync() => _lifetime.RetireAsync(() =>
    {
        _transports = null;
        return Task.CompletedTask;
    });
}
