namespace Darkspyre.Inference.Connect.Image;

/// <summary>Image-provider protocols implemented by this capability family.</summary>
public enum ImageTransport
{
    ComfyUi = 0,
    XaiImagine = 1
}

public sealed record ImageTransportCapabilities(
    bool SupportsProgress,
    bool SupportsReferenceImages,
    bool SupportsCancellation)
{
    public bool SupportsBuiltInWorkflows { get; init; }
}

public sealed record ImageTransportDescriptor(
    ImageTransport Transport,
    string DisplayName,
    string Description,
    ImageTransportCapabilities Capabilities);
