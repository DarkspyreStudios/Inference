using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.TTS;

/// <summary>A text-to-speech provider protocol.</summary>
public enum TtsTransport
{
    /// <summary>A Pocket TTS HTTP server.</summary>
    PocketTtsHttp = 0,
    /// <summary>The xAI text-to-speech API.</summary>
    XaiTts = 1
}

/// <summary>How speech requests to a Pocket TTS server are encoded.</summary>
public enum PocketTtsProtocol
{
    /// <summary>A multipart form request.</summary>
    Multipart,
    /// <summary>A JSON request to a bridge server.</summary>
    BridgeJson
}

/// <summary>An audio format for synthesized speech.</summary>
public enum TtsAudioFormat
{
    /// <summary>WAV audio.</summary>
    Wav = 0,
    /// <summary>MP3 audio.</summary>
    Mp3 = 1,
    /// <summary>Raw PCM samples.</summary>
    Pcm = 2,
    /// <summary>G.711 mu-law audio.</summary>
    Mulaw = 3,
    /// <summary>G.711 A-law audio.</summary>
    Alaw = 4
}

/// <summary>Request settings for a Pocket TTS HTTP server.</summary>
public sealed record PocketTtsEndpointOptions
{
    /// <summary>How speech requests are encoded.</summary>
    public PocketTtsProtocol Protocol { get; init; }
    /// <summary>The path that lists voices.</summary>
    public string VoicesPath { get; init; } = "/voices";
    /// <summary>The path that synthesizes speech.</summary>
    public string SpeakPath { get; init; } = "/tts";
    /// <summary>The path that reports server health.</summary>
    public string HealthPath { get; init; } = "/health";
    /// <summary>The multipart form field that carries the voice.</summary>
    public string VoiceFormField { get; init; } = "voice_url";
}

/// <summary>A host-supplied connection to a text-to-speech provider.</summary>
public sealed record TtsConnection
{
    /// <summary>The provider protocol.</summary>
    public TtsTransport Transport { get; init; } = TtsTransport.PocketTtsHttp;
    /// <summary>The provider base address.</summary>
    public required Uri Endpoint { get; init => field = TtsData.Required(value); }
    /// <summary>The time limit for one request.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Short-lived protected values supplied by the host. Never log or display this map.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init => field = TtsData.Copy(TtsData.Required(value), StringComparer.OrdinalIgnoreCase)!; } = TtsData.Copy(new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)!;
    /// <summary>Request settings used when the transport is Pocket TTS.</summary>
    public PocketTtsEndpointOptions PocketTts { get; init => field = TtsData.Required(value); } = new();
    /// <summary>The provider authorization, or null when the provider needs none.</summary>
    public IProviderAuthorization? Authorization { get; init; }
    /// <summary>Returns a fixed placeholder so its values never reach logs.</summary>
    public override string ToString() => "TtsConnection { <data> }";
}

/// <summary>A request to synthesize speech from text.</summary>
public sealed record TtsRequest
{
    /// <summary>The text to speak.</summary>
    public required string Text { get; init => field = TtsData.Required(value); }
    /// <summary>The voice to use, or null for the provider default.</summary>
    public string? VoiceId { get; init; }
    /// <summary>The speaking speed multiplier, or null for the provider default.</summary>
    public double? Speed { get; init; }
    /// <summary>The audio format to return.</summary>
    public TtsAudioFormat Format { get; init; } = TtsAudioFormat.Wav;
    /// <summary>The language of the text, or null for the provider default.</summary>
    public string? Language { get; init; }
    /// <summary>The sample rate in hertz, or null for the provider default.</summary>
    public int? SampleRate { get; init; }
    /// <summary>The bit rate in bits per second, or null for the provider default.</summary>
    public int? BitRate { get; init; }
    /// <summary>Whether the result carries word timestamps, or null for the provider default.</summary>
    public bool? IncludeTimestamps { get; init; }
    /// <summary>Returns a fixed placeholder so its values never reach logs.</summary>
    public override string ToString() => "TtsRequest { <data> }";
}

