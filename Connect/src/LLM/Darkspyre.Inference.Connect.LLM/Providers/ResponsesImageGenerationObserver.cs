using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Reports the image_generation_call items of a Codex or OpenAI API response as hosted image calls.</summary>
internal sealed class ResponsesImageGenerationObserver(Func<Exception> malformed) : ResponsesHostedToolObserver
{
    private const string Name = "image_generation";

    /// <summary>Started and completed events for image_generation_call items; nothing for any other event.
    /// Throws malformed() for an item without an id or a base64 result.</summary>
    internal override IEnumerable<LlmStreamEvent> Observe(JsonElement root)
    {
        string? type = LlmJson.GetString(root, "type");
        if (type is not ("response.output_item.added" or "response.output_item.done")
            || !root.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object
            || LlmJson.GetString(item, "type") != "image_generation_call")
            yield break;
        string id = LlmJson.GetString(item, "id") is { } value && !string.IsNullOrWhiteSpace(value) ? value : throw malformed();
        if (type == "response.output_item.added")
        {
            yield return new LlmHostedToolCallStartedEvent(id, LlmHostedToolKind.ImageGeneration, Name);
            yield break;
        }
        string result = LlmJson.GetString(item, "result") is { Length: > 0 } encoded ? encoded : throw malformed();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(result); }
        catch (FormatException) { throw malformed(); }
        string format = LlmJson.GetString(item, "output_format") is { Length: > 0 } reported ? reported : "png";
        yield return new LlmHostedToolCallCompletedEvent(new(id, LlmHostedToolKind.ImageGeneration, Name,
            LlmJson.GetString(item, "status"), null, new LlmHostedImageOutput(bytes, "image/" + format)));
    }

    /// <summary>Returns no events: Observe already reported every image of the response.</summary>
    internal override Task<IReadOnlyList<LlmStreamEvent>> CompleteAsync(JsonElement root, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<LlmStreamEvent>>([]);

    internal override LlmChatResult Decorate(LlmChatResult result) => result;
}
