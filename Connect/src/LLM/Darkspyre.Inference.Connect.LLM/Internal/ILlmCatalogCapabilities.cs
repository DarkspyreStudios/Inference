using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// An adapter whose per-model capabilities depend on its provider catalog. The service attaches the connection's
/// kept model list as <see cref="LlmConnection.Catalog"/> before it asks such an adapter for capabilities or
/// generation.
/// </summary>
internal interface ILlmCatalogCapabilities
{
    /// <summary>The provider's catalog entry for the model, or null when the connection carries no entry for it.</summary>
    static JsonElement? EntryFor(LlmConnection connection, string? modelId) =>
        modelId is null ? null : connection.Catalog?.FirstOrDefault(model => model.Id == modelId)?.CatalogEntry;
}
