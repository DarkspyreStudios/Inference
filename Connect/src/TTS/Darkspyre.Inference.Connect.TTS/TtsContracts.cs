using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.TTS;

public enum TtsTransport { PocketTtsHttp = 0, XaiTts = 1 }
public enum PocketTtsProtocol { Multipart, BridgeJson }
public enum TtsAudioFormat { Wav = 0, Mp3 = 1, Pcm = 2, Mulaw = 3, Alaw = 4 }

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
    public string? Language { get; init; }
    public int? SampleRate { get; init; }
    public int? BitRate { get; init; }
    public bool? IncludeTimestamps { get; init; }
    public XaiTtsOptions? Xai { get; init; }
    public override string ToString() => "TtsRequest { <data> }";
}

public sealed record TtsVoice(string Id, string DisplayName, string? Locale = null, string? Description = null);
public sealed record TtsCapabilities(bool SupportsVoiceListing, bool SupportsSpeed, IReadOnlyList<TtsAudioFormat> Formats)
{
    [JsonRequired] public bool SupportsVoiceListing { get; init; } = SupportsVoiceListing;
    [JsonRequired] public bool SupportsSpeed { get; init; } = SupportsSpeed;
    [JsonRequired] public IReadOnlyList<TtsAudioFormat> Formats { get; init => field = TtsData.Copy(value); } = TtsData.Copy(Formats);
    public bool SupportsLanguage { get; init; }
    public bool SupportsSampleRate { get; init; }
    public bool SupportsBitRate { get; init; }
    public bool SupportsTimestamps { get; init; }
    public bool SupportsStreaming { get; init; }
    public bool SupportsRepeatedUtterances { get; init; }
    public bool SupportsTextNormalization { get; init; }
    public bool SupportsTextReplacement { get; init; }
    public bool SupportsOptimizeStreamingLatency { get; init; }
    public IReadOnlyList<int>? SampleRates { get; init => field = value is null ? null : TtsData.Copy(value); }
    public IReadOnlyList<int>? BitRates { get; init => field = value is null ? null : TtsData.Copy(value); }
}

public sealed record TtsResult(string ContentType, ReadOnlyMemory<byte> Audio, InferenceProvenance Provenance)
{
    [JsonRequired] public string ContentType { get; init => field = TtsData.Required(value); } = TtsData.Required(ContentType);
    [JsonRequired] public ReadOnlyMemory<byte> Audio { get; init => field = value.ToArray(); } = Audio.ToArray();
    [JsonRequired] public InferenceProvenance Provenance { get; init => field = TtsData.Required(value); } = TtsData.Required(Provenance);
    public TimeSpan? Duration { get; init; }
    public IReadOnlyList<TtsTimestamp>? Timestamps { get; init => field = value is null ? null : TtsData.Copy(value); }
    public override string ToString() => "TtsResult { <data> }";
}

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
