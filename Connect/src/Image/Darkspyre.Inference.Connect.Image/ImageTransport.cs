using Darkspyre.Inference.Common;
namespace Darkspyre.Inference.Connect.Image;

/// <summary>Image-provider protocols implemented by this capability family.</summary>
public enum ImageTransport
{
    /// <summary>The ComfyUI server protocol.</summary>
    ComfyUi = 0,
    /// <summary>The xAI image generation protocol.</summary>
    XaiImagine = 1
}

/// <summary>The optional features an image transport supports.</summary>
/// <param name="SupportsProgress">Whether the transport reports live job progress.</param>
/// <param name="SupportsReferenceImages">Whether requests can carry reference images.</param>
/// <param name="SupportsCancellation">Whether submitted jobs can be cancelled.</param>
public sealed record ImageTransportCapabilities(
    InferenceSupport SupportsProgress,
    InferenceSupport SupportsReferenceImages,
    InferenceSupport SupportsCancellation)
{
    /// <summary>Whether the transport provides its own workflows instead of requiring user-supplied ones.</summary>
    public InferenceSupport SupportsBuiltInWorkflows { get; init; }
}

/// <summary>Identifies an image transport and describes it for display.</summary>
/// <param name="Transport">The protocol the transport implements.</param>
/// <param name="DisplayName">The name shown to the user.</param>
/// <param name="Description">A description shown to the user.</param>
/// <param name="Capabilities">The optional features the transport supports.</param>
public sealed record ImageTransportDescriptor(
    ImageTransport Transport,
    string DisplayName,
    string Description,
    ImageTransportCapabilities Capabilities);
