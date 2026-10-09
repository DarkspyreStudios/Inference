using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.STT;

namespace Darkspyre.Inference.Connect.STT;

internal static class XaiSttProtocol
{
    internal static readonly int[] Rates = [8000, 16000, 22050, 24000, 44100, 48000];
    internal static SpeechTranscriptionOptions Snapshot(SpeechTranscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = options with { KeyTerms = options.KeyTerms };
        Validate(snapshot.SampleRate, snapshot.VadThreshold, snapshot.KeyTerms, snapshot.Channels, snapshot.TextNormalization, snapshot.Language);
        if (snapshot.RawAudioFormat == SpeechAudioEncoding.Opus) throw Unsupported();
        if (snapshot.RawAudioFormat is not null && snapshot.SampleRate is null
            || snapshot.Multichannel == true && snapshot.RawAudioFormat is not null && snapshot.Channels is null) throw Invalid();
        return snapshot;
    }
    internal static SpeechStreamOptions Snapshot(SpeechStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = options with { KeyTerms = options.KeyTerms };
        Validate(snapshot.SampleRate, snapshot.VadThreshold, snapshot.KeyTerms, snapshot.Channels, snapshot.TextNormalization, snapshot.Language);
        if (snapshot.Multichannel == true && (snapshot.Channels is null || snapshot.Encoding == SpeechAudioEncoding.Opus)) throw Invalid();
        Milliseconds(snapshot.Endpointing, 0); Milliseconds(snapshot.SmartTurnTimeout, 1);
        if (snapshot.SmartTurnThreshold is < 0 or > 1
            || snapshot.SmartTurnTimeout is not null && snapshot.SmartTurnThreshold is null) throw Invalid();
        return snapshot;
    }
    private static void Validate(int? rate, double? vad, IReadOnlyList<string>? terms, int? channels, bool? normalize, string? language)
    {
        if (rate is { } value && !Rates.Contains(value) || vad is < 0 or > 1 || channels is < 2 or > 8
            || terms is { Count: > 100 } || terms?.Any(string.IsNullOrWhiteSpace) == true
            || normalize == true && string.IsNullOrWhiteSpace(language)) throw Invalid();
    }
    internal static void ValidateFileMetadata(string? contentType, string? fileName, SpeechTranscriptionOptions options)
    {
        if (contentType is null || !MediaTypeHeaderValue.TryParse(contentType, out var parsed)) throw Invalid();
        if (fileName is not null)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw Invalid();
            var disposition = new ContentDispositionHeaderValue("form-data") { Name = "file", FileName = fileName, FileNameStar = fileName };
            if (!ContentDispositionHeaderValue.TryParse(disposition.ToString(), out _)) throw Invalid();
        }
        // A declared container is not raw/headerless audio. Do not inspect bytes or infer a format from its name.
        if (options.RawAudioFormat is not null && ContainerTypes.Contains(parsed.MediaType!)) throw Invalid();
    }
    private static readonly FrozenSet<string> ContainerTypes = new[]
    { "audio/wav", "audio/x-wav", "audio/vnd.wave", "audio/mpeg", "audio/ogg", "audio/flac", "audio/aac", "audio/mp4", "video/mp4", "video/x-matroska", "audio/opus" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static long? Milliseconds(TimeSpan? value, int minimum)
    {
        if (value is null) return null;
        if (value.Value.Ticks % TimeSpan.TicksPerMillisecond != 0 || value.Value.TotalMilliseconds < minimum || value.Value.TotalMilliseconds > 5000) throw Invalid();
        return value.Value.Ticks / TimeSpan.TicksPerMillisecond;
    }
    internal static SpeechTranscriptionInput Snapshot(SpeechTranscriptionInput input) => input switch
    {
        BinarySpeechInput binary when !string.IsNullOrWhiteSpace(binary.ContentType) => binary with { Data = binary.Data },
        UrlSpeechInput url when url.Url.IsAbsoluteUri && !url.Url.OriginalString.Any(char.IsControl) => url with { },
        ResourceSpeechInput resource => resource with { Resource = new(resource.Resource.WorkspaceId, resource.Resource.Id) },
        _ => throw Invalid()
    };
    internal static IEnumerable<KeyValuePair<string, string>> Fields(SpeechTranscriptionOptions options)
    {
        foreach (var field in Common(options.ModelId, options.Language, options.SampleRate, options.Diarization, options.Multichannel,
            options.Channels, options.KeyTerms, options.IncludeFillerWords, options.VadThreshold)) yield return field;
        if (options.RawAudioFormat is { } encoding) yield return new("audio_format", Encoding(encoding));
        if (options.TextNormalization is { } normalize) yield return new("format", Boolean(normalize));
    }
    internal static Uri StreamUri(Uri endpoint, SpeechStreamOptions options)
    {
        var fields = Common(options.ModelId, options.Language, options.SampleRate, options.Diarization, options.Multichannel,
            options.Channels, options.KeyTerms, options.IncludeFillerWords, options.VadThreshold).ToList();
        if (options.Encoding is { } encoding) fields.Add(new("encoding", Encoding(encoding)));
        if (options.TextNormalization is { } normalize) fields.Add(new("format", Boolean(normalize)));
        if (options.InterimResults is { } interim) fields.Add(new("interim_results", Boolean(interim)));
        if (Milliseconds(options.Endpointing, 0) is { } endpointing) fields.Add(new("endpointing", Number(endpointing)));
        if (options.SmartTurnThreshold is { } smart) fields.Add(new("smart_turn", Number(smart)));
        if (Milliseconds(options.SmartTurnTimeout, 1) is { } timeout) fields.Add(new("smart_turn_timeout", Number(timeout)));
        return new UriBuilder(new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/stt"))
        {
            Scheme = "wss",
            Port = -1,
            Query = string.Join("&", fields.Select(field => Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value)))
        }.Uri;
    }
    private static IEnumerable<KeyValuePair<string, string>> Common(string? model, string? language, int? rate, bool? diarize,
        bool? multichannel, int? channels, IReadOnlyList<string>? terms, bool? filler, double? vad)
    {
        if (model is not null) yield return new("model", model);
        if (language is not null) yield return new("language", language);
        if (rate is { } sampleRate) yield return new("sample_rate", Number(sampleRate));
        if (diarize is { } speakers) yield return new("diarize", Boolean(speakers));
        if (multichannel is { } multiple) yield return new("multichannel", Boolean(multiple));
        if (channels is { } count) yield return new("channels", Number(count));
        if (terms is not null) foreach (string term in terms) yield return new("keyterm", term);
        if (filler is { } fillers) yield return new("filler_words", Boolean(fillers));
        if (vad is { } threshold) yield return new("vad_threshold", Number(threshold));
    }
    private static string Number<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);
    private static string Boolean(bool value) => value ? "true" : "false";
    private static string Encoding(SpeechAudioEncoding value) => value switch
    { SpeechAudioEncoding.Pcm16LittleEndian => "pcm", SpeechAudioEncoding.Mulaw => "mulaw", SpeechAudioEncoding.Alaw => "alaw", SpeechAudioEncoding.Opus => "opus", _ => throw Invalid() };
    internal static SpeechTranscriptionCapabilities Capabilities() => new()
    {
        Transcription = Mode(false),
        Streaming = Mode(true)
    };
    private static SpeechTranscriptionModeCapabilities Mode(bool streaming) => new()
    {
        Available = true,
        InputKinds = (streaming ? new[] { InferenceMediaSourceKind.Binary } : new[] { InferenceMediaSourceKind.Binary, InferenceMediaSourceKind.Url, InferenceMediaSourceKind.Resource }).ToFrozenSet(),
        Encodings = (streaming ? Enum.GetValues<SpeechAudioEncoding>() : new[] { SpeechAudioEncoding.Pcm16LittleEndian, SpeechAudioEncoding.Mulaw, SpeechAudioEncoding.Alaw }).ToFrozenSet(),
        SampleRates = Rates,
        MaximumChannels = 8,
        SupportsLanguage = InferenceSupport.Supported,
        SupportsMultichannel = InferenceSupport.Supported,
        SupportsDiarization = InferenceSupport.Supported,
        SupportsKeyTerms = InferenceSupport.Supported,
        SupportsFillerWords = InferenceSupport.Supported,
        SupportsVadThreshold = InferenceSupport.Supported,
        SupportsTextNormalization = InferenceSupport.Supported,
        SupportsInterimResults = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported),
        SupportsEndpointing = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported),
        SupportsSmartTurn = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported),
        SupportsFinalize = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported)
    };
    internal static SpeechTranscriptionResult Result(JsonElement root, InferenceProvenance provenance) => new(
        RequiredString(root, "text"), provenance, OptionalString(root, "language"), Seconds(root, "duration"), Words(root), Channels(root));
    internal static JsonDocument Parse(string data)
    { try { return JsonDocument.Parse(data); } catch (JsonException) { throw Protocol(); } }
    internal static JsonDocument Parse(ReadOnlyMemory<byte> data)
    { try { return JsonDocument.Parse(data); } catch (JsonException) { throw Protocol(); } }
    internal static IReadOnlyList<SpeechWord>? Words(JsonElement root)
    {
        Object(root);
        if (!root.TryGetProperty("words", out var words)) return null;
        if (words.ValueKind != JsonValueKind.Array) throw Protocol();
        return Array.AsReadOnly(words.EnumerateArray().Select(word =>
        {
            TimeSpan start = Seconds(word, "start") ?? throw Protocol(), end = Seconds(word, "end") ?? throw Protocol();
            if (end < start) throw Protocol();
            return new SpeechWord(RequiredString(word, "text"), start, end, OptionalNumber(word, "confidence"), OptionalInteger(word, "speaker"));
        }).ToArray());
    }
    private static ReadOnlyCollection<SpeechChannelTranscript>? Channels(JsonElement root)
    {
        Object(root);
        if (!root.TryGetProperty("channels", out var channels)) return null;
        if (channels.ValueKind != JsonValueKind.Array) throw Protocol();
        return Array.AsReadOnly(channels.EnumerateArray().Select(channel => new SpeechChannelTranscript(
            OptionalInteger(channel, "index") ?? throw Protocol(), RequiredString(channel, "text"), OptionalString(channel, "language"), Words(channel))).ToArray());
    }
    internal static string RequiredString(JsonElement root, string name) => OptionalString(root, name) ?? throw Protocol();
    internal static string? OptionalString(JsonElement root, string name)
    {
        Object(root);
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw Protocol(); return value.GetString();
    }
    internal static double? OptionalNumber(JsonElement root, string name)
    {
        Object(root);
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number) || number < 0) throw Protocol(); return number;
    }
    internal static TimeSpan? Seconds(JsonElement root, string name)
    {
        double? number = OptionalNumber(root, name); if (number is null) return null;
        try { return TimeSpan.FromSeconds(number.Value); } catch (OverflowException) { throw Protocol(); }
    }
    internal static int? OptionalInteger(JsonElement root, string name)
    {
        Object(root);
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < 0) throw Protocol(); return number;
    }
    internal static bool RequiredBoolean(JsonElement root, string name)
    { Object(root); if (!root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Protocol(); return value.GetBoolean(); }
    private static void Object(JsonElement root) { if (root.ValueKind != JsonValueKind.Object) throw Protocol(); }
    internal static byte[] Command(string type, int? channel = null)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream))
        { writer.WriteStartObject(); writer.WriteString("type", type); if (channel is { } index) writer.WriteNumber("channel", index); writer.WriteEndObject(); }
        return stream.ToArray();
    }
    internal static ArgumentException Invalid() => new("The xAI transcription input or controls are invalid.");
    internal static NotSupportedException Unsupported() => new("The selected xAI transcription route does not support that control.");
    internal static InferenceException Protocol() => new(InferenceFailureCode.ProtocolFailure, "The xAI transcription response contains invalid protocol facts.");
}
