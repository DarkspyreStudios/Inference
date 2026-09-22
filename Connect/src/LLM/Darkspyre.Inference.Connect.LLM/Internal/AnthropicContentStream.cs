using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal sealed class AnthropicContentStream
{
    private readonly Dictionary<int, JsonObject> _blocks = [];
    private readonly Dictionary<int, StringBuilder> _toolArguments = [];

    public void ObserveStart(JsonElement root)
    {
        if (LlmJson.GetInt(root, "index") is not int index
            || !root.TryGetProperty("content_block", out JsonElement contentBlock)
            || contentBlock.ValueKind != JsonValueKind.Object
            || JsonNode.Parse(contentBlock.GetRawText()) is not JsonObject block)
        {
            return;
        }

        _blocks[index] = block;
        if (LlmJson.GetString(contentBlock, "type") == "tool_use")
        {
            _toolArguments[index] = new StringBuilder();
        }
    }

    public void ObserveDelta(JsonElement root)
    {
        if (LlmJson.GetInt(root, "index") is not int index
            || !_blocks.TryGetValue(index, out JsonObject? block)
            || LlmJson.GetObject(root, "delta") is not { } delta)
        {
            return;
        }

        switch (LlmJson.GetString(delta, "type"))
        {
            case "text_delta":
                Append(block, "text", LlmJson.GetString(delta, "text"));
                break;
            case "thinking_delta":
                Append(block, "thinking", LlmJson.GetString(delta, "thinking"));
                break;
            case "signature_delta":
                Append(block, "signature", LlmJson.GetString(delta, "signature"));
                break;
            case "input_json_delta":
                if (_toolArguments.TryGetValue(index, out StringBuilder? arguments))
                {
                    arguments.Append(LlmJson.GetString(delta, "partial_json"));
                }

                break;
        }
    }

    public void ObserveStop(JsonElement root)
    {
        if (LlmJson.GetInt(root, "index") is int index)
        {
            FinalizeToolInput(index);
        }
    }

    public LlmProviderResponseState? Encode(LlmConnection connection, string modelId) =>
        _blocks.Count == 0 ? null : ProviderResponseStateCodec.Encode(connection, modelId, BuildContent());

    private JsonArray BuildContent()
    {
        foreach (int index in _toolArguments.Keys)
        {
            FinalizeToolInput(index);
        }

        var content = new JsonArray();
        foreach (JsonObject block in _blocks.OrderBy(pair => pair.Key).Select(pair => pair.Value))
        {
            content.Add(block.DeepClone());
        }

        return content;
    }

    private void FinalizeToolInput(int index)
    {
        if (!_blocks.TryGetValue(index, out JsonObject? block)
            || !_toolArguments.TryGetValue(index, out StringBuilder? arguments)
            || arguments.Length == 0)
        {
            return;
        }

        block["input"] = JsonNode.Parse(arguments.ToString());
    }

    private static void Append(JsonObject block, string name, string? delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        block[name] = (block[name]?.GetValue<string>() ?? string.Empty) + delta;
    }
}
