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

    public IImageTransport GetRequiredTransport(ImageTransport transport) =>
        _transports.TryGetValue(transport, out IImageTransport? implementation)
            ? implementation
            : throw new InvalidOperationException(
                $"No image transport is registered for provider transport '{transport}'.");
}
