using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
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
    public required Uri Endpoint { get; init => field = TtsData.Required(value); }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Short-lived protected values supplied by the host. Never log or display this map.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init => field = TtsData.Copy(TtsData.Required(value), StringComparer.OrdinalIgnoreCase)!; } = TtsData.Copy(new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)!;
    public PocketTtsEndpointOptions PocketTts { get; init => field = TtsData.Required(value); } = new();
    public IProviderAuthorization? Authorization { get; init; }
    public override string ToString() => "TtsConnection { <data> }";
}

public sealed record TtsRequest
{
    public required string Text { get; init => field = TtsData.Required(value); }
    public string? VoiceId { get; init; }
    public double? Speed { get; init; }
    public TtsAudioFormat Format { get; init; } = TtsAudioFormat.Wav;
    public string? Language { get; init; }
    public int? SampleRate { get; init; }
    public int? BitRate { get; init; }
    public bool? IncludeTimestamps { get; init; }
    public override string ToString() => "TtsRequest { <data> }";
}

public sealed record TtsVoice(string Id, string DisplayName, string? Locale = null, string? Description = null)
{
    [JsonRequired] public string Id { get; init => field = TtsData.Required(value); } = TtsData.Required(Id);
    [JsonRequired] public string DisplayName { get; init => field = TtsData.Required(value); } = TtsData.Required(DisplayName);
    public override string ToString() => "TtsVoice { <data> }";
}
public sealed record TtsCapabilities(InferenceSupport SupportsVoiceListing, InferenceSupport SupportsSpeed, IReadOnlyList<TtsAudioFormat> Formats)
{
    [JsonRequired] public InferenceSupport SupportsVoiceListing { get; init; } = SupportsVoiceListing;
    [JsonRequired] public InferenceSupport SupportsSpeed { get; init; } = SupportsSpeed;
    [JsonRequired] public IReadOnlyList<TtsAudioFormat> Formats { get; init => field = TtsData.Copy(value); } = TtsData.Copy(Formats);
    public InferenceSupport SupportsLanguage { get; init; }
    public InferenceSupport SupportsSampleRate { get; init; }
    public InferenceSupport SupportsBitRate { get; init; }
    public InferenceSupport SupportsTimestamps { get; init; }
    public InferenceSupport SupportsStreaming { get; init; }
    public InferenceSupport SupportsRepeatedUtterances { get; init; }
    public InferenceSupport SupportsReplacementUpdates { get; init; }
    public InferenceSupport SupportsUtteranceClearing { get; init; }
    public InferenceSupport SupportsTextNormalization { get; init; }
    public InferenceSupport SupportsTextReplacement { get; init; }
    public InferenceSupport SupportsOptimizeStreamingLatency { get; init; }
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
    public InferenceResourceDescriptor? Resource { get; init; }
    public override string ToString() => "TtsResult { <data> }";
}

public interface ITtsClient : IDisposable, IAsyncDisposable
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
    ITtsClient Bind(TtsConnection connection,
        IInferenceResourceAccess? resources = null);
}
