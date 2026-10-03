using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.TTS;

internal static class XaiTtsProtocol
{
    internal static readonly int[] Rates = [8000, 16000, 22050, 24000, 44100, 48000];
    internal static readonly int[] BitRates = [32000, 64000, 96000, 128000, 192000];

    internal static TtsRequest Snapshot(TtsConnection connection, TtsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        TtsRequest snapshot = request with { Xai = TtsData.EffectiveOptions(connection.XaiDefaults, request.Xai) };
        if (string.IsNullOrWhiteSpace(snapshot.Text)) throw Invalid();
        Validate(new(snapshot.VoiceId, snapshot.Speed, snapshot.Format, snapshot.Language, snapshot.SampleRate, snapshot.BitRate, snapshot.IncludeTimestamps, snapshot.Xai));
        return snapshot;
    }

    internal static TtsStreamOptions Snapshot(TtsConnection connection, TtsStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = options with { Xai = TtsData.EffectiveOptions(connection.XaiDefaults, options.Xai) };
        Validate(snapshot); return snapshot;
    }

    private static void Validate(TtsStreamOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Language) || !Enum.IsDefined(options.Format)
            || options.Speed is { } speed && (!double.IsFinite(speed) || speed is < 0.7 or > 1.5)
            || options.SampleRate is { } rate && !Rates.Contains(rate)
            || options.BitRate is { } bits && (options.Format != TtsAudioFormat.Mp3 || !BitRates.Contains(bits))) throw Invalid();
        // The guide admits latency 2 while the standalone schema lists 0/1. Preserve the caller value for provider qualification.
    }

    internal static byte[] Payload(TtsRequest request) => Json(writer =>
    {
        writer.WriteString("text", request.Text); writer.WriteString("language", request.Language);
        if (request.VoiceId is not null) writer.WriteString("voice_id", request.VoiceId);
        writer.WriteStartObject("output_format"); writer.WriteString("codec", Codec(request.Format));
        if (request.SampleRate is { } rate) writer.WriteNumber("sample_rate", rate);
        if (request.BitRate is { } bits) writer.WriteNumber("bit_rate", bits);
        writer.WriteEndObject();
        if (request.Speed is { } speed) writer.WriteNumber("speed", speed);
        if (request.IncludeTimestamps is { } timing) writer.WriteBoolean("with_timestamps", timing);
        if (request.Xai?.OptimizeStreamingLatency is { } latency) writer.WriteNumber("optimize_streaming_latency", latency);
        if (request.Xai?.TextNormalization is { } normalize) writer.WriteBoolean("text_normalization", normalize);
        if (request.Xai?.Replace is { } replace) WriteReplace(writer, replace);
    });

    internal static Uri StreamUri(Uri endpoint, TtsStreamOptions options)
    {
        var query = new List<KeyValuePair<string, string>> { new("language", options.Language!), new("codec", Codec(options.Format)) };
        Add("voice", options.VoiceId); Add("speed", options.Speed); Add("sample_rate", options.SampleRate); Add("bit_rate", options.BitRate);
        Add("with_timestamps", options.IncludeTimestamps); Add("text_normalization", options.Xai?.TextNormalization); Add("optimize_streaming_latency", options.Xai?.OptimizeStreamingLatency);
        var builder = new UriBuilder(new Uri(new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/"), "tts")) { Scheme = "wss", Port = -1 };
        builder.Query = string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return builder.Uri;
        void Add(string name, object? value)
        {
            if (value is not null) query.Add(new(name, value is bool boolean ? boolean ? "true" : "false" : Convert.ToString(value, CultureInfo.InvariantCulture)!));
        }
    }

    internal static string Codec(TtsAudioFormat format) => format switch
    { TtsAudioFormat.Wav => "wav", TtsAudioFormat.Mp3 => "mp3", TtsAudioFormat.Pcm => "pcm", TtsAudioFormat.Mulaw => "mulaw", TtsAudioFormat.Alaw => "alaw", _ => throw Invalid() };

    internal static byte[] Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(); using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject(); write(writer); writer.WriteEndObject(); writer.Flush(); return buffer.WrittenSpan.ToArray();
    }
    internal static void WriteReplace(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> replacements)
    {
        writer.WriteStartObject("replace"); foreach ((string key, string value) in replacements) writer.WriteString(key, value); writer.WriteEndObject();
    }
    internal static string RequiredString(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw Protocol();
    internal static string? OptionalString(JsonElement parent, string name) => !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
        ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw Protocol();
    internal static byte[] Audio(JsonElement parent, string name)
    {
        try { return Convert.FromBase64String(RequiredString(parent, name)); }
        catch (FormatException) { throw Protocol(); }
    }
    internal static TimeSpan? Seconds(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return Time(value);
    }
    private static TimeSpan Time(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double seconds) || !double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds) throw Protocol();
        try { return TimeSpan.FromSeconds(seconds); } catch (OverflowException) { throw Protocol(); }
    }
    internal static IReadOnlyList<TtsTimestamp>? Timings(JsonElement parent)
    {
        if (!parent.TryGetProperty("audio_timestamps", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("graph_chars", out var characters) || characters.ValueKind != JsonValueKind.Array
            || !value.TryGetProperty("graph_times", out var times) || times.ValueKind != JsonValueKind.Array || characters.GetArrayLength() != times.GetArrayLength()) throw Protocol();
        var result = new List<TtsTimestamp>();
        for (int i = 0; i < characters.GetArrayLength(); i++)
        {
            var pair = times[i]; if (characters[i].ValueKind != JsonValueKind.String || pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2) throw Protocol();
            TimeSpan start = Time(pair[0]), end = Time(pair[1]); if (end < start) throw Protocol();
            result.Add(new(characters[i].GetString()!, start, end));
        }
        return result.AsReadOnly();
    }
    internal static InferenceException Protocol() => new(InferenceFailureCode.ProtocolFailure, "xAI returned invalid speech protocol data.");
    internal static InferenceException Invalid() => new(InferenceFailureCode.InvalidRequest, "Supply supported xAI speech controls and an explicit language.");
}