/// <summary>A voice the provider offers.</summary>
/// <param name="Id">The voice identifier.</param>
/// <param name="DisplayName">The voice name shown to the user.</param>
/// <param name="Locale">The voice locale, or null when unknown.</param>
/// <param name="Description">A description of the voice, or null.</param>
public sealed record TtsVoice(string Id, string DisplayName, string? Locale = null, string? Description = null)
{
    /// <summary>The voice identifier.</summary>
    [JsonRequired] public string Id { get; init => field = TtsData.Required(value); } = TtsData.Required(Id);
    /// <summary>The voice name shown to the user.</summary>
    [JsonRequired] public string DisplayName { get; init => field = TtsData.Required(value); } = TtsData.Required(DisplayName);
    /// <summary>Returns a fixed placeholder so its values never reach logs.</summary>
    public override string ToString() => "TtsVoice { <data> }";
}
/// <summary>The text-to-speech features a provider supports.</summary>
/// <param name="SupportsVoiceListing">Whether the provider lists its voices.</param>
/// <param name="SupportsSpeed">Whether a request can set the speaking speed.</param>
/// <param name="Formats">The audio formats the provider returns.</param>
public sealed record TtsCapabilities(InferenceSupport SupportsVoiceListing, InferenceSupport SupportsSpeed, IReadOnlyList<TtsAudioFormat> Formats)
{
    /// <summary>Whether the provider lists its voices.</summary>
    [JsonRequired] public InferenceSupport SupportsVoiceListing { get; init; } = SupportsVoiceListing;
    /// <summary>Whether a request can set the speaking speed.</summary>
    [JsonRequired] public InferenceSupport SupportsSpeed { get; init; } = SupportsSpeed;
    /// <summary>The audio formats the provider returns. The list is copied.</summary>
    [JsonRequired] public IReadOnlyList<TtsAudioFormat> Formats { get; init => field = TtsData.Copy(value); } = TtsData.Copy(Formats);
    /// <summary>Whether a request can set the language.</summary>
    public InferenceSupport SupportsLanguage { get; init; }
    /// <summary>Whether a request can set the sample rate.</summary>
    public InferenceSupport SupportsSampleRate { get; init; }
    /// <summary>Whether a request can set the bit rate.</summary>
    public InferenceSupport SupportsBitRate { get; init; }
    /// <summary>Whether the provider returns word timestamps.</summary>
    public InferenceSupport SupportsTimestamps { get; init; }
    /// <summary>Whether the provider streams audio.</summary>
    public InferenceSupport SupportsStreaming { get; init; }
    /// <summary>Whether a streaming session can speak more than one utterance.</summary>
    public InferenceSupport SupportsRepeatedUtterances { get; init; }
    /// <summary>Whether a streaming session accepts updated text replacements.</summary>
    public InferenceSupport SupportsReplacementUpdates { get; init; }
    /// <summary>Whether a streaming session can clear a pending utterance.</summary>
    public InferenceSupport SupportsUtteranceClearing { get; init; }
    /// <summary>Whether the provider can normalize text before speaking it.</summary>
    public InferenceSupport SupportsTextNormalization { get; init; }
    /// <summary>Whether the provider applies text replacements.</summary>
    public InferenceSupport SupportsTextReplacement { get; init; }
    /// <summary>Whether a request can trade quality for lower streaming latency.</summary>
    public InferenceSupport SupportsOptimizeStreamingLatency { get; init; }
    /// <summary>The sample rates in hertz the provider accepts, or null when unknown.</summary>
    public IReadOnlyList<int>? SampleRates { get; init => field = value is null ? null : TtsData.Copy(value); }
    /// <summary>The bit rates in bits per second the provider accepts, or null when unknown.</summary>
    public IReadOnlyList<int>? BitRates { get; init => field = value is null ? null : TtsData.Copy(value); }
}

/// <summary>Synthesized speech audio.</summary>
/// <param name="ContentType">The audio media type.</param>
/// <param name="Audio">The audio bytes.</param>
/// <param name="Provenance">Which implementation and model produced the audio.</param>
public sealed record TtsResult(string ContentType, ReadOnlyMemory<byte> Audio, InferenceProvenance Provenance)
{
    /// <summary>The audio media type.</summary>
    [JsonRequired] public string ContentType { get; init => field = TtsData.Required(value); } = TtsData.Required(ContentType);
    /// <summary>The audio bytes. The value is copied.</summary>
    [JsonRequired] public ReadOnlyMemory<byte> Audio { get; init => field = value.ToArray(); } = Audio.ToArray();
    /// <summary>Which implementation and model produced the audio.</summary>
    [JsonRequired] public InferenceProvenance Provenance { get; init => field = TtsData.Required(value); } = TtsData.Required(Provenance);
    /// <summary>The audio length, or null when unknown.</summary>
    public TimeSpan? Duration { get; init; }
    /// <summary>Word timestamps, or null when not requested or not supported.</summary>
    public IReadOnlyList<TtsTimestamp>? Timestamps { get; init => field = value is null ? null : TtsData.Copy(value); }
    /// <summary>The caller resource the audio was saved to, or null.</summary>
    public InferenceResourceDescriptor? Resource { get; init; }
    /// <summary>Returns a fixed placeholder so its values never reach logs.</summary>
    public override string ToString() => "TtsResult { <data> }";
}

/// <summary>A text-to-speech client bound to one connection.</summary>
public interface ITtsClient : IDisposable, IAsyncDisposable
{
    /// <summary>Identifies the implementation behind the client.</summary>
    InferenceImplementationDescriptor Descriptor { get; }
    /// <summary>Returns the features the provider supports.</summary>
    Task<TtsCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns the voices the provider offers.</summary>
    Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken cancellationToken = default);
    /// <summary>Synthesizes speech for the request.</summary>
    Task<TtsResult> SpeakAsync(TtsRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Adapter seam; the service binds a host-supplied connection to the request-only client.</summary>
public interface ITtsProvider
{
    /// <summary>The provider protocol this adapter implements.</summary>
    TtsTransport Transport { get; }
    /// <summary>How the provider takes part in setup.</summary>
    InferenceSetupDefinition SetupDefinition { get; }
    /// <summary>Binds a connection to a client, reading and writing caller resources through <paramref name="resources"/> when given.</summary>
    ITtsClient Bind(TtsConnection connection,
        IInferenceResourceAccess? resources = null);
}
