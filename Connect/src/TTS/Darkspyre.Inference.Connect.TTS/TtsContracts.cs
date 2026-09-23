using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.TTS;

public enum TtsTransport { PocketTtsHttp }
public enum PocketTtsProtocol { Multipart, BridgeJson }
public enum TtsAudioFormat { Wav }

public sealed record PocketTtsEndpointOptions
{
    public PocketTtsProtocol Protocol { get; init; }
    public string VoicesPath { get; init; } = "/voices";
    public string SpeakPath { get; init; } = "/tts";
    public string HealthPath { get; init; } = "/health";
    public string VoiceFormField { get; init; } = "voice_url";
}

public sealed record TtsConnection
{
    public TtsTransport Transport { get; init; } = TtsTransport.PocketTtsHttp;
    public required Uri Endpoint { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Short-lived protected values supplied by the host. Never log or display this map.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public PocketTtsEndpointOptions PocketTts { get; init; } = new();
}

public sealed record TtsRequest
{
    public required string Text { get; init; }
    public string? VoiceId { get; init; }
    public double? Speed { get; init; }
    public TtsAudioFormat Format { get; init; } = TtsAudioFormat.Wav;
}

public sealed record TtsVoice(string Id, string DisplayName, string? Locale = null, string? Description = null);
public sealed record TtsCapabilities(bool SupportsVoiceListing, bool SupportsSpeed, IReadOnlyList<TtsAudioFormat> Formats);
public sealed record TtsResult(string ContentType, ReadOnlyMemory<byte> Audio, InferenceProvenance Provenance);

public interface ITtsClient
{
    InferenceImplementationDescriptor Descriptor { get; }
    Task<TtsCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken cancellationToken = default);
    Task<TtsResult> SpeakAsync(TtsRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Adapter seam; the service binds a host-supplied connection to the request-only client.</summary>
public interface ITtsProvider
{
    TtsTransport Transport { get; }
    InferenceSetupDefinition SetupDefinition { get; }
    ITtsClient Bind(TtsConnection connection);
}
