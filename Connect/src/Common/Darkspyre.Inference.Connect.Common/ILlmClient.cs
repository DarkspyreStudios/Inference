using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>A storage-ignorant adapter for one provider wire protocol.</summary>
internal interface ILlmClient
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

    Task<LlmModelListing> ListModelsAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default);
}

/// <summary>The selected adapter's own-reservation release capability.</summary>
internal interface ILlmModelReservationClient
{
    /// <summary>Releases only the admitted caller's hold and reports the actual public model state.</summary>
    Task<LlmModelReleaseResult> ReleaseModelAsync(LlmConnection connection, string modelId,
        CancellationToken cancellationToken);
}
