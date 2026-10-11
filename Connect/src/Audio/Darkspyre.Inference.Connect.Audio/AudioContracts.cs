using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.Audio;

/// <summary>The wire protocol an audio generation connection uses. <c>TangoFluxHttp</c> is the TangoFlux HTTP generation API.</summary>
public enum AudioTransport
{
    /// <summary>The connection calls the generation API of a TangoFlux server over HTTP.</summary>
    TangoFluxHttp
}

/// <summary>Endpoint settings specific to a TangoFlux server.</summary>
public sealed record TangoFluxEndpointOptions
{
    /// <summary>The generation path, relative to <see cref="AudioConnection.Endpoint"/>.</summary>
    public string GeneratePath { get; init; } = "/generate";
}

/// <summary>A host-supplied connection to an audio generation server.</summary>
public sealed record AudioConnection
{
    /// <summary>The wire protocol of the server.</summary>
    public AudioTransport Transport { get; init; } = AudioTransport.TangoFluxHttp;
    /// <summary>The base URI of the server.</summary>
    public required Uri Endpoint { get; init; }
    /// <summary>The time limit for one generation request.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(12);
    /// <summary>Short-lived protected values supplied by the host. Never log or display this map.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    /// <summary>TangoFlux endpoint settings, used when <see cref="Transport"/> is <see cref="AudioTransport.TangoFluxHttp"/>.</summary>
    public TangoFluxEndpointOptions TangoFlux { get; init; } = new();
}

/// <summary>One request to generate audio from a text prompt.</summary>
public sealed record AudioGenerationRequest
{
    /// <summary>Opaque model input, transmitted verbatim as JSON text; never markup, a path or a log message.</summary>
    public required string Prompt { get; init; }
    /// <summary>The length of audio to generate.</summary>
    public required TimeSpan Duration { get; init; }
    /// <summary>The number of diffusion steps, or null for the provider default.</summary>
    public int? Steps { get; init; }
    /// <summary>The classifier-free guidance scale, or null for the provider default.</summary>
    public double? GuidanceScale { get; init; }
    /// <summary>Null leaves seed selection to the server; the connector never invents one.</summary>
    public int? Seed { get; init; }
    /// <summary>Null leaves provider caching unchanged; false does not imply a random seed.</summary>
    public bool? UseCache { get; init; }
}

/// <summary>The request limits and optional features an audio generation provider supports.</summary>
/// <param name="MinimumDuration">The shortest audio length a request may ask for.</param>
/// <param name="MaximumDuration">The longest audio length a request may ask for.</param>
/// <param name="MinimumSteps">The smallest accepted diffusion step count.</param>
/// <param name="MaximumSteps">The largest accepted diffusion step count.</param>
/// <param name="MinimumGuidanceScale">The smallest accepted guidance scale.</param>
/// <param name="MaximumGuidanceScale">The largest accepted guidance scale.</param>
/// <param name="SupportsSeed">Whether the provider honours a requested seed.</param>
/// <param name="SupportsCachePreference">Whether the provider honours a cache preference.</param>
/// <param name="SupportsModelListing">Whether the provider lists its models.</param>
/// <param name="SupportsHealthCheck">Whether the provider offers a health check.</param>
/// <param name="ContentTypes">The media types of the audio the provider returns.</param>
public sealed record AudioGenerationCapabilities(
    TimeSpan MinimumDuration, TimeSpan MaximumDuration,
    int MinimumSteps, int MaximumSteps, double MinimumGuidanceScale, double MaximumGuidanceScale,
    InferenceSupport SupportsSeed, InferenceSupport SupportsCachePreference, InferenceSupport SupportsModelListing, InferenceSupport SupportsHealthCheck,
    IReadOnlyList<string> ContentTypes);

/// <summary>Audio plus provenance. MeasuredDuration is null when not measured; it is never the requested duration.</summary>
/// <param name="ContentType">The media type of the audio.</param>
/// <param name="Audio">The generated audio bytes.</param>
/// <param name="MeasuredDuration">The measured length of the audio, or null when not measured.</param>
/// <param name="Provenance">Where the audio came from.</param>
public sealed record AudioGenerationResult(
    string ContentType, ReadOnlyMemory<byte> Audio, TimeSpan? MeasuredDuration, InferenceProvenance Provenance);

/// <summary>Generates audio over one bound connection.</summary>
public interface IAudioGenerationClient
{
    /// <summary>Identifies the implementation behind this client.</summary>
    InferenceImplementationDescriptor Descriptor { get; }
    /// <summary>Returns the request limits and optional features of the provider.</summary>
    Task<AudioGenerationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    /// <summary>Generates audio for the request and returns it with its provenance. Throws when the request falls outside the provider limits.</summary>
    Task<AudioGenerationResult> GenerateAsync(AudioGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Adapter seam; the service binds a host-supplied connection to the request-only client.</summary>
public interface IAudioProvider
{
    /// <summary>The wire protocol this provider implements.</summary>
    AudioTransport Transport { get; }
    /// <summary>The setup definition that describes this provider's settings.</summary>
    InferenceSetupDefinition SetupDefinition { get; }
    /// <summary>Creates a client that sends requests over the given connection. Throws when the connection is invalid for this provider.</summary>
    IAudioGenerationClient Bind(AudioConnection connection);
}
