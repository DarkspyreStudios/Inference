using System.Runtime.ExceptionServices;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed class XaiHostedToolObserver(Func<Exception> malformed, XaiHostedToolPreparation preparation)
    : ResponsesHostedToolObserver
{
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private readonly HashSet<string> _citationKeys = new(StringComparer.Ordinal);
    private readonly List<LlmCitation> _citations = [];
    private readonly List<InferenceResourceCommitResult> _generated = [];
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] JpegSignature = [255, 216, 255];
    private bool _completed;

    internal override IEnumerable<LlmStreamEvent> Observe(JsonElement root)
    {
        string? type = LlmJson.GetString(root, "type");
        if (type == "response.output_item.added" && root.TryGetProperty("item", out JsonElement item)
            && Identify(item) is { } tool && CallIdentity(item) is { } id
            && !string.IsNullOrWhiteSpace(id) && _started.Add(id))
            yield return new LlmHostedToolCallStartedEvent(id, tool.Kind, tool.Name);
        if (type == "response.output_item.done" && root.TryGetProperty("item", out JsonElement done))
            foreach (LlmStreamEvent citation in ObserveCitations(done, LlmJson.GetInt(root, "output_index"))) yield return citation;
        if (type is "response.image_generation_call.in_progress" or "response.image_generation_call.generating")
        {
            string? imageId = OptionalString(root, "item_id");
            if (string.IsNullOrWhiteSpace(imageId)) yield break;
            if (_started.Add(imageId)) yield return new LlmHostedToolCallStartedEvent(imageId, LlmHostedToolKind.ImageGeneration, "image_generation");
            if (type == "response.image_generation_call.generating")
                yield return new LlmHostedToolCallProgressEvent(imageId, LlmHostedToolPhase.Generating);
        }
    }

    internal override async Task<IReadOnlyList<LlmStreamEvent>> CompleteAsync(JsonElement root, CancellationToken token)
    {
        if (_completed) return [];
        _completed = true;
        preparation.Fence();
        JsonElement response = root.TryGetProperty("response", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.Object ? nested : root;
        var events = new List<LlmStreamEvent>();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        if (response.TryGetProperty("output", out JsonElement output))
        {
            if (output.ValueKind != JsonValueKind.Array) throw malformed();
            int index = 0;
            foreach (JsonElement item in output.EnumerateArray())
            {
                events.AddRange(ObserveCitations(item, index++));
                if (Identify(item) is not { } tool) continue;
                string? id = OptionalString(item, "id");
                string? callId = CallIdentity(item);
                string? status = OptionalString(item, "status");
                // Equal id-less payloads can represent distinct invocations.
                if (!string.IsNullOrWhiteSpace(id) && !completed.Add(id)) continue;
                JsonElement? input = null;
                LlmHostedToolOutput? result = null;
                switch (tool.Kind)
                {
                    case LlmHostedToolKind.DocumentSearch:
                        input = OptionalArray(item, "queries", strings: true);
                        result = new LlmHostedDocumentSearchOutput(ReadDocuments(item));
                        break;
                    case LlmHostedToolKind.WebSearch:
                        input = OptionalValue(item, "action");
                        if (input is { ValueKind: not JsonValueKind.Object }) throw malformed();
                        result = new LlmHostedSearchOutput(input is { } action ? ReadSources(action) : null);
                        break;
                    case LlmHostedToolKind.SocialSearch:
                        input = OptionalValue(item, "input");
                        if (input is { ValueKind: not JsonValueKind.String }) throw malformed();
                        break;
                    case LlmHostedToolKind.CodeExecution:
                        result = ReadCode(item);
                        break;
                    case LlmHostedToolKind.RemoteMcp:
                        input = OptionalValue(item, "arguments");
                        result = new LlmHostedMcpOutput(input, OptionalValue(item, "output"), OptionalValue(item, "error"));
                        break;
                    case LlmHostedToolKind.ImageGeneration:
                        input = OptionalValue(item, "prompt");
                        result = await ReadImageAsync(item, token).ConfigureAwait(false);
                        break;
                }
                preparation.Fence();
                events.Add(new LlmHostedToolCallCompletedEvent(new(callId, tool.Kind, tool.Name, status, input, result)));
            }
        }
        if (response.TryGetProperty("citations", out JsonElement citations) && citations.ValueKind != JsonValueKind.Null)
        {
            if (citations.ValueKind != JsonValueKind.Array) throw malformed();
            int index = 0;
            foreach (JsonElement citation in citations.EnumerateArray())
            {
                if (citation.ValueKind != JsonValueKind.String) throw malformed();
                string url = citation.GetString()!;
                string key = $"top:{index++}";
                // Top-level URLs summarize annotations; they do not supply positional occurrences.
                if (_citations.Any(observed => observed.Url == url) || !_citationKeys.Add(key)) continue;
                var observed = new LlmCitation(url);
                _citations.Add(observed);
                events.Add(new LlmCitationEvent(observed));
            }
        }
        preparation.Fence();
        return events;
    }

    internal override LlmChatResult Decorate(LlmChatResult result) => result with
    {
        Citations = _citations.ToArray(),
        GeneratedResources = _generated.ToArray()
    };

    private (LlmHostedToolKind Kind, string Name)? Identify(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw malformed();
        return LlmJson.GetString(item, "type") switch
        {
            "file_search_call" => (LlmHostedToolKind.DocumentSearch, "file_search"),
            "web_search_call" => (LlmHostedToolKind.WebSearch, "web_search"),
            "code_interpreter_call" => (LlmHostedToolKind.CodeExecution, "code_interpreter"),
            "image_generation_call" => (LlmHostedToolKind.ImageGeneration, "image_generation"),
            "mcp_call" => (LlmHostedToolKind.RemoteMcp, RequiredString(item, "name")),
            "custom_tool_call" when LlmJson.GetString(item, "name") is "x_keyword_search" or "x_semantic_search" or "x_user_search" or "x_thread_fetch"
                => (LlmHostedToolKind.SocialSearch, RequiredString(item, "name")),
            _ => null
        };
    }

    private IEnumerable<LlmStreamEvent> ObserveCitations(JsonElement item, int? index)
    {
        if (item.ValueKind != JsonValueKind.Object || LlmJson.GetString(item, "type") != "message") yield break;
        if (!item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array) throw malformed();
        string? id = OptionalString(item, "id");
        // No stable item identity or output position means terminal reconciliation must supply the occurrence.
        if (id is null && index is null) yield break;
        string identity = id is not null ? $"id:{id}" : $"index:{index}";
        int partIndex = 0;
        foreach (JsonElement part in content.EnumerateArray())
        {
            int currentPart = partIndex++;
            if (LlmJson.GetString(part, "type") != "output_text" || !part.TryGetProperty("annotations", out JsonElement annotations)
                || annotations.ValueKind == JsonValueKind.Null) continue;
            if (annotations.ValueKind != JsonValueKind.Array) throw malformed();
            int annotationIndex = 0;
            foreach (JsonElement annotation in annotations.EnumerateArray())
            {
                int currentAnnotation = annotationIndex++;
                if (LlmJson.GetString(annotation, "type") != "url_citation") continue;
                string key = $"{identity}:{currentPart}:{currentAnnotation}";
                if (!_citationKeys.Add(key)) continue;
                // The published REST fields do not establish their offset unit for emitted no-inline text.
                var citation = new LlmCitation(RequiredString(annotation, "url"), OptionalString(annotation, "title"));
                _citations.Add(citation);
                yield return new LlmCitationEvent(citation);
            }
        }
    }

    private LlmHostedSearchSource[]? ReadSources(JsonElement action)
    {
        if (OptionalArray(action, "sources") is not { } sources) return null;
        return sources.EnumerateArray().Select(source => new LlmHostedSearchSource(
            OptionalString(source, "url"), OptionalString(source, "title"))).ToArray();
    }

    private List<LlmHostedDocumentMatch>? ReadDocuments(JsonElement item)
    {
        if (OptionalArray(item, "results") is not { } results) return null;
        var matches = new List<LlmHostedDocumentMatch>();
        foreach (JsonElement result in results.EnumerateArray())
        {
            string file = RequiredString(result, "file_id");
            string filename = RequiredString(result, "filename");
            string text = RequiredString(result, "text");
            double? score = null;
            if (result.TryGetProperty("score", out JsonElement observedScore))
            {
                if (observedScore.ValueKind != JsonValueKind.Number || !observedScore.TryGetDouble(out double value) || !double.IsFinite(value)) throw malformed();
                score = value;
            }
            matches.Add(new(file, filename, score, text));
        }
        return matches;
    }

    private LlmHostedCodeOutput ReadCode(JsonElement item)
    {
        List<string>? logs = null;
        List<string>? images = null;
        if (OptionalArray(item, "outputs") is { } outputs)
        {
            logs = []; images = [];
            foreach (JsonElement output in outputs.EnumerateArray())
                switch (LlmJson.GetString(output, "type"))
                {
                    case "logs": logs.Add(RequiredString(output, "logs")); break;
                    case "image": images.Add(RequiredString(output, "url")); break;
                    default: throw malformed();
                }
        }
        return new(OptionalString(item, "code"), logs, images);
    }

    private async Task<LlmHostedImageOutput?> ReadImageAsync(JsonElement item, CancellationToken token)
    {
        string? encoded = OptionalString(item, "result");
        if (encoded is null) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw malformed(); }
        string media = bytes.AsSpan().StartsWith(PngSignature) ? "image/png"
            : bytes.AsSpan().StartsWith(JpegSignature) ? "image/jpeg" : throw malformed();
        var output = new LlmHostedImageOutput(bytes, media);
        if (preparation.Resources is not { } access) return output;
        InferenceResourceSelectionVersion scope = preparation.Selection ?? throw malformed();
        SpeechResourceWriter.Writer? writer = null;
        Exception? failure = null;
        _generated.EnsureCapacity(_generated.Count + 1);
        try
        {
            preparation.Fence();
            writer = await SpeechResourceWriter.OpenAsync(access, scope, media, bytes.Length,
                preparation.Fence, preparation.RecordCleanup, token).ConfigureAwait(false);
            await writer.WriteAsync(bytes, token).ConfigureAwait(false);
            InferenceResourceDescriptor descriptor = await writer.CommitAsync(token).ConfigureAwait(false);
            InferenceResourceCommitResult receipt = writer.ObservedCommit ?? throw malformed();
            _generated.Add(receipt);
            output = output with { Resources = new[] { descriptor } };
        }
        catch (Exception error) { failure = error; }
        if (writer is not null)
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                if (failure is not null) throw new AggregateException(failure, cleanup);
                ExceptionDispatchInfo.Capture(cleanup).Throw();
            }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        preparation.Fence();
        return output;
    }

    private static JsonElement? OptionalValue(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value)
        && value.ValueKind != JsonValueKind.Null ? value.Clone() : null;

    private JsonElement? OptionalArray(JsonElement item, string name, bool strings = false)
    {
        JsonElement? value = OptionalValue(item, name);
        if (value is { } array && (array.ValueKind != JsonValueKind.Array
            || strings && array.EnumerateArray().Any(entry => entry.ValueKind != JsonValueKind.String))) throw malformed();
        return value;
    }

    private string RequiredString(JsonElement item, string name) => OptionalString(item, name) ?? throw malformed();

    private string? CallIdentity(JsonElement item) => OptionalString(item,
        LlmJson.GetString(item, "type") == "custom_tool_call" ? "call_id" : "id");

    private string? OptionalString(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object) throw malformed();
        if (!item.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw malformed();
    }
}
