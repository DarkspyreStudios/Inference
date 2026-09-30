using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Projects public reasoning text while retaining opaque replay fields without modifying their content.</summary>
internal sealed class ChatReasoning
{
    private readonly StringBuilder _content = new();
    private JsonArray? _details;

    public string? ReasoningContent => _content.Length == 0 ? null : _content.ToString();
    public JsonArray? ReasoningDetails => (JsonArray?)_details?.DeepClone();

    public LlmReasoningDeltaEvent? Observe(JsonElement message)
    {
        string? content = LlmJson.GetString(message, "reasoning_content");
        if (!string.IsNullOrEmpty(content)) { _content.Append(content); }
        var texts = new StringBuilder();
        var summaries = new StringBuilder();
        if (message.TryGetProperty("reasoning_details", out JsonElement details)
            && details.ValueKind != JsonValueKind.Null)
        {
            if (details.ValueKind != JsonValueKind.Array) { throw Failure(); }
            _details ??= [];
            foreach (JsonElement entry in details.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) { throw Failure(); }
                var detail = new JsonObject();
                foreach (JsonProperty field in entry.EnumerateObject())
                {
                    if (Allowed(field.Name)) { detail[field.Name] = JsonNode.Parse(field.Value.GetRawText()); }
                }
                try { ValidateDetail(detail); }
                catch (ArgumentException) { throw Failure(); }
                string type = (string)detail["type"]!;
                if (type == "reasoning.text") { texts.Append((string?)detail["text"]); }
                if (type == "reasoning.summary") { summaries.Append((string?)detail["summary"]); }
                if (type is "reasoning.text" or "reasoning.summary"
                    && _details.LastOrDefault() is JsonObject previous && (string?)previous["type"] == type)
                {
                    string field = type == "reasoning.text" ? "text" : "summary";
                    previous[field] = ((string?)previous[field] ?? string.Empty) + ((string?)detail[field] ?? string.Empty);
                    foreach (string name in MetadataFields)
                    {
                        if (previous[name] is null && detail[name] is { } value) { previous[name] = value.DeepClone(); }
                    }
                }
                else { _details.Add(detail); }
            }
        }

        if (!string.IsNullOrEmpty(content)) { return new(content, false); }
        if (LlmJson.GetString(message, "reasoning") is { Length: > 0 } reasoning) { return new(reasoning, false); }
        if (texts.Length > 0) { return new(texts.ToString(), false); }
        return summaries.Length > 0 ? new(summaries.ToString(), true) : null;
    }

    private static readonly string[] MetadataFields = ["signature", "format", "id"];
    private static bool Allowed(string name) => name is "type" or "id" or "format" or "index" or "text" or "summary" or "data" or "signature";

    internal static void ValidateDetail(JsonObject detail)
    {
        try
        {
            if (detail.Any(field => !Allowed(field.Key))
                || (string?)detail["type"] is not ("reasoning.text" or "reasoning.summary" or "reasoning.encrypted"))
            { throw new ArgumentException("Invalid reasoning detail."); }
            foreach (var field in detail.Where(field => field.Key != "index")) { _ = field.Value?.GetValue<string>(); }
            if (detail["index"] is { } index && index.GetValue<int>() < 0) { throw new ArgumentException("Invalid reasoning detail index."); }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
        { throw new ArgumentException("Invalid reasoning detail."); }
    }

    private static LlmProviderException Failure() => new("The provider returned an unsupported reasoning detail.", LlmTransport.OpenAiCompatibleChat);
}
