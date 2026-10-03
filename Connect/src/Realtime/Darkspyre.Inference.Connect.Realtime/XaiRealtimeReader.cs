using System.Globalization;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Realtime;

internal static class XaiRealtimeReader
{
    internal static void ValidateMetadata(JsonElement root, string[] secrets)
    {
        // Identity/configuration strings stay exact. A protected value cannot be replaced and
        // presented as a different actual observation. Content and numeric facts are not rewritten.
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is "headers" or "metadata")
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                    foreach (var entry in property.Value.EnumerateObject())
                    {
                        Check(entry.Name);
                        if (entry.Value.ValueKind == JsonValueKind.String) Check(entry.Value.GetString()!);
                    }
            }
            else if (property.Value.ValueKind == JsonValueKind.String && property.Name is "event_id" or "response_id" or "item_id" or "previous_item_id"
                or "id" or "model" or "name" or "call_id" or "status" or "code" or "param" or "server_url" or "server_label" or "type")
                Check(property.Value.GetString()!);
            if (property.Value.ValueKind == JsonValueKind.Object && property.Name is "session" or "conversation" or "response" or "item" or "error")
                ValidateMetadata(property.Value, secrets);
            if (property.Name == "tools" && property.Value.ValueKind == JsonValueKind.Array)
                foreach (var tool in property.Value.EnumerateArray())
                    if (tool.ValueKind == JsonValueKind.Object) ValidateMetadata(tool, secrets);
        }
        void Check(string value) { if (SecretRedactor.ContainsProtectedValue(value, null, secrets)) throw XaiRealtimeProtocol.Malformed(); }
    }
    internal static RealtimeEvent? Parse(JsonElement root)
    {
        string type = Required(root, "type"); RealtimeEventIdentity identity = Identity(root);
        switch (type)
        {
            case "session.created": case "session.updated":
                JsonElement session = Object(root, "session");
                return new RealtimeSessionEvent(identity, type, String(session, "id"), Config(session)) { Modalities = Modalities(session, "modalities") };
            case "input_audio_buffer.speech_started": case "input_audio_buffer.speech_stopped":
                return new RealtimeInputSpeechEvent(identity, type, Milliseconds(root, "audio_start_ms"), Milliseconds(root, "audio_end_ms"));
            case "input_audio_buffer.committed": case "input_audio_buffer.cleared": case "input_audio_buffer.timeout_triggered":
                return new RealtimeInputBufferEvent(identity, type, String(root, "previous_item_id"));
            case "conversation.item.added": case "conversation.item.created": case "response.output_item.added": case "response.output_item.done":
                JsonElement item = Object(root, "item");
                return new RealtimeItemEvent(identity with { ItemId = identity.ItemId ?? String(item, "id") }, type, Item(item), String(root, "previous_item_id")) { ProviderStatus = String(item, "status") };
            case "conversation.item.deleted": case "conversation.item.truncated":
                return new RealtimeItemEvent(identity, type, null, null, Milliseconds(root, "audio_end_ms"), String(root, "transcript"));
            case "conversation.item.input_audio_transcription.updated": case "conversation.item.input_audio_transcription.completed":
                return new RealtimeTranscriptEvent(identity, Required(root, "transcript"), true, true, type.EndsWith("completed", StringComparison.Ordinal));
            case "response.created": case "response.done":
                JsonElement response = Object(root, "response"); string? status = String(response, "status");
                LlmUsage? usage = null;
                if (OptionalObject(response, "usage") is { } observed)
                    usage = new LlmUsage(Count(observed, "input_tokens"), Count(observed, "output_tokens"), Count(observed, "total_tokens"));
                return new RealtimeResponseEvent(identity with { ResponseId = String(response, "id") ?? identity.ResponseId }, type,
                    status switch { "completed" => LlmCompletionStatus.Completed, "incomplete" => LlmCompletionStatus.Incomplete, "failed" => LlmCompletionStatus.Failed, _ => null },
                    usage, Map(response, "metadata")) { ProviderStatus = status };
            case "response.content_part.added": case "response.content_part.done":
                return new RealtimeContentPartEvent(identity, type, Content(Object(root, "part")));
            case "response.output_audio.delta": case "response.audio.delta":
                return new RealtimeAudioDeltaEvent(identity, Audio(root, "delta"));
            case "response.output_audio.done":
                return new RealtimeAudioDoneEvent(identity);
            case "response.output_audio_transcript.delta": case "response.output_audio_transcript.done":
                bool done = type.EndsWith("done", StringComparison.Ordinal);
                return new RealtimeTranscriptEvent(identity, Required(root, done ? "transcript" : "delta"), false, done, done);
            case "response.text.delta": case "response.output_text.delta":
                return new RealtimeTextDeltaEvent(identity, Required(root, "delta"));
            case "response.function_call_arguments.delta": case "response.function_call_arguments.done":
                bool final = type.EndsWith("done", StringComparison.Ordinal);
                return new RealtimeFunctionArgumentsEvent(identity, String(root, "call_id"), String(root, "name"), Required(root, final ? "arguments" : "delta"), final);
            case "mcp_list_tools.in_progress": case "mcp_list_tools.completed": case "mcp_list_tools.failed":
            case "response.mcp_call.in_progress": case "response.mcp_call.completed": case "response.mcp_call.failed":
                return new RealtimeHostedObservationEvent(identity, type, LlmHostedToolKind.RemoteMcp, Error: Error(root, "error"));
            case "response.mcp_call_arguments.delta": case "response.mcp_call_arguments.done":
                bool argumentsFinal = type.EndsWith("done", StringComparison.Ordinal);
                return new RealtimeHostedObservationEvent(identity, type, LlmHostedToolKind.RemoteMcp, String(root, "call_id"), String(root, "name"),
                    Required(root, argumentsFinal ? "arguments" : "delta"), argumentsFinal);
            case "error":
                JsonElement error = Object(root, "error");
                return new RealtimeErrorEvent(identity, new(InferenceFailureCode.ExecutionFailed, SafeCode(String(error, "code")), "xAI realtime reported a failure."),
                    SafeCode(String(error, "param")), String(error, "event_id"));
            default: return null;
        }
    }

    internal static RealtimeEventIdentity Identity(JsonElement root) => new(String(root, "event_id"), String(root, "response_id"), String(root, "item_id"), Index(root, "output_index"), Index(root, "content_index"));
    private static int? Index(JsonElement root, string name)
    {
        if (!Value(root, name, out JsonElement value)) return null;
        return value.TryGetInt32(out int number) && number >= 0 ? number : throw XaiRealtimeProtocol.Malformed();
    }
    private static int? Count(JsonElement root, string name)
    {
        if (!Value(root, name, out JsonElement value)) return null;
        return value.TryGetInt32(out int number) && number >= 0 ? number : throw XaiRealtimeProtocol.Malformed();
    }
    internal static string? String(JsonElement root, string name)
    {
        if (!Value(root, name, out JsonElement value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw XaiRealtimeProtocol.Malformed();
    }
    internal static string Required(JsonElement root, string name) => String(root, name) ?? throw XaiRealtimeProtocol.Malformed();
    internal static bool Value(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind != JsonValueKind.Object) throw XaiRealtimeProtocol.Malformed();
        return root.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }
    internal static JsonElement Object(JsonElement root, string name) => OptionalObject(root, name) ?? throw XaiRealtimeProtocol.Malformed();
    private static JsonElement? OptionalObject(JsonElement root, string name)
    { if (!Value(root, name, out JsonElement value)) return null; return value.ValueKind == JsonValueKind.Object ? value : throw XaiRealtimeProtocol.Malformed(); }
    private static TimeSpan? Milliseconds(JsonElement root, string name)
    {
        if (!Value(root, name, out JsonElement value)) return null;
        if (!value.TryGetDouble(out double milliseconds) || !double.IsFinite(milliseconds) || milliseconds < 0) throw XaiRealtimeProtocol.Malformed();
        try { return TimeSpan.FromMilliseconds(milliseconds); } catch (OverflowException) { throw XaiRealtimeProtocol.Malformed(); }
    }
    private static byte[] Audio(JsonElement root, string name)
    {
        string raw = Required(root, name);
        try { return Convert.FromBase64String(raw); } catch (FormatException) { throw XaiRealtimeProtocol.Malformed(); }
    }
    private static string? SafeCode(string? value) => value is not null && value.Length <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') ? value : null;
    private static InferenceResourceFailure? Error(JsonElement root, string name)
    {
        if (!Value(root, name, out JsonElement value)) return null;
        return new(InferenceFailureCode.ExecutionFailed, value.ValueKind == JsonValueKind.Object ? SafeCode(String(value, "code")) : null, "xAI hosted realtime work reported a failure.");
    }
    private static Dictionary<string, string>? Map(JsonElement root, string name)
    {
        if (!Value(root, name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.Object) throw XaiRealtimeProtocol.Malformed();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in value.EnumerateObject())
            if (pair.Value.ValueKind != JsonValueKind.String || !map.TryAdd(pair.Name, pair.Value.GetString()!)) throw XaiRealtimeProtocol.Malformed();
        return map;
    }
    private static string[]? Strings(JsonElement root, string name)
    {
        if (!Value(root, name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.Array) throw XaiRealtimeProtocol.Malformed();
        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw XaiRealtimeProtocol.Malformed()).ToArray();
    }
    private static RealtimeModality[]? Modalities(JsonElement root, string name) => Strings(root, name)?.Select(value => value switch
    { "text" => RealtimeModality.Text, "audio" => RealtimeModality.Audio, _ => throw XaiRealtimeProtocol.Malformed() }).ToArray();

    private static RealtimeContent Content(JsonElement part) => Required(part, "type") switch
    {
        "input_text" or "text" or "output_text" => new RealtimeTextContent(String(part, "text")) { Transcript = String(part, "transcript") },
        "input_audio" or "audio" or "output_audio" => new RealtimeAudioContent(Value(part, "audio", out _) ? Audio(part, "audio") : null, String(part, "transcript")),
        _ => throw XaiRealtimeProtocol.Malformed()
    };
    private static RealtimeConversationItem Item(JsonElement item)
    {
        RealtimeConversationItem result;
        switch (Required(item, "type"))
        {
            case "message":
                if (!Value(item, "content", out JsonElement content) || content.ValueKind != JsonValueKind.Array) throw XaiRealtimeProtocol.Malformed();
                var role = Required(item, "role") switch { "user" => RealtimeMessageRole.User, "assistant" => RealtimeMessageRole.Assistant, "system" => RealtimeMessageRole.System, _ => throw XaiRealtimeProtocol.Malformed() };
                result = new RealtimeMessageItem(role, content.EnumerateArray().Select(Content).ToArray()); break;
            case "function_call": result = new RealtimeFunctionCallItem(String(item, "name"), String(item, "arguments"), String(item, "call_id")); break;
            case "function_call_output": result = new RealtimeFunctionOutputItem(Required(item, "call_id"), LlmToolOutput.FromText(Required(item, "output"))); break;
            default: throw XaiRealtimeProtocol.Malformed();
        }
        return result with { Id = String(item, "id") };
    }
    private static RealtimeAudioConfig? AudioConfig(JsonElement root, string name)
    {
        if (OptionalObject(root, name) is not { } audio || OptionalObject(audio, "format") is not { } format) return null;
        var encoding = Required(format, "type") switch
        { "audio/pcm" => RealtimeAudioEncoding.Pcm16LittleEndian, "audio/pcmu" => RealtimeAudioEncoding.Mulaw, "audio/pcma" => RealtimeAudioEncoding.Alaw, "audio/opus" => RealtimeAudioEncoding.Opus, _ => throw XaiRealtimeProtocol.Malformed() };
        int? rate = Index(format, "rate");
        RealtimeAudioTransport? transport = String(audio, "transport") switch { null => null, "json" => RealtimeAudioTransport.Json, "binary" => RealtimeAudioTransport.Binary, _ => throw XaiRealtimeProtocol.Malformed() };
        return new(encoding, rate, transport);
    }
    private static RealtimeSessionConfig Config(JsonElement session)
    {
        var functions = new List<LlmToolDefinition>(); var hosted = new List<LlmHostedTool>();
        bool hasTools = Value(session, "tools", out JsonElement tools);
        if (hasTools)
        {
            if (tools.ValueKind != JsonValueKind.Array) throw XaiRealtimeProtocol.Malformed();
            foreach (var tool in tools.EnumerateArray())
            {
                switch (Required(tool, "type"))
                {
                    case "function":
                        var function = Object(tool, "function"); var parameters = Object(function, "parameters");
                        functions.Add(new() { Name = Required(function, "name"), Description = String(function, "description"), InputSchema = parameters.Clone() }); break;
                    case "web_search":
                        LlmWebSearchLocation? location = OptionalObject(tool, "location") is { } place ? new(String(place, "country"), String(place, "city"), String(place, "region"), String(place, "timezone")) : null;
                        hosted.Add(new LlmWebSearchTool(Strings(tool, "allowed_domains"), Strings(tool, "excluded_domains"), Boolean(tool, "enable_image_understanding") ?? false) { Location = location }); break;
                    case "x_search":
                        hosted.Add(new XaiXSearchTool(Strings(tool, "allowed_x_handles"), Strings(tool, "excluded_x_handles"), Date(tool, "from_date"), Date(tool, "to_date"), Boolean(tool, "enable_image_understanding") ?? false, Boolean(tool, "enable_video_understanding") ?? false)); break;
                    case "file_search": hosted.Add(new LlmDocumentSearchTool(Strings(tool, "vector_store_ids") ?? throw XaiRealtimeProtocol.Malformed(), Index(tool, "max_num_results"))); break;
                    case "mcp":
                        // Wire authorization is not a caller runtime authority. It is never persisted into observations.
                        hosted.Add(new LlmRemoteMcpTool(Required(tool, "server_url"), Required(tool, "server_label"), String(tool, "server_description"), Strings(tool, "allowed_tools"), Headers: Map(tool, "headers"))); break;
                    default: throw XaiRealtimeProtocol.Malformed();
                }
            }
        }
        RealtimeTurnDetection? detection = OptionalObject(session, "turn_detection") is { } turn
            ? String(turn, "type") switch { null => new RealtimeManualTurnDetection(), "server_vad" => new RealtimeServerVad(IdleTimeout: Milliseconds(turn, "idle_timeout_ms")), _ => throw XaiRealtimeProtocol.Malformed() } : null;
        RealtimeSessionConfig result = new()
        {
            ModelId = String(session, "model"), Instructions = String(session, "instructions"), Voice = String(session, "voice"),
            ReasoningEffort = OptionalObject(session, "reasoning") is { } reasoning ? String(reasoning, "effort") switch { null => null, "none" => LlmReasoningLevel.None, "high" => LlmReasoningLevel.High, _ => throw XaiRealtimeProtocol.Malformed() } : null,
            TurnDetection = detection, Tools = hasTools ? functions.ToArray() : null, HostedTools = hasTools ? hosted.ToArray() : null,
            Resumption = OptionalObject(session, "resumption") is { } resumption && Boolean(resumption, "enabled") is { } enabled ? new(enabled) : null,
            ProviderOptions = Value(session, "replace", out _) ? new XaiRealtimeOptions(Map(session, "replace")) : null
        };
        if (OptionalObject(session, "audio") is { } audio)
        {
            var input = OptionalObject(audio, "input"); var transcription = input is { } value ? OptionalObject(value, "transcription") : null;
            string? transcriptionModel = transcription is { } model ? String(model, "model") : null;
            result = result with { InputAudio = AudioConfig(audio, "input"), OutputAudio = AudioConfig(audio, "output"),
                TranscriptionLanguage = transcription is { } t ? String(t, "language_hint") : null,
                TranscriptionKeyTerms = transcription is { } terms ? Strings(terms, "keyterms") : null,
                ProviderOptions = result.ProviderOptions is null && transcriptionModel is null ? null
                    : new XaiRealtimeOptions((result.ProviderOptions as XaiRealtimeOptions)?.Replace) { TranscriptionModel = transcriptionModel },
                OutputSpeed = OptionalObject(audio, "output") is { } output && Value(output, "speed", out JsonElement speed) ? speed.GetDouble() : null };
        }
        return result;
    }
    private static bool? Boolean(JsonElement root, string name)
    { if (!Value(root, name, out var value)) return null; return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw XaiRealtimeProtocol.Malformed(); }
    private static DateOnly? Date(JsonElement root, string name)
    { string? value = String(root, name); return value is null ? null : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw XaiRealtimeProtocol.Malformed(); }
}
