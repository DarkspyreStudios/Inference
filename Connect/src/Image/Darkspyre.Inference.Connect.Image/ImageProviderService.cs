namespace Darkspyre.Inference.Connect.Image;

/// <summary>Discovers and resolves the image transports registered by the host.</summary>
public sealed class ImageProviderService
{
    private readonly Dictionary<ImageTransport, IImageTransport> _transports;

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

    public Darkspyre.Inference.Common.Image.IImageInference Bind(
        ImageConnection connection,
        ImageWorkflow workflow,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(workflow);
        if (connection.Transport != workflow.Transport)
        {
            throw new ArgumentException("The image connection and workflow select different transports.");
        }

        return new ConnectImageInference(
            GetRequiredTransport(connection.Transport),
            connection,
            workflow,
            pollInterval ?? TimeSpan.FromMilliseconds(250));
    }

    public IImageTransport GetRequiredTransport(ImageTransport transport) =>
        _transports.TryGetValue(transport, out IImageTransport? implementation)
            ? implementation
            : throw new InvalidOperationException(
                $"No image transport is registered for provider transport '{transport}'.");
}
