using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

internal sealed class CacheListingClient(Func<CancellationToken, Task<IReadOnlyList<LlmModel>>> list) : ILlmClient
{
    public LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;
    public LlmProviderDefinition ProviderDefinition => new()
    {
        Id = "cache-test",
        DisplayName = "Cache test",
        Description = "Controlled listing",
        Transport = Transport,
        DefaultEndpoint = "https://provider.invalid/v1",
        SetupSteps = []
    };

    public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new() { SupportsModelListing = true };
    public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default) => list(cancellationToken);
    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    internal static LlmConnection Connection(string version) => new()
    { Version = version, Transport = LlmTransport.OpenAiCompatibleChat, Endpoint = "https://provider.invalid/v1" };
}
