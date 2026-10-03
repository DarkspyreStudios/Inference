using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal enum ModelCatalogFamily { Chat, Image, Video, Embedding }
internal enum ModelCatalogPart { Generic, Chat, Image, Video, Embedding }

internal interface ILlmModelCatalogClient
{
    Task<IReadOnlyList<JsonElement>> ReadCatalogPartAsync(LlmConnection snapshot, ModelCatalogPart part,
        Action ownerFence, CancellationToken cancellationToken);

    IReadOnlyList<LlmModel> ProjectCatalog(LlmConnection snapshot, ModelCatalogFamily family,
        IReadOnlyList<JsonElement> generic, IReadOnlyList<JsonElement> specific);
}
