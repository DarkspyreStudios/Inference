using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.Image;

/// <summary>An image size in pixels.</summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
public sealed record ImageSize([property: JsonRequired] int Width, [property: JsonRequired] int Height);

/// <summary>A request to generate one or more images from a text prompt and optional reference media.</summary>
public sealed record ImageGenerationRequest
{
    /// <summary>The model that generates the images.</summary>
    public required string ModelId { get; init; }

    /// <summary>The text that describes the images to generate.</summary>
    public required string Prompt { get; init; }

    /// <summary>Text that describes content the images must avoid, or null for none.</summary>
    public string? NegativePrompt { get; init; }

    /// <summary>The requested width in pixels, or null for the model default.</summary>
    public int? Width { get; init; }

    /// <summary>The requested height in pixels, or null for the model default.</summary>
    public int? Height { get; init; }

    /// <summary>The number of images to generate. The default is 1.</summary>
    public int Count { get; init; } = 1;

    /// <summary>The random seed for generation, or null for a provider-chosen seed.</summary>
    public long? Seed { get; init; }

    /// <summary>The number of sampling steps, or null for the model default.</summary>
    public int? Steps { get; init; }

    /// <summary>How strongly generation follows the prompt, or null for the model default.</summary>
    public double? GuidanceScale { get; init; }

    /// <summary>Reference media that guide generation. Empty for a text-only request.</summary>
    public IReadOnlyList<InferenceMediaSource> References { get; init; } = [];
}

/// <summary>One generated image with its bytes and where it was stored.</summary>
public sealed record GeneratedImage
{
    /// <summary>The image's media type, or null when neither the provider nor storage reports one.</summary>
    public required string? ContentType { get; init; }

    /// <summary>The encoded image bytes.</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>The seed the image was generated with, or null when unknown.</summary>
    public long? Seed { get; init; }

    /// <summary>The provider-side file that holds the image, or null when the provider keeps none.</summary>
    public ProviderFileReference? File { get; init; }

    /// <summary>The caller-owned resource the image was committed to, or null when it was not stored.</summary>
    public InferenceResourceDescriptor? Resource { get; init; }
    /// <summary>The resource selection after the image was committed, or null when it was not stored.</summary>
    public InferenceResourceSelection? Selection { get; init; }

    /// <summary>The image width in pixels, or null when unknown.</summary>
    public int? Width { get; init; }

    /// <summary>The image height in pixels, or null when unknown.</summary>
    public int? Height { get; init; }
}

/// <summary>The images an image generation request produced, with their provenance.</summary>
public sealed record ImageGenerationResult
{
    /// <summary>The generated images, in provider output order.</summary>
    public IReadOnlyList<GeneratedImage> Images { get; init; } = [];

    /// <summary>The implementation, model and runtime that produced the images.</summary>
    public required InferenceProvenance Provenance { get; init; }
}

/// <summary>The stage an image generation request has reached.</summary>
public enum ImageGenerationStage
{
    /// <summary>Waiting to start.</summary>
    Queued,
    /// <summary>Encoding the prompt.</summary>
    EncodingPrompt,
    /// <summary>Generating the image.</summary>
    Generating,
    /// <summary>Decoding the generated image.</summary>
    Decoding,
    /// <summary>All images are ready.</summary>
    Completed
}

/// <summary>A progress report for an image generation request.</summary>
/// <param name="Stage">The stage the request has reached.</param>
/// <param name="Value">The progress within the stage, or null when the stage reports none.</param>
/// <param name="Maximum">The value that marks the stage complete, or null when unknown.</param>
public readonly record struct ImageGenerationProgress(
    ImageGenerationStage Stage,
    int? Value = null,
    int? Maximum = null);

/// <summary>What an image model accepts and produces.</summary>
public sealed record ImageCapabilities
{
    /// <summary>The image sizes the model produces. Empty when the provider lists no fixed sizes.</summary>
    public required IReadOnlyList<ImageSize> SupportedSizes { get; init; }

    /// <summary>The kinds of media source the model accepts as references.</summary>
    public IReadOnlySet<InferenceMediaSourceKind> SupportedReferenceKinds { get; init; } =
        System.Collections.Frozen.FrozenSet<InferenceMediaSourceKind>.Empty;

    /// <summary>The largest number of references one request may carry, or null for no stated limit.</summary>
    public int? MaximumReferences { get; init; }

    /// <summary>Whether the model honours <see cref="ImageGenerationRequest.NegativePrompt"/>.</summary>
    public InferenceSupport SupportsNegativePrompt { get; init; }

    /// <summary>Whether the model honours <see cref="ImageGenerationRequest.Seed"/>.</summary>
    public InferenceSupport SupportsSeed { get; init; }

    /// <summary>Whether the model honours <see cref="ImageGenerationRequest.Steps"/>.</summary>
    public InferenceSupport SupportsSteps { get; init; }

    /// <summary>Whether the model honours <see cref="ImageGenerationRequest.GuidanceScale"/>.</summary>
    public InferenceSupport SupportsGuidanceScale { get; init; }

    /// <summary>The largest <see cref="ImageGenerationRequest.Count"/> one request may ask for.</summary>
    public int MaximumImageCount { get; init; } = 1;
}

/// <summary>Generates images from text prompts and reference media.</summary>
public interface IImageInference : IAsyncDisposable
{
    /// <summary>Identifies the implementation behind this instance.</summary>
    InferenceImplementationDescriptor Descriptor { get; }

    /// <summary>Returns what the given model accepts and produces. A null model id leaves the model choice to the implementation.</summary>
    Task<ImageCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Generates the requested images, reporting stage progress to <paramref name="progress"/> when given.</summary>
    Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        IProgress<ImageGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
