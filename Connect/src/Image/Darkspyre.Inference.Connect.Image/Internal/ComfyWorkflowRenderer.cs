using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.Image.Internal;

internal static class ComfyWorkflowRenderer
{
    public static JsonObject Render(
        JsonElement workflowGraph,
        IReadOnlyDictionary<string, object> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        JsonNode root = JsonNode.Parse(workflowGraph.GetRawText())
            ?? throw new InvalidOperationException("The image workflow graph was empty.");
        Replace(root, values);
        string rendered = root.ToJsonString();
        if (rendered.Contains("${", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The rendered image workflow contains unresolved placeholders.");
        }

        return root.AsObject();
    }

    public static string RenderText(string template, IReadOnlyDictionary<string, object> values)
    {
        string rendered = template;
        foreach ((string key, object value) in values)
        {
            rendered = rendered.Replace(
                $"${{{key}}}",
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                StringComparison.Ordinal);
        }

        return rendered;
    }

    private static void Replace(JsonNode node, IReadOnlyDictionary<string, object> values)
    {
        if (node is JsonObject objectNode)
        {
            foreach (string key in objectNode.Select(item => item.Key).ToList())
            {
                JsonNode? child = objectNode[key];
                JsonNode? replacement = ReplaceValue(child, values);
                if (!ReferenceEquals(child, replacement))
                {
                    objectNode[key] = replacement;
                }
            }
        }
        else if (node is JsonArray arrayNode)
        {
            for (int index = 0; index < arrayNode.Count; index++)
            {
                JsonNode? child = arrayNode[index];
                JsonNode? replacement = ReplaceValue(child, values);
                if (!ReferenceEquals(child, replacement))
                {
                    arrayNode[index] = replacement;
                }
            }
        }
    }

    private static JsonNode? ReplaceValue(JsonNode? node, IReadOnlyDictionary<string, object> values)
    {
        if (node is not JsonValue valueNode || !valueNode.TryGetValue(out string? text))
        {
            if (node is not null)
            {
                Replace(node, values);
            }

            return node;
        }

        foreach ((string key, object value) in values)
        {
            string placeholder = $"${{{key}}}";
            if (string.Equals(text, placeholder, StringComparison.Ordinal))
            {
                return JsonValue.Create(value);
            }
        }

        return JsonValue.Create(RenderText(text, values));
    }
}
