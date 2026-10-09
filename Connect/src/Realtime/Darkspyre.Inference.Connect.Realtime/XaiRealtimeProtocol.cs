using System.Globalization;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Realtime;

internal static class XaiRealtimeProtocol
{
    internal static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000];
    internal static readonly LlmHostedToolKind[] HostedKinds = [LlmHostedToolKind.WebSearch, LlmHostedToolKind.SocialSearch, LlmHostedToolKind.DocumentSearch, LlmHostedToolKind.RemoteMcp];
    internal static InferenceException Invalid() => new(InferenceFailureCode.InvalidRequest, "The realtime request contains invalid data.");
    internal static InferenceException Unsupported() => new(InferenceFailureCode.UnsupportedFeature, "The realtime route does not map this explicit control.");
    internal static InferenceException Malformed() => new(InferenceFailureCode.ProtocolFailure, "xAI returned an invalid realtime message.");
    internal static InferenceException Capacity() => new(InferenceFailureCode.CapacityExhausted, "The configured realtime capacity was exceeded.");

    internal static RealtimeConnection Connection(RealtimeConnection connection, XaiFamilyBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Transport != RealtimeTransport.XaiRealtime || binding is null || connection.Xai is null
            || connection.Xai.ConnectionId != binding.Selection.ConnectionId
            || connection.Authorization is not null && !ReferenceEquals(connection.Authorization, binding.Authorization)
            || connection.Endpoint is not { IsAbsoluteUri: true } || connection.Endpoint.Scheme != "https"
            || connection.Endpoint.UserInfo.Length != 0 || connection.Endpoint.Query.Length != 0 || connection.Endpoint.Fragment.Length != 0
            || connection.RequestTimeout <= TimeSpan.Zero || connection.RequestTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw Invalid();
        ProviderHttp.ValidateAdditionalHeaders(connection.AdditionalHeaders);
        return connection with { AdditionalHeaders = new Dictionary<string, string>(connection.AdditionalHeaders, StringComparer.OrdinalIgnoreCase) };
    }

    internal static RealtimeSessionConfig Config(RealtimeSessionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        RealtimeSessionConfig result = RealtimeRequestData.Snapshot(config);
        if (result.ModelId is not null && string.IsNullOrWhiteSpace(result.ModelId)) throw Invalid();
        if (result.ReasoningEffort is not (null or LlmReasoningLevel.None or LlmReasoningLevel.High)) throw Unsupported();
        if (result.OutputSpeed is { } speed && (!double.IsFinite(speed) || speed < .7 || speed > 1.5)) throw Invalid();
        if (result.Resumption is { Enabled: false, Conversation: not null }) throw Invalid();
        if (result.TurnDetection is RealtimeServerVad vad)
        {
            if (vad.Threshold is not null || vad.SilenceDuration is not null || vad.PrefixPadding is not null) throw Unsupported();
            if (vad.IdleTimeout is { } idle && idle < TimeSpan.Zero) throw Invalid();
        }
        else if (result.TurnDetection is not (null or RealtimeManualTurnDetection)) throw Unsupported();
        ValidateAudio(result.InputAudio); ValidateAudio(result.OutputAudio);
        if (result.TranscriptionKeyTerms is { } terms && (terms.Count > 100 || terms.Any(term => term is null))) throw Invalid();
        foreach (var tool in result.Tools ?? [])
            if (string.IsNullOrWhiteSpace(tool.Name) || tool.Strict || tool.InputSchema.ValueKind != JsonValueKind.Object) throw Unsupported();
        if (result.HostedTools is { } hosted)
        {
            HostedTools.Validate(hosted, null, HostedKinds.ToHashSet());
            foreach (var tool in hosted)
                switch (tool)
                {
                    case LlmWebSearchTool web:
                        Filters(web.AllowedDomains, web.ExcludedDomains, 5);
                        if (web.IncludeImageSearch) throw Unsupported();
                        break;
                    case XaiXSearchTool search:
                        Filters(search.AllowedHandles, search.ExcludedHandles, 20);
                        if (search.FromDate > search.ToDate) throw Invalid();
                        break;
                    case LlmRemoteMcpTool mcp:
                        if (mcp.DeferLoading) throw Unsupported();
                        if (!Uri.TryCreate(mcp.ServerUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) throw Invalid();
                        break;
                }
        }
        return result;
    }

    private static void Filters(IReadOnlyList<string>? allow, IReadOnlyList<string>? exclude, int limit)
    {
        if (allow is not null && exclude is not null || allow?.Count > limit || exclude?.Count > limit
            || (allow ?? []).Concat(exclude ?? []).Any(string.IsNullOrWhiteSpace)) throw Invalid();
    }

    private static void ValidateAudio(RealtimeAudioConfig? audio)
    {
        if (audio is null) return;
        if (!Enum.IsDefined(audio.Encoding) || audio.Transport is { } transport && !Enum.IsDefined(transport)) throw Invalid();
        if (audio.SampleRate is { } rate && (audio.Encoding switch
        { RealtimeAudioEncoding.Pcm16LittleEndian => !Rates.Contains(rate), RealtimeAudioEncoding.Opus => rate != 24000, _ => rate != 8000 })) throw Invalid();
    }

    internal static async Task<byte[]> UpdateAsync(RealtimeSessionConfig config, Action fence, CancellationToken token, Action<string>? protect = null)
    {
        var authorizations = new Dictionary<LlmRemoteMcpTool, string>(ReferenceEqualityComparer.Instance);
        foreach (var mcp in (config.HostedTools ?? []).OfType<LlmRemoteMcpTool>())
            if (mcp.Authorization is { } authorization)
            {
                fence(); token.ThrowIfCancellationRequested();
                ProviderAuthorizationHeader header = await authorization.GetAsync(token).ConfigureAwait(false);
                fence(); token.ThrowIfCancellationRequested();
                if (header is null || string.IsNullOrWhiteSpace(header.Scheme) || string.IsNullOrWhiteSpace(header.Value)
                    || header.Scheme.Any(char.IsControl) || header.Value.Any(char.IsControl)) throw Invalid();
                protect?.Invoke(header.Value);
                authorizations.Add(mcp, header.Scheme + " " + header.Value);
            }
        foreach (var mcp in (config.HostedTools ?? []).OfType<LlmRemoteMcpTool>())
            foreach (string value in mcp.Headers?.Values ?? []) protect?.Invoke(value);
        return Message("session.update", writer =>
        {
            writer.WriteStartObject("session");
            WriteString(writer, "model", config.ModelId); WriteString(writer, "instructions", config.Instructions); WriteString(writer, "voice", config.Voice);
            if (config.ReasoningEffort is { } reasoning)
            { writer.WriteStartObject("reasoning"); writer.WriteString("effort", reasoning == LlmReasoningLevel.None ? "none" : "high"); writer.WriteEndObject(); }
            if (config.TurnDetection is { } detection)
            {
                writer.WriteStartObject("turn_detection");
                if (detection is RealtimeManualTurnDetection) writer.WriteNull("type");
                else { writer.WriteString("type", "server_vad"); if (((RealtimeServerVad)detection).IdleTimeout is { } idle) writer.WriteNumber("idle_timeout_ms", idle.TotalMilliseconds); }
                writer.WriteEndObject();
            }
            if (config.Resumption is { } resumption)
            { writer.WriteStartObject("resumption"); writer.WriteBoolean("enabled", resumption.Enabled); writer.WriteEndObject(); }
            if (config.InputAudio is not null || config.OutputAudio is not null || config.TranscriptionLanguage is not null
                || config.TranscriptionKeyTerms is not null || config.OutputSpeed is not null || config.ProviderOptions is XaiRealtimeOptions { TranscriptionModel: not null })
            {
                writer.WriteStartObject("audio");
                if (config.InputAudio is not null || config.TranscriptionLanguage is not null || config.TranscriptionKeyTerms is not null
                    || config.ProviderOptions is XaiRealtimeOptions { TranscriptionModel: not null })
                {
                    writer.WriteStartObject("input"); WriteAudio(writer, config.InputAudio);
                    if (config.TranscriptionLanguage is not null || config.TranscriptionKeyTerms is not null || config.ProviderOptions is XaiRealtimeOptions { TranscriptionModel: not null })
                    {
                        writer.WriteStartObject("transcription"); WriteString(writer, "language_hint", config.TranscriptionLanguage);
                        WriteStrings(writer, "keyterms", config.TranscriptionKeyTerms);
                        WriteString(writer, "model", (config.ProviderOptions as XaiRealtimeOptions)?.TranscriptionModel); writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                if (config.OutputAudio is not null || config.OutputSpeed is not null)
                {
                    writer.WriteStartObject("output"); WriteAudio(writer, config.OutputAudio);
                    if (config.OutputSpeed is { } speed) writer.WriteNumber("speed", speed); writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            if (config.Tools is not null || config.HostedTools is not null)
            {
                writer.WriteStartArray("tools");
                foreach (var tool in config.Tools ?? [])
                {
                    writer.WriteStartObject(); writer.WriteString("type", "function"); writer.WriteStartObject("function");
                    writer.WriteString("name", tool.Name); WriteString(writer, "description", tool.Description);
                    writer.WritePropertyName("parameters"); tool.InputSchema.WriteTo(writer); writer.WriteEndObject(); writer.WriteEndObject();
                }
                foreach (var tool in config.HostedTools ?? []) WriteHosted(writer, tool, authorizations);
                writer.WriteEndArray();
            }
            if (config.ProviderOptions is XaiRealtimeOptions { Replace: { } replace })
            { writer.WriteStartObject("replace"); foreach (var pair in replace) writer.WriteString(pair.Key, pair.Value); writer.WriteEndObject(); }
            writer.WriteEndObject();
        });
    }

    private static void WriteHosted(Utf8JsonWriter writer, LlmHostedTool tool, Dictionary<LlmRemoteMcpTool, string> authorizations)
    {
        writer.WriteStartObject();
        switch (tool)
        {
            case LlmWebSearchTool web:
                writer.WriteString("type", "web_search"); WriteStrings(writer, "allowed_domains", web.AllowedDomains); WriteStrings(writer, "excluded_domains", web.ExcludedDomains);
                writer.WriteBoolean("enable_image_understanding", web.IncludeImageUnderstanding);
                if (web.Location is { } location)
                {
                    writer.WriteStartObject("location"); WriteString(writer, "country", location.Country); WriteString(writer, "city", location.City);
                    WriteString(writer, "region", location.Region); WriteString(writer, "timezone", location.Timezone); writer.WriteEndObject();
                }
                break;
            case XaiXSearchTool search:
                writer.WriteString("type", "x_search"); WriteStrings(writer, "allowed_x_handles", search.AllowedHandles); WriteStrings(writer, "excluded_x_handles", search.ExcludedHandles);
                if (search.FromDate is { } from) writer.WriteString("from_date", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (search.ToDate is { } to) writer.WriteString("to_date", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                writer.WriteBoolean("enable_image_understanding", search.IncludeImageUnderstanding); writer.WriteBoolean("enable_video_understanding", search.IncludeVideoUnderstanding); break;
            case LlmDocumentSearchTool document:
                writer.WriteString("type", "file_search"); WriteStrings(writer, "vector_store_ids", document.CollectionIds);
                if (document.MaxResults is { } count) writer.WriteNumber("max_num_results", count); break;
            case LlmRemoteMcpTool mcp:
                writer.WriteString("type", "mcp"); writer.WriteString("server_label", mcp.ServerLabel); writer.WriteString("server_url", mcp.ServerUrl);
                WriteString(writer, "server_description", mcp.ServerDescription); WriteStrings(writer, "allowed_tools", mcp.AllowedTools);
                if (authorizations.TryGetValue(mcp, out string? authorization)) writer.WriteString("authorization", authorization);
                if (mcp.Headers is { } headers) { writer.WriteStartObject("headers"); foreach (var pair in headers) writer.WriteString(pair.Key, pair.Value); writer.WriteEndObject(); }
                break;
            default: throw Unsupported();
        }
        writer.WriteEndObject();
    }

    internal static RealtimeConversationItem Item(RealtimeConversationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item switch
        {
            RealtimeMessageItem message when Enum.IsDefined(message.Role) => message with
            {
                Content = message.Content.Select<RealtimeContent, RealtimeContent>(part => part switch
                {
                    RealtimeTextContent { Text: not null } text => text,
                    RealtimeAudioContent { Audio: { } audio } observed => observed with { Audio = audio.ToArray() },
                    _ => throw Invalid()
                }).ToArray()
            },
            RealtimeFunctionCallItem call when !string.IsNullOrWhiteSpace(call.Name) && IsArguments(call.ArgumentsJson) => call,
            RealtimeFunctionOutputItem output => output with { Output = XaiRealtimeToolOutput.Snapshot(output.Output) },
            XaiRealtimeForceMessageItem force when force.Id is null => force,
            _ => throw Invalid()
        };
    }
    private static bool IsArguments(string? arguments)
    {
        if (arguments is null) return false;
        try { using var document = JsonDocument.Parse(arguments); return document.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }
    internal static byte[] ItemMessage(RealtimeConversationItem item, string? previous, string? output = null) => Message("conversation.item.create", writer =>
    {
        WriteString(writer, "previous_item_id", previous); writer.WriteStartObject("item"); WriteString(writer, "id", item.Id);
        switch (item)
        {
            case RealtimeMessageItem message:
                writer.WriteString("type", "message"); writer.WriteString("role", message.Role switch { RealtimeMessageRole.User => "user", RealtimeMessageRole.Assistant => "assistant", _ => "system" });
                writer.WriteStartArray("content");
                foreach (var part in message.Content)
                {
                    writer.WriteStartObject();
                    switch (part)
                    {
                        case RealtimeTextContent text:
                            writer.WriteString("type", message.Role == RealtimeMessageRole.User ? "input_text" : "text"); writer.WriteString("text", text.Text); break;
                        case RealtimeAudioContent audio:
                            writer.WriteString("type", message.Role == RealtimeMessageRole.User ? "input_audio" : "audio"); writer.WriteBase64String("audio", audio.Audio!.Value.Span);
                            WriteString(writer, "transcript", audio.Transcript); break;
                        default: throw Unsupported();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray(); break;
            case RealtimeFunctionCallItem call:
                writer.WriteString("type", "function_call"); writer.WriteString("name", call.Name); writer.WriteString("arguments", call.ArgumentsJson); WriteString(writer, "call_id", call.CallId); break;
            case RealtimeFunctionOutputItem result:
                writer.WriteString("type", "function_call_output"); writer.WriteString("call_id", result.CallId); writer.WriteString("output", output ?? throw Invalid()); break;
            case XaiRealtimeForceMessageItem force:
                writer.WriteString("type", "force_message"); writer.WriteString("role", "assistant");
                writer.WriteStartArray("content"); writer.WriteStartObject(); writer.WriteString("type", "output_text"); writer.WriteString("text", force.Text); writer.WriteEndObject(); writer.WriteEndArray();
                if (force.Interruptible is { } interruptible) writer.WriteBoolean("interruptible", interruptible); break;
            default: throw Unsupported();
        }
        writer.WriteEndObject();
    });

    internal static byte[] Response(RealtimeResponseOptions? options)
    {
        if (options?.Modalities?.Any(value => !Enum.IsDefined(value)) is true || options?.Metadata?.Count > 16) throw Invalid();
        // The published character unit is unspecified; preserve strings without an invented UTF-16 cutoff.
        var modalities = options?.Modalities?.ToArray();
        var metadata = options?.Metadata?.ToArray();
        return Message("response.create", writer =>
        {
            if (options is null) return;
            writer.WriteStartObject("response"); WriteString(writer, "instructions", options.Instructions);
            if (modalities is not null) { writer.WriteStartArray("modalities"); foreach (var value in modalities) writer.WriteStringValue(value == RealtimeModality.Text ? "text" : "audio"); writer.WriteEndArray(); }
            if (metadata is not null) { writer.WriteStartObject("metadata"); foreach (var pair in metadata) writer.WriteString(pair.Key, pair.Value); writer.WriteEndObject(); }
            writer.WriteEndObject();
        });
    }
    internal static byte[] Message(string type, Action<Utf8JsonWriter>? fields = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); writer.WriteString("type", type); fields?.Invoke(writer); writer.WriteEndObject(); }
        return stream.ToArray();
    }
    private static void WriteAudio(Utf8JsonWriter writer, RealtimeAudioConfig? audio)
    {
        if (audio is null) return;
        writer.WriteStartObject("format"); writer.WriteString("type", audio.Encoding switch
        { RealtimeAudioEncoding.Pcm16LittleEndian => "audio/pcm", RealtimeAudioEncoding.Mulaw => "audio/pcmu", RealtimeAudioEncoding.Alaw => "audio/pcma", RealtimeAudioEncoding.Opus => "audio/opus", _ => throw Invalid() });
        if (audio.Encoding == RealtimeAudioEncoding.Pcm16LittleEndian && audio.SampleRate is { } rate) writer.WriteNumber("rate", rate); writer.WriteEndObject();
        if (audio.Transport is { } transport) writer.WriteString("transport", transport == RealtimeAudioTransport.Json ? "json" : "binary");
    }
    internal static void WriteString(Utf8JsonWriter writer, string name, string? value) { if (value is not null) writer.WriteString(name, value); }
    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string>? values)
    { if (values is null) return; writer.WriteStartArray(name); foreach (var value in values) writer.WriteStringValue(value); writer.WriteEndArray(); }
}
