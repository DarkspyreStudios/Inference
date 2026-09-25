using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>A storage-ignorant adapter for one provider wire protocol.</summary>
public interface ILlmClient
{
    LlmTransport Transport { get; }

    /// <summary>Canonical provider name, defaults, and guided setup owned by this adapter.</summary>
    LlmProviderDefinition ProviderDefinition { get; }

    /// <summary>Canonical user-facing provider name.</summary>
    string DisplayName => ProviderDefinition.DisplayName;

    LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null);

    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LlmModel>> ListModelsAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default);
}
