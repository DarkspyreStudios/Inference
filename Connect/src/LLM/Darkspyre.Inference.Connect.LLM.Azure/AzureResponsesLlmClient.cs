using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Azure;

internal sealed class AzureResponsesLlmClient(HttpClient http) : ResponsesLlmClientBase(http)
{
    public override LlmTransport Transport => LlmTransport.AzureOpenAiResponses;
    public override LlmProviderDefinition ProviderDefinition => AzureProviderDefinition.Responses;
    protected override LlmProviderCapabilities DefaultCapabilities => OpenAiResponsesLlmClient.ProtocolCapabilities;
    protected override bool OffersNonStreamingRequests => true;
    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        EnsureTransport(connection);
        return AzureModelProfiles.Resolve(connection, modelId, DefaultCapabilities, responses: true, OffersUsageDeltas);
    }
    protected override Uri BuildRequestUri(LlmConnection connection, LlmChatRequest request) => AzureEndpoints.Generation(connection, request.ModelId);
    protected override void ApplyRequestHeaders(HttpRequestMessage message, LlmConnection connection, LlmChatRequest request) => AzureEndpoints.Authorize(message, connection);
    protected override void ValidateModelSampling(LlmConnection connection, LlmChatRequest request) => AzureModelProfiles.ValidateSampling(connection, request);
    protected override bool ReasonsByDefault(LlmConnection connection, string modelId) =>
        AzureModelProfiles.Profile(connection) is { Verified: true, IsReasoning: true };
    protected override LlmAccountUsageSnapshot? ObserveAccountUsage(HttpResponseMessage response) => null;
}
