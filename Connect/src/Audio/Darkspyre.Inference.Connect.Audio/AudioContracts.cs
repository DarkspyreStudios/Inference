using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.Audio;

public enum AudioTransport { TangoFluxHttp }

public sealed record TangoFluxEndpointOptions
{
    public string GeneratePath { get; init; } = "/generate";
}

public sealed record AudioConnection
{
    public AudioTransport Transport { get; init; } = AudioTransport.TangoFluxHttp;
    public required Uri Endpoint { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(12);
    /// <summary>Short-lived protected values supplied by the host. Never log or display this map.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public TangoFluxEndpointOptions TangoFlux { get; init; } = new();
}

public sealed record AudioGenerationRequest
{
    /// <summary>Opaque model input, transmitted verbatim as JSON text; never markup, a path or a log message.</summary>
    public required string Prompt { get; init; }
    public required TimeSpan Duration { get; init; }
    public int? Steps { get; init; }
    public double? GuidanceScale { get; init; }
    /// <summary>Null leaves seed selection to the server; the connector never invents one.</summary>
    public int? Seed { get; init; }
    /// <summary>Null leaves provider caching unchanged; false does not imply a random seed.</summary>
    public bool? UseCache { get; init; }
}

public sealed record AudioGenerationCapabilities(
    TimeSpan MinimumDuration, TimeSpan MaximumDuration,
    int MinimumSteps, int MaximumSteps, double MinimumGuidanceScale, double MaximumGuidanceScale,
    bool SupportsSeed, bool SupportsCachePreference, bool SupportsModelListing, bool SupportsHealthCheck,
    IReadOnlyList<string> ContentTypes);

/// <summary>Audio plus provenance. MeasuredDuration is null when not measured; it is never the requested duration.</summary>
public sealed record AudioGenerationResult(
    string ContentType, ReadOnlyMemory<byte> Audio, TimeSpan? MeasuredDuration, InferenceProvenance Provenance);

public interface IAudioGenerationClient
{
    InferenceImplementationDescriptor Descriptor { get; }
    Task<AudioGenerationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<AudioGenerationResult> GenerateAsync(AudioGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Adapter seam; the service binds a host-supplied connection to the request-only client.</summary>
public interface IAudioProvider
{
    AudioTransport Transport { get; }
    InferenceSetupDefinition SetupDefinition { get; }
    IAudioGenerationClient Bind(AudioConnection connection);
}
