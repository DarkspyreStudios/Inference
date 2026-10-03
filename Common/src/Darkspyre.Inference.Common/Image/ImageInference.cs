using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.Image;

public sealed record ImageSize([property: JsonRequired] int Width, [property: JsonRequired] int Height);

public sealed record ImageGenerationRequest
{
    public required string ModelId { get; init; }

    public required string Prompt { get; init; }

    public string? NegativePrompt { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public int Count { get; init; } = 1;

    public long? Seed { get; init; }

    public int? Steps { get; init; }

    public double? GuidanceScale { get; init; }

    public IReadOnlyList<InferenceMediaSource> References { get; init; } = [];
}

public sealed record GeneratedImage
{
    public required string? ContentType { get; init; }

    public required ReadOnlyMemory<byte> Data { get; init; }

    public long? Seed { get; init; }

    public ProviderFileReference? File { get; init; }

    public InferenceResourceDescriptor? Resource { get; init; }
    public InferenceResourceSelection? Selection { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }
}

public sealed record ImageGenerationResult
{
    public IReadOnlyList<GeneratedImage> Images { get; init; } = [];

    public required InferenceProvenance Provenance { get; init; }
}

public enum ImageGenerationStage
{
    Queued,
    EncodingPrompt,
    Generating,
    Decoding,
    Completed
}

public readonly record struct ImageGenerationProgress(
    ImageGenerationStage Stage,
    int? Value = null,
    int? Maximum = null);

public sealed record ImageCapabilities
{
    public required IReadOnlyList<ImageSize> SupportedSizes { get; init; }

    public IReadOnlySet<InferenceMediaSourceKind> SupportedReferenceKinds { get; init; } =
        System.Collections.Frozen.FrozenSet<InferenceMediaSourceKind>.Empty;

    public int? MaximumReferences { get; init; }

    public bool SupportsNegativePrompt { get; init; }

    public bool SupportsSeed { get; init; }

    public bool SupportsSteps { get; init; }

    public bool SupportsGuidanceScale { get; init; }

    public int MaximumImageCount { get; init; } = 1;
}

public interface IImageInference : IAsyncDisposable
{
    InferenceImplementationDescriptor Descriptor { get; }

    Task<ImageCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default);

    Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        IProgress<ImageGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
