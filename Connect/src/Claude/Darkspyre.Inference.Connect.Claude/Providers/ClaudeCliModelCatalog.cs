using System.Collections.ObjectModel;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class ClaudeCliModelCatalog
{
    /// <summary>
    /// The CLI's models. The CLI reports no window, so each model takes the window and output limit
    /// <paramref name="discovered"/> lists for the model it resolves to, or for its own id; a model it does not list, or
    /// lists without a size, takes the published limits. A discovered model the CLI does not list under its id follows
    /// the CLI's models, so a session on that id reads its discovered limits.
    /// </summary>
    /// <param name="initialization">The CLI's initialize response.</param>
    /// <param name="discovered">The connection's discovered catalog, such as Anthropic's model listing, or null.</param>
    internal static ReadOnlyCollection<LlmModel> Project(JsonElement initialization, IReadOnlyList<LlmModel>? discovered = null)
    {
        if (initialization.ValueKind != JsonValueKind.Object
            || !initialization.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            throw ProtocolFailure();
        var result = new List<LlmModel>();
        foreach (JsonElement model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("value", out JsonElement value)
                || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
                || !model.TryGetProperty("displayName", out JsonElement name) || name.ValueKind != JsonValueKind.String)
                throw ProtocolFailure();
            string resolved = model.TryGetProperty("resolvedModel", out JsonElement target) && target.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(target.GetString()) ? target.GetString()! : value.GetString()!;
            LlmModel? known = discovered?.FirstOrDefault(item => item.Id == resolved)
                ?? discovered?.FirstOrDefault(item => item.Id == value.GetString());
            ClaudeModelLimit limit = ClaudeModelLimits.Get(resolved);
            result.Add(new(value.GetString()!, name.GetString()!)
            {
                CatalogEntry = model.Clone(),
                MaxContextTokens = known?.MaxContextTokens ?? limit.ContextTokens,
                MaxOutputTokens = known?.MaxOutputTokens ?? limit.OutputTokens,
            });
        }
        LlmModel[] unlisted = [.. discovered?.Where(item => result.All(model => model.Id != item.Id)) ?? []];
        return Array.AsReadOnly([.. result, .. unlisted]);
    }

    private static LlmProviderException ProtocolFailure() => new("Claude CLI returned invalid model initialization.",
        LlmTransport.AnthropicClaudeCli, providerCode: "claude_cli_model_initialization_failed");
}
